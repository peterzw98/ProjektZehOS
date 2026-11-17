// ZehOS: WAMR-Plattformschicht fuer den reinen AOT-Lader (Plan 5c, Etappe B2, Normalbetrieb ab Etappe D).
// Ein Worker, kein Dateisystem, keine Signale: Sperren sind leer, Speicher kommt aus der freistehenden C-Laufzeit.

#include "platform_api_vmcore.h"
#include "platform_api_extension.h"

extern void __cosmos_serial_write(const char* text);

int bh_platform_init(void) { return 0; }
void bh_platform_destroy(void) {}

// malloc der freistehenden C-Laufzeit liefert unter Cosmos nur 2-Byte-ausgerichtete Bloecke (Probe B3); WAMR liest
// Puffer 4-Byte-ausgerichtet und der Compiler setzt 16 Byte (max_align_t) voraus. Vor dem Block stehen deshalb der
// Zeiger aus malloc und die Groesse.
typedef struct { void* raw; size_t size; } zehos_aligned_header;
#define ZEHOS_ALIGN 16u

void* os_malloc(unsigned size)
{
    uint8_t* raw = (uint8_t*)malloc((size_t)size + sizeof(zehos_aligned_header) + ZEHOS_ALIGN);
    if (raw == NULL) return NULL;
    uintptr_t aligned = ((uintptr_t)raw + sizeof(zehos_aligned_header) + ZEHOS_ALIGN - 1) & ~(uintptr_t)(ZEHOS_ALIGN - 1);
    zehos_aligned_header* header = (zehos_aligned_header*)aligned - 1;
    header->raw = raw;
    header->size = size;
    return (void*)aligned;
}

void os_free(void* ptr)
{
    if (ptr != NULL) free(((zehos_aligned_header*)ptr - 1)->raw);
}

void* os_realloc(void* ptr, unsigned size)
{
    if (ptr == NULL) return os_malloc(size);
    void* result = os_malloc(size);
    if (result == NULL) return NULL;
    size_t old_size = ((zehos_aligned_header*)ptr - 1)->size;
    memcpy(result, ptr, old_size < size ? old_size : size);
    os_free(ptr);
    return result;
}

int os_vprintf(const char* format, va_list ap)
{
    char buffer[512];
    int length = vsnprintf(buffer, sizeof(buffer), format, ap);
    __cosmos_serial_write(buffer);
    return length;
}

int os_printf(const char* format, ...)
{
    va_list ap;
    va_start(ap, format);
    int length = os_vprintf(format, ap);
    va_end(ap);
    return length;
}

int os_dumps_proc_mem_info(char* out, unsigned int size) { (void)out; (void)size; return -1; }

// Vor dem ausgerichteten Block steht der Zeiger aus malloc, damit os_munmap ihn freigeben kann.
#define ZEHOS_PAGE 4096u

void* os_mmap(void* hint, size_t size, int prot, int flags, os_file_handle file)
{
    (void)hint; (void)prot; (void)file;
    // Cosmos blendet den Heap ueber 2 GiB hinter dem Kernel ein; WAMR versucht danach ohne die Vorgabe.
    if (flags & MMAP_MAP_32BIT) return NULL;
    if (size == 0 || size > SIZE_MAX - ZEHOS_PAGE - sizeof(void*)) return NULL;
    uint8_t* raw = (uint8_t*)malloc(size + ZEHOS_PAGE + sizeof(void*));
    if (raw == NULL) return NULL;
    uintptr_t aligned = ((uintptr_t)raw + sizeof(void*) + ZEHOS_PAGE - 1) & ~(uintptr_t)(ZEHOS_PAGE - 1);
    ((void**)aligned)[-1] = raw;
    // Wasm-Speicher und AOT-Datenbereiche erwarten wie bei anonymem mmap genullten Speicher.
    memset((void*)aligned, 0, size);
    return (void*)aligned;
}

void os_munmap(void* addr, size_t size)
{
    (void)size;
    if (addr != NULL) free(((void**)addr)[-1]);
}

// Der Heap ist ohne NX-Bit eingeblendet (Probe B1); Schutzwechsel gibt es unter Cosmos nicht.
int os_mprotect(void* addr, size_t size, int prot) { (void)addr; (void)size; (void)prot; return 0; }

