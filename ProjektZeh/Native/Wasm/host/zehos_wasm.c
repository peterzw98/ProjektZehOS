// ZehOS: duenne C-Schicht zwischen wasm3 und dem C#-Kernel.
// Ueber die Grenze laufen nur primitive Typen; Fehler kommen als Statuscode plus Meldungstext zurueck.
// WASI-Dienste, die ZehOS noch nicht anbietet, melden ENOSYS bzw. EBADF statt den Kernel zu gefaehrden.

#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "wasm3.h"

// Exporte des C#-Kernels (UnmanagedCallersOnly).
extern void zehos_wasm_host_write(int32_t fd, const uint8_t* data, uint32_t length);
extern uint64_t zehos_wasm_host_time_ns(int32_t clockId);
extern void zehos_wasm_host_random(uint8_t* buffer, uint32_t length);
// Nur lesender Zugriff unterhalb des freigegebenen App-Ordners; der Pfad ist bereits relativ und geprueft.
extern int32_t zehos_wasm_host_open(const uint8_t* path, uint32_t length);
extern int64_t zehos_wasm_host_file_size(int32_t handle);
extern int32_t zehos_wasm_host_file_read(int32_t handle, uint64_t offset, uint8_t* buffer, uint32_t length);
extern void zehos_wasm_host_close(int32_t handle);
// zehos_v1: Bild, Zeit und Eingabe fuer Anwendungen mit eigener Darstellung.
extern void zehos_wasm_host_present(const uint32_t* pixels, int32_t width, int32_t height, int32_t tic, int32_t test);
extern uint32_t zehos_wasm_host_ticks_ms(void);
extern void zehos_wasm_host_sleep_ms(uint32_t milliseconds);
extern int32_t zehos_wasm_host_poll_key(void);
extern int32_t zehos_wasm_host_should_exit(void);

enum
{
    ZEHOS_WASM_OK = 0,
    ZEHOS_WASM_NO_MEMORY = 1,
    ZEHOS_WASM_INVALID_MODULE = 2,
    ZEHOS_WASM_LOAD_FAILED = 3,
    ZEHOS_WASM_ENTRY_MISSING = 4,
    ZEHOS_WASM_TRAP = 5,
    ZEHOS_WASM_OUT_OF_GAS = 6,
    ZEHOS_WASM_LIMIT_FAILED = 7
};

enum
{
    WASI_SUCCESS = 0, WASI_EACCES = 2, WASI_EBADF = 8, WASI_EINVAL = 28, WASI_EIO = 29,
    WASI_EMFILE = 33, WASI_ENOENT = 44, WASI_ENOSYS = 52, WASI_ENOTCAPABLE = 76
};

// fd 3 ist der einzige freigegebene Ordner ("/"); geoeffnete Dateien belegen ab fd 4 feste Plaetze.
enum { PREOPEN_FD = 3, FIRST_FILE_FD = 4, MAXIMUM_FILES = 16, MAXIMUM_PATH = 255 };

typedef struct { int32_t handle; uint64_t position; uint64_t size; } zehos_wasm_file;

typedef struct
{
    int32_t exitCode;
    zehos_wasm_file files[MAXIMUM_FILES];
} zehos_wasm_context;

static zehos_wasm_file* file_of(zehos_wasm_context* context, int32_t fd)
{
    if (fd < FIRST_FILE_FD || fd >= FIRST_FILE_FD + MAXIMUM_FILES) return NULL;
    zehos_wasm_file* file = &context->files[fd - FIRST_FILE_FD];
    return file->handle >= 0 ? file : NULL;
}

static void copy_message(char* target, uint32_t capacity, const char* text)
{
    if (target == NULL || capacity == 0) return;
    uint32_t index = 0;
    while (text != NULL && text[index] != 0 && index + 1 < capacity) { target[index] = text[index]; index++; }
    target[index] = 0;
}

