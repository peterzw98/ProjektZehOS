// ZehOS: WASI- und zehos_v1-Dienste als gewoehnliche C-Funktionen, getrennt von der Laufzeitbindung.
// Ueber die Grenze zum C#-Kernel laufen nur primitive Typen und bereits gepruefte Zeiger.
// WASI-Dienste, die ZehOS noch nicht anbietet, melden ENOSYS bzw. EBADF statt den Kernel zu gefaehrden.

#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include "zehos_host_core.h"

volatile int32_t zehos_host_depth;

// Der Kernel liest den Zaehler im Ausnahmepfad ohne Aufruf: die Adresse holt er einmal beim Start des Workers.
volatile int32_t* zehos_host_depth_address(void) { return &zehos_host_depth; }

// Exporte des C#-Kernels (UnmanagedCallersOnly).
extern void zehos_wasm_host_write(int32_t fd, const uint8_t* data, uint32_t length);
extern uint64_t zehos_wasm_host_time_ns(int32_t clockId);
extern int32_t zehos_wasm_host_random(uint8_t* buffer, uint32_t length);
// Nur lesender Zugriff unterhalb des freigegebenen App-Ordners; der Pfad ist bereits relativ und geprueft.
extern int32_t zehos_wasm_host_open(const uint8_t* path, uint32_t length);
extern int64_t zehos_wasm_host_file_size(int32_t handle);
extern int32_t zehos_wasm_host_file_read(int32_t handle, uint64_t offset, uint8_t* buffer, uint32_t length);
extern void zehos_wasm_host_close(int32_t handle);
extern int32_t zehos_wasm_host_stat(const uint8_t* path, uint32_t length, uint64_t* size, int32_t* type);
// Etappe 13.4: App-Speicher /save. Rueckgaben sind WASI-Fehlernummern; der C#-Teil prueft Pfade, Quote und Sperren.
extern int32_t zehos_wasm_host_save_available(void);
extern int32_t zehos_wasm_host_save_open(const uint8_t* path, uint32_t length, uint32_t flags, int32_t* handle);
extern int32_t zehos_wasm_host_save_read(int32_t handle, uint64_t offset, uint8_t* buffer, uint32_t length, uint32_t* read);
extern int32_t zehos_wasm_host_save_write(int32_t handle, uint64_t offset, const uint8_t* data, uint32_t length);
extern int64_t zehos_wasm_host_save_size(int32_t handle);
extern int32_t zehos_wasm_host_save_set_size(int32_t handle, uint64_t size);
extern int32_t zehos_wasm_host_save_sync(int32_t handle);
extern int32_t zehos_wasm_host_save_close(int32_t handle);
extern int32_t zehos_wasm_host_save_stat(const uint8_t* path, uint32_t length, uint64_t* size, int32_t* type);
extern int32_t zehos_wasm_host_save_path(int32_t operation, const uint8_t* path, uint32_t length);
extern int32_t zehos_wasm_host_save_rename(const uint8_t* from, uint32_t fromLength, const uint8_t* to, uint32_t toLength);
extern int32_t zehos_wasm_host_save_readdir(int32_t handle, uint8_t* buffer, uint32_t length, uint64_t cookie, uint32_t* used);
// zehos_v1: Bild, Zeit und Eingabe fuer Anwendungen mit eigener Darstellung.
extern void zehos_wasm_host_present(const uint32_t* pixels, int32_t width, int32_t height, int32_t tic, int32_t test);
extern void zehos_wasm_host_present_indexed(const uint8_t* indices, const uint32_t* palette, int32_t width, int32_t height, int32_t tic);
extern uint32_t zehos_wasm_host_ticks_ms(void);
extern void zehos_wasm_host_sleep_ms(uint32_t milliseconds);
extern int32_t zehos_wasm_host_poll_key(void);
extern int32_t zehos_wasm_host_poll_event(int32_t* event);
extern int32_t zehos_wasm_host_pointer_capture(int32_t enabled);
extern int32_t zehos_wasm_host_should_exit(void);
extern int32_t zehos_wasm_host_net_open(const uint8_t* host, uint32_t length, int32_t port, int32_t flags, int32_t* handle);
extern int32_t zehos_wasm_host_net_status(int32_t handle);
extern int32_t zehos_wasm_host_net_send(int32_t handle, const uint8_t* data, uint32_t length, int32_t* count);
extern int32_t zehos_wasm_host_net_recv(int32_t handle, uint8_t* data, uint32_t length, int32_t* count);
extern int32_t zehos_wasm_host_net_close(int32_t handle);
// Rasterizer (C#): erhaelt den ganzen linearen Speicher und prueft jeden Bereich selbst; -1 heisst Bereichsfehler.
extern int32_t zehos_wasm_host_raster_draw(uint8_t* memory, uint32_t length, uint32_t call);
extern int32_t zehos_wasm_host_raster_fill(uint8_t* memory, uint32_t length, uint32_t offset, uint32_t count, uint32_t value);

enum
{
    WASI_SUCCESS = 0, WASI_EACCES = 2, WASI_EBADF = 8, WASI_EINVAL = 28, WASI_EIO = 29,
    WASI_EMFILE = 33, WASI_ENOENT = 44, WASI_ENOSYS = 52, WASI_ENOTCAPABLE = 76
};

