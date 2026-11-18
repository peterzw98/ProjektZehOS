// ZehOS: minimale C-Laufzeit fuer den statisch gebundenen wasm3-Kern.
// floor(f), ceil(f), trunc(f) und sqrt(f) stellt Cosmos bereits bereit; sie werden nicht erneut definiert.
// memcpy, memmove, memset und memcmp kommen standardkonform aus Cosmos (siehe string.h).

#include <stddef.h>
#include <stdint.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <ctype.h>
#include <errno.h>
#include <zehos_memory.h>

extern void* __cosmos_heap_alloc(size_t size);
extern void __cosmos_heap_free(void* pointer);
extern void __cosmos_serial_write(const char* text);

// Roadmap 12.4-I1: Heapaufrufe nehmen die Seiten-/Heapsperre von Cosmos. Faellt der Wasm-Worker darin, darf der Kernel
// ihn nicht aufgeben (die Sperre bliebe fuer immer belegt); deshalb zaehlen sie wie Hostaufrufe. Definiert in
// zehos_host_core.c; schwach, damit der Hosttest libc.c allein binden kann.
__attribute__((weak)) volatile int zehos_host_depth;

int errno;

// Cosmos-Rohbloecke sind teils nur 2 Byte ausgerichtet (AOT-Probe B3), C sagt aber 16 Byte (max_align_t) zu.
// Der Kopf direkt vor dem ausgerichteten Nutzzeiger haelt Groesse (fuer realloc) und Rohzeiger (fuer free).
#define ZEHOS_ALIGN 16u
typedef struct zehos_block_header
{
    size_t size;
    void* raw;
    struct zehos_block_header* previous;
    struct zehos_block_header* next;
} zehos_block_header;
#define ZEHOS_BLOCK_EXTRA (sizeof(zehos_block_header) + ZEHOS_ALIGN - 1)

static zehos_block_header* memory_head;
static size_t memory_limit, memory_used, memory_peak;
static int memory_active, memory_owner, memory_exceeded;
static int (*memory_current_thread)(void);

int zehos_memory_begin(size_t limit, int owner, int (*current_thread)(void))
{
    __atomic_add_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
    if (memory_active || owner < 0 || current_thread == NULL || current_thread() != owner)
    {
        __atomic_sub_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
        return 0;
    }
    memory_limit = limit;
    memory_owner = owner;
    memory_current_thread = current_thread;
    memory_used = memory_peak = 0;
    memory_exceeded = 0;
    memory_active = 1;
    __atomic_sub_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
    return 1;
}

void zehos_memory_end(void)
{
    __atomic_add_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
    memory_active = 0;
    while (memory_head != NULL) free(memory_head + 1);
    memory_current_thread = NULL;
    __atomic_sub_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
}

size_t zehos_memory_used(void) { return memory_used; }
size_t zehos_memory_peak(void) { return memory_peak; }
int zehos_memory_exceeded(void) { return memory_exceeded; }

void* malloc(size_t size)
{
    // __cosmos_heap_alloc kuerzt die Groesse auf 32 Bit (HeapNative.Alloc, Cosmos 3.0.97); groessere Bloecke scheitern hier.
    if (size > UINT32_MAX - ZEHOS_BLOCK_EXTRA) return NULL;
    __atomic_add_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
    size_t total = ZEHOS_BLOCK_EXTRA + size;
    int tracked = memory_active && memory_current_thread() == memory_owner;
    if (tracked && (total > SIZE_MAX - memory_used || (memory_limit != 0 && total > memory_limit - memory_used)))
    {
        memory_exceeded = 1;
        __atomic_sub_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
        return NULL;
    }
    uint8_t* raw = (uint8_t*)__cosmos_heap_alloc(total);
    if (raw == NULL)
    {
        __atomic_sub_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
        return NULL;
    }
    uintptr_t aligned = ((uintptr_t)raw + ZEHOS_BLOCK_EXTRA) & ~(uintptr_t)(ZEHOS_ALIGN - 1);
    zehos_block_header* header = (zehos_block_header*)aligned - 1;
    header->size = size;
    header->raw = raw;
    header->previous = header->next = NULL;
    if (tracked)
    {
        header->next = memory_head;
        if (memory_head != NULL) memory_head->previous = header;
        memory_head = header;
        memory_used += total;
        if (memory_used > memory_peak) memory_peak = memory_used;
    }
    __atomic_sub_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
    return (void*)aligned;
}

void free(void* pointer)
{
    if (pointer == NULL) return;
    __atomic_add_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
    zehos_block_header* header = (zehos_block_header*)pointer - 1;
    // Die Blockherkunft entscheidet, auch wenn die Hauptschleife einen aufgegebenen Lauf aufraeumt.
    if (header == memory_head || header->previous != NULL || header->next != NULL)
    {
        if (header->previous != NULL) header->previous->next = header->next;
        else memory_head = header->next;
        if (header->next != NULL) header->next->previous = header->previous;
        memory_used -= ZEHOS_BLOCK_EXTRA + header->size;
    }
    __cosmos_heap_free(header->raw);
    __atomic_sub_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
}

