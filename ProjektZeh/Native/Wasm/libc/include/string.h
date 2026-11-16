#pragma once
#include <stddef.h>
/* Die mem*-Funktionen stellt Cosmos bereit; seit 3.0.97 (Upstream 56784efc9) vergleicht memcmp byteweise mit
   Vorzeichen, und memmove/memset liefern das Ziel zurueck. Laengen ueber INT_MAX unterstuetzt Cosmos nicht. */
void* memcpy(void* destination, const void* source, size_t count);
void* memmove(void* destination, const void* source, size_t count);
void* memset(void* destination, int value, size_t count);
int memcmp(const void* left, const void* right, size_t count);
size_t strlen(const char* text);
int strcmp(const char* left, const char* right);
int strncmp(const char* left, const char* right, size_t count);
