// Hosttest: zehos_wasm_run_aot aus zehos_host_wamr.c mit gestelltem WAMR. Eine CPU-Ausnahme gibt den Worker auf, der Lauf
// kehrt nie zurueck; hier stellt ein longjmp aus dem WAMR-Aufruf das nach. Nicht erreichte Symbole bleiben offen.
#include <setjmp.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "wasm_export.h"
#include "zehos_host_core.h"

int32_t zehos_wasm_run_aot(const uint8_t* bytes, uint32_t length, const char* entry, uint32_t stackBytes,
    uint64_t memoryBytes, int32_t* result, char* message, uint32_t messageLength, int32_t* instantiated);

enum { START_FAILS, CRASH_IN_INIT, HOST_FAILS, CRASH_AFTER_INIT };
static int mode, initCalls, destroyCalls;
static jmp_buf fault;

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
    return false;
}
void zehos_host_context_init(zehos_wasm_context* context) { (void)context; }
void zehos_host_context_close_files(zehos_wasm_context* context) { (void)context; }

static char message[256];
static int32_t instantiated;

// Liefert den Status oder -1, wenn der Lauf "abgestuerzt" ist.
static int run(int next)
{
    int32_t result = 7;
    uint8_t bytes[4] = { 0 };
    mode = next;
    instantiated = 99;
    if (setjmp(fault)) return -1;
    return zehos_wasm_run_aot(bytes, sizeof bytes, "run", 65536, 65536, &result, message, sizeof message, &instantiated);
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

int main(int argc, char** argv)
{
    (void)argc;
    // Gewoehnliche Fehler sperren nicht, beide Ausgaenge loeschen die Marke.
    for (int i = 0; i < 2; i++)
    {
        CHECK(run(START_FAILS) == ZEHOS_WASM_NO_MEMORY && strstr(message, "nicht gestartet") != NULL);
        CHECK(run(HOST_FAILS) == ZEHOS_WASM_NO_MEMORY && strstr(message, "nicht registriert") != NULL);
    }
    CHECK(initCalls == 4 && destroyCalls == 2);
    CHECK(run(argv[1][0] == 'i' ? CRASH_IN_INIT : CRASH_AFTER_INIT) == -1);
    // Bleibt gesperrt, auch wiederholt.
    for (int i = 0; i < 3; i++) if (blocked()) return 1;
    puts("OK");
    return 0;
}
