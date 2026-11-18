// ZehOS: WAMR-AOT-Bindung der Hostfunktionen und Einstieg zehos_wasm_run_aot fuer den C#-Kernel (AOT-Plan Etappe D).
// Wie zehos_host_wasm3.c nur Argumentuebernahme, Trap-Umsetzung und die aus ZEHOS_HOST_FUNCTIONS abgeleitete Tabelle;
// die Dienste selbst und jede Pruefung von Gastadresse und -laenge liegen in zehos_host_core.c.
// Nur mit -p:ZehOSWasmAot=true uebersetzt (ZEHOS_WASM_AOT); sonst fehlen WAMR und seine Kopfdateien im Build.

#ifdef ZEHOS_WASM_AOT

#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "wasm_export.h"
#include "platform_api_vmcore.h"
#include "zehos_host_core.h"

// Wie d_m3MaxNativeStack bei wasm3: Der Worker hat 8 MiB Stack (WasmRuntime.ThreadStackBytes), der AOT-Code darf
// 7 MiB unter dem Einstieg nutzen; der Rest bleibt fuer Hostfunktionen und C#-Rueckrufe.
enum { ZEHOS_AOT_NATIVE_STACK = 7 * 1024 * 1024, ZEHOS_WASM_PAGE = 65536, ZEHOS_WASM_MAXIMUM_PAGES = 65536 };

// Gehoert genau einem Lauf; die Hostfunktionen erreichen ihn ueber die Nutzerdaten der Ausfuehrungsumgebung.
typedef struct
{
    zehos_wasm_context context;
    int exited;
} zehos_wamr_run;

// Text der Ausnahme, mit der proc_exit den AOT-Code verlaesst; ausgewertet wird das Flag exited, nicht der Text.
static const char exit_exception[] = "proc_exit";
static const char memory_exception[] = "out of bounds memory access";

static void copy_message(char* target, uint32_t capacity, const char* text)
{
    if (target == NULL || capacity == 0) return;
    uint32_t index = 0;
    while (text != NULL && text[index] != 0 && index + 1 < capacity) { target[index] = text[index]; index++; }
    target[index] = 0;
}

// Basis und Ende des linearen Speichers kommen aus WAMRs eigener Adresspruefung (Offset 0 und der Bereich, in dem er
// liegt). Die Kernfunktionen pruefen jede Gastadresse und -laenge dagegen; ohne Speicher bleibt die Groesse 0, und
// jede Gastadresse trappt.
static zehos_host_call begin_call(wasm_exec_env_t env)
{
    wasm_module_inst_t instance = wasm_runtime_get_module_inst(env);
    zehos_wamr_run* run = (zehos_wamr_run*)wasm_runtime_get_user_data(env);
    zehos_host_call call = { &run->context, NULL, 0, ZEHOS_HOST_CONTINUE };
    zehos_host_depth++;
    uint8_t* base = (uint8_t*)wasm_runtime_addr_app_to_native(instance, 0);
    uint8_t* start = NULL;
    uint8_t* end = NULL;
    if (base != NULL && wasm_runtime_get_native_addr_range(instance, base, &start, &end) && start == base && end >= start)
    {
        call.memory = start;
        call.memorySize = (size_t)(end - start);
    }
    return call;
}

// Ein Trap der Kernfunktion wird zur WAMR-Ausnahme; der AOT-Code prueft sie nach jedem Hostaufruf und kehrt zurueck.
static void end_call(wasm_exec_env_t env, const zehos_host_call* call)
{
    zehos_host_depth--;
    if (call->trap == ZEHOS_HOST_CONTINUE) return;
    wasm_module_inst_t instance = wasm_runtime_get_module_inst(env);
    if (call->trap == ZEHOS_HOST_TRAP_EXIT)
    {
        ((zehos_wamr_run*)wasm_runtime_get_user_data(env))->exited = 1;
        wasm_runtime_set_exception(instance, exit_exception);
    }
    else wasm_runtime_set_exception(instance, memory_exception);
}

