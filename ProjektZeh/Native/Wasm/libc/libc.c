// ZehOS: minimale C-Laufzeit fuer den statisch gebundenen wasm3-Kern.
// floor(f), ceil(f), trunc(f) und sqrt(f) stellt Cosmos 3.0.94 bereits bereit; sie werden nicht erneut definiert.
// Die mem*-Funktionen heissen ueber string.h zehos_mem*, weil die gleichnamigen Cosmos-Symbole abweichen.

#include <stddef.h>
#include <stdint.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

extern void* __cosmos_heap_alloc(size_t size);
extern void __cosmos_heap_free(void* pointer);
extern void __cosmos_serial_write(const char* text);

int errno;

void* memcpy(void* destination, const void* source, size_t count)
{
    void* result = destination;
    __asm__ volatile("rep movsb" : "+D"(destination), "+S"(source), "+c"(count) : : "memory");
    return result;
}

void* memmove(void* destination, const void* source, size_t count)
{
    unsigned char* target = (unsigned char*)destination;
    const unsigned char* origin = (const unsigned char*)source;
    if (target <= origin || target >= origin + count) return memcpy(destination, source, count);
    // Ueberlappung mit hoeherem Ziel: rueckwaerts kopieren.
    while (count-- != 0) target[count] = origin[count];
    return destination;
}

void* memset(void* destination, int value, size_t count)
{
    void* result = destination;
    __asm__ volatile("rep stosb" : "+D"(destination), "+c"(count) : "a"(value) : "memory");
    return result;
}

int memcmp(const void* left, const void* right, size_t count)
{
    const unsigned char* a = (const unsigned char*)left;
    const unsigned char* b = (const unsigned char*)right;
    for (size_t index = 0; index < count; index++)
        if (a[index] != b[index]) return a[index] < b[index] ? -1 : 1;
    return 0;
}

// Kopfbereich vor jedem Block, damit realloc die alte Groesse kennt; 16 Byte erhalten die Ausrichtung.
typedef struct { size_t size; size_t reserved; } zehos_block_header;

void* malloc(size_t size)
{
    if (size > SIZE_MAX - sizeof(zehos_block_header)) return NULL;
    zehos_block_header* header = (zehos_block_header*)__cosmos_heap_alloc(sizeof(zehos_block_header) + size);
    if (header == NULL) return NULL;
    header->size = size;
    return header + 1;
}

void free(void* pointer)
{
    if (pointer != NULL) __cosmos_heap_free((zehos_block_header*)pointer - 1);
}

void* calloc(size_t count, size_t size)
{
    if (size != 0 && count > SIZE_MAX / size) return NULL;
    void* pointer = malloc(count * size);
    if (pointer != NULL) memset(pointer, 0, count * size);
    return pointer;
}

void* realloc(void* pointer, size_t size)
{
    if (pointer == NULL) return malloc(size);
    if (size == 0) { free(pointer); return NULL; }
    size_t previous = ((zehos_block_header*)pointer - 1)->size;
    void* result = malloc(size);
    if (result == NULL) return NULL;
    memcpy(result, pointer, previous < size ? previous : size);
    free(pointer);
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

// Nur fuer m3_CallArgv; ZehOS ruft Funktionen mit typisierten Argumenten auf, daher ist Zahlenparsing nicht vorgesehen.
long long strtoll(const char* text, char** end, int base) { (void)base; if (end) *end = (char*)text; return 0; }
unsigned long long strtoull(const char* text, char** end, int base) { (void)base; if (end) *end = (char*)text; return 0; }
double strtod(const char* text, char** end) { if (end) *end = (char*)text; return 0; }

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
