// ZehOS: WAMR-Plattformschicht fuer den reinen AOT-Lader im Cosmos-Kernel (ein Thread, kein Dateisystem).
#ifndef _PLATFORM_INTERNAL_H
#define _PLATFORM_INTERNAL_H

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdarg.h>
#include <inttypes.h>
#include <limits.h>
#include <errno.h>
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#ifndef BH_PLATFORM_ZEHOS
#define BH_PLATFORM_ZEHOS
#endif

#define BH_APPLET_PRESERVED_STACK_SIZE (32 * 1024)
#define BH_THREAD_DEFAULT_PRIORITY 0

// Apps laufen auf genau einem Worker; Sperren und Bedingungen sind deshalb leer.
typedef int korp_tid;
typedef int korp_mutex;
typedef int korp_cond;
typedef int korp_thread;
typedef int korp_rwlock;
typedef int korp_sem;

typedef int os_file_handle;
typedef void *os_dir_stream;
typedef int os_raw_file_handle;

// Fehlen in der freistehenden C-Laufzeit von ZehOS; die Plattformschicht stellt sie fuer WAMR bereit.
void qsort(void *base, size_t count, size_t size, int (*compare)(const void *, const void *));
void *bsearch(const void *key, const void *base, size_t count, size_t size, int (*compare)(const void *, const void *));
int strncasecmp(const char *left, const char *right, size_t count);
int atoi(const char *text);
unsigned long strtoul(const char *text, char **end, int base);
float strtof(const char *text, char **end);
long labs(long value);
double fmin(double left, double right);
float fminf(float left, float right);
double fmax(double left, double right);
float fmaxf(float left, float right);

// Stackgrenze des Wasm-Workers fuer den naechsten AOT-Lauf; NULL schaltet die Pruefung ab (zehos_platform.c).
void zehos_wamr_set_stack_boundary(uint8_t *boundary);

static inline os_file_handle os_get_invalid_handle(void) { return -1; }
static inline int os_getpagesize(void) { return 4096; }

#endif
