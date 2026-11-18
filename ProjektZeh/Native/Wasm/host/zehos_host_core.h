// ZehOS: Hostfunktionen fuer Wasm-Apps, unabhaengig von der Laufzeit (heute wasm3, spaeter WAMR-AOT).
// Jede Kernfunktion erhaelt den Lauf-Kontext, Speicherbasis und -groesse sowie die rohen Gastargumente und prueft
// jede Gastadresse und -laenge selbst. Bindungen uebernehmen nur Argumente und setzen gemeldete Traps um.

#ifndef ZEHOS_HOST_CORE_H
#define ZEHOS_HOST_CORE_H

#include <stdint.h>
#include <stddef.h>

// Statuscodes von zehos_wasm_run; WasmRuntime.cs wertet dieselben Zahlen aus.
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

enum { ZEHOS_HOST_MAXIMUM_FILES = 16 };

// kind: Paketdatei (nur lesend), Datei oder Ordner im App-Speicher /save (Etappe 13.4). Rechte gelten je fd.
enum { ZEHOS_FILE_PACKAGE = 0, ZEHOS_FILE_SAVE = 1, ZEHOS_FILE_SAVE_DIRECTORY = 2 };

typedef struct { int32_t handle; uint64_t position; uint64_t size; uint8_t kind; uint8_t writable; uint8_t append; } zehos_wasm_file;

// Gehoert genau einem Lauf; offene Dateien schliesst zehos_host_context_close_files auf jedem Weg.
typedef struct
{
    int32_t exitCode;
    zehos_wasm_file files[ZEHOS_HOST_MAXIMUM_FILES];
} zehos_wasm_context;

void zehos_host_context_init(zehos_wasm_context* context);
void zehos_host_context_close_files(zehos_wasm_context* context);

// Ein Trap beendet den Gast; der Rueckgabewert der Kernfunktion gilt dann nicht.
typedef enum
{
    ZEHOS_HOST_CONTINUE = 0,
    ZEHOS_HOST_TRAP_MEMORY = 1, // Gastadresse oder -laenge ausserhalb des linearen Speichers
    ZEHOS_HOST_TRAP_EXIT = 2    // proc_exit; der Exitcode steht in context->exitCode
} zehos_host_trap;

typedef struct
{
    zehos_wasm_context* context;
    uint8_t* memory;
    size_t memorySize;
    zehos_host_trap trap;
} zehos_host_call;