static m3ApiRawFunction(wasi_fd_write)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    m3ApiGetArg(uint32_t, iovs)
    m3ApiGetArg(uint32_t, iovsCount)
    m3ApiGetArgMem(uint32_t*, written)
    m3ApiCheckMem(written, sizeof(uint32_t));
    if (fd != 1 && fd != 2) m3ApiReturn(WASI_EBADF);
    // Jede Adresse und Laenge aus dem Gast wird gegen den linearen Speicher geprueft.
    if (iovsCount > 1024) m3ApiReturn(WASI_EINVAL);
    uint8_t* vector = (uint8_t*)m3ApiOffsetToPtr(iovs);
    m3ApiCheckMem(vector, (size_t)iovsCount * 8);
    uint32_t total = 0;
    for (uint32_t index = 0; index < iovsCount; index++)
    {
        uint32_t offset, length;
        memcpy(&offset, vector + index * 8, 4);
        memcpy(&length, vector + index * 8 + 4, 4);
        uint8_t* data = (uint8_t*)m3ApiOffsetToPtr(offset);
        m3ApiCheckMem(data, length);
        zehos_wasm_host_write(fd, data, length);
        total += length;
    }
    memcpy(written, &total, 4);
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_proc_exit)
{
    m3ApiGetArg(int32_t, code)
    ((zehos_wasm_context*)_ctx->userdata)->exitCode = code;
    m3ApiTrap(m3Err_trapExit);
}

// Laufzeiten wie Mono setzen argv[0] voraus; weitere Argumente und eine Umgebung gibt es noch nicht.
static const char program_name[] = "app";

static m3ApiRawFunction(wasi_args_sizes_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArgMem(uint32_t*, count)
    m3ApiGetArgMem(uint32_t*, bytes)
    m3ApiCheckMem(count, 4);
    m3ApiCheckMem(bytes, 4);
    uint32_t one = 1, size = sizeof(program_name);
    memcpy(count, &one, 4);
    memcpy(bytes, &size, 4);
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_args_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(uint32_t, pointers)
    m3ApiGetArg(uint32_t, buffer)
    uint8_t* list = (uint8_t*)m3ApiOffsetToPtr(pointers);
    uint8_t* text = (uint8_t*)m3ApiOffsetToPtr(buffer);
    m3ApiCheckMem(list, 4);
    m3ApiCheckMem(text, sizeof(program_name));
    memcpy(list, &buffer, 4);
    memcpy(text, program_name, sizeof(program_name));
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_environ_sizes_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArgMem(uint32_t*, count)
    m3ApiGetArgMem(uint32_t*, bytes)
    m3ApiCheckMem(count, 4);
    m3ApiCheckMem(bytes, 4);
    uint32_t zero = 0;
    memcpy(count, &zero, 4);
    memcpy(bytes, &zero, 4);
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_environ_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_clock_time_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, clockId)
    m3ApiGetArg(uint64_t, precision)
    m3ApiGetArgMem(uint64_t*, time)
    (void)precision;
    m3ApiCheckMem(time, 8);
    if (clockId < 0 || clockId > 3) m3ApiReturn(WASI_EINVAL);
    uint64_t now = zehos_wasm_host_time_ns(clockId);
    memcpy(time, &now, 8);
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_clock_res_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, clockId)
    m3ApiGetArgMem(uint64_t*, resolution)
    (void)clockId;
    m3ApiCheckMem(resolution, 8);
    uint64_t value = 1000000;
    memcpy(resolution, &value, 8);
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_random_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArgMem(uint8_t*, buffer)
    m3ApiGetArg(uint32_t, length)
    m3ApiCheckMem(buffer, length);
    zehos_wasm_host_random(buffer, length);
    m3ApiReturn(WASI_SUCCESS);
}