// fd 3 ist das Paket ("/", nur lesend), fd 4 der App-Speicher ("/save", nur mit Freigabe); geoeffnete Dateien belegen
// ab fd 5 feste Plaetze.
enum { PREOPEN_FD = 3, SAVE_FD = 4, FIRST_FILE_FD = 5, MAXIMUM_FILES = ZEHOS_HOST_MAXIMUM_FILES, MAXIMUM_PATH = 255 };
// Flags fuer zehos_wasm_host_save_open
enum { SAVE_CREATE = 1, SAVE_DIRECTORY = 2, SAVE_EXCLUSIVE = 4, SAVE_TRUNCATE = 8, SAVE_WRITE = 16 };
// Operationen fuer zehos_wasm_host_save_path
enum { SAVE_MKDIR = 1, SAVE_RMDIR = 2, SAVE_UNLINK = 3 };

static int save_open(void) { return zehos_wasm_host_save_available() != 0; }

void zehos_host_context_init(zehos_wasm_context* context)
{
    memset(context, 0, sizeof(*context));
    for (int index = 0; index < MAXIMUM_FILES; index++) context->files[index].handle = -1;
}

// Offen gebliebene Dateien gehoeren dem Lauf und werden auch bei Traps geschlossen.
void zehos_host_context_close_files(zehos_wasm_context* context)
{
    for (int index = 0; index < MAXIMUM_FILES; index++)
    {
        zehos_wasm_file* file = &context->files[index];
        if (file->handle < 0) continue;
        if (file->kind == ZEHOS_FILE_PACKAGE) zehos_wasm_host_close(file->handle);
        else (void)zehos_wasm_host_save_close(file->handle);
        file->handle = -1;
    }
}

// Aktuelle Groesse: Paketdateien aendern sich nicht, Dateien im App-Speicher wachsen beim Schreiben.
static uint64_t file_size(const zehos_wasm_file* file)
{
    if (file->kind != ZEHOS_FILE_SAVE) return file->size;
    int64_t size = zehos_wasm_host_save_size(file->handle);
    return size < 0 ? 0 : (uint64_t)size;
}

static zehos_wasm_file* file_of(zehos_wasm_context* context, int32_t fd)
{
    if (fd < FIRST_FILE_FD || fd >= FIRST_FILE_FD + MAXIMUM_FILES) return NULL;
    zehos_wasm_file* file = &context->files[fd - FIRST_FILE_FD];
    return file->handle >= 0 ? file : NULL;
}

// [offset, offset + length) liegt im linearen Speicher; als Differenz gerechnet, damit nichts ueberlaeuft.
static int in_memory(const zehos_host_call* call, uint32_t offset, uint64_t length)
{
    return offset <= call->memorySize && length <= (uint64_t)(call->memorySize - offset);
}

// Wie in_memory, meldet aber einen Speicher-Trap; der Aufrufer kehrt danach sofort zurueck.
static int guest_range(zehos_host_call* call, uint32_t offset, uint64_t length)
{
    if (in_memory(call, offset, length)) return 1;
    call->trap = ZEHOS_HOST_TRAP_MEMORY;
    return 0;
}

// Schreibt einen iovec-Vektor ab offset in eine beschreibbare Datei des App-Speichers; gemeinsam fuer fd_write und
// fd_pwrite. Ungueltige Vektoren melden EINVAL statt eines Traps, wie read_vectors.
static uint32_t write_vectors(zehos_host_call* call, zehos_wasm_file* file, uint32_t iovs, uint32_t count, uint64_t offset,
    uint32_t* total)
{
    *total = 0;
    if (count > 1024) return WASI_EINVAL;
    if (!in_memory(call, iovs, (uint64_t)count * 8)) return WASI_EINVAL;
    const uint8_t* vector = call->memory + iovs;
    for (uint32_t index = 0; index < count; index++)
    {
        uint32_t source, length;
        memcpy(&source, vector + index * 8, 4);
        memcpy(&length, vector + index * 8 + 4, 4);
        if (!in_memory(call, source, length)) return WASI_EINVAL;
        if (length == 0) continue;
        int32_t error = zehos_wasm_host_save_write(file->handle, offset, call->memory + source, length);
        if (error != WASI_SUCCESS) return (uint32_t)error;
        offset += length;
        *total += length;
    }
    return WASI_SUCCESS;
}