void* calloc(size_t count, size_t size)
{
    if (size != 0 && count > SIZE_MAX / size) return NULL;
    // Auch Nullsetzen/Kopieren gehoert zum Allokator; ein Kill darf keine halbfertige Mutation sehen.
    __atomic_add_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
    void* pointer = malloc(count * size);
    if (pointer != NULL) memset(pointer, 0, count * size);
    __atomic_sub_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
    return pointer;
}

void* realloc(void* pointer, size_t size)
{
    if (pointer == NULL) return malloc(size);
    if (size == 0) { free(pointer); return NULL; }
    __atomic_add_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
    size_t previous = ((zehos_block_header*)pointer - 1)->size;
    void* result = malloc(size);
    if (result == NULL)
    {
        __atomic_sub_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
        return NULL;
    }
    memcpy(result, pointer, previous < size ? previous : size);
    free(pointer);
    __atomic_sub_fetch(&zehos_host_depth, 1, __ATOMIC_SEQ_CST);
    return result;
}

_Noreturn void abort(void)
{
    __cosmos_serial_write("[wasm3] abort\n");
    for (;;) __asm__ volatile("cli; hlt");
}

size_t strlen(const char* text)
{
    size_t length = 0;
    while (text[length] != 0) length++;
    return length;
}

int strncmp(const char* left, const char* right, size_t count)
{
    for (size_t index = 0; index < count; index++)
    {
        unsigned char a = (unsigned char)left[index], b = (unsigned char)right[index];
        if (a != b) return a < b ? -1 : 1;
        if (a == 0) return 0;
    }
    return 0;
}

int strcmp(const char* left, const char* right) { return strncmp(left, right, SIZE_MAX); }

static int digit_value(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'z') return c - 'a' + 10;
    if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
    return 99;
}

// Gemeinsamer Kern nach C11 7.22.1.4: Betrag und Vorzeichen getrennt, Ueberlauf ueber *overflow; ohne Ziffern bleibt *end = text.
static unsigned long long parse_integer(const char* text, char** end, int base, int* negative, int* overflow)
{
    *negative = 0;
    *overflow = 0;
    if (end != NULL) *end = (char*)text;
    if (base < 0 || base == 1 || base > 36) { errno = EINVAL; return 0; }
    const char* p = text;
    while (isspace((unsigned char)*p)) p++;
    if (*p == '-' || *p == '+') *negative = *p++ == '-';
    // "0x" ohne folgende Hexziffer ist nur die Zahl 0; *end zeigt dann auf das 'x'.
    if ((base == 0 || base == 16) && p[0] == '0' && (p[1] == 'x' || p[1] == 'X') && digit_value(p[2]) < 16) { p += 2; base = 16; }
    else if (base == 0) base = p[0] == '0' ? 8 : 10;
    const char* digits = p;
    unsigned long long value = 0;
    for (int digit; (digit = digit_value(*p)) < base; p++)
    {
        if (value > (~0ull - (unsigned)digit) / (unsigned)base) *overflow = 1;
        else value = value * (unsigned)base + (unsigned)digit;
    }
    if (p != digits && end != NULL) *end = (char*)p;
    return value;
}

static long long parse_signed(const char* text, char** end, int base, long long max)
{
    int negative, overflow;
    unsigned long long value = parse_integer(text, end, base, &negative, &overflow);
    unsigned long long limit = negative ? (unsigned long long)max + 1 : (unsigned long long)max;
    if (overflow || value > limit) { errno = ERANGE; return negative ? -max - 1 : max; }
    // Betrag bis max + 1 ohne implementierungsdefinierte Umwandlung negieren; "-0" ergibt 0.
    if (!negative || value == 0) return (long long)value;
    return -(long long)(value - 1) - 1;
}

unsigned long long strtoull(const char* text, char** end, int base)
{
    int negative, overflow;
    unsigned long long value = parse_integer(text, end, base, &negative, &overflow);
    if (overflow) { errno = ERANGE; return ~0ull; }
    return negative ? 0ull - value : value;
}

long long strtoll(const char* text, char** end, int base) { return parse_signed(text, end, base, __LONG_LONG_MAX__); }
long strtol(const char* text, char** end, int base) { return (long)parse_signed(text, end, base, __LONG_MAX__); }
// strtoul, strtof und atoi definiert derzeit die WAMR-Plattformschicht (zehos_platform.c); hier nicht doppelt anlegen.

// shortcut: Gleitkommazahlen werden nicht gelesen (keine Umwandlung, errno = EINVAL); umsetzen, sobald ein Aufrufer sie braucht.
double strtod(const char* text, char** end) { if (end) *end = (char*)text; errno = EINVAL; return 0; }

