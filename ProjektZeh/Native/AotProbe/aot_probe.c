// ZehOS: Gastprobe fuer Etappe B des AOT-Plans (nur mit -p:ZehOSAotProbe=true).
// Prueft, ob Cosmos Heap-Speicher ausfuehrbar einblendet; WAMR-AOT legt seinen Maschinencode in solchen Speicher.

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "wasm_export.h"
#include "platform_api_vmcore.h"

// mov eax, 42; ret
static const uint8_t return_42[] = { 0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3 };

uint64_t zehos_aot_probe_efer(void)
{
    uint32_t low, high;
    __asm__ volatile("rdmsr" : "=a"(low), "=d"(high) : "c"(0xC0000080u));
    return ((uint64_t)high << 32) | low;
}

uint64_t zehos_aot_probe_cr0(void)
{
    uint64_t value;
    __asm__ volatile("mov %%cr0, %0" : "=r"(value));
    return value;
}

uint64_t zehos_aot_probe_cr3(void)
{
    uint64_t value;
    __asm__ volatile("mov %%cr3, %0" : "=r"(value));
    return value;
}

uint64_t zehos_aot_probe_cr4(void)
{
    uint64_t value;
    __asm__ volatile("mov %%cr4, %0" : "=r"(value));
    return value;
}

// Adresse eines Kernelsymbols: Abstand zum Heap entscheidet, ob AOT-Code den Kernel per 32-Bit-Sprung erreicht.
uint64_t zehos_aot_probe_kernel_address(void)
{
    return (uint64_t)(uintptr_t)&zehos_aot_probe_kernel_address;
}

// Legt einen Heap-Block an, meldet seine Adresse und ruft den kopierten Code auf. Bei NX endet der Aufruf mit #PF;
// deshalb meldet der Aufrufer die Adresse vor dem Aufruf seriell. Liefert -1, wenn kein Speicher frei ist.
int32_t zehos_aot_probe_exec(uint64_t* block_address)
{
    uint8_t* block = (uint8_t*)malloc(sizeof(return_42));
    *block_address = (uint64_t)(uintptr_t)block;
    if (block == NULL) return -1;
    memcpy(block, return_42, sizeof(return_42));
    int32_t result = ((int32_t (*)(void))(uintptr_t)block)();
    free(block);
    return result;
}

// Nur die Adresse des naechsten Blocks, ohne Aufruf: die Probe meldet sie, bevor sie den Code ausfuehrt.
uint64_t zehos_aot_probe_heap_address(void)
{
    void* block = malloc(sizeof(return_42));
    uint64_t address = (uint64_t)(uintptr_t)block;
    free(block);
    return address;
}

// B2: WAMR startet mit dem Systemallokator (malloc der freistehenden C-Laufzeit) und faehrt wieder herunter.
// Liefert 1 bei Erfolg und die WAMR-Version in major/minor/patch.
int32_t zehos_aot_probe_wamr_init(uint32_t* major, uint32_t* minor, uint32_t* patch)
{
    RuntimeInitArgs init;
    memset(&init, 0, sizeof(init));
    init.mem_alloc_type = Alloc_With_System_Allocator;
    if (!wasm_runtime_full_init(&init)) return 0;
    wasm_runtime_get_version(major, minor, patch);
    wasm_runtime_destroy();
    return 1;
}

// B3: Fixture-.aot laden und jeden Fall aufrufen. Muss auf einem Thread mit mindestens 8 MiB Stack laufen;
// die Grenze liegt wie bei wasm3 7 MiB unter dem Einstieg, damit ein Ueberlauf als Trap und nicht im Triple Fault endet.
extern const uint32_t zehos_aot_fixture_size;
extern const uint8_t zehos_aot_fixture[];
extern void __cosmos_serial_write(const char* text);

#define ZEHOS_AOT_NATIVE_STACK (7u * 1024 * 1024)

static int32_t host_add(wasm_exec_env_t exec_env, int32_t left, int32_t right)
{
    (void)exec_env;
    return left + right;
}

static NativeSymbol host_symbols[] = {
    { "host_add", (void*)host_add, "(ii)i", NULL },
};

// Die freistehende C-Laufzeit hat keine Teilstringsuche.
static int contains(const char* text, const char* part)
{
    size_t length = strlen(part);
    for (; *text != '\0'; text++)
        if (strncmp(text, part, length) == 0) return 1;
    return length == 0;
}

static void report(const char* text)
{
    __cosmos_serial_write("AOT_PROBE ");
    __cosmos_serial_write(text);
    __cosmos_serial_write("\n");
}