// Standardkanaele sind Zeichengeraete, fd 3 der freigegebene Ordner, ab fd 4 regulaere Dateien.
static m3ApiRawFunction(wasi_fd_fdstat_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    m3ApiGetArgMem(uint8_t*, stat)
    m3ApiCheckMem(stat, 24);
    uint8_t type;
    if (fd >= 0 && fd <= 2) type = 2;
    else if (fd == PREOPEN_FD) type = 3;
    else if (file_of((zehos_wasm_context*)_ctx->userdata, fd) != NULL) type = 4;
    else m3ApiReturn(WASI_EBADF);
    memset(stat, 0, 24);
    stat[0] = type;
    // Rechte nur zum Lesen, Suchen und Abfragen; wasi-libc prueft sie vor eigenen Aufrufen.
    uint64_t rights = (1ull << 1) | (1ull << 2) | (1ull << 5) | (1ull << 13) | (1ull << 18) | (1ull << 21);
    memcpy(stat + 8, &rights, 8);
    memcpy(stat + 16, &rights, 8);
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_fd_prestat_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    m3ApiGetArgMem(uint8_t*, prestat)
    m3ApiCheckMem(prestat, 8);
    if (fd != PREOPEN_FD) m3ApiReturn(WASI_EBADF);
    uint32_t nameLength = 1;
    memset(prestat, 0, 8);
    memcpy(prestat + 4, &nameLength, 4);
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_fd_prestat_dir_name)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    m3ApiGetArgMem(uint8_t*, path)
    m3ApiGetArg(uint32_t, length)
    if (fd != PREOPEN_FD) m3ApiReturn(WASI_EBADF);
    if (length < 1) m3ApiReturn(WASI_EINVAL);
    m3ApiCheckMem(path, 1);
    path[0] = '/';
    m3ApiReturn(WASI_SUCCESS);
}

// Erlaubt nur relative Pfade aus einfachen Komponenten; "..", leere Komponenten, "\\" und ":" werden abgelehnt.
static int is_safe_path(const uint8_t* path, uint32_t length)
{
    if (length == 0 || length > MAXIMUM_PATH || path[0] == '/') return 0;
    uint32_t start = 0;
    for (uint32_t index = 0; index <= length; index++)
    {
        if (index < length && path[index] != '/')
        {
            if (path[index] == '\\' || path[index] == ':' || path[index] == 0) return 0;
            continue;
        }
        uint32_t size = index - start;
        if (size == 0 || (size == 2 && path[start] == '.' && path[start + 1] == '.')) return 0;
        start = index + 1;
    }
    return 1;
}

static m3ApiRawFunction(wasi_path_open)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, dirFd)
    m3ApiGetArg(uint32_t, lookupFlags)
    m3ApiGetArgMem(const uint8_t*, path)
    m3ApiGetArg(uint32_t, pathLength)
    m3ApiGetArg(uint32_t, openFlags)
    m3ApiGetArg(uint64_t, rightsBase)
    m3ApiGetArg(uint64_t, rightsInheriting)
    m3ApiGetArg(uint32_t, fdFlags)
    m3ApiGetArgMem(uint32_t*, openedFd)
    (void)lookupFlags; (void)rightsInheriting;
    m3ApiCheckMem(openedFd, 4);
    m3ApiCheckMem(path, pathLength);
    if (dirFd != PREOPEN_FD) m3ApiReturn(WASI_EBADF);
    // Anlegen, Leeren, Ordner und jede Schreib- oder Anhaengeabsicht sind in diesem Profil gesperrt.
    const uint64_t writeRights = (1ull << 4) | (1ull << 6) | (1ull << 7) | (1ull << 9) | (1ull << 10) | (1ull << 22);
    if ((openFlags & 0xF) != 0 || (rightsBase & writeRights) != 0 || (fdFlags & 0x1F & ~0x4) != 0) m3ApiReturn(WASI_ENOTCAPABLE);
    if (!is_safe_path(path, pathLength)) m3ApiReturn(WASI_EACCES);
    zehos_wasm_context* context = (zehos_wasm_context*)_ctx->userdata;
    int32_t slot = -1;
    for (int32_t index = 0; index < MAXIMUM_FILES && slot < 0; index++)
        if (context->files[index].handle < 0) slot = index;
    if (slot < 0) m3ApiReturn(WASI_EMFILE);
    int32_t handle = zehos_wasm_host_open(path, pathLength);
    if (handle < 0) m3ApiReturn(WASI_ENOENT);
    int64_t size = zehos_wasm_host_file_size(handle);
    if (size < 0) { zehos_wasm_host_close(handle); m3ApiReturn(WASI_EIO); }
    context->files[slot].handle = handle;
    context->files[slot].position = 0;
    context->files[slot].size = (uint64_t)size;
    uint32_t fd = (uint32_t)(FIRST_FILE_FD + slot);
    memcpy(openedFd, &fd, 4);
    m3ApiReturn(WASI_SUCCESS);
}

