// ZehOS: Gastprobe fuer Etappe B des AOT-Plans (nur mit -p:ZehOSAotProbe=true).
// Prueft, ob Cosmos Heap-Speicher ausfuehrbar einblendet; WAMR-AOT legt seinen Maschinencode in solchen Speicher.

#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "wasm_export.h"

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
