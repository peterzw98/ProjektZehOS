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

enum { WASI_SUCCESS = 0, WASI_EBADF = 8, WASI_EINVAL = 28, WASI_ENOSYS = 52 };

typedef struct { int32_t exitCode; } zehos_wasm_context;

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

// Nur die Standardkanaele existieren; sie sind Zeichengeraete.
static m3ApiRawFunction(wasi_fd_fdstat_get)
{
    m3ApiReturnType(uint32_t)
    m3ApiGetArg(int32_t, fd)
    m3ApiGetArgMem(uint8_t*, stat)
    m3ApiCheckMem(stat, 24);
    if (fd < 0 || fd > 2) m3ApiReturn(WASI_EBADF);
    memset(stat, 0, 24);
    stat[0] = 2;
    m3ApiReturn(WASI_SUCCESS);
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
        { "fd_prestat_get", "i(i*)", wasi_badf },
        { "fd_prestat_dir_name", "i(i*i)", wasi_badf },
        { "fd_close", "i(i)", wasi_badf },
        { "fd_read", "i(i*i*)", wasi_badf },
        { "fd_seek", "i(iIi*)", wasi_badf },
        { "fd_tell", "i(i*)", wasi_badf },
        { "fd_filestat_get", "i(i*)", wasi_badf },
        { "fd_pread", "i(i*iI*)", wasi_badf },
        { "fd_pwrite", "i(i*iI*)", wasi_badf },
        { "fd_advise", "i(iIIi)", wasi_badf },
        { "fd_sync", "i(i)", wasi_badf },
        { "fd_fdstat_set_flags", "i(ii)", wasi_badf },
        { "fd_filestat_set_size", "i(iI)", wasi_badf },
        { "fd_filestat_set_times", "i(iIIi)", wasi_badf },
        { "fd_readdir", "i(i*iI*)", wasi_badf },
        { "path_open", "i(ii*iiIIi*)", wasi_badf },
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
    return link(module, "proc_exit", "v(i)", wasi_proc_exit, context);
}

// Fuehrt einen Einstiegspunkt aus: "_start" (WASI, Ergebnis = Exitcode) oder eine parameterlose i32-Funktion.
// Das Modul bleibt Eigentum des Aufrufers; wasm3 haelt waehrend des Laufs nur einen Verweis darauf.
int32_t zehos_wasm_run(const uint8_t* bytes, uint32_t length, const char* entry, uint64_t gas,
    uint32_t stackBytes, uint64_t memoryBytes, int32_t* result, char* message, uint32_t messageLength)
{
    zehos_wasm_context context = { 0 };
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
    m3_FreeRuntime(runtime);
    m3_FreeEnvironment(environment);
    return status;
}