// Gleiche Pruefung wie m3ApiCheckMem, aber als Funktion fuer Hilfsroutinen ohne Trap-Rueckgabe.
static int in_memory(void* memory, const void* address, size_t length)
{
    uintptr_t base = (uintptr_t)memory, start = (uintptr_t)address;
    size_t size = m3_GetMemorySizeAt(memory);
    return start >= base && (size_t)(start - base) <= size && length <= size - (size_t)(start - base);
}

// Liest ueber einen iovec-Vektor ab offset; gemeinsamer Teil von fd_read und fd_pread.
static uint32_t read_vectors(zehos_wasm_file* file, void* _mem, uint32_t iovs, uint32_t count,
    uint64_t offset, uint32_t* total)
{
    *total = 0;
    if (count > 1024) return WASI_EINVAL;
    uint8_t* vector = (uint8_t*)m3ApiOffsetToPtr(iovs);
    if (!in_memory(_mem, vector, (size_t)count * 8)) return WASI_EINVAL;
    for (uint32_t index = 0; index < count; index++)
    {
        uint32_t target, length;
        memcpy(&target, vector + index * 8, 4);
        memcpy(&length, vector + index * 8 + 4, 4);
        uint8_t* buffer = (uint8_t*)m3ApiOffsetToPtr(target);
        if (!in_memory(_mem, buffer, length)) return WASI_EINVAL;
        if (offset >= file->size || length == 0) continue;
        uint64_t available = file->size - offset;
        uint32_t wanted = available < length ? (uint32_t)available : length;
        int32_t read = zehos_wasm_host_file_read(file->handle, offset, buffer, wanted);
        if (read < 0) return WASI_EIO;
        offset += (uint32_t)read;
        *total += (uint32_t)read;
        if ((uint32_t)read < wanted) break;
    }
    return WASI_SUCCESS;
}

static m3ApiRawFunction(wasi_fd_read)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    m3ApiGetArg(uint32_t, iovs)
    m3ApiGetArg(uint32_t, count)
    m3ApiGetArgMem(uint32_t*, readBytes)
    m3ApiCheckMem(readBytes, 4);
    zehos_wasm_file* file = file_of((zehos_wasm_context*)_ctx->userdata, fd);
    if (file == NULL) m3ApiReturn(WASI_EBADF);
    uint32_t total;
    uint32_t error = read_vectors(file, _mem, iovs, count, file->position, &total);
    if (error == WASI_SUCCESS) file->position += total;
    memcpy(readBytes, &total, 4);
    m3ApiReturn(error);
}

static m3ApiRawFunction(wasi_fd_pread)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    m3ApiGetArg(uint32_t, iovs)
    m3ApiGetArg(uint32_t, count)
    m3ApiGetArg(uint64_t, offset)
    m3ApiGetArgMem(uint32_t*, readBytes)
    m3ApiCheckMem(readBytes, 4);
    zehos_wasm_file* file = file_of((zehos_wasm_context*)_ctx->userdata, fd);
    if (file == NULL) m3ApiReturn(WASI_EBADF);
    uint32_t total;
    uint32_t error = read_vectors(file, _mem, iovs, count, offset, &total);
    memcpy(readBytes, &total, 4);
    m3ApiReturn(error);
}