void* os_mremap(void* old_addr, size_t old_size, size_t new_size) { return os_mremap_slow(old_addr, old_size, new_size); }

// x86-64 haelt Daten- und Befehlscache kohaerent.
void os_dcache_flush(void) {}
void os_icache_flush(void* start, size_t len) { (void)start; (void)len; }

int os_mutex_init(korp_mutex* mutex) { (void)mutex; return BHT_OK; }
int os_mutex_destroy(korp_mutex* mutex) { (void)mutex; return BHT_OK; }
int os_mutex_lock(korp_mutex* mutex) { (void)mutex; return BHT_OK; }
int os_mutex_unlock(korp_mutex* mutex) { (void)mutex; return BHT_OK; }
int os_cond_init(korp_cond* cond) { (void)cond; return BHT_OK; }
int os_cond_destroy(korp_cond* cond) { (void)cond; return BHT_OK; }
int os_cond_reltimedwait(korp_cond* cond, korp_mutex* mutex, uint64 useconds) { (void)cond; (void)mutex; (void)useconds; return BHT_OK; }
int os_cond_signal(korp_cond* cond) { (void)cond; return BHT_OK; }

korp_tid os_self_thread(void) { return 1; }

// Grenze des nativen Stacks fuer den laufenden AOT-Aufruf. zehos_wasm_run_aot setzt sie vor jedem Lauf auf dem
// Wasm-Worker (8 MiB Stack, Grenze 7 MiB unter dem Einstieg wie bei wasm3) und loescht sie danach. WAMR liest sie bei
// jedem Aufruf (wasm_exec_env_set_thread_info) und laesst WASM_STACK_GUARD_SIZE Abstand; der AOT-Code mit
// --stack-bounds-checks trappt davor mit "native stack overflow", statt ohne Schutzseite im Triple Fault zu enden.
// Ein Thread, ein Lauf zur Zeit: eine globale Variable genuegt.
static uint8* stack_boundary;

void zehos_wamr_set_stack_boundary(uint8_t* boundary) { stack_boundary = boundary; }

uint8* os_thread_get_stack_boundary(void) { return stack_boundary; }
void os_thread_jit_write_protect_np(bool enabled) { (void)enabled; }

// Monotone Kerneluhr ueber denselben C#-Rueckruf wie WASI clock_time_get (Uhr 1 = ab Boot).
extern uint64_t zehos_wasm_host_time_ns(int32_t clockId);

uint64 os_time_get_boot_us(void) { return zehos_wasm_host_time_ns(1) / 1000u; }

// Fehlende Funktionen der freistehenden C-Laufzeit, nur fuer WAMR.
static void swap_bytes(uint8_t* left, uint8_t* right, size_t size)
{
    while (size-- > 0) { uint8_t value = *left; *left++ = *right; *right++ = value; }
}

// Einfuegesortierung: WAMR sortiert nur kurze Export- und Symboltabellen.
void qsort(void* base, size_t count, size_t size, int (*compare)(const void*, const void*))
{
    uint8_t* items = (uint8_t*)base;
    for (size_t i = 1; i < count; i++)
        for (size_t j = i; j > 0 && compare(items + (j - 1) * size, items + j * size) > 0; j--)
            swap_bytes(items + (j - 1) * size, items + j * size, size);
}

void* bsearch(const void* key, const void* base, size_t count, size_t size, int (*compare)(const void*, const void*))
{
    size_t low = 0, high = count;
    while (low < high)
    {
        size_t middle = low + (high - low) / 2;
        const uint8_t* item = (const uint8_t*)base + middle * size;
        int order = compare(key, item);
        if (order == 0) return (void*)item;
        if (order < 0) high = middle; else low = middle + 1;
    }
    return NULL;
}

static int lower(int c) { return c >= 'A' && c <= 'Z' ? c + ('a' - 'A') : c; }

int strncasecmp(const char* left, const char* right, size_t count)
{
    for (; count > 0; count--, left++, right++)
    {
        int difference = lower((unsigned char)*left) - lower((unsigned char)*right);
        if (difference != 0 || *left == '\0') return difference;
    }
    return 0;
}