uint32_t zehos_host_fd_write(zehos_host_call* call, int32_t fd, uint32_t iovs, uint32_t iovsCount, uint32_t written)
{
    if (!guest_range(call, written, sizeof(uint32_t))) return 0;
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file != NULL)
    {
        // Rechte je fd: Paketdateien und Ordner bleiben lesend.
        if (file->kind != ZEHOS_FILE_SAVE || !file->writable) return WASI_EBADF;
        if (file->append) file->position = file_size(file);
        uint32_t total;
        uint32_t error = write_vectors(call, file, iovs, iovsCount, file->position, &total);
        file->position += total;
        memcpy(call->memory + written, &total, 4);
        return error;
    }
    if (fd != 1 && fd != 2) return WASI_EBADF;
    // Jede Adresse und Laenge aus dem Gast wird gegen den linearen Speicher geprueft.
    if (iovsCount > 1024) return WASI_EINVAL;
    if (!guest_range(call, iovs, (uint64_t)iovsCount * 8)) return 0;
    const uint8_t* vector = call->memory + iovs;
    uint32_t total = 0;
    for (uint32_t index = 0; index < iovsCount; index++)
    {
        uint32_t offset, length;
        memcpy(&offset, vector + index * 8, 4);
        memcpy(&length, vector + index * 8 + 4, 4);
        if (!guest_range(call, offset, length)) return 0;
        zehos_wasm_host_write(fd, call->memory + offset, length);
        total += length;
    }
    memcpy(call->memory + written, &total, 4);
    return WASI_SUCCESS;
}

void zehos_host_proc_exit(zehos_host_call* call, int32_t code)
{
    call->context->exitCode = code;
    call->trap = ZEHOS_HOST_TRAP_EXIT;
}

// Laufzeiten wie Mono setzen argv[0] voraus; weitere Argumente gibt es noch nicht.
static const char program_name[] = "app";
// Die Umgebung meldet optionale zehos_v1-Faehigkeiten. Aeltere Kernel liefern eine leere Umgebung; Apps rufen neue
// Imports nur, wenn sie hier genannt sind (wasm3 uebersetzt eine Funktion erst bei ihrem ersten Aufruf).
static const char environment_entry[] = "ZEHOS_FEATURES=raster_v1";

// Gemeinsamer Teil von args_sizes_get und environ_sizes_get: genau ein Eintrag der Groesse size.
static uint32_t single_entry_sizes(zehos_host_call* call, uint32_t count, uint32_t bytes, uint32_t size)
{
    if (!guest_range(call, count, 4)) return 0;
    if (!guest_range(call, bytes, 4)) return 0;
    uint32_t one = 1;
    memcpy(call->memory + count, &one, 4);
    memcpy(call->memory + bytes, &size, 4);
    return WASI_SUCCESS;
}

// Gemeinsamer Teil von args_get und environ_get: Zeigerliste mit einem Eintrag und den Text dahinter.
static uint32_t single_entry_get(zehos_host_call* call, uint32_t pointers, uint32_t buffer, const char* text, uint32_t size)
{
    if (!guest_range(call, pointers, 4)) return 0;
    if (!guest_range(call, buffer, size)) return 0;
    memcpy(call->memory + pointers, &buffer, 4);
    memcpy(call->memory + buffer, text, size);
    return WASI_SUCCESS;
}

uint32_t zehos_host_args_sizes_get(zehos_host_call* call, uint32_t count, uint32_t bytes)
{
    return single_entry_sizes(call, count, bytes, sizeof(program_name));
}

uint32_t zehos_host_args_get(zehos_host_call* call, uint32_t pointers, uint32_t buffer)
{
    return single_entry_get(call, pointers, buffer, program_name, sizeof(program_name));
}

uint32_t zehos_host_environ_sizes_get(zehos_host_call* call, uint32_t count, uint32_t bytes)
{
    return single_entry_sizes(call, count, bytes, sizeof(environment_entry));
}

uint32_t zehos_host_environ_get(zehos_host_call* call, uint32_t pointers, uint32_t buffer)
{
    return single_entry_get(call, pointers, buffer, environment_entry, sizeof(environment_entry));
}

uint32_t zehos_host_clock_time_get(zehos_host_call* call, int32_t clockId, uint64_t precision, uint32_t time)
{
    (void)precision;
    if (!guest_range(call, time, 8)) return 0;
    if (clockId < 0 || clockId > 3) return WASI_EINVAL;
    uint64_t now = zehos_wasm_host_time_ns(clockId);
    memcpy(call->memory + time, &now, 8);
    return WASI_SUCCESS;
}

uint32_t zehos_host_clock_res_get(zehos_host_call* call, int32_t clockId, uint32_t resolution)
{
    (void)clockId;
    if (!guest_range(call, resolution, 8)) return 0;
    uint64_t value = 1000000;
    memcpy(call->memory + resolution, &value, 8);
    return WASI_SUCCESS;
}

uint32_t zehos_host_random_get(zehos_host_call* call, uint32_t buffer, uint32_t length)
{
    if (!guest_range(call, buffer, length)) return 0;
    // Ohne Hardwarezufall meldet der Host EIO; der Gast darf den Puffer dann nicht verwenden.
    return (uint32_t)zehos_wasm_host_random(call->memory + buffer, length);
}

