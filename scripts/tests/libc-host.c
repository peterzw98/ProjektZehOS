// Hostersatz fuer die Cosmos-Symbole, gegen die libc.c im Kernel gebunden wird. Der Heap liefert absichtlich nur
// 2-Byte-ausgerichtete Rohbloecke wie Cosmos und merkt sich angeforderte und freigegebene Rohzeiger.
#include <stddef.h>
#include <stdint.h>
#include <errno.h>

static _Alignas(16) unsigned char arena[1 << 20];
static size_t used;
static int allocs, frees;
static void* last_raw;
static void* last_freed;
static size_t last_request;

void* __cosmos_heap_alloc(size_t size)
{
    last_request = size;
    // Wie HeapNative.Alloc: die Groesse wird auf 32 Bit gekuerzt.
    size = (uint32_t)size;
    size_t start = ((used + 15) & ~(size_t)15) + 2;
    if (start + size > sizeof(arena)) return NULL;
    used = start + size;
    allocs++;
    return last_raw = arena + start;
}

void __cosmos_heap_free(void* pointer) { frees++; last_freed = pointer; }
void __cosmos_serial_write(const char* text) { (void)text; }

// Cosmos stellt die mem*-Funktionen bereit; hier schlichte Hostfassungen.
void* memcpy(void* destination, const void* source, size_t count)
{
    unsigned char* d = destination; const unsigned char* s = source;
    while (count--) *d++ = *s++;
    return destination;
}
void* memmove(void* destination, const void* source, size_t count)
{
    unsigned char* d = destination; const unsigned char* s = source;
    if (d < s) while (count--) *d++ = *s++;
    else while (count--) d[count] = s[count];
    return destination;
}
void* memset(void* destination, int value, size_t count)
{
    unsigned char* d = destination;
    while (count--) *d++ = (unsigned char)value;
    return destination;
}
int memcmp(const void* left, const void* right, size_t count)
{
    const unsigned char* a = left; const unsigned char* b = right;
    for (; count--; a++, b++) if (*a != *b) return *a < *b ? -1 : 1;
    return 0;
}

void heap_test_reset(void) { used = 0; allocs = frees = 0; last_raw = last_freed = NULL; last_request = 0; }
int heap_test_allocs(void) { return allocs; }
int heap_test_frees(void) { return frees; }
void* heap_test_last_raw(void) { return last_raw; }
void* heap_test_last_freed(void) { return last_freed; }
size_t heap_test_last_request(void) { return last_request; }
int libc_test_errno(void) { return errno; }
void libc_test_set_errno(int value) { errno = value; }

#ifdef _WIN32
// Ohne C-Laufzeit braucht die Test-DLL einen eigenen Einstiegspunkt.
int __stdcall DllMainCRTStartup(void* module, unsigned reason, void* reserved) { (void)module; (void)reason; (void)reserved; return 1; }
#endif