// Erste Bytes und Adresse eines Puffers, um Ladefehler einzugrenzen.
static void report_head(const char* label, const uint8_t* data)
{
    char line[160];
    snprintf(line, sizeof(line), "%s adr=0x%llx %02x %02x %02x %02x %02x %02x %02x %02x", label,
             (unsigned long long)(uintptr_t)data, data[0], data[1], data[2], data[3], data[4], data[5], data[6], data[7]);
    report(line);
}

// Ruft eine exportierte Funktion ohne Argumente auf. Erwartet entweder den Wert expected oder eine Ausnahme,
// deren Text expected_trap enthaelt. Liefert 1 bei erwartetem Ausgang.
static int run_case(wasm_module_inst_t instance, wasm_exec_env_t env, const char* name, int32_t expected,
                    const char* expected_trap)
{
    char line[256];
    wasm_function_inst_t function = wasm_runtime_lookup_function(instance, name);
    if (function == NULL)
    {
        snprintf(line, sizeof(line), "fixture %s FAIL Export fehlt", name);
        report(line);
        return 0;
    }
    uint32_t argv[2] = { 0, 0 };
    bool ok = wasm_runtime_call_wasm(env, function, 0, argv);
    const char* exception = wasm_runtime_get_exception(instance);
    int passed = expected_trap == NULL ? ok && (int32_t)argv[0] == expected
                                       : !ok && exception != NULL && contains(exception, expected_trap);
    snprintf(line, sizeof(line), "fixture %s %s ok=%d result=%d exception=%s", name, passed ? "PASS" : "FAIL",
             ok ? 1 : 0, (int32_t)argv[0], exception != NULL ? exception : "-");
    report(line);
    wasm_runtime_clear_exception(instance);
    return passed;
}

int32_t zehos_aot_probe_fixture(void)
{
    char error[128];
    char line[200];
    RuntimeInitArgs init;
    memset(&init, 0, sizeof(init));
    init.mem_alloc_type = Alloc_With_System_Allocator;
    if (!wasm_runtime_full_init(&init)) { report("fixture FAIL wasm_runtime_full_init"); return 0; }

    int32_t passed = 0;
    // WAMR veraendert den Ladepuffer beim Laden; die Konstante bleibt deshalb unberuehrt. os_malloc statt malloc,
    // weil der Lader 4-Byte-Ausrichtung voraussetzt.
    uint8_t* bytes = (uint8_t*)os_malloc(zehos_aot_fixture_size);
    wasm_module_t module = NULL;
    wasm_module_inst_t instance = NULL;
    wasm_exec_env_t env = NULL;
    if (bytes == NULL) { report("fixture FAIL kein Speicher"); goto done; }
    memcpy(bytes, zehos_aot_fixture, zehos_aot_fixture_size);
    if (!wasm_runtime_register_natives("zehos", host_symbols, sizeof(host_symbols) / sizeof(host_symbols[0])))
    { report("fixture FAIL register_natives"); goto done; }
    report_head("fixture vor load", bytes);
    module = wasm_runtime_load(bytes, zehos_aot_fixture_size, error, sizeof(error));
    if (module == NULL)
    {
        snprintf(line, sizeof(line), "fixture FAIL load %s", error);
        report(line);
        report_head("fixture nach load", bytes);
        goto done;
    }
    instance = wasm_runtime_instantiate(module, 64 * 1024, 0, error, sizeof(error));
    if (instance == NULL) { snprintf(line, sizeof(line), "fixture FAIL instantiate %s", error); report(line); goto done; }
    env = wasm_runtime_create_exec_env(instance, 64 * 1024);
    if (env == NULL) { report("fixture FAIL exec_env"); goto done; }
    uint8_t* top = (uint8_t*)__builtin_frame_address(0);
    wasm_runtime_set_native_stack_boundary(env, top - ZEHOS_AOT_NATIVE_STACK);
    snprintf(line, sizeof(line), "fixture load PASS %u Byte, Modul bei 0x%llx, Stackgrenze 0x%llx", zehos_aot_fixture_size,
             (unsigned long long)(uintptr_t)module, (unsigned long long)(uintptr_t)(top - ZEHOS_AOT_NATIVE_STACK));
    report(line);

    passed = run_case(instance, env, "ret42", 42, NULL)
           + run_case(instance, env, "call_host", 42, NULL)
           + run_case(instance, env, "oob", 0, "out of bounds memory access")
           + run_case(instance, env, "recurse", 0, "out of bounds memory access")
           + run_case(instance, env, "recurse_native", 0, "native stack overflow")
           // Nach den Traps muss dieselbe Instanz weiter rechnen.
           + run_case(instance, env, "ret42", 42, NULL);

done:
    if (env != NULL) wasm_runtime_destroy_exec_env(env);
    if (instance != NULL) wasm_runtime_deinstantiate(instance);
    if (module != NULL) wasm_runtime_unload(module);
    os_free(bytes);
    wasm_runtime_destroy();
    return passed;
}