// Die einzige Liste aller Imports. Jede Bindung leitet ihre Eintraege daraus ab:
// X(Modul, Name, Ergebnis, Parameter, Kernfunktion, Argumente)
//   Ergebnis:  i (i32) oder v (kein Ergebnis); Parameter in wasm3-Schreibweise (i = i32, I = i64, * = Gastadresse als i32)
//   Argumente: Zahl der Gastargumente, die die Kernfunktion uebernimmt; Stubs ignorieren ihre Argumente (0).
// Die Reihenfolge ist die Bindungsreihenfolge; proc_exit bleibt wie bisher zuletzt.
#define ZEHOS_HOST_FUNCTIONS(X) \
    X(wasi_snapshot_preview1, fd_write, i, "i*i*", zehos_host_fd_write, 4) \
    X(wasi_snapshot_preview1, args_sizes_get, i, "**", zehos_host_args_sizes_get, 2) \
    X(wasi_snapshot_preview1, environ_sizes_get, i, "**", zehos_host_environ_sizes_get, 2) \
    X(wasi_snapshot_preview1, args_get, i, "**", zehos_host_args_get, 2) \
    X(wasi_snapshot_preview1, environ_get, i, "**", zehos_host_environ_get, 2) \
    X(wasi_snapshot_preview1, clock_time_get, i, "iI*", zehos_host_clock_time_get, 3) \
    X(wasi_snapshot_preview1, clock_res_get, i, "i*", zehos_host_clock_res_get, 2) \
    X(wasi_snapshot_preview1, random_get, i, "*i", zehos_host_random_get, 2) \
    X(wasi_snapshot_preview1, fd_fdstat_get, i, "i*", zehos_host_fd_fdstat_get, 2) \
    X(wasi_snapshot_preview1, sched_yield, i, "", zehos_host_sched_yield, 0) \
    X(wasi_snapshot_preview1, fd_prestat_get, i, "i*", zehos_host_fd_prestat_get, 2) \
    X(wasi_snapshot_preview1, fd_prestat_dir_name, i, "i*i", zehos_host_fd_prestat_dir_name, 3) \
    X(wasi_snapshot_preview1, fd_close, i, "i", zehos_host_fd_close, 1) \
    X(wasi_snapshot_preview1, fd_read, i, "i*i*", zehos_host_fd_read, 4) \
    X(wasi_snapshot_preview1, fd_seek, i, "iIi*", zehos_host_fd_seek, 4) \
    X(wasi_snapshot_preview1, fd_tell, i, "i*", zehos_host_fd_tell, 2) \
    X(wasi_snapshot_preview1, fd_filestat_get, i, "i*", zehos_host_fd_filestat_get, 2) \
    X(wasi_snapshot_preview1, fd_pread, i, "i*iI*", zehos_host_fd_pread, 5) \
    X(wasi_snapshot_preview1, fd_pwrite, i, "i*iI*", zehos_host_fd_pwrite, 5) \
    X(wasi_snapshot_preview1, fd_advise, i, "iIIi", zehos_host_badf, 0) \
    X(wasi_snapshot_preview1, fd_sync, i, "i", zehos_host_fd_sync, 1) \
    X(wasi_snapshot_preview1, fd_fdstat_set_flags, i, "ii", zehos_host_badf, 0) \
    X(wasi_snapshot_preview1, fd_filestat_set_size, i, "iI", zehos_host_fd_filestat_set_size, 2) \
    X(wasi_snapshot_preview1, fd_filestat_set_times, i, "iIIi", zehos_host_badf, 0) \
    X(wasi_snapshot_preview1, fd_readdir, i, "i*iI*", zehos_host_fd_readdir, 5) \
    X(wasi_snapshot_preview1, path_open, i, "ii*iiIIi*", zehos_host_path_open, 9) \
    X(wasi_snapshot_preview1, path_filestat_get, i, "ii*i*", zehos_host_path_filestat_get, 5) \
    X(wasi_snapshot_preview1, path_create_directory, i, "i*i", zehos_host_path_create_directory, 3) \
    X(wasi_snapshot_preview1, path_remove_directory, i, "i*i", zehos_host_path_remove_directory, 3) \
    X(wasi_snapshot_preview1, path_unlink_file, i, "i*i", zehos_host_path_unlink_file, 3) \
    X(wasi_snapshot_preview1, path_rename, i, "i*ii*i", zehos_host_path_rename, 6) \
    X(wasi_snapshot_preview1, path_link, i, "ii*ii*i", zehos_host_notcapable, 0) \
    X(wasi_snapshot_preview1, path_symlink, i, "*ii*i", zehos_host_notcapable, 0) \
    X(wasi_snapshot_preview1, path_readlink, i, "i*i*i*", zehos_host_path_readlink, 6) \
    X(wasi_snapshot_preview1, path_filestat_set_times, i, "ii*iIIi", zehos_host_badf, 0) \
    X(wasi_snapshot_preview1, poll_oneoff, i, "**i*", zehos_host_nosys, 0) \
    X(wasi_snapshot_preview1, sock_accept, i, "ii*", zehos_host_nosys, 0) \
    X(wasi_snapshot_preview1, sock_recv, i, "i*ii**", zehos_host_nosys, 0) \
    X(wasi_snapshot_preview1, sock_send, i, "i*ii*", zehos_host_nosys, 0) \
    X(wasi_snapshot_preview1, sock_shutdown, i, "ii", zehos_host_nosys, 0) \
    X(zehos_v1, present, v, "iiiii", zehos_host_present, 5) \
    X(zehos_v1, present_indexed, v, "iiiii", zehos_host_present_indexed, 5) \
    X(zehos_v1, ticks_ms, i, "", zehos_host_ticks_ms, 0) \
    X(zehos_v1, sleep_ms, v, "i", zehos_host_sleep_ms, 1) \
    X(zehos_v1, poll_key, i, "", zehos_host_poll_key, 0) \
    X(zehos_v1, poll_event, i, "i", zehos_host_poll_event, 1) \
    X(zehos_v1, pointer_capture, i, "i", zehos_host_pointer_capture, 1) \
    X(zehos_v1, should_exit, i, "", zehos_host_should_exit, 0) \
    X(zehos_v1, net_open, i, "iiiii", zehos_host_net_open, 5) \
    X(zehos_v1, net_status, i, "i", zehos_host_net_status, 1) \
    X(zehos_v1, net_send, i, "iiii", zehos_host_net_send, 4) \
    X(zehos_v1, net_recv, i, "iiii", zehos_host_net_recv, 4) \
    X(zehos_v1, net_close, i, "i", zehos_host_net_close, 1) \
    X(zehos_v1, raster_draw, i, "i", zehos_host_raster_draw, 1) \
    X(zehos_v1, raster_fill, i, "iii", zehos_host_raster_fill, 3) \
    X(wasi_snapshot_preview1, proc_exit, v, "i", zehos_host_proc_exit, 1)

