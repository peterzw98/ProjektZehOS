// Hosttest: zehos_wasm_run_aot und zehos_wasm_aot_reclaim aus zehos_host_wamr.c mit gestelltem WAMR. Eine CPU-Ausnahme gibt
// den Worker auf, der Lauf kehrt nie zurueck; hier stellt ein longjmp aus dem WAMR-Aufruf das nach. Danach baut die
// Hauptschleife den Lauf ab (#582), wenn die Ausnahme im AOT-Code lag; sonst bleibt AOT gesperrt. Nicht erreichte Symbole
// bleiben offen.
#include <setjmp.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "wasm_export.h"
#include "zehos_host_core.h"
#include "zehos_host_aot_exit.h"

int32_t zehos_wasm_run_aot(const uint8_t* bytes, uint32_t length, const char* entry, uint32_t stackBytes,
    uint64_t memoryBytes, int32_t* result, char* message, uint32_t messageLength, int32_t* instantiated);
int32_t zehos_wasm_aot_reclaim(uint64_t faultRip);

enum { START_FAILS, CRASH_IN_INIT, HOST_FAILS, CRASH_AFTER_INIT, RUNS, CRASH_IN_CALL };
enum { RECLAIM_NONE = 0, RECLAIM_RELEASED = 1, RECLAIM_KEPT = 2 };
// Gestellter Codebereich des Moduls; RIP der gestellten Ausnahme liegt darin oder nicht.
enum { CODE = 0x10000, CODE_END = 0x10400 };
static int mode, initCalls, destroyCalls, envFrees, instanceFrees, moduleFrees, bufferFrees, buffersLive, closeCalls;
static jmp_buf fault;
static int fakeModule, fakeInstance, fakeEnv, fakeFunction;

zehos_aot_exit_state zehos_aot_exit_current;

bool wasm_runtime_full_init(RuntimeInitArgs* args)
{
    (void)args;
    initCalls++;
    if (mode == CRASH_IN_INIT) longjmp(fault, 1);
    return mode != START_FAILS;
}

void wasm_runtime_destroy(void) { destroyCalls++; }
bool wasm_runtime_register_natives(const char* module, NativeSymbol* symbols, uint32_t count)
{
    (void)module; (void)symbols; (void)count;
    if (mode == CRASH_AFTER_INIT) longjmp(fault, 1);
    return mode == RUNS || mode == CRASH_IN_CALL;
}
package_type_t wasm_runtime_get_file_package_type(const uint8_t* buffer, uint32_t size)
{
    (void)buffer; (void)size;
    return Wasm_Module_AoT;
}
void* os_malloc(unsigned size)
{
    buffersLive++;
    return malloc(size);
}
void os_free(void* pointer)
{
    bufferFrees++;
    buffersLive--;
    free(pointer);
}
wasm_module_t wasm_runtime_load(uint8_t* buffer, uint32_t size, char* error, uint32_t errorSize)
{
    (void)buffer; (void)size; (void)error; (void)errorSize;
    return (wasm_module_t)&fakeModule;
}
int32_t wasm_runtime_get_export_count(wasm_module_t module) { (void)module; return 0; }
int zehos_aot_runs_on_instantiate(void* module) { (void)module; return 0; }
wasm_module_inst_t wasm_runtime_instantiate_ex(wasm_module_t module, const InstantiationArgs* args, char* error, uint32_t errorSize)
{
    (void)module; (void)args; (void)error; (void)errorSize;
    return (wasm_module_inst_t)&fakeInstance;
}
wasm_memory_inst_t wasm_runtime_get_default_memory(wasm_module_inst_t instance) { (void)instance; return NULL; }
wasm_function_inst_t wasm_runtime_lookup_function(wasm_module_inst_t instance, const char* name)
{
    (void)instance; (void)name;
    return (wasm_function_inst_t)&fakeFunction;
}
uint32_t wasm_func_get_param_count(wasm_function_inst_t function, wasm_module_inst_t instance) { (void)function; (void)instance; return 0; }
uint32_t wasm_func_get_result_count(wasm_function_inst_t function, wasm_module_inst_t instance) { (void)function; (void)instance; return 1; }
void wasm_func_get_result_types(wasm_function_inst_t function, wasm_module_inst_t instance, wasm_valkind_t* kinds)
{
    (void)function; (void)instance;
    kinds[0] = WASM_I32;
}
wasm_exec_env_t wasm_runtime_create_exec_env(wasm_module_inst_t instance, uint32_t stackSize)
{
    (void)instance; (void)stackSize;
    return (wasm_exec_env_t)&fakeEnv;
}
void wasm_runtime_set_user_data(wasm_exec_env_t env, void* data) { (void)env; (void)data; }
void zehos_wamr_set_stack_boundary(uint8_t* boundary) { (void)boundary; }
void zehos_aot_code_range(void* module, uint64_t* code, uint64_t* codeEnd)
{
    (void)module;
    *code = CODE;
    *codeEnd = CODE_END;
}
int32_t zehos_aot_call(void* env, void* function, uint32_t argc, uint32_t* argv)
{
    (void)env; (void)function; (void)argc;
    if (mode == CRASH_IN_CALL) longjmp(fault, 1);
    argv[0] = 42;
    return ZEHOS_AOT_CALL_OK;
}
void wasm_runtime_destroy_exec_env(wasm_exec_env_t env) { if (env == (wasm_exec_env_t)&fakeEnv) envFrees++; }
void wasm_runtime_deinstantiate(wasm_module_inst_t instance) { if (instance == (wasm_module_inst_t)&fakeInstance) instanceFrees++; }
void wasm_runtime_unload(wasm_module_t module) { if (module == (wasm_module_t)&fakeModule) moduleFrees++; }