// Standardkanaele sind Zeichengeraete, fd 3 der freigegebene Ordner, ab fd 4 regulaere Dateien.
uint32_t zehos_host_fd_fdstat_get(zehos_host_call* call, int32_t fd, uint32_t stat)
{
    if (!guest_range(call, stat, 24)) return 0;
    uint8_t type;
    int writable = 0;
    uint16_t flags = 0;
    zehos_wasm_file* file = file_of(call->context, fd);
    if (fd >= 0 && fd <= 2) type = 2;
    else if (fd == PREOPEN_FD) type = 3;
    else if (fd == SAVE_FD && save_open()) { type = 3; writable = 1; }
    else if (file != NULL)
    {
        type = file->kind == ZEHOS_FILE_SAVE_DIRECTORY ? 3 : 4;
        writable = file->writable || file->kind == ZEHOS_FILE_SAVE_DIRECTORY;
        if (file->append) flags = 1;
    }
    else return WASI_EBADF;
    uint8_t* target = call->memory + stat;
    memset(target, 0, 24);
    target[0] = type;
    memcpy(target + 2, &flags, 2);
    // Rechte nur zum Lesen, Suchen und Abfragen; wasi-libc prueft sie vor eigenen Aufrufen. Der App-Speicher meldet
    // zusaetzlich alle Schreib- und Ordnerrechte (0..29), die tatsaechliche Pruefung erfolgt je Aufruf.
    uint64_t rights = writable ? (1ull << 30) - 1 :
        (1ull << 1) | (1ull << 2) | (1ull << 5) | (1ull << 13) | (1ull << 15) | (1ull << 18) | (1ull << 21);
    memcpy(target + 8, &rights, 8);
    memcpy(target + 16, &rights, 8);
    return WASI_SUCCESS;
}

static const char save_name[] = "/save";

uint32_t zehos_host_fd_prestat_get(zehos_host_call* call, int32_t fd, uint32_t prestat)
{
    if (!guest_range(call, prestat, 8)) return 0;
    // Ohne Freigabe endet die Aufzaehlung der WASI-libc bei fd 4 mit EBADF; die App kennt dann nur "/".
    if (fd == SAVE_FD && save_open())
    {
        uint32_t saveLength = sizeof(save_name) - 1;
        memset(call->memory + prestat, 0, 8);
        memcpy(call->memory + prestat + 4, &saveLength, 4);
        return WASI_SUCCESS;
    }
    if (fd != PREOPEN_FD) return WASI_EBADF;
    uint32_t nameLength = 1;
    memset(call->memory + prestat, 0, 8);
    memcpy(call->memory + prestat + 4, &nameLength, 4);
    return WASI_SUCCESS;
}