static m3ApiRawFunction(wasi_fd_seek)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    m3ApiGetArg(int64_t, offset)
    m3ApiGetArg(uint32_t, whence)
    m3ApiGetArgMem(uint64_t*, newOffset)
    m3ApiCheckMem(newOffset, 8);
    zehos_wasm_file* file = file_of((zehos_wasm_context*)_ctx->userdata, fd);
    if (file == NULL) m3ApiReturn(WASI_EBADF);
    int64_t base;
    if (whence == 0) base = 0;
    else if (whence == 1) base = (int64_t)file->position;
    else if (whence == 2) base = (int64_t)file->size;
    else m3ApiReturn(WASI_EINVAL);
    if ((offset > 0 && base > INT64_MAX - offset) || base + offset < 0) m3ApiReturn(WASI_EINVAL);
    file->position = (uint64_t)(base + offset);
    memcpy(newOffset, &file->position, 8);
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_fd_tell)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    m3ApiGetArgMem(uint64_t*, position)
    m3ApiCheckMem(position, 8);
    zehos_wasm_file* file = file_of((zehos_wasm_context*)_ctx->userdata, fd);
    if (file == NULL) m3ApiReturn(WASI_EBADF);
    memcpy(position, &file->position, 8);
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_fd_filestat_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    m3ApiGetArgMem(uint8_t*, stat)
    m3ApiCheckMem(stat, 64);
    zehos_wasm_file* file = file_of((zehos_wasm_context*)_ctx->userdata, fd);
    if (file == NULL && fd != PREOPEN_FD) m3ApiReturn(WASI_EBADF);
    memset(stat, 0, 64);
    stat[16] = file != NULL ? 4 : 3;
    if (file != NULL) memcpy(stat + 32, &file->size, 8);
    m3ApiReturn(WASI_SUCCESS);
}

static m3ApiRawFunction(wasi_fd_close)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    zehos_wasm_file* file = file_of((zehos_wasm_context*)_ctx->userdata, fd);
    if (file == NULL) m3ApiReturn(WASI_EBADF);
    zehos_wasm_host_close(file->handle);
    file->handle = -1;
    m3ApiReturn(WASI_SUCCESS);
}

// zehos_v1: Bildpuffer 0x00RRGGBB zeilenweise; Groesse und Speicherbereich werden vor dem Zugriff geprueft.
static m3ApiRawFunction(zehos_present)
{
    m3ApiGetArg(uint32_t, pixels)
    m3ApiGetArg(int32_t, width)
    m3ApiGetArg(int32_t, height)
    m3ApiGetArg(int32_t, tic)
    m3ApiGetArg(int32_t, test)
    if (width <= 0 || height <= 0 || width > 1920 || height > 1200) m3ApiTrap(m3Err_trapOutOfBoundsMemoryAccess);
    const uint32_t* data = (const uint32_t*)m3ApiOffsetToPtr(pixels);
    m3ApiCheckMem(data, (size_t)width * (size_t)height * 4);
    zehos_wasm_host_present(data, width, height, tic, test);
    m3ApiSuccess();
}

static m3ApiRawFunction(zehos_ticks_ms)
{
    m3ApiReturnType(uint32_t)
    m3ApiReturn(zehos_wasm_host_ticks_ms());
}

static m3ApiRawFunction(zehos_sleep_ms)
{
    m3ApiGetArg(uint32_t, milliseconds)
    zehos_wasm_host_sleep_ms(milliseconds > 1000 ? 1000 : milliseconds);
    m3ApiSuccess();
}

static m3ApiRawFunction(zehos_poll_key)
{
    m3ApiReturnType(int32_t)
    m3ApiReturn(zehos_wasm_host_poll_key());
}

static m3ApiRawFunction(zehos_should_exit)
{
    m3ApiReturnType(int32_t)
    m3ApiReturn(zehos_wasm_host_should_exit());
}

static m3ApiRawFunction(wasi_badf)
{
    m3ApiReturnType(uint32_t)
    m3ApiReturn(WASI_EBADF);
}

static m3ApiRawFunction(wasi_nosys)
{
    m3ApiReturnType(uint32_t)
    m3ApiReturn(WASI_ENOSYS);
}