volatile int32_t zehos_host_abort;
void zehos_host_set_abort(int32_t on) { zehos_host_abort = on != 0; }
void zehos_host_context_init(zehos_wasm_context* context) { (void)context; }
void zehos_host_context_close_files(zehos_wasm_context* context) { (void)context; closeCalls++; }

static char message[256];
static int32_t instantiated, value;

// Liefert den Status oder -1, wenn der Lauf "abgestuerzt" ist.
static int run(int next)
{
    uint8_t bytes[4] = { 0 };
    mode = next;
    value = 7;
    instantiated = 99;
    if (setjmp(fault)) return -1;
    return zehos_wasm_run_aot(bytes, sizeof bytes, "run", 65536, 65536, &value, message, sizeof message, &instantiated);
}

#define CHECK(condition) do { if (!(condition)) { printf("FEHLER Zeile %d: %s\n", __LINE__, #condition); return 1; } } while (0)

static int blocked(void)
{
    int inits = initCalls, destroys = destroyCalls;
    CHECK(run(HOST_FAILS) == ZEHOS_WASM_LOAD_FAILED);
    CHECK(strstr(message, "gesperrt") != NULL);
    // 0: WasmRuntime weicht auf wasm3 aus; und WAMR wurde nicht mehr angefasst.
    CHECK(instantiated == 0 && initCalls == inits && destroyCalls == destroys);
    return 0;
}

// Ein vollstaendiger Lauf bis zum Ergebnis; jeder Handle genau einmal frei, WAMR wieder auf Referenzzaehler 0.
static int completes(void)
{
    int envs = envFrees, instances = instanceFrees, modules = moduleFrees, closes = closeCalls;
    int inits = initCalls, destroys = destroyCalls;
    CHECK(run(RUNS) == ZEHOS_WASM_OK && value == 42 && instantiated == 1);
    CHECK(envFrees == envs + 1 && instanceFrees == instances + 1 && moduleFrees == modules + 1 && closeCalls == closes + 1);
    CHECK(initCalls == inits + 1 && destroyCalls == destroys + 1 && buffersLive == 0);
    return 0;
}