uint32_t zehos_host_fd_prestat_dir_name(zehos_host_call* call, int32_t fd, uint32_t path, uint32_t length)
{
    if (fd == SAVE_FD && save_open())
    {
        if (length < sizeof(save_name) - 1) return WASI_EINVAL;
        if (!guest_range(call, path, sizeof(save_name) - 1)) return 0;
        memcpy(call->memory + path, save_name, sizeof(save_name) - 1);
        return WASI_SUCCESS;
    }
    if (fd != PREOPEN_FD) return WASI_EBADF;
    if (length < 1) return WASI_EINVAL;
    if (!guest_range(call, path, 1)) return 0;
    call->memory[path] = '/';
    return WASI_SUCCESS;
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

uint32_t zehos_host_path_filestat_get(zehos_host_call* call, int32_t dirFd, uint32_t lookupFlags, uint32_t path,
    uint32_t pathLength, uint32_t stat)
{
    if (!guest_range(call, stat, 64)) return 0;
    if (!guest_range(call, path, pathLength)) return 0;
    int save = dirFd == SAVE_FD && save_open();
    if (dirFd != PREOPEN_FD && !save) return WASI_EBADF;
    if ((lookupFlags & ~1u) != 0) return WASI_EINVAL;
    const uint8_t* name = call->memory + path;
    if (!is_safe_path(name, pathLength)) return WASI_EACCES;
    uint64_t size = 0;
    int32_t type = 0;
    int32_t error = save ? zehos_wasm_host_save_stat(name, pathLength, &size, &type) : zehos_wasm_host_stat(name, pathLength, &size, &type);
    if (error != WASI_SUCCESS) return (uint32_t)error;
    uint8_t* target = call->memory + stat;
    memset(target, 0, 64);
    target[16] = (uint8_t)type;
    uint64_t links = 1;
    memcpy(target + 24, &links, 8);
    memcpy(target + 32, &size, 8);
    return WASI_SUCCESS;
}

uint32_t zehos_host_path_readlink(zehos_host_call* call, int32_t dirFd, uint32_t path, uint32_t pathLength,
    uint32_t buffer, uint32_t bufferLength, uint32_t used)
{
    if (!guest_range(call, path, pathLength)) return 0;
    if (!guest_range(call, buffer, bufferLength)) return 0;
    if (!guest_range(call, used, 4)) return 0;
    int save = dirFd == SAVE_FD && save_open();
    if (dirFd != PREOPEN_FD && !save) return WASI_EBADF;
    const uint8_t* name = call->memory + path;
    if (!is_safe_path(name, pathLength)) return WASI_EACCES;
    uint64_t size = 0;
    int32_t type = 0;
    int32_t error = save ? zehos_wasm_host_save_stat(name, pathLength, &size, &type) : zehos_wasm_host_stat(name, pathLength, &size, &type);
    // Pakete enthalten keine Symlinks. realpath der WASI-libc erwartet fuer bestehende Nicht-Links EINVAL.
    return error == WASI_SUCCESS ? WASI_EINVAL : (uint32_t)error;
}

static int32_t free_slot(zehos_wasm_context* context)
{
    for (int32_t index = 0; index < MAXIMUM_FILES; index++)
        if (context->files[index].handle < 0) return index;
    return -1;
}

// App-Speicher: O_CREAT, O_DIRECTORY, O_EXCL, O_TRUNC; Schreibabsicht aus fd_write/fd_filestat_set_size-Rechten oder
// O_TRUNC/O_CREAT. Die Pruefung von Pfad, Quote und Sperren uebernimmt der C#-Teil.
static uint32_t save_path_open(zehos_host_call* call, const uint8_t* name, uint32_t pathLength, uint32_t openFlags,
    uint64_t rightsBase, uint32_t fdFlags, uint32_t openedFd)
{
    if ((openFlags & ~0xFu) != 0) return WASI_EINVAL;
    if (!is_safe_path(name, pathLength)) return WASI_EACCES;
    const uint64_t writeRights = (1ull << 6) | (1ull << 22);
    uint32_t flags = 0;
    if (openFlags & 1) flags |= SAVE_CREATE;
    if (openFlags & 2) flags |= SAVE_DIRECTORY;
    if (openFlags & 4) flags |= SAVE_EXCLUSIVE;
    if (openFlags & 8) flags |= SAVE_TRUNCATE;
    if ((rightsBase & writeRights) != 0 && (openFlags & 2) == 0) flags |= SAVE_WRITE;
    int32_t slot = free_slot(call->context);
    if (slot < 0) return WASI_EMFILE;
    int32_t handle = -1;
    int32_t error = zehos_wasm_host_save_open(name, pathLength, flags, &handle);
    if (error != WASI_SUCCESS) return (uint32_t)error;
    zehos_wasm_file* file = &call->context->files[slot];
    file->handle = handle;
    file->position = 0;
    file->size = 0;
    file->kind = (openFlags & 2) ? ZEHOS_FILE_SAVE_DIRECTORY : ZEHOS_FILE_SAVE;
    // Schreiben nur mit fd_write- bzw. fd_filestat_set_size-Recht; O_CREAT/O_TRUNC allein machen den fd nicht beschreibbar.
    file->writable = (flags & SAVE_WRITE) != 0;
    file->append = (fdFlags & 1) != 0;
    uint32_t fd = (uint32_t)(FIRST_FILE_FD + slot);
    memcpy(call->memory + openedFd, &fd, 4);
    return WASI_SUCCESS;
}

uint32_t zehos_host_path_open(zehos_host_call* call, int32_t dirFd, uint32_t lookupFlags, uint32_t path,
    uint32_t pathLength, uint32_t openFlags, uint64_t rightsBase, uint64_t rightsInheriting, uint32_t fdFlags,
    uint32_t openedFd)
{
    (void)lookupFlags; (void)rightsInheriting;
    if (!guest_range(call, openedFd, 4)) return 0;
    if (!guest_range(call, path, pathLength)) return 0;
    if (dirFd == SAVE_FD && save_open())
        return save_path_open(call, call->memory + path, pathLength, openFlags, rightsBase, fdFlags, openedFd);
    if (dirFd != PREOPEN_FD) return WASI_EBADF;
    // Anlegen, Leeren, Ordner und jede Schreib- oder Anhaengeabsicht sind in diesem Profil gesperrt.
    const uint64_t writeRights = (1ull << 4) | (1ull << 6) | (1ull << 7) | (1ull << 9) | (1ull << 10) | (1ull << 22);
    if ((openFlags & 0xF) != 0 || (rightsBase & writeRights) != 0 || (fdFlags & 0x1F & ~0x4) != 0) return WASI_ENOTCAPABLE;
    const uint8_t* name = call->memory + path;
    if (!is_safe_path(name, pathLength)) return WASI_EACCES;
    zehos_wasm_context* context = call->context;
    int32_t slot = free_slot(context);
    if (slot < 0) return WASI_EMFILE;
    int32_t handle = zehos_wasm_host_open(name, pathLength);
    if (handle < 0) return WASI_ENOENT;
    int64_t size = zehos_wasm_host_file_size(handle);
    if (size < 0) { zehos_wasm_host_close(handle); return WASI_EIO; }
    context->files[slot].handle = handle;
    context->files[slot].position = 0;
    context->files[slot].size = (uint64_t)size;
    context->files[slot].kind = ZEHOS_FILE_PACKAGE;
    context->files[slot].writable = 0;
    context->files[slot].append = 0;
    uint32_t fd = (uint32_t)(FIRST_FILE_FD + slot);
    memcpy(call->memory + openedFd, &fd, 4);
    return WASI_SUCCESS;
}

// Liest ueber einen iovec-Vektor ab offset; gemeinsamer Teil von fd_read und fd_pread.
// Ungueltige Vektoren melden EINVAL statt eines Traps.
static uint32_t read_vectors(zehos_host_call* call, zehos_wasm_file* file, uint32_t iovs, uint32_t count,
    uint64_t offset, uint32_t* total)
{
    *total = 0;
    if (count > 1024) return WASI_EINVAL;
    if (!in_memory(call, iovs, (uint64_t)count * 8)) return WASI_EINVAL;
    const uint8_t* vector = call->memory + iovs;
    for (uint32_t index = 0; index < count; index++)
    {
        uint32_t target, length;
        memcpy(&target, vector + index * 8, 4);
        memcpy(&length, vector + index * 8 + 4, 4);
        if (!in_memory(call, target, length)) return WASI_EINVAL;
        uint64_t size = file_size(file);
        if (offset >= size || length == 0) continue;
        uint64_t available = size - offset;
        uint32_t wanted = available < length ? (uint32_t)available : length;
        int32_t read;
        if (file->kind == ZEHOS_FILE_SAVE)
        {
            uint32_t count = 0;
            int32_t error = zehos_wasm_host_save_read(file->handle, offset, call->memory + target, wanted, &count);
            if (error != WASI_SUCCESS) return (uint32_t)error;
            read = (int32_t)count;
        }
        else read = zehos_wasm_host_file_read(file->handle, offset, call->memory + target, wanted);
        if (read < 0) return WASI_EIO;
        offset += (uint32_t)read;
        *total += (uint32_t)read;
        if ((uint32_t)read < wanted) break;
    }
    return WASI_SUCCESS;
}

uint32_t zehos_host_fd_read(zehos_host_call* call, int32_t fd, uint32_t iovs, uint32_t count, uint32_t readBytes)
{
    if (!guest_range(call, readBytes, 4)) return 0;
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file == NULL || file->kind == ZEHOS_FILE_SAVE_DIRECTORY) return WASI_EBADF;
    uint32_t total;
    uint32_t error = read_vectors(call, file, iovs, count, file->position, &total);
    if (error == WASI_SUCCESS) file->position += total;
    memcpy(call->memory + readBytes, &total, 4);
    return error;
}

