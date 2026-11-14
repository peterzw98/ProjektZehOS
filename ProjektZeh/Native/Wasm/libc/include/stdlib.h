/* ZehOS: minimale freistehende C-Bibliothek fuer wasm3; nur die tatsaechlich benoetigten Teile. */
#pragma once
#include <stddef.h>
void* malloc(size_t size);
void* calloc(size_t count, size_t size);
void* realloc(void* pointer, size_t size);
void free(void* pointer);
_Noreturn void abort(void);
long long strtoll(const char* text, char** end, int base);
unsigned long long strtoull(const char* text, char** end, int base);
double strtod(const char* text, char** end);