// AOT-Code ruft aufgeloeste Importe direkt ueber seine Funktionszeigertabelle mit der System-V-Aufrufkonvention
// (exec_env, Argumente ...) auf, ohne WAMRs Rohfunktions-Bruecke; deshalb gewoehnliche Natives. Fuer Importe, die wamrc
// aus WAMRs eigener WASI-Bibliothek kennt, wandelt der erzeugte Code Gastadressen dabei schon in Hostzeiger und prueft
// nur das erste Byte bzw. eine mit "~" folgende Laenge (im Gast belegt: fd_write erhielt iovs und written als
// Hostzeiger, fd und Anzahl als Zahl). Der Weg ueber invokeNative (etwa call_indirect auf einen Import) wandelt nach
// der hier registrierten Signatur, die "*" deshalb wie die Tabelle nennt. So kommt jede Gastadresse ("*") auf beiden
// Wegen als Hostzeiger an, alle anderen i32-Argumente als Zahl. Die Entscheidung haengt nur an der Tabelle, nicht am
// Wert: "*" steht ausschliesslich bei WASI-Funktionen, die WAMRs WASI-Bibliothek mit derselben Zeigerstelle kennt
// (zehos_v1 uebergibt Offsets als "i"). Stimmt das fuer einen Import nicht, ist der Wert ein Offset unterhalb der
// Speicherbasis und endet hier als Trap statt als falscher Zugriff (fail-closed).
// Ein Hostzeiger wird wieder zum Offset; erlaubt ist [base, base + memorySize], das Ende eingeschlossen: ein Zeiger
// genau auf das Ende ist mit Laenge 0 gueltig, jede groessere Laenge weist die Kernfunktion ab, die wie bei wasm3
// Adresse und volle Laenge prueft.
static void guest_offsets(zehos_host_call* call, const char* params, uint64_t* slots, size_t count)
{
    uintptr_t base = (uintptr_t)call->memory;
    for (size_t index = 0; index < count && params[index] != 0; index++)
    {
        if (params[index] != '*') continue;
        if (call->memory == NULL || slots[index] < base || slots[index] - base > call->memorySize)
        {
            call->trap = ZEHOS_HOST_TRAP_MEMORY;
            return;
        }
        slots[index] -= base;
    }
}

// Jedes Argument steht in einem 64-Bit-Register bzw. Stapelplatz; die Wrapper nehmen es als uint64_t, und der
// Prototyp der Kernfunktion kuerzt i32-Werte auf 32 Bit, wie bei der wasm3-Bindung. Stubs (0 Argumente) ignorieren
// uebergebene Argumente; der Aufrufer raeumt den Stapel auf.
#define ZEHOS_WAMR_PARAMS_0
#define ZEHOS_WAMR_PARAMS_1 , uint64_t a0
#define ZEHOS_WAMR_PARAMS_2 ZEHOS_WAMR_PARAMS_1, uint64_t a1
#define ZEHOS_WAMR_PARAMS_3 ZEHOS_WAMR_PARAMS_2, uint64_t a2
#define ZEHOS_WAMR_PARAMS_4 ZEHOS_WAMR_PARAMS_3, uint64_t a3
#define ZEHOS_WAMR_PARAMS_5 ZEHOS_WAMR_PARAMS_4, uint64_t a4
#define ZEHOS_WAMR_PARAMS_6 ZEHOS_WAMR_PARAMS_5, uint64_t a5
#define ZEHOS_WAMR_PARAMS_7 ZEHOS_WAMR_PARAMS_6, uint64_t a6
#define ZEHOS_WAMR_PARAMS_8 ZEHOS_WAMR_PARAMS_7, uint64_t a7
#define ZEHOS_WAMR_PARAMS_9 ZEHOS_WAMR_PARAMS_8, uint64_t a8
#define ZEHOS_WAMR_ARGS_0
#define ZEHOS_WAMR_ARGS_1 , a0
#define ZEHOS_WAMR_ARGS_2 ZEHOS_WAMR_ARGS_1, a1
#define ZEHOS_WAMR_ARGS_3 ZEHOS_WAMR_ARGS_2, a2
#define ZEHOS_WAMR_ARGS_4 ZEHOS_WAMR_ARGS_3, a3
#define ZEHOS_WAMR_ARGS_5 ZEHOS_WAMR_ARGS_4, a4
#define ZEHOS_WAMR_ARGS_6 ZEHOS_WAMR_ARGS_5, a5
#define ZEHOS_WAMR_ARGS_7 ZEHOS_WAMR_ARGS_6, a6
#define ZEHOS_WAMR_ARGS_8 ZEHOS_WAMR_ARGS_7, a7
#define ZEHOS_WAMR_ARGS_9 ZEHOS_WAMR_ARGS_8, a8
// slots[0] ist ein Platzhalter, damit auch Stubs ohne Argumente ein gueltiges Feld haben.
#define ZEHOS_WAMR_SLOTS_0
#define ZEHOS_WAMR_SLOTS_1 , slots[1]
#define ZEHOS_WAMR_SLOTS_2 ZEHOS_WAMR_SLOTS_1, slots[2]
#define ZEHOS_WAMR_SLOTS_3 ZEHOS_WAMR_SLOTS_2, slots[3]
#define ZEHOS_WAMR_SLOTS_4 ZEHOS_WAMR_SLOTS_3, slots[4]
#define ZEHOS_WAMR_SLOTS_5 ZEHOS_WAMR_SLOTS_4, slots[5]
#define ZEHOS_WAMR_SLOTS_6 ZEHOS_WAMR_SLOTS_5, slots[6]
#define ZEHOS_WAMR_SLOTS_7 ZEHOS_WAMR_SLOTS_6, slots[7]
#define ZEHOS_WAMR_SLOTS_8 ZEHOS_WAMR_SLOTS_7, slots[8]
#define ZEHOS_WAMR_SLOTS_9 ZEHOS_WAMR_SLOTS_8, slots[9]
#define ZEHOS_WAMR_PREPARE(params, count)     zehos_host_call call = begin_call(env);     uint64_t slots[(count) + 1] = { 0 ZEHOS_WAMR_ARGS_##count };     guest_offsets(&call, params, slots + 1, count);