// Tiefe laufender Hostaufrufe des Wasm-Workers (Roadmap 12.4-I1): Nur bei 0 laeuft Gastcode, und nur dann darf eine
// CPU-Ausnahme den Worker aufgeben, weil Hostcode Kernelsperren halten kann. Es zaehlen die Wrapper beider Engines,
// die Heapaufrufe in libc.c und die serielle Ausgabe der WAMR-Plattform. Einfaches ++/-- in den Wrappern genuegt, weil nur
// der eine Wasm-Worker Gastcode ausfuehrt; libc.c zaehlt atomar, weil der Allokator auch anderen nativen Code bedient.
extern volatile int32_t zehos_host_depth;
volatile int32_t* zehos_host_depth_address(void);

uint32_t zehos_host_fd_write(zehos_host_call* call, int32_t fd, uint32_t iovs, uint32_t iovsCount, uint32_t written);
uint32_t zehos_host_args_sizes_get(zehos_host_call* call, uint32_t count, uint32_t bytes);
uint32_t zehos_host_environ_sizes_get(zehos_host_call* call, uint32_t count, uint32_t bytes);
uint32_t zehos_host_args_get(zehos_host_call* call, uint32_t pointers, uint32_t buffer);
uint32_t zehos_host_environ_get(zehos_host_call* call, uint32_t pointers, uint32_t buffer);
uint32_t zehos_host_clock_time_get(zehos_host_call* call, int32_t clockId, uint64_t precision, uint32_t time);
uint32_t zehos_host_clock_res_get(zehos_host_call* call, int32_t clockId, uint32_t resolution);
uint32_t zehos_host_random_get(zehos_host_call* call, uint32_t buffer, uint32_t length);
uint32_t zehos_host_fd_fdstat_get(zehos_host_call* call, int32_t fd, uint32_t stat);
uint32_t zehos_host_sched_yield(zehos_host_call* call);
uint32_t zehos_host_fd_prestat_get(zehos_host_call* call, int32_t fd, uint32_t prestat);
uint32_t zehos_host_fd_prestat_dir_name(zehos_host_call* call, int32_t fd, uint32_t path, uint32_t length);
uint32_t zehos_host_fd_close(zehos_host_call* call, int32_t fd);
uint32_t zehos_host_fd_read(zehos_host_call* call, int32_t fd, uint32_t iovs, uint32_t count, uint32_t readBytes);
uint32_t zehos_host_fd_seek(zehos_host_call* call, int32_t fd, int64_t offset, uint32_t whence, uint32_t newOffset);
uint32_t zehos_host_fd_tell(zehos_host_call* call, int32_t fd, uint32_t position);
uint32_t zehos_host_fd_filestat_get(zehos_host_call* call, int32_t fd, uint32_t stat);
uint32_t zehos_host_fd_pread(zehos_host_call* call, int32_t fd, uint32_t iovs, uint32_t count, uint64_t offset,
    uint32_t readBytes);