// Module erreichen wasm3 als Speicherblock ueber den ZehOS-Loader; direkter Dateizugriff ist gesperrt.
FILE* fopen(const char* path, const char* mode) { (void)path; (void)mode; return NULL; }
int fseek(FILE* stream, long offset, int origin) { (void)stream; (void)offset; (void)origin; return -1; }
long ftell(FILE* stream) { (void)stream; return -1; }
size_t fread(void* buffer, size_t size, size_t count, FILE* stream) { (void)buffer; (void)size; (void)count; (void)stream; return 0; }
int fclose(FILE* stream) { (void)stream; return -1; }
int rename(const char* from, const char* to) { (void)from; (void)to; return -1; }
int remove(const char* path) { (void)path; return -1; }

static void append(char* buffer, size_t size, size_t* used, char value)
{
    if (*used + 1 < size) buffer[*used] = value;
    (*used)++;
}

static void append_unsigned(char* buffer, size_t size, size_t* used, unsigned long long value, unsigned base)
{
    char digits[24];
    int count = 0;
    do { digits[count++] = "0123456789abcdef"[value % base]; value /= base; } while (value != 0);
    while (count > 0) append(buffer, size, used, digits[--count]);
}

// Teilmenge fuer Fehlermeldungen: %s %c %d %i %u %x %p %% mit l/ll/z; Breite und Genauigkeit werden uebersprungen.
int vsnprintf(char* buffer, size_t size, const char* format, va_list arguments)
{
    size_t used = 0;
    for (const char* p = format; *p != 0; p++)
    {
        if (*p != '%') { append(buffer, size, &used, *p); continue; }
        p++;
        while (*p == '-' || *p == '+' || *p == ' ' || *p == '#' || *p == '0') p++;
        if (*p == '*') { (void)va_arg(arguments, int); p++; } else while (*p >= '0' && *p <= '9') p++;
        if (*p == '.') { p++; if (*p == '*') { (void)va_arg(arguments, int); p++; } else while (*p >= '0' && *p <= '9') p++; }
        int longs = 0;
        while (*p == 'l' || *p == 'z') { longs++; p++; }
        switch (*p)
        {
            case 's': { const char* text = va_arg(arguments, const char*); if (text == NULL) text = "(null)"; while (*text) append(buffer, size, &used, *text++); break; }
            case 'c': append(buffer, size, &used, (char)va_arg(arguments, int)); break;
            case 'd': case 'i':
            {
                long long value = longs ? va_arg(arguments, long long) : va_arg(arguments, int);
                unsigned long long magnitude = value < 0 ? 0ull - (unsigned long long)value : (unsigned long long)value;
                if (value < 0) append(buffer, size, &used, '-');
                append_unsigned(buffer, size, &used, magnitude, 10);
                break;
            }
            case 'u': append_unsigned(buffer, size, &used, longs ? va_arg(arguments, unsigned long long) : va_arg(arguments, unsigned), 10); break;
            case 'x': append_unsigned(buffer, size, &used, longs ? va_arg(arguments, unsigned long long) : va_arg(arguments, unsigned), 16); break;
            case 'p': append(buffer, size, &used, '0'); append(buffer, size, &used, 'x');
                      append_unsigned(buffer, size, &used, (uintptr_t)va_arg(arguments, void*), 16); break;
            case '%': append(buffer, size, &used, '%'); break;
            case 0: p--; break;
            default: append(buffer, size, &used, '?'); break;
        }
    }
    if (size != 0) buffer[used < size ? used : size - 1] = 0;
    return (int)used;
}

int snprintf(char* buffer, size_t size, const char* format, ...)
{
    va_list arguments;
    va_start(arguments, format);
    int result = vsnprintf(buffer, size, format, arguments);
    va_end(arguments);
    return result;
}

static uint64_t bits_of(double value) { union { double d; uint64_t u; } v = { value }; return v.u; }
static double double_of(uint64_t value) { union { uint64_t u; double d; } v = { value }; return v.d; }
static uint32_t bits_of_float(float value) { union { float f; uint32_t u; } v = { value }; return v.u; }
static float float_of(uint32_t value) { union { uint32_t u; float f; } v = { value }; return v.f; }

double fabs(double value) { return double_of(bits_of(value) & ~(1ull << 63)); }
float fabsf(float value) { return float_of(bits_of_float(value) & ~(1u << 31)); }
double copysign(double magnitude, double sign) { return double_of((bits_of(magnitude) & ~(1ull << 63)) | (bits_of(sign) & (1ull << 63))); }
float copysignf(float magnitude, float sign) { return float_of((bits_of_float(magnitude) & ~(1u << 31)) | (bits_of_float(sign) & (1u << 31))); }

// Ab 2^52 (double) bzw. 2^23 (float) sind alle Werte ganzzahlig; NaN und Unendlich fallen ebenfalls darunter.
// Rundung zur naechsten geraden Zahl ueber die Gleitkommaeinheit; volatile verhindert das Wegfalten.
double rint(double value)
{
    if (!(fabs(value) < 4503599627370496.0)) return value;
    volatile double shifted = fabs(value) + 4503599627370496.0;
    return copysign(shifted - 4503599627370496.0, value);
}

float rintf(float value)
{
    if (!(fabsf(value) < 8388608.0f)) return value;
    volatile float shifted = fabsf(value) + 8388608.0f;
    return copysignf(shifted - 8388608.0f, value);
}