// Nach einem Trap ist das Ergebnis bedeutungslos; der AOT-Code prueft die Ausnahme und verwirft es.
#define ZEHOS_WAMR_WRAPPER_i(module, name, params, function, count)     static uint32_t zehos_wamr_##module##_##name(wasm_exec_env_t env ZEHOS_WAMR_PARAMS_##count)     {         ZEHOS_WAMR_PREPARE(params, count)         uint32_t value = 0;         if (call.trap == ZEHOS_HOST_CONTINUE) value = (uint32_t)function(&call ZEHOS_WAMR_SLOTS_##count);         end_call(env, &call);         return call.trap == ZEHOS_HOST_CONTINUE ? value : 0;     }
#define ZEHOS_WAMR_WRAPPER_v(module, name, params, function, count)     static void zehos_wamr_##module##_##name(wasm_exec_env_t env ZEHOS_WAMR_PARAMS_##count)     {         ZEHOS_WAMR_PREPARE(params, count)         if (call.trap == ZEHOS_HOST_CONTINUE) function(&call ZEHOS_WAMR_SLOTS_##count);         end_call(env, &call);     }
#define ZEHOS_WAMR_WRAPPER(module, name, result, params, function, count)     ZEHOS_WAMR_WRAPPER_##result(module, name, params, function, count)
ZEHOS_HOST_FUNCTIONS(ZEHOS_WAMR_WRAPPER)