// strtoll/strtoull/strtod der C-Laufzeit sind nur Platzhalter, die 0 liefern; der AOT-Lader liest aber Funktionsnummern
// aus Symbolnamen wie "aot_func#3" (aot_loader.c). Ohne eigene Zahlenumwandlung landet jeder interne Aufruf bei Funktion 0.
// shortcut: ohne Ueberlauf- und errno-Behandlung; reicht fuer Funktionsnummern, vor allgemeiner Nutzung ergaenzen.
unsigned long strtoul(const char* text, char** end, int base)
{
    const char* p = text;
    while (isspace((unsigned char)*p)) p++;
    if (*p == '+') p++;
    if ((base == 0 || base == 16) && p[0] == '0' && (p[1] == 'x' || p[1] == 'X')) { p += 2; base = 16; }
    else if (base == 0) base = p[0] == '0' ? 8 : 10;
    unsigned long value = 0;
    const char* digits = p;
    for (;; p++)
    {
        int c = lower((unsigned char)*p);
        int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : 99;
        if (digit >= base) break;
        value = value * (unsigned long)base + (unsigned long)digit;
    }
    if (end != NULL) *end = (char*)(p == digits ? text : p);
    return value;
}

int atoi(const char* text)
{
    while (isspace((unsigned char)*text)) text++;
    int negative = *text == '-';
    if (*text == '-' || *text == '+') text++;
    unsigned long value = strtoul(text, NULL, 10);
    return negative ? -(int)value : (int)value;
}

// shortcut: nur fuer den nicht genutzten Kommandozeilenaufruf in wasm_application.c; liefert wie strtod der C-Laufzeit 0.
float strtof(const char* text, char** end) { return (float)strtod(text, end); }
long labs(long value) { return value < 0 ? -value : value; }

// NaN-Regel wie C99: ist ein Argument NaN, gilt das andere.
double fmin(double left, double right) { return left != left ? right : right != right ? left : left < right ? left : right; }
float fminf(float left, float right) { return left != left ? right : right != right ? left : left < right ? left : right; }
double fmax(double left, double right) { return left != left ? right : right != right ? left : left > right ? left : right; }
float fmaxf(float left, float right) { return left != left ? right : right != right ? left : left > right ? left : right; }

// invokeNative aus WAMR 2.4.5 (core/iwasm/common/arch/invokeNative_em64.s, Apache-2.0 WITH LLVM-exception) als
// C-Datei, weil GCCProject nur .c-Quellen uebersetzt. Ruft eine Hostfunktion mit Argumenten aus dem WAMR-Puffer auf.
__asm__(
    ".text\n"
    ".align 16\n"
    ".globl invokeNative\n"
    ".type invokeNative, @function\n"
    "invokeNative:\n"
    "    push %rbp\n"
    "    mov %rsp, %rbp\n"
    "    mov %rdx, %r10\n"
    "    mov %rsp, %r11\n"
    "    and $8, %r11\n"
    "    je 1f\n"
    "    int3\n"
    "1:  mov %r10, %r11\n"
    "    and $1, %r11\n"
    "    shl $3, %r11\n"
    "    sub %r11, %rsp\n"
    "    movq %rdi, %r11\n"
    "    movq %r10, %rcx\n"
    "    lea 64+48-8(%rsi,%rcx,8), %r10\n"
    "    sub %rsp, %r10\n"
    "    cmpq $0, %rcx\n"
    "    je 3f\n"
    "2:  push 0(%rsp,%r10)\n"
    "    loop 2b\n"
    "3:  movq 0x00(%rsi), %xmm0\n"
    "    movq 0x08(%rsi), %xmm1\n"
    "    movq 0x10(%rsi), %xmm2\n"
    "    movq 0x18(%rsi), %xmm3\n"
    "    movq 0x20(%rsi), %xmm4\n"
    "    movq 0x28(%rsi), %xmm5\n"
    "    movq 0x30(%rsi), %xmm6\n"
    "    movq 0x38(%rsi), %xmm7\n"
    "    movq 0x40(%rsi), %rdi\n"
    "    movq 0x50(%rsi), %rdx\n"
    "    movq 0x58(%rsi), %rcx\n"
    "    movq 0x60(%rsi), %r8\n"
    "    movq 0x68(%rsi), %r9\n"
    "    movq 0x48(%rsi), %rsi\n"
    "    call *%r11\n"
    "    leave\n"
    "    ret\n"
    ".size invokeNative, .-invokeNative\n");