static m3ApiRawFunction(wasi_sched_yield)
{
    m3ApiReturnType(uint32_t)
    m3ApiReturn(WASI_SUCCESS);
}

static M3Result link(IM3Module module, const char* name, const char* signature, M3RawCall function, void* userdata)
{
    M3Result result = m3_LinkRawFunctionEx(module, "wasi_snapshot_preview1", name, signature, function, userdata);
    // Nicht importierte Funktionen sind kein Fehler.
    return result == m3Err_functionLookupFailed ? m3Err_none : result;
}

static M3Result link_wasi(IM3Module module, zehos_wasm_context* context)
{
    static const struct { const char* name; const char* signature; M3RawCall function; } table[] =
    {
        { "fd_write", "i(i*i*)", wasi_fd_write },
        { "args_sizes_get", "i(**)", wasi_args_sizes_get },
        { "environ_sizes_get", "i(**)", wasi_environ_sizes_get },
        { "args_get", "i(**)", wasi_args_get },
        { "environ_get", "i(**)", wasi_environ_get },
        { "clock_time_get", "i(iI*)", wasi_clock_time_get },
        { "clock_res_get", "i(i*)", wasi_clock_res_get },
        { "random_get", "i(*i)", wasi_random_get },
        { "fd_fdstat_get", "i(i*)", wasi_fd_fdstat_get },
        { "sched_yield", "i()", wasi_sched_yield },
        { "fd_prestat_get", "i(i*)", wasi_fd_prestat_get },
        { "fd_prestat_dir_name", "i(i*i)", wasi_fd_prestat_dir_name },
        { "fd_close", "i(i)", wasi_fd_close },
        { "fd_read", "i(i*i*)", wasi_fd_read },
        { "fd_seek", "i(iIi*)", wasi_fd_seek },
        { "fd_tell", "i(i*)", wasi_fd_tell },
        { "fd_filestat_get", "i(i*)", wasi_fd_filestat_get },
        { "fd_pread", "i(i*iI*)", wasi_fd_pread },
        { "fd_pwrite", "i(i*iI*)", wasi_badf },
        { "fd_advise", "i(iIIi)", wasi_badf },
        { "fd_sync", "i(i)", wasi_badf },
        { "fd_fdstat_set_flags", "i(ii)", wasi_badf },
        { "fd_filestat_set_size", "i(iI)", wasi_badf },
        { "fd_filestat_set_times", "i(iIIi)", wasi_badf },
        { "fd_readdir", "i(i*iI*)", wasi_badf },
        { "path_open", "i(ii*iiIIi*)", wasi_path_open },
        { "path_filestat_get", "i(ii*i*)", wasi_badf },
        { "path_create_directory", "i(i*i)", wasi_badf },
        { "path_remove_directory", "i(i*i)", wasi_badf },
        { "path_unlink_file", "i(i*i)", wasi_badf },
        { "path_rename", "i(i*ii*i)", wasi_badf },
        { "path_link", "i(ii*ii*i)", wasi_badf },
        { "path_symlink", "i(*ii*i)", wasi_badf },
        { "path_readlink", "i(i*i*i*)", wasi_badf },
        { "path_filestat_set_times", "i(ii*iIIi)", wasi_badf },
        { "poll_oneoff", "i(**i*)", wasi_nosys },
        { "sock_accept", "i(ii*)", wasi_nosys },
        { "sock_recv", "i(i*ii**)", wasi_nosys },
        { "sock_send", "i(i*ii*)", wasi_nosys },
        { "sock_shutdown", "i(ii)", wasi_nosys },
    };
    for (size_t index = 0; index < sizeof(table) / sizeof(table[0]); index++)
    {
        M3Result result = link(module, table[index].name, table[index].signature, table[index].function, context);
        if (result != m3Err_none) return result;
    }
    static const struct { const char* name; const char* signature; M3RawCall function; } zehos[] =
    {
        { "present", "v(iiiii)", zehos_present },
        { "ticks_ms", "i()", zehos_ticks_ms },
        { "sleep_ms", "v(i)", zehos_sleep_ms },
        { "poll_key", "i()", zehos_poll_key },
        { "should_exit", "i()", zehos_should_exit },
    };
    for (size_t index = 0; index < sizeof(zehos) / sizeof(zehos[0]); index++)
    {
        M3Result result = m3_LinkRawFunctionEx(module, "zehos_v1", zehos[index].name, zehos[index].signature, zehos[index].function, context);
        if (result != m3Err_none && result != m3Err_functionLookupFailed) return result;
    }
    return link(module, "proc_exit", "v(i)", wasi_proc_exit, context);
}

