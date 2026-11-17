// ZehOS: wasm3-Bindung der Hostfunktionen und Einstieg zehos_wasm_run fuer den C#-Kernel.
// Die Dienste selbst liegen laufzeitunabhaengig in zehos_host_core.c; hier stehen nur Argumentuebernahme,
// Trap-Umsetzung und die aus ZEHOS_HOST_FUNCTIONS abgeleitete Bindungstabelle.

#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "wasm3.h"
#include "zehos_host_core.h"

static void copy_message(char* target, uint32_t capacity, const char* text)
{
    if (target == NULL || capacity == 0) return;
    uint32_t index = 0;
    while (text != NULL && text[index] != 0 && index + 1 < capacity) { target[index] = text[index]; index++; }
    target[index] = 0;
}

static M3Result trap_result(zehos_host_trap trap)
{
    if (trap == ZEHOS_HOST_TRAP_EXIT) return m3Err_trapExit;
    if (trap == ZEHOS_HOST_TRAP_MEMORY) return m3Err_trapOutOfBoundsMemoryAccess;
    return m3Err_none;
}

// wasm3 legt jedes Argument in einen eigenen 64-Bit-Platz; i32-Werte stehen in den unteren 32 Bit. Die Umwandlung
// in den Parametertyp der Kernfunktion uebernimmt deren Prototyp (wie m3ApiGetArg). Die Kuerzung eines 64-Bit-Platzes
// auf i32 ist gewollt: Die oberen 32 Bit eines i32-Arguments sind bei wasm3 bedeutungslos.
#define ZEHOS_M3_ARGS_0(slots)
#define ZEHOS_M3_ARGS_1(slots) , (slots)[0]
#define ZEHOS_M3_ARGS_2(slots) ZEHOS_M3_ARGS_1(slots), (slots)[1]
#define ZEHOS_M3_ARGS_3(slots) ZEHOS_M3_ARGS_2(slots), (slots)[2]
#define ZEHOS_M3_ARGS_4(slots) ZEHOS_M3_ARGS_3(slots), (slots)[3]
#define ZEHOS_M3_ARGS_5(slots) ZEHOS_M3_ARGS_4(slots), (slots)[4]
#define ZEHOS_M3_ARGS_6(slots) ZEHOS_M3_ARGS_5(slots), (slots)[5]
#define ZEHOS_M3_ARGS_7(slots) ZEHOS_M3_ARGS_6(slots), (slots)[6]
#define ZEHOS_M3_ARGS_8(slots) ZEHOS_M3_ARGS_7(slots), (slots)[7]
#define ZEHOS_M3_ARGS_9(slots) ZEHOS_M3_ARGS_8(slots), (slots)[8]

