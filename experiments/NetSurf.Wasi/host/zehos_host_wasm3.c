// Prüfhost mit dem wasm3-Kern und der Importbindung des Kernels (ProjektZeh/ThirdParty/wasm3,
// ProjektZeh/Native/Wasm/host/zehos_wasm.c): dasselbe Parsen, dieselben Features (exnref) und dieselben
// Gastprüfungen wie in ZehOS. Diese Datei liefert nur die Linux-Seite der WASI-Rückrufe und den Start.
//
// Aufruf: zehos-wasm3 <paketordner> <modul.wasm>; Umgebung siehe zehos_host_core.c.
// Exitcode: der des Gasts, sonst 100 + ZEHOS_WASM_*-Status (z. B. 102 ungültiges Modul, 105 Trap).

#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/random.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#include "zehos_host_core.h"

int32_t zehos_wasm_run(const uint8_t* bytes, uint32_t length, const char* entry, uint64_t gas,
    uint32_t stackBytes, uint64_t memoryBytes, int32_t* result, char* message, uint32_t messageLength);

// Werte wie WasmRuntime.cs; d_m3MaxNativeStack wird wie in ProjektZeh.csproj beim Übersetzen gesetzt.
#define THREAD_STACK_BYTES (8u * 1024 * 1024)
#define WASM_STACK_BYTES (1024u * 1024)
#define MEMORY_LIMIT_BYTES (256ull * 1024 * 1024)
#define MAXIMUM_OPEN_FILES 16
#define MAXIMUM_PATH 255

enum { WASI_SUCCESS = 0, WASI_EACCES = 2, WASI_EIO = 29, WASI_ENOENT = 44 };

static const char *root;
static int open_files;

void zehos_wasm_host_write(int32_t fd, const uint8_t *data, uint32_t length)
{
    if (length > 65536) length = 65536;
    FILE *target = fd == 1 ? stdout : stderr;
    fwrite(data, 1, length, target);
    fflush(target);
}

uint64_t zehos_wasm_host_time_ns(int32_t clockId)
{
    struct timespec ts;
    clock_gettime(clockId == 0 ? CLOCK_REALTIME : CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000000000u + (uint64_t)ts.tv_nsec;
}

int32_t zehos_wasm_host_random(uint8_t *buffer, uint32_t length)
{
    while (length > 0) {
        ssize_t count = getrandom(buffer, length, 0);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) return WASI_EIO;
        buffer += count;
        length -= (uint32_t)count;
    }
    return WASI_SUCCESS;
}

// zehos_wasm.c hat den Pfad schon auf einfache relative Komponenten geprüft; hier nur an den Paketordner hängen.
static int package_path(const uint8_t *path, uint32_t length, char *full, size_t capacity)
{
    if (length > MAXIMUM_PATH) return 0;
    int written = snprintf(full, capacity, "%s/%.*s", root, (int)length, (const char *)path);
    return written > 0 && (size_t)written < capacity;
}