uint32_t zehos_host_path_open(zehos_host_call* call, int32_t dirFd, uint32_t lookupFlags, uint32_t path,
    uint32_t pathLength, uint32_t openFlags, uint64_t rightsBase, uint64_t rightsInheriting, uint32_t fdFlags,
    uint32_t openedFd);
uint32_t zehos_host_path_filestat_get(zehos_host_call* call, int32_t dirFd, uint32_t lookupFlags, uint32_t path,
    uint32_t pathLength, uint32_t stat);
uint32_t zehos_host_path_readlink(zehos_host_call* call, int32_t dirFd, uint32_t path, uint32_t pathLength,
    uint32_t buffer, uint32_t bufferLength, uint32_t used);
uint32_t zehos_host_fd_pwrite(zehos_host_call* call, int32_t fd, uint32_t iovs, uint32_t count, uint64_t offset,
    uint32_t written);
uint32_t zehos_host_fd_sync(zehos_host_call* call, int32_t fd);
uint32_t zehos_host_fd_filestat_set_size(zehos_host_call* call, int32_t fd, uint64_t size);
uint32_t zehos_host_fd_readdir(zehos_host_call* call, int32_t fd, uint32_t buffer, uint32_t length, uint64_t cookie,
    uint32_t used);
uint32_t zehos_host_path_create_directory(zehos_host_call* call, int32_t dirFd, uint32_t path, uint32_t pathLength);
uint32_t zehos_host_path_remove_directory(zehos_host_call* call, int32_t dirFd, uint32_t path, uint32_t pathLength);
uint32_t zehos_host_path_unlink_file(zehos_host_call* call, int32_t dirFd, uint32_t path, uint32_t pathLength);
uint32_t zehos_host_path_rename(zehos_host_call* call, int32_t fromFd, uint32_t fromPath, uint32_t fromLength,
    int32_t toFd, uint32_t toPath, uint32_t toLength);
uint32_t zehos_host_notcapable(zehos_host_call* call);
uint32_t zehos_host_badf(zehos_host_call* call);
uint32_t zehos_host_nosys(zehos_host_call* call);
void zehos_host_proc_exit(zehos_host_call* call, int32_t code);
void zehos_host_present(zehos_host_call* call, uint32_t pixels, int32_t width, int32_t height, int32_t tic, int32_t test);
void zehos_host_present_indexed(zehos_host_call* call, uint32_t indices, uint32_t palette, int32_t width,
    int32_t height, int32_t tic);
uint32_t zehos_host_ticks_ms(zehos_host_call* call);
void zehos_host_sleep_ms(zehos_host_call* call, uint32_t milliseconds);
int32_t zehos_host_poll_key(zehos_host_call* call);
int32_t zehos_host_poll_event(zehos_host_call* call, uint32_t offset);
int32_t zehos_host_pointer_capture(zehos_host_call* call, int32_t enabled);
int32_t zehos_host_should_exit(zehos_host_call* call);
int32_t zehos_host_net_open(zehos_host_call* call, uint32_t hostOffset, uint32_t length, int32_t port, int32_t flags,
    uint32_t outOffset);
int32_t zehos_host_net_status(zehos_host_call* call, int32_t handle);
int32_t zehos_host_net_send(zehos_host_call* call, int32_t handle, uint32_t dataOffset, uint32_t length,
    uint32_t outOffset);
int32_t zehos_host_net_recv(zehos_host_call* call, int32_t handle, uint32_t dataOffset, uint32_t length,
    uint32_t outOffset);
int32_t zehos_host_net_close(zehos_host_call* call, int32_t handle);
int32_t zehos_host_raster_draw(zehos_host_call* call, uint32_t description);
int32_t zehos_host_raster_fill(zehos_host_call* call, uint32_t offset, uint32_t count, uint32_t value);

#endif