uint32_t zehos_host_fd_pread(zehos_host_call* call, int32_t fd, uint32_t iovs, uint32_t count, uint64_t offset,
    uint32_t readBytes)
{
    if (!guest_range(call, readBytes, 4)) return 0;
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file == NULL || file->kind == ZEHOS_FILE_SAVE_DIRECTORY) return WASI_EBADF;
    uint32_t total;
    uint32_t error = read_vectors(call, file, iovs, count, offset, &total);
    memcpy(call->memory + readBytes, &total, 4);
    return error;
}

uint32_t zehos_host_fd_seek(zehos_host_call* call, int32_t fd, int64_t offset, uint32_t whence, uint32_t newOffset)
{
    if (!guest_range(call, newOffset, 8)) return 0;
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file == NULL) return WASI_EBADF;
    int64_t base;
    if (whence == 0) base = 0;
    else if (whence == 1) base = (int64_t)file->position;
    else if (whence == 2) base = (int64_t)file_size(file);
    else return WASI_EINVAL;
    if ((offset > 0 && base > INT64_MAX - offset) || base + offset < 0) return WASI_EINVAL;
    file->position = (uint64_t)(base + offset);
    memcpy(call->memory + newOffset, &file->position, 8);
    return WASI_SUCCESS;
}

uint32_t zehos_host_fd_tell(zehos_host_call* call, int32_t fd, uint32_t position)
{
    if (!guest_range(call, position, 8)) return 0;
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file == NULL) return WASI_EBADF;
    memcpy(call->memory + position, &file->position, 8);
    return WASI_SUCCESS;
}

uint32_t zehos_host_fd_filestat_get(zehos_host_call* call, int32_t fd, uint32_t stat)
{
    if (!guest_range(call, stat, 64)) return 0;
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file == NULL && fd != PREOPEN_FD && !(fd == SAVE_FD && save_open())) return WASI_EBADF;
    uint8_t* target = call->memory + stat;
    memset(target, 0, 64);
    target[16] = file != NULL && file->kind != ZEHOS_FILE_SAVE_DIRECTORY ? 4 : 3;
    if (file != NULL && file->kind != ZEHOS_FILE_SAVE_DIRECTORY)
    {
        uint64_t size = file_size(file);
        memcpy(target + 32, &size, 8);
    }
    return WASI_SUCCESS;
}

uint32_t zehos_host_fd_close(zehos_host_call* call, int32_t fd)
{
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file == NULL) return WASI_EBADF;
    // Im App-Speicher schreibt Close den Inhalt; ein Fehler (ENOSPC, EBUSY ...) wird gemeldet, der fd ist trotzdem frei.
    int32_t error = WASI_SUCCESS;
    if (file->kind == ZEHOS_FILE_PACKAGE) zehos_wasm_host_close(file->handle);
    else error = zehos_wasm_host_save_close(file->handle);
    file->handle = -1;
    return (uint32_t)error;
}