// Mit Ergebnis belegt dieses den ersten Platz, die Argumente folgen; geschrieben wird es nur ohne Trap.
#define ZEHOS_M3_CALL_i(function, count) \
    uint32_t value = (uint32_t)function(&call ZEHOS_M3_ARGS_##count(_sp + 1)); \
    if (call.trap == ZEHOS_HOST_CONTINUE) *(uint32_t*)_sp = value;
#define ZEHOS_M3_CALL_v(function, count) \
    function(&call ZEHOS_M3_ARGS_##count(_sp));

// Je Tabelleneintrag eine Rohfunktion: Kontext und Speicher uebernehmen, Kernfunktion rufen, Trap melden.
#define ZEHOS_M3_WRAPPER(module, name, result, params, function, count) \
    static m3ApiRawFunction(zehos_m3_##module##_##name) \
    { \
        zehos_host_call call = { (zehos_wasm_context*)_ctx->userdata, (uint8_t*)_mem, m3_GetMemorySizeAt(_mem), \
            ZEHOS_HOST_CONTINUE }; \
        ZEHOS_M3_CALL_##result(function, count) \
        return trap_result(call.trap); \
    }
ZEHOS_HOST_FUNCTIONS(ZEHOS_M3_WRAPPER)

#define ZEHOS_M3_ENTRY(module, name, result, params, function, count) \
    { #module, #name, #result "(" params ")", zehos_m3_##module##_##name },
static const struct { const char* module; const char* name; const char* signature; M3RawCall function; } bindings[] =
{
    ZEHOS_HOST_FUNCTIONS(ZEHOS_M3_ENTRY)
};

static M3Result link_host(IM3Module module, zehos_wasm_context* context)
{
    for (size_t index = 0; index < sizeof(bindings) / sizeof(bindings[0]); index++)
    {
        M3Result result = m3_LinkRawFunctionEx(module, bindings[index].module, bindings[index].name,
            bindings[index].signature, bindings[index].function, context);
        // Nicht importierte Funktionen sind kein Fehler.
        if (result != m3Err_none && result != m3Err_functionLookupFailed) return result;
    }
    return m3Err_none;
}

// Fuehrt einen Einstiegspunkt aus: "_start" (WASI, Ergebnis = Exitcode) oder eine parameterlose i32-Funktion.
// Das Modul bleibt Eigentum des Aufrufers; wasm3 haelt waehrend des Laufs nur einen Verweis darauf.
int32_t zehos_wasm_run(const uint8_t* bytes, uint32_t length, const char* entry, uint64_t gas,
    uint32_t stackBytes, uint64_t memoryBytes, int32_t* result, char* message, uint32_t messageLength)
{
    zehos_wasm_context context;
    zehos_host_context_init(&context);
    int32_t status = ZEHOS_WASM_OK;
    *result = 0;
    copy_message(message, messageLength, "");

    IM3Environment environment = m3_NewEnvironment();
    if (environment == NULL) return ZEHOS_WASM_NO_MEMORY;
    IM3Runtime runtime = m3_NewRuntime(environment, stackBytes, NULL);
    if (runtime == NULL) { m3_FreeEnvironment(environment); return ZEHOS_WASM_NO_MEMORY; }

    IM3Module module = NULL;
    M3Result error = NULL;
    // Gas muss vor dem Uebersetzen scharf geschaltet sein, damit der erzeugte Code es misst.
    if ((error = m3_SetResourceLimit(runtime, c_m3Limit_GasUnits, gas)) != NULL ||
        (error = m3_SetResourceLimit(runtime, c_m3Limit_MemoryBytes, memoryBytes)) != NULL)
    {
        status = ZEHOS_WASM_LIMIT_FAILED;
    }
    else if ((error = m3_ParseModule(environment, &module, bytes, length)) != NULL)
    {
        status = ZEHOS_WASM_INVALID_MODULE;
    }
    else if ((error = m3_LoadModule(runtime, module)) != NULL)
    {
        m3_FreeModule(module);
        status = ZEHOS_WASM_LOAD_FAILED;
    }
    else if ((error = link_host(module, &context)) != NULL)
    {
        status = ZEHOS_WASM_LOAD_FAILED;
    }
    else
    {
        IM3Function function = NULL;
        if ((error = m3_FindFunction(&function, runtime, entry)) != NULL)
        {
            status = ZEHOS_WASM_ENTRY_MISSING;
        }
        else
        {
            error = m3_CallV(function);
            if (error == m3Err_trapExit) { *result = context.exitCode; error = NULL; }
            else if (error == m3Err_trapOutOfGas) status = ZEHOS_WASM_OUT_OF_GAS;
            else if (error != NULL) status = ZEHOS_WASM_TRAP;
            else if (m3_GetRetCount(function) == 1)
            {
                int32_t value = 0;
                if ((error = m3_GetResultsV(function, &value)) != NULL) status = ZEHOS_WASM_TRAP;
                else *result = value;
            }
        }
    }

    if (error != NULL) copy_message(message, messageLength, error);
    zehos_host_context_close_files(&context);
    m3_FreeRuntime(runtime);
    m3_FreeEnvironment(environment);
    return status;
}