// Signaturen entstehen beim Registrieren aus der Tabelle; WAMR braucht sie veraenderbar (Sortierung der Symbole).
#define ZEHOS_WAMR_ENTRY(module, name, result, params, function, count) \
    { #module, #result, params, { #name, (void*)zehos_wamr_##module##_##name, NULL, NULL } },
static struct
{
    const char* module;
    const char* result;
    const char* params;
    NativeSymbol symbol;
} bindings[] =
{
    ZEHOS_HOST_FUNCTIONS(ZEHOS_WAMR_ENTRY)
};

enum { ZEHOS_WAMR_BINDINGS = sizeof(bindings) / sizeof(bindings[0]), ZEHOS_WAMR_SIGNATURE = 16 };
static char signatures[ZEHOS_WAMR_BINDINGS][ZEHOS_WAMR_SIGNATURE];

// "i*i*" mit Ergebnis i wird zu "(i*i*)i". "*" bleibt, damit auch der Weg ueber invokeNative Hostzeiger liefert wie der
// direkte Aufruf aus AOT-Code (guest_offsets).
static int build_signature(char* target, const char* params, const char* result)
{
    size_t length = strlen(params);
    if (length + 4 > ZEHOS_WAMR_SIGNATURE) return 0;
    size_t index = 0;
    target[index++] = '(';
    for (size_t param = 0; param < length; param++) target[index++] = params[param];
    target[index++] = ')';
    if (result[0] == 'i') target[index++] = 'i';
    target[index] = 0;
    return 1;
}

// Je Eintrag eine eigene Gruppe: Die Tabelle mischt die Module, WAMR sucht Importe ueber alle Gruppen.
// wasm_runtime_destroy gibt die Gruppen wieder frei.
static int register_host(void)
{
    for (size_t index = 0; index < ZEHOS_WAMR_BINDINGS; index++)
    {
        if (!build_signature(signatures[index], bindings[index].params, bindings[index].result)) return 0;
        bindings[index].symbol.signature = signatures[index];
        if (!wasm_runtime_register_natives(bindings[index].module, &bindings[index].symbol, 1)) return 0;
    }
    return 1;
}

static const char memory_limit[] = "memory size exceeds limit";

// Anfangsgroesse eines exportierten Speichers; wasi-sdk exportiert ihn immer als "memory".
static int initial_memory_exceeds(wasm_module_t module, uint32_t maximumPages)
{
    int32_t count = wasm_runtime_get_export_count(module);
    for (int32_t index = 0; index < count; index++)
    {
        wasm_export_t item;
        memset(&item, 0, sizeof(item));
        wasm_runtime_get_export_type(module, index, &item);
        if (item.kind == WASM_IMPORT_EXPORT_KIND_MEMORY && item.u.memory_type != NULL &&
            wasm_memory_type_get_init_page_count(item.u.memory_type) > maximumPages) return 1;
    }
    return 0;
}

// Einstieg wie bei wasm3: ohne Parameter, ohne Ergebnis ("_start") oder mit einem i32-Ergebnis.
static int entry_usable(wasm_function_inst_t function, wasm_module_inst_t instance)
{
    if (function == NULL || wasm_func_get_param_count(function, instance) != 0) return 0;
    uint32_t results = wasm_func_get_result_count(function, instance);
    if (results == 0) return 1;
    if (results != 1) return 0;
    wasm_valkind_t kind = WASM_I32;
    wasm_func_get_result_types(function, instance, &kind);
    return kind == WASM_I32;
}

// Fuehrt einen Einstiegspunkt eines AOT-Moduls aus, mit denselben Statuscodes wie zehos_wasm_run. Ohne Gasbudget:
// Laeufe mit Budget bleiben bei wasm3 (WasmEngineSelection). WAMR wird je Lauf gestartet und beendet; so sind
// Instanz, Modul, ausfuehrbarer Speicher, Hostgruppen und offene Dateien nach jedem Weg freigegeben.
// *instantiated wird 1, sobald das Instanziieren beginnt: ab dann kann Gastcode gelaufen sein (start-Funktion,
// Konstruktoren), und der Aufrufer darf dieselbe App nicht unter wasm3 wiederholen.
int32_t zehos_wasm_run_aot(const uint8_t* bytes, uint32_t length, const char* entry, uint32_t stackBytes,
    uint64_t memoryBytes, int32_t* result, char* message, uint32_t messageLength, int32_t* instantiated)
{
    zehos_wamr_run run;
    zehos_host_context_init(&run.context);
    run.exited = 0;
    *result = 0;
    *instantiated = 0;
    copy_message(message, messageLength, "");

    RuntimeInitArgs init;
    memset(&init, 0, sizeof(init));
    init.mem_alloc_type = Alloc_With_System_Allocator;
    if (!wasm_runtime_full_init(&init))
    {
        copy_message(message, messageLength, "WAMR nicht gestartet");
        return ZEHOS_WASM_NO_MEMORY;
    }

    int32_t status = ZEHOS_WASM_OK;
    char error[128];
    error[0] = 0;
    const char* text = NULL;
    uint8_t* copy = NULL;
    wasm_module_t module = NULL;
    wasm_module_inst_t instance = NULL;
    wasm_exec_env_t env = NULL;

    if (!register_host())
    {
        status = ZEHOS_WASM_NO_MEMORY;
        text = "Hostfunktionen nicht registriert";
    }
    // Nur AOT-Dateien: Interpreter und JIT sind nicht im Kernel, ein .wasm gehoert zu zehos_wasm_run.
    else if (bytes == NULL || length == 0 || wasm_runtime_get_file_package_type(bytes, length) != Wasm_Module_AoT)
    {
        status = ZEHOS_WASM_INVALID_MODULE;
        text = "kein AOT-Modul";
    }
    // WAMR veraendert den Ladepuffer und verweist danach weiter hinein; er lebt deshalb bis nach dem Entladen. os_malloc
    // statt malloc, weil der Lader 4-Byte-Ausrichtung voraussetzt (COSMOS_SUPPORT.md, Probe B3).
    else if ((copy = (uint8_t*)os_malloc(length)) == NULL)
    {
        status = ZEHOS_WASM_NO_MEMORY;
        text = "kein Speicher fuer die AOT-Datei";
    }
    else
    {
        memcpy(copy, bytes, length);
        module = wasm_runtime_load(copy, length, error, sizeof(error));
        if (module == NULL)
        {
            status = ZEHOS_WASM_INVALID_MODULE;
            text = error;
        }
    }

    uint64_t pages = memoryBytes / ZEHOS_WASM_PAGE;
    uint32_t maximumPages = pages > ZEHOS_WASM_MAXIMUM_PAGES ? ZEHOS_WASM_MAXIMUM_PAGES : (uint32_t)pages;
    // max_memory_pages begrenzt nur das Wachsen; einen groesseren Anfangsspeicher uebernimmt WAMR mit einer Warnung
    // (wasm_runtime_get_max_mem). wasm3 lehnt ihn ab, deshalb hier ebenso: vor dem Instanziieren, damit WAMR ihn gar
    // nicht erst belegt, und danach fuer einen nicht exportierten Speicher.
    if (status == ZEHOS_WASM_OK && initial_memory_exceeds(module, maximumPages))
    {
        status = ZEHOS_WASM_LOAD_FAILED;
        text = memory_limit;
    }
    if (status == ZEHOS_WASM_OK)
    {
        InstantiationArgs arguments;
        memset(&arguments, 0, sizeof(arguments));
        arguments.default_stack_size = stackBytes;
        arguments.max_memory_pages = maximumPages;
        *instantiated = 1;
        instance = wasm_runtime_instantiate_ex(module, &arguments, error, sizeof(error));
        if (instance == NULL)
        {
            status = ZEHOS_WASM_LOAD_FAILED;
            text = error;
        }
        else
        {
            wasm_memory_inst_t memory = wasm_runtime_get_default_memory(instance);
            if (memory != NULL && wasm_memory_get_cur_page_count(memory) > maximumPages)
            {
                status = ZEHOS_WASM_LOAD_FAILED;
                text = memory_limit;
            }
        }
    }

    wasm_function_inst_t function = NULL;
    if (status == ZEHOS_WASM_OK)
    {
        function = wasm_runtime_lookup_function(instance, entry);
        if (!entry_usable(function, instance))
        {
            status = ZEHOS_WASM_ENTRY_MISSING;
            text = "function lookup failed";
        }
    }

    if (status == ZEHOS_WASM_OK && (env = wasm_runtime_create_exec_env(instance, stackBytes)) == NULL)
    {
        status = ZEHOS_WASM_NO_MEMORY;
        text = "keine Ausfuehrungsumgebung";
    }

    if (status == ZEHOS_WASM_OK)
    {
        wasm_runtime_set_user_data(env, &run);
        uint8_t* top = (uint8_t*)__builtin_frame_address(0);
        zehos_wamr_set_stack_boundary(top - ZEHOS_AOT_NATIVE_STACK);
        uint32_t values[2] = { 0, 0 };
        if (wasm_runtime_call_wasm(env, function, 0, values))
        {
            if (wasm_func_get_result_count(function, instance) == 1) *result = (int32_t)values[0];
        }
        else if (run.exited)
        {
            *result = run.context.exitCode;
        }
        else
        {
            status = ZEHOS_WASM_TRAP;
            text = wasm_runtime_get_exception(instance);
        }
        zehos_wamr_set_stack_boundary(NULL);
    }

    // Der Text kann in der Instanz liegen; er wird kopiert, bevor sie freigegeben wird.
    if (text != NULL) copy_message(message, messageLength, text);
    zehos_host_context_close_files(&run.context);
    if (env != NULL) wasm_runtime_destroy_exec_env(env);
    if (instance != NULL) wasm_runtime_deinstantiate(instance);
    if (module != NULL) wasm_runtime_unload(module);
    if (copy != NULL) os_free(copy);
    wasm_runtime_destroy();
    return status;
}

#endif