// zehos_v1: Bildpuffer 0x00RRGGBB zeilenweise; Groesse und Speicherbereich werden vor dem Zugriff geprueft.
void zehos_host_present(zehos_host_call* call, uint32_t pixels, int32_t width, int32_t height, int32_t tic, int32_t test)
{
    if (width <= 0 || height <= 0 || width > 1920 || height > 1200) { call->trap = ZEHOS_HOST_TRAP_MEMORY; return; }
    if (!guest_range(call, pixels, (uint64_t)width * (uint64_t)height * 4)) return;
    zehos_wasm_host_present((const uint32_t*)(call->memory + pixels), width, height, tic, test);
}

// Rasterizer: Beschreibung und alle Puffer liegen im Gastspeicher; die Pruefung uebernimmt der C#-Kern.
int32_t zehos_host_raster_draw(zehos_host_call* call, uint32_t description)
{
    if (call->memorySize > UINT32_MAX) { call->trap = ZEHOS_HOST_TRAP_MEMORY; return 0; }
    int32_t status = zehos_wasm_host_raster_draw(call->memory, (uint32_t)call->memorySize, description);
    if (status < 0) call->trap = ZEHOS_HOST_TRAP_MEMORY;
    return status;
}

int32_t zehos_host_raster_fill(zehos_host_call* call, uint32_t offset, uint32_t count, uint32_t value)
{
    if (call->memorySize > UINT32_MAX) { call->trap = ZEHOS_HOST_TRAP_MEMORY; return 0; }
    int32_t status = zehos_wasm_host_raster_fill(call->memory, (uint32_t)call->memorySize, offset, count, value);
    if (status < 0) call->trap = ZEHOS_HOST_TRAP_MEMORY;
    return status;
}

void zehos_host_present_indexed(zehos_host_call* call, uint32_t indices, uint32_t palette, int32_t width,
    int32_t height, int32_t tic)
{
    if (width <= 0 || height <= 0 || width > 1920 || height > 1200) { call->trap = ZEHOS_HOST_TRAP_MEMORY; return; }
    if (!guest_range(call, indices, (uint64_t)width * (uint64_t)height)) return;
    if (!guest_range(call, palette, 256 * 4)) return;
    zehos_wasm_host_present_indexed(call->memory + indices, (const uint32_t*)(call->memory + palette), width, height, tic);
}

uint32_t zehos_host_ticks_ms(zehos_host_call* call)
{
    (void)call;
    return zehos_wasm_host_ticks_ms();
}

void zehos_host_sleep_ms(zehos_host_call* call, uint32_t milliseconds)
{
    (void)call;
    zehos_wasm_host_sleep_ms(milliseconds > 1000 ? 1000 : milliseconds);
}

int32_t zehos_host_poll_key(zehos_host_call* call)
{
    (void)call;
    return zehos_wasm_host_poll_key();
}

int32_t zehos_host_pointer_capture(zehos_host_call* call, int32_t enabled)
{
    (void)call;
    return zehos_wasm_host_pointer_capture(enabled);
}

int32_t zehos_host_poll_event(zehos_host_call* call, uint32_t offset)
{
    // Vor der Entnahme pruefen; lokale Felder erlauben auch nicht ausgerichtete Gastadressen.
    if (!guest_range(call, offset, 16)) return 0;
    int32_t event[4] = {0};
    int32_t status = zehos_wasm_host_poll_event(event);
    if (status == 1) memcpy(call->memory + offset, event, sizeof(event));
    return status;
}

int32_t zehos_host_should_exit(zehos_host_call* call)
{
    (void)call;
    return zehos_wasm_host_should_exit();
}

// Ausgabewerte lokal halten: Gastadressen koennen unausgerichtet oder mit dem Eingabepuffer ueberlappend sein.
int32_t zehos_host_net_open(zehos_host_call* call, uint32_t hostOffset, uint32_t length, int32_t port, int32_t flags,
    uint32_t outOffset)
{
    if (!guest_range(call, hostOffset, length)) return 0;
    if (!guest_range(call, outOffset, 4)) return 0;
    int32_t handle = 0;
    int32_t status = zehos_wasm_host_net_open(call->memory + hostOffset, length, port, flags, &handle);
    if (status == WASI_SUCCESS) memcpy(call->memory + outOffset, &handle, 4);
    return status;
}

int32_t zehos_host_net_status(zehos_host_call* call, int32_t handle)
{
    (void)call;
    return zehos_wasm_host_net_status(handle);
}

int32_t zehos_host_net_send(zehos_host_call* call, int32_t handle, uint32_t dataOffset, uint32_t length,
    uint32_t outOffset)
{
    if (!guest_range(call, dataOffset, length)) return 0;
    if (!guest_range(call, outOffset, 4)) return 0;
    int32_t count = 0;
    int32_t status = zehos_wasm_host_net_send(handle, call->memory + dataOffset, length, &count);
    if (status == WASI_SUCCESS) memcpy(call->memory + outOffset, &count, 4);
    return status;
}

int32_t zehos_host_net_recv(zehos_host_call* call, int32_t handle, uint32_t dataOffset, uint32_t length,
    uint32_t outOffset)
{
    if (!guest_range(call, dataOffset, length)) return 0;
    if (!guest_range(call, outOffset, 4)) return 0;
    int32_t count = 0;
    int32_t status = zehos_wasm_host_net_recv(handle, call->memory + dataOffset, length, &count);
    if (status == WASI_SUCCESS) memcpy(call->memory + outOffset, &count, 4);
    return status;
}