int32_t zehos_wasm_host_open(const uint8_t *path, uint32_t length)
{
    char full[4096];
    if (open_files >= MAXIMUM_OPEN_FILES || !package_path(path, length, full, sizeof(full))) return -1;
    int fd = open(full, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return -1;
    struct stat info;
    // FileStream im Kernel öffnet keine Ordner.
    if (fstat(fd, &info) != 0 || S_ISDIR(info.st_mode)) { close(fd); return -1; }
    open_files++;
    return fd;
}

int64_t zehos_wasm_host_file_size(int32_t handle)
{
    struct stat info;
    return fstat(handle, &info) == 0 ? (int64_t)info.st_size : -1;
}

int32_t zehos_wasm_host_file_read(int32_t handle, uint64_t offset, uint8_t *buffer, uint32_t length)
{
    ssize_t count = pread(handle, buffer, length, (off_t)offset);
    return count < 0 ? -1 : (int32_t)count;
}

void zehos_wasm_host_close(int32_t handle)
{
    if (close(handle) == 0) open_files--;
}

// Wie WasmPackagePaths.Stat: symbolische Links gelten als verweigert, Ordner haben Größe 0.
int32_t zehos_wasm_host_stat(const uint8_t *path, uint32_t length, uint64_t *size, int32_t *type)
{
    char full[4096];
    if (!package_path(path, length, full, sizeof(full))) return WASI_EIO;
    struct stat info;
    if (lstat(full, &info) != 0) return errno == ENOENT || errno == ENOTDIR ? WASI_ENOENT : errno == EACCES ? WASI_EACCES : WASI_EIO;
    if (S_ISLNK(info.st_mode)) return WASI_EACCES;
    *size = S_ISDIR(info.st_mode) ? 0 : (uint64_t)info.st_size;
    *type = S_ISDIR(info.st_mode) ? 3 : 4;
    return WASI_SUCCESS;
}

void zehos_wasm_host_present_indexed(const uint8_t *indices, const uint32_t *palette, int32_t width, int32_t height, int32_t tic)
{
    static uint32_t *frame;
    static size_t capacity;
    size_t count = (size_t)width * (size_t)height;
    if (count > capacity) {
        uint32_t *grown = realloc(frame, count * sizeof(uint32_t));
        if (grown == NULL) return;
        frame = grown;
        capacity = count;
    }
    for (size_t i = 0; i < count; i++) frame[i] = palette[indices[i]] & 0x00FFFFFF;
    zehos_wasm_host_present(frame, width, height, tic, 0);
}

// Tastatur und Zeigerfang gibt es im Prüfhost nicht; der Kernel meldet dasselbe für ein inaktives Fenster.
int32_t zehos_wasm_host_poll_key(void) { return -1; }
int32_t zehos_wasm_host_pointer_capture(int32_t enabled) { (void)enabled; return -1; }

struct run { uint8_t *bytes; uint32_t length; int32_t status, result; char message[256]; };

static void *execute(void *argument)
{
    struct run *run = argument;
    run->status = zehos_wasm_run(run->bytes, run->length, "_start", 0, WASM_STACK_BYTES, MEMORY_LIMIT_BYTES,
                                 &run->result, run->message, sizeof(run->message));
    return NULL;
}

static uint8_t *read_module(const char *path, uint32_t *length)
{
    FILE *file = fopen(path, "rb");
    if (file == NULL) return NULL;
    uint8_t *bytes = NULL;
    long size = fseek(file, 0, SEEK_END) == 0 ? ftell(file) : -1;
    if (size > 0 && size <= UINT32_MAX && fseek(file, 0, SEEK_SET) == 0 && (bytes = malloc((size_t)size)) != NULL &&
        fread(bytes, 1, (size_t)size, file) != (size_t)size) {
        free(bytes);
        bytes = NULL;
    }
    fclose(file);
    *length = (uint32_t)size;
    return bytes;
}

int main(int argc, char **argv)
{
    if (argc != 3) { fprintf(stderr, "Aufruf: %s <paketordner> <modul.wasm>\n", argv[0]); return 2; }
    root = argv[1];
    struct run run = { 0 };
    run.bytes = read_module(argv[2], &run.length);
    if (run.bytes == NULL) { fprintf(stderr, "zehos-wasm3: %s nicht lesbar\n", argv[2]); return 2; }
    // Eigener Thread mit dem Stack des Kernel-Workers, weil d_m3MaxNativeStack darauf abgestimmt ist.
    pthread_attr_t attributes;
    pthread_t thread;
    if (pthread_attr_init(&attributes) != 0 || pthread_attr_setstacksize(&attributes, THREAD_STACK_BYTES) != 0 ||
        pthread_create(&thread, &attributes, execute, &run) != 0 || pthread_join(thread, NULL) != 0) {
        fprintf(stderr, "zehos-wasm3: Worker-Thread nicht startbar\n");
        return 2;
    }
    free(run.bytes);
    if (run.status != 0) {
        fprintf(stderr, "zehos-wasm3: Status %d: %s\n", run.status, run.message);
        return 100 + run.status;
    }
    return run.result;
}
