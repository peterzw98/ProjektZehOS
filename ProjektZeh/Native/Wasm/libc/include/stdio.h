#pragma once
#include <stddef.h>
#include <stdarg.h>
typedef struct zehos_file FILE;
extern FILE* const stdout;
extern FILE* const stderr;
int printf(const char* format, ...);
int fprintf(FILE* stream, const char* format, ...);
int snprintf(char* buffer, size_t size, const char* format, ...);
int vsnprintf(char* buffer, size_t size, const char* format, va_list arguments);
int fflush(FILE* stream);
/* Dateizugriff laeuft ueber ZehOS-Dienste; diese Funktionen schlagen immer fehl. */
#define SEEK_SET 0
#define SEEK_END 2
FILE* fopen(const char* path, const char* mode);
int fseek(FILE* stream, long offset, int origin);
long ftell(FILE* stream);
size_t fread(void* buffer, size_t size, size_t count, FILE* stream);
int fclose(FILE* stream);
int rename(const char* from, const char* to);
int remove(const char* path);