// Gewoehnliche Fehler sperren nicht, beide Ausgaenge loeschen die Marke.
static int ordinary(void)
{
    for (int i = 0; i < 2; i++)
    {
        CHECK(run(START_FAILS) == ZEHOS_WASM_NO_MEMORY && strstr(message, "nicht gestartet") != NULL);
        CHECK(run(HOST_FAILS) == ZEHOS_WASM_NO_MEMORY && strstr(message, "nicht registriert") != NULL);
    }
    CHECK(initCalls == 4 && destroyCalls == 2);
    // Ohne offenen Lauf gibt es nichts abzubauen.
    CHECK(zehos_wasm_aot_reclaim(CODE) == RECLAIM_NONE && destroyCalls == 2);
    return 0;
}

// Absturz ausserhalb des AOT-Codes (WAMR-Start, Registrierung oder ein WAMR-Helfer im Aufruf): belassen, gesperrt.
static int kept(int crash, uint64_t rip)
{
    int destroys = destroyCalls, envs = envFrees, instances = instanceFrees, modules = moduleFrees, buffers = bufferFrees;
    CHECK(run(crash) == -1);
    CHECK(zehos_wasm_aot_reclaim(rip) == RECLAIM_KEPT);
    // Auch ein spaeterer Aufruf mit RIP im Code baut den halben Zustand nicht doch noch ab.
    CHECK(zehos_wasm_aot_reclaim(CODE) == RECLAIM_KEPT);
    CHECK(destroyCalls == destroys && envFrees == envs && instanceFrees == instances && moduleFrees == modules && bufferFrees == buffers);
    // Bleibt gesperrt, auch wiederholt.
    for (int i = 0; i < 3; i++) if (blocked()) return 1;
    return 0;
}

// Absturz im AOT-Code: die Hauptschleife baut genau einmal ab, danach laufen weitere AOT-Laeufe normal.
static int released(void)
{
    if (completes()) return 1;
    for (int round = 0; round < 3; round++)
    {
        int envs = envFrees, instances = instanceFrees, modules = moduleFrees, buffers = bufferFrees, closes = closeCalls;
        // Gewoehnliche Fehlstarts (ordinary) riefen init ohne destroy; gezaehlt wird ab hier.
        int open = initCalls - destroyCalls;
        CHECK(run(CRASH_IN_CALL) == -1);
        // Vor dem Abbau ist ein Lauf offen: WAMR gestartet, aber nicht beendet.
        CHECK(initCalls - destroyCalls == open + 1 && buffersLive == 1);
        CHECK(zehos_wasm_aot_reclaim(CODE_END - 1) == RECLAIM_RELEASED);
        CHECK(envFrees == envs + 1 && instanceFrees == instances + 1 && moduleFrees == modules + 1 && bufferFrees == buffers + 1);
        // Referenzzaehler ausgeglichen; Dateien schliesst die Hauptschleife selbst (ReleaseRunResources).
        CHECK(initCalls - destroyCalls == open && buffersLive == 0 && closeCalls == closes);
        // Kein zweiter Abbau desselben Laufs.
        CHECK(zehos_wasm_aot_reclaim(CODE) == RECLAIM_NONE && initCalls - destroyCalls == open && envFrees == envs + 1);
        if (completes()) return 1;
    }
    return 0;
}

int main(int argc, char** argv)
{
    if (argc < 2) return 2;
    if (ordinary()) return 1;
    int failed;
    switch (argv[1][0])
    {
        case 'i': failed = kept(CRASH_IN_INIT, CODE); break;
        case 'r': failed = kept(CRASH_AFTER_INIT, CODE); break;
        // RIP knapp vor bzw. genau am Ende des Codebereichs: ein WAMR-Helfer oder Hostrahmen, kein AOT-Code.
        case 'b': failed = kept(CRASH_IN_CALL, CODE - 1); break;
        case 'e': failed = kept(CRASH_IN_CALL, CODE_END); break;
        case 'c': failed = released(); break;
        default: return 2;
    }
    if (failed) return 1;
    puts("OK");
    return 0;
}
