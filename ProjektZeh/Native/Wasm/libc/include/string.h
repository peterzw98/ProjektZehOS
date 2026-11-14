#pragma once
#include <stddef.h>
/* Cosmos 3.0.94 exportiert memcmp nur wortweise (Restbytes werden ignoriert, Ergebnis nur 0/1).
   wasm3 braucht exakte Semantik; deshalb leiten diese Makros auf eigene Implementierungen um. */
#define memcpy zehos_memcpy
#define memmove zehos_memmove
#define memset zehos_memset
#define memcmp zehos_memcmp
void* memcpy(void* destination, const void* source, size_t count);
void* memmove(void* destination, const void* source, size_t count);
void* memset(void* destination, int value, size_t count);
int memcmp(const void* left, const void* right, size_t count);
size_t strlen(const char* text);
int strcmp(const char* left, const char* right);
int strncmp(const char* left, const char* right, size_t count);