// Fuehrt einen Einstiegspunkt aus: "_start" (WASI, Ergebnis = Exitcode) oder eine parameterlose i32-Funktion.
// Das Modul bleibt Eigentum des Aufrufers; wasm3 haelt waehrend des Laufs nur einen Verweis darauf.
int32_t zehos_wasm_run(const uint8_t* bytes, uint32_t length, const char* entry, uint64_t gas,
    uint32_t stackBytes, uint64_t memoryBytes, int32_t* result, char* message, uint32_t messageLength)
{
    zehos_wasm_context context = { 0 };
    for (int index = 0; index < MAXIMUM_FILES; index++) context.files[index].handle = -1;
    int32_t status = ZEHOS_WASM_OK;
    *result = 0;
    copy_message(message, messageLength, "");

    IM3Environment environment = m3_NewEnvironment();
    if (environment == NULL) return ZEHOS_WASM_NO_MEMORY;
    IM3Runtime runtime = m3_NewRuntime(environment, stackBytes, NULL);
    if (runtime == NULL) { m3_FreeEnvironment(environment); return ZEHOS_WASM_NO_MEMORY; }

    IM3Module module = NULL;
    M3Result error = NULL;
    // Gas muss vor dem Uebersetzen scharf geschaltet sein, damit der erzeugte Code es misst.
    if ((error = m3_SetResourceLimit(runtime, c_m3Limit_GasUnits, gas)) != NULL ||
        (error = m3_SetResourceLimit(runtime, c_m3Limit_MemoryBytes, memoryBytes)) != NULL)
    {
        status = ZEHOS_WASM_LIMIT_FAILED;
    }
    else if ((error = m3_ParseModule(environment, &module, bytes, length)) != NULL)
    {
        status = ZEHOS_WASM_INVALID_MODULE;
    }
    else if ((error = m3_LoadModule(runtime, module)) != NULL)
    {
        m3_FreeModule(module);
        status = ZEHOS_WASM_LOAD_FAILED;
    }
    else if ((error = link_wasi(module, &context)) != NULL)
    {
        status = ZEHOS_WASM_LOAD_FAILED;
    }
    else
    {
        IM3Function function = NULL;
        if ((error = m3_FindFunction(&function, runtime, entry)) != NULL)
        {
            status = ZEHOS_WASM_ENTRY_MISSING;
        }
        else
        {
            error = m3_CallV(function);
            if (error == m3Err_trapExit) { *result = context.exitCode; error = NULL; }
            else if (error == m3Err_trapOutOfGas) status = ZEHOS_WASM_OUT_OF_GAS;
            else if (error != NULL) status = ZEHOS_WASM_TRAP;
            else if (m3_GetRetCount(function) == 1)
            {
                int32_t value = 0;
                if ((error = m3_GetResultsV(function, &value)) != NULL) status = ZEHOS_WASM_TRAP;
                else *result = value;
            }
        }
    }

    if (error != NULL) copy_message(message, messageLength, error);
    // Offen gebliebene Dateien gehoeren dem Lauf und werden auch bei Traps geschlossen.
    for (int index = 0; index < MAXIMUM_FILES; index++)
        if (context.files[index].handle >= 0) zehos_wasm_host_close(context.files[index].handle);
    m3_FreeRuntime(runtime);
    m3_FreeEnvironment(environment);
    return status;
}