int32_t zehos_host_net_close(zehos_host_call* call, int32_t handle)
{
    (void)call;
    return zehos_wasm_host_net_close(handle);
}

uint32_t zehos_host_fd_pwrite(zehos_host_call* call, int32_t fd, uint32_t iovs, uint32_t count, uint64_t offset,
    uint32_t written)
{
    if (!guest_range(call, written, 4)) return 0;
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file == NULL || file->kind != ZEHOS_FILE_SAVE || !file->writable) return WASI_EBADF;
    uint32_t total;
    uint32_t error = write_vectors(call, file, iovs, count, offset, &total);
    memcpy(call->memory + written, &total, 4);
    return error;
}

uint32_t zehos_host_fd_sync(zehos_host_call* call, int32_t fd)
{
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file == NULL) return WASI_EBADF;
    if (file->kind != ZEHOS_FILE_SAVE) return WASI_SUCCESS;
    return (uint32_t)zehos_wasm_host_save_sync(file->handle);
}

uint32_t zehos_host_fd_filestat_set_size(zehos_host_call* call, int32_t fd, uint64_t size)
{
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file == NULL || file->kind != ZEHOS_FILE_SAVE || !file->writable) return WASI_EBADF;
    return (uint32_t)zehos_wasm_host_save_set_size(file->handle, size);
}

uint32_t zehos_host_fd_readdir(zehos_host_call* call, int32_t fd, uint32_t buffer, uint32_t length, uint64_t cookie,
    uint32_t used)
{
    if (!guest_range(call, used, 4)) return 0;
    if (!guest_range(call, buffer, length)) return 0;
    zehos_wasm_file* file = file_of(call->context, fd);
    if (file == NULL || file->kind != ZEHOS_FILE_SAVE_DIRECTORY) return WASI_EBADF;
    uint32_t count = 0;
    int32_t error = zehos_wasm_host_save_readdir(file->handle, call->memory + buffer, length, cookie, &count);
    if (error != WASI_SUCCESS) return (uint32_t)error;
    if (count > length) return WASI_EIO;
    memcpy(call->memory + used, &count, 4);
    return WASI_SUCCESS;
}

// Gemeinsamer Teil der Pfadoperationen: nur unter /save, das Paket bleibt unveraenderlich.
static uint32_t save_path(zehos_host_call* call, int32_t dirFd, uint32_t path, uint32_t pathLength, int32_t operation)
{
    if (!guest_range(call, path, pathLength)) return 0;
    if (dirFd == PREOPEN_FD) return WASI_ENOTCAPABLE;
    if (dirFd != SAVE_FD || !save_open()) return WASI_EBADF;
    const uint8_t* name = call->memory + path;
    if (!is_safe_path(name, pathLength)) return WASI_EACCES;
    return (uint32_t)zehos_wasm_host_save_path(operation, name, pathLength);
}

uint32_t zehos_host_path_create_directory(zehos_host_call* call, int32_t dirFd, uint32_t path, uint32_t pathLength)
{
    return save_path(call, dirFd, path, pathLength, SAVE_MKDIR);
}

uint32_t zehos_host_path_remove_directory(zehos_host_call* call, int32_t dirFd, uint32_t path, uint32_t pathLength)
{
    return save_path(call, dirFd, path, pathLength, SAVE_RMDIR);
}

uint32_t zehos_host_path_unlink_file(zehos_host_call* call, int32_t dirFd, uint32_t path, uint32_t pathLength)
{
    return save_path(call, dirFd, path, pathLength, SAVE_UNLINK);
}

// Quelle und Ziel muessen beide unter /save liegen.
uint32_t zehos_host_path_rename(zehos_host_call* call, int32_t fromFd, uint32_t fromPath, uint32_t fromLength,
    int32_t toFd, uint32_t toPath, uint32_t toLength)
{
    if (!guest_range(call, fromPath, fromLength)) return 0;
    if (!guest_range(call, toPath, toLength)) return 0;
    if (fromFd == PREOPEN_FD || toFd == PREOPEN_FD) return WASI_ENOTCAPABLE;
    if (fromFd != SAVE_FD || toFd != SAVE_FD || !save_open()) return WASI_EBADF;
    const uint8_t* from = call->memory + fromPath;
    const uint8_t* to = call->memory + toPath;
    if (!is_safe_path(from, fromLength) || !is_safe_path(to, toLength)) return WASI_EACCES;
    return (uint32_t)zehos_wasm_host_save_rename(from, fromLength, to, toLength);
}

uint32_t zehos_host_notcapable(zehos_host_call* call)
{
    (void)call;
    return WASI_ENOTCAPABLE;
}

uint32_t zehos_host_badf(zehos_host_call* call)
{
    (void)call;
    return WASI_EBADF;
}

uint32_t zehos_host_nosys(zehos_host_call* call)
{
    (void)call;
    return WASI_ENOSYS;
}

uint32_t zehos_host_sched_yield(zehos_host_call* call)
{
    (void)call;
    return WASI_SUCCESS;
}
