// Fuzz-Harness fuer die Hostschicht (Roadmap 12.4, Paket I2-F): bindet zehos_host_core.c, ersetzt die Rueckrufe des
// C#-Kernels durch Attrappen und ruft jede Funktion aus ZEHOS_HOST_FUNCTIONS mit Zufallsargumenten auf einen 64-KiB-
// Gastspeicher, der zwischen zwei Seiten ohne Zugriffsrecht liegt. Jeder Zugriff ausserhalb des Speichers endet als SIGSEGV.
//
// Aufruf: host-fuzz [Aufrufe je Funktion] [Saat] [Funktion|-] [Nummer|-1] [--guard-selbsttest]
// Jeder Aufruf (Funktion, Nummer) leitet seinen Zufall allein aus Saat, Funktionsindex und Nummer ab und beginnt mit
// frischem Kontext; ein Fund laesst sich deshalb mit Funktionsname und Nummer einzeln wiederholen.
#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>
#include "zehos_host_core.h"

#if !(defined(__x86_64__) || defined(__aarch64__))
#error "host-fuzz ruft die Kernfunktionen ueber einen einheitlichen Zeigertyp mit 64-Bit-Argumenten auf (x86-64/aarch64 SysV)"
#endif

enum { PAGE = 4096, GUEST_SIZE = 65536, DEFAULT_SEED_LOW = 0x5EED5EED, FUNCTION_LIMIT = 128 };

// ---------------------------------------------------------------------------------------------------------------
// Zufall: xorshift64 mit fester Saat; splitmix64 mischt Saat, Funktion und Nummer zu einem Startwert je Aufruf.
static uint64_t rng_state;
static uint64_t next_random(void)
{
    uint64_t x = rng_state;
    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
    return rng_state = x;
}
static uint64_t mix(uint64_t z)
{
    z += 0x9E3779B97F4A7C15ull;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
    return z ^ (z >> 31);
}
static uint32_t below(uint32_t limit) { return limit == 0 ? 0 : (uint32_t)(next_random() % limit); }

// ---------------------------------------------------------------------------------------------------------------
// Attrappen der Kernel-Rueckrufe. Sie tun, was der echte Kernel tut, soweit es Speicher betrifft: Eingaben werden
// ganz gelesen, Ausgaben ganz geschrieben. Ueberschreitet der Kern einen Bereich, trifft das hier die Schutzseite.
static volatile uint8_t sink;
static void read_all(const void* data, uint32_t length)
{
    const volatile uint8_t* bytes = data;
    for (uint32_t i = 0; i < length; i++) sink += bytes[i];
}
static void fill_all(void* data, uint32_t length) { memset(data, 0xA5, length); }

// Verhalten je Aufruf (aus dem Zufall des Aufrufs): ob der Speicher Fehler meldet und wie viele Aufrufe der Gast noch schlaeft.
static int fail_mode, save_enabled, should_exit_after, exit_calls, next_handle;
static uint64_t sim_ns;

void zehos_wasm_host_write(int32_t fd, const uint8_t* data, uint32_t length) { (void)fd; read_all(data, length); }
uint64_t zehos_wasm_host_time_ns(int32_t clockId) { (void)clockId; return sim_ns; }
int32_t zehos_wasm_host_random(uint8_t* buffer, uint32_t length) { fill_all(buffer, length); return fail_mode == 3 ? 29 : 0; }
int32_t zehos_wasm_host_open(const uint8_t* path, uint32_t length)
{
    read_all(path, length);
    return fail_mode == 1 ? -1 : 1 + (next_handle++ & 7);
}
int64_t zehos_wasm_host_file_size(int32_t handle) { return handle < 0 ? -1 : 4096; }
int32_t zehos_wasm_host_file_read(int32_t handle, uint64_t offset, uint8_t* buffer, uint32_t length)
{
    if (handle < 0 || offset > 4096) return -1;
    uint64_t available = 4096 - offset;
    uint32_t count = length < available ? length : (uint32_t)available;
    fill_all(buffer, count);
    return (int32_t)count;
}
void zehos_wasm_host_close(int32_t handle) { (void)handle; }
int32_t zehos_wasm_host_stat(const uint8_t* path, uint32_t length, uint64_t* size, int32_t* type, uint64_t* modifiedNs)
{
    read_all(path, length);
    if (fail_mode == 1) return 44;
    *size = 4096; *type = (int32_t)(length & 1); *modifiedNs = 0;
    return 0;
}
int32_t zehos_wasm_host_fstat(int32_t handle, uint64_t* modifiedNs) { *modifiedNs = 12345; return handle < 0 ? 8 : 0; }
uint64_t zehos_wasm_host_clock_res_ns(int32_t clockId) { (void)clockId; return 1000; }

int32_t zehos_wasm_host_save_available(void) { return save_enabled; }
int32_t zehos_wasm_host_save_open(const uint8_t* path, uint32_t length, uint32_t flags, int32_t* handle)
{
    (void)flags; read_all(path, length);
    if (fail_mode == 1) return 44;
    if (fail_mode == 2) return 33;
    *handle = 100 + (next_handle++ & 7);
    return 0;
}
int32_t zehos_wasm_host_save_read(int32_t handle, uint64_t offset, uint8_t* buffer, uint32_t length, uint32_t* read)
{
    (void)handle;
    uint32_t count = offset >= 4096 ? 0 : (length < 4096 - offset ? length : (uint32_t)(4096 - offset));
    fill_all(buffer, count); *read = count;
    return fail_mode == 1 ? 29 : 0;
}
int32_t zehos_wasm_host_save_write(int32_t handle, uint64_t offset, const uint8_t* data, uint32_t length)
{
    (void)handle; (void)offset; read_all(data, length);
    return fail_mode == 1 ? 29 : 0;
}
int64_t zehos_wasm_host_save_size(int32_t handle) { return handle < 0 ? -1 : 4096; }
int32_t zehos_wasm_host_save_set_size(int32_t handle, uint64_t size) { (void)handle; (void)size; return fail_mode == 1 ? 29 : 0; }
int32_t zehos_wasm_host_save_sync(int32_t handle) { (void)handle; return 0; }
int32_t zehos_wasm_host_save_close(int32_t handle) { (void)handle; return 0; }
int32_t zehos_wasm_host_save_stat(const uint8_t* path, uint32_t length, uint64_t* size, int32_t* type, uint64_t* modifiedNs)
{
    return zehos_wasm_host_stat(path, length, size, type, modifiedNs);
}
int32_t zehos_wasm_host_save_fstat(int32_t handle, uint64_t* modifiedNs) { return zehos_wasm_host_fstat(handle, modifiedNs); }
int32_t zehos_wasm_host_save_path(int32_t operation, const uint8_t* path, uint32_t length)
{
    (void)operation; read_all(path, length);
    return fail_mode == 1 ? 44 : 0;
}
int32_t zehos_wasm_host_save_rename(const uint8_t* from, uint32_t fromLength, const uint8_t* to, uint32_t toLength)
{
    read_all(from, fromLength); read_all(to, toLength);
    return fail_mode == 1 ? 44 : 0;
}
int32_t zehos_wasm_host_save_readdir(int32_t handle, uint8_t* buffer, uint32_t length, uint64_t cookie, uint32_t* used)
{
    (void)handle; (void)cookie;
    fill_all(buffer, length); *used = length;
    return fail_mode == 1 ? 8 : 0;
}
int32_t zehos_wasm_host_package_opendir(int32_t dirHandle, const uint8_t* path, uint32_t length, int32_t* handle)
{
    (void)dirHandle; read_all(path, length);
    if (fail_mode == 1) return 44;
    *handle = 200 + (next_handle++ & 7);
    return 0;
}
int32_t zehos_wasm_host_package_readdir(int32_t handle, uint8_t* buffer, uint32_t length, uint64_t cookie, uint32_t* used)
{
    return zehos_wasm_host_save_readdir(handle, buffer, length, cookie, used);
}
void zehos_wasm_host_package_closedir(int32_t handle) { (void)handle; }
int32_t zehos_wasm_host_package_openat(int32_t dirHandle, const uint8_t* path, uint32_t length, int32_t* handle, uint64_t* size)
{
    (void)dirHandle; read_all(path, length);
    if (fail_mode == 1) return 44;
    *handle = 300 + (next_handle++ & 7); *size = 4096;
    return 0;
}
int32_t zehos_wasm_host_package_statat(int32_t dirHandle, const uint8_t* path, uint32_t length, uint8_t* type, uint64_t* size)
{
    (void)dirHandle; read_all(path, length);
    if (fail_mode == 1) return 44;
    *type = (uint8_t)(length & 1); *size = 4096;
    return 0;
}
// Bild: der Kernel liest width*height Pixel; die Attrappe liest sie ganz, soweit die Groesse in den Speicher passt.
void zehos_wasm_host_present(const uint32_t* pixels, int32_t width, int32_t height, int32_t tic, int32_t test)
{
    (void)tic; (void)test;
    if (width > 0 && height > 0 && (uint64_t)width * (uint64_t)height * 4 <= GUEST_SIZE) read_all(pixels, (uint32_t)width * (uint32_t)height * 4);
}
void zehos_wasm_host_present_indexed(const uint8_t* indices, const uint32_t* palette, int32_t width, int32_t height, int32_t tic)
{
    (void)tic;
    if (width > 0 && height > 0 && (uint64_t)width * (uint64_t)height <= GUEST_SIZE) read_all(indices, (uint32_t)width * (uint32_t)height);
    read_all(palette, 256 * 4);
}
uint32_t zehos_wasm_host_ticks_ms(void) { return (uint32_t)(sim_ns / 1000000); }
void zehos_wasm_host_sleep_ms(uint32_t milliseconds) { sim_ns += (uint64_t)milliseconds * 1000000ull; }
int32_t zehos_wasm_host_poll_key(void) { return -1; }
int32_t zehos_wasm_host_poll_event(int32_t* event) { event[0] = 3; event[1] = 65; event[2] = -17; event[3] = 29; return fail_mode == 1 ? 0 : 1; }
int32_t zehos_wasm_host_pointer_capture(int32_t enabled) { (void)enabled; return -1; }
int32_t zehos_wasm_host_should_exit(void) { return ++exit_calls >= should_exit_after; }
int32_t zehos_wasm_host_net_open(const uint8_t* host, uint32_t length, int32_t port, int32_t flags, int32_t* handle)
{
    (void)port; (void)flags; read_all(host, length);
    if (fail_mode == 1) return -1;
    *handle = 1 + (next_handle++ & 7);
    return 0;
}
int32_t zehos_wasm_host_net_status(int32_t handle) { return handle < 0 ? -1 : 1; }
int32_t zehos_wasm_host_net_send(int32_t handle, const uint8_t* data, uint32_t length, int32_t* count)
{
    (void)handle; read_all(data, length); *count = (int32_t)length;
    return 0;
}
int32_t zehos_wasm_host_net_recv(int32_t handle, uint8_t* data, uint32_t length, int32_t* count)
{
    (void)handle; fill_all(data, length); *count = (int32_t)length;
    return 0;
}
int32_t zehos_wasm_host_net_close(int32_t handle) { (void)handle; return 0; }
// Der Rasterizer ist C# und prueft selbst (Grenze des Tests); die Attrappe fasst den Speicher nicht an.
int32_t zehos_wasm_host_raster_draw(uint8_t* memory, uint32_t length, uint32_t call) { (void)memory; (void)length; (void)call; return 0; }
int32_t zehos_wasm_host_raster_fill(uint8_t* memory, uint32_t length, uint32_t offset, uint32_t count, uint32_t value)
{
    (void)memory; (void)length; (void)offset; (void)count; (void)value;
    return 0;
}

// ---------------------------------------------------------------------------------------------------------------
// Funktionstabelle aus der einzigen Importliste.
typedef uint64_t (*generic_function)(zehos_host_call*, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t, uint64_t,
    uint64_t, uint64_t, uint64_t);
typedef struct { const char* name; const char* module; generic_function function; int count; const char* signature; char result; } entry;
#define FUZZ_ENTRY(module, name, result, signature, function, count) \
    { #name, #module, (generic_function)(void*)function, count, signature, #result[0] },
static const entry functions[] = { ZEHOS_HOST_FUNCTIONS(FUZZ_ENTRY) };
enum { FUNCTION_COUNT = sizeof(functions) / sizeof(functions[0]) };

// ---------------------------------------------------------------------------------------------------------------
// Gastspeicher mit Schutzseiten und aktueller Aufruf (fuer die Fehlerausgabe im Signalhandler).
static uint8_t* region;
static uint8_t* guest_base; // je nach Aufruf Seitenanfang oder buendig vor der oberen Schutzseite
static size_t guest_size;

static uint64_t g_seed;
static const entry* current;
static long current_number;
static uint64_t current_args[9];
static int current_variant;

static void report_state(const char* what)
{
    char line[512];
    int length = snprintf(line, sizeof line,
        "FUZZ-FUND: %s Funktion=%s Nummer=%ld Saat=0x%llX Variante=%d Speicher=%zu Argumente=%llX,%llX,%llX,%llX,%llX,%llX,%llX,%llX,%llX\n",
        what, current ? current->name : "-", current_number, (unsigned long long)g_seed, current_variant, guest_size,
        (unsigned long long)current_args[0], (unsigned long long)current_args[1], (unsigned long long)current_args[2],
        (unsigned long long)current_args[3], (unsigned long long)current_args[4], (unsigned long long)current_args[5],
        (unsigned long long)current_args[6], (unsigned long long)current_args[7], (unsigned long long)current_args[8]);
    if (length > 0) (void)!write(2, line, (size_t)length);
}
static void on_signal(int signal_number)
{
    report_state(signal_number == SIGALRM ? "Haenger (SIGALRM)" : signal_number == SIGSEGV ? "Zugriff ausserhalb (SIGSEGV)" :
        signal_number == SIGBUS ? "Zugriff ausserhalb (SIGBUS)" : "Abbruch");
    _exit(signal_number == SIGALRM ? 3 : 2);
}

// ---------------------------------------------------------------------------------------------------------------
// Argumente: Randwerte der Speichergroesse, Ueberlaufpaare und Werte, die tief in die Funktionen fuehren.
static uint32_t edge32(void)
{
    const uint32_t size = (uint32_t)guest_size;
    switch (below(14))
    {
        case 0: return 0;
        case 1: return 1;
        case 2: return size - 1;
        case 3: return size;
        case 4: return size + 1;
        case 5: return 0x7FFFFFFFu;
        case 6: return 0x80000000u;
        case 7: return 0xFFFFFFFFu;
        case 8: return 0xFFFFFFFEu;
        case 9: return 0x10000u;
        case 10: return size - 1 - below(64);
        case 11: return below(40);
        case 12: return (uint32_t)next_random();
        default: return below(size);
    }
}
static uint64_t edge64(void)
{
    switch (below(10))
    {
        case 0: return 0;
        case 1: return 1;
        case 2: return UINT64_MAX;
        case 3: return INT64_MAX;
        case 4: return (uint64_t)INT64_MIN;
        case 5: return guest_size;
        case 6: return 0x100000000ull;
        case 7: return below(8192);
        case 8: return edge32();
        default: return next_random();
    }
}

static void make_arguments(const entry* function, uint64_t* args)
{
    const int mode = (int)below(4); // 0 Randwerte, 1 meist gueltig, 2 Ueberlaufpaare, 3 Mischung
    const char* signature = function->signature;
    uint64_t previous_pointer = 0;
    int has_pointer = 0;
    for (int index = 0; index < function->count; index++)
    {
        char kind = signature[index];
        uint64_t value;
        if (mode == 1 && kind != 'I')
        {
            // Gueltige Zeiger (ausgerichtet) und kleine Laengen/Handles, damit die Funktionen tief laufen.
            value = kind == '*' ? (uint64_t)below((uint32_t)guest_size - 512) & ~3ull : (uint64_t)(index == 0 ? 3 + below(8) : below(48));
        }
        else if (mode == 2 && kind == 'i' && has_pointer)
        {
            // Laenge relativ zum letzten Zeiger: Summe knapp an Speichergroesse oder 2^32.
            uint32_t sums[4] = { (uint32_t)guest_size, 0u, 0x80000000u, 0xFFFFFFFFu };
            value = (uint32_t)(sums[below(4)] - (uint32_t)previous_pointer + (uint32_t)((int32_t)below(5) - 2));
        }
        else value = kind == 'I' ? edge64() : edge32();
        if (kind != 'I') value &= 0xFFFFFFFFu;
        if (kind == '*') { previous_pointer = value; has_pointer = 1; }
        args[index] = value;
    }
}

// Gueltige und halbgueltige Dateien im Kontext: Variante 0 leer, 1 vier Plaetze, 2 alle Plaetze belegt.
static void prepare_context(zehos_wasm_context* context, int variant)
{
    zehos_host_context_init(context);
    if (variant == 0) return;
    int occupied = variant == 1 ? 4 : ZEHOS_HOST_MAXIMUM_FILES;
    for (int index = 0; index < occupied; index++)
    {
        zehos_wasm_file* file = &context->files[index];
        file->handle = 10 + index;
        file->position = below(8192);
        file->size = 4096;
        file->kind = (uint8_t)(index & 3);
        file->writable = (uint8_t)(file->kind == ZEHOS_FILE_SAVE);
        file->append = (uint8_t)(below(4) == 0);
        file->inode = next_random();
    }
}

static void fill_guest_memory(void)
{
    // Mischung aus Pfadzeichen und Zufall am Anfang und Ende (dort liegen Pfade, Abos und Puffer); der Rest bleibt stehen.
    static const uint8_t alphabet[] = { 0, 1, 2, 3, 4, 8, '.', '/', 'a', 'b', 'c', '_', 0xFF, 0x80 };
    size_t part = guest_size < 256 ? guest_size : 256;
    for (size_t index = 0; index < part; index++)
    {
        uint8_t first = below(3) ? alphabet[below(sizeof alphabet)] : (uint8_t)next_random();
        uint8_t last = below(3) ? alphabet[below(sizeof alphabet)] : (uint8_t)next_random();
        guest_base[index] = first;
        guest_base[guest_size - 1 - index] = last;
    }
}

// Ergebnisse eines Laufs
static unsigned long long total_calls, total_continue, total_memory, total_exit;

static int run_call(int function_index, long number, int verbose)
{
    const entry* function = &functions[function_index];
    rng_state = mix(g_seed ^ mix((uint64_t)function_index * 0x10001u + (uint64_t)number * 0x1000003ull + 1)) | 1;
    current = function; current_number = number;

    current_variant = (int)below(3);
    // Jeder vierte Aufruf: ungerade Groesse buendig vor der oberen Schutzseite (Ueberlauf um wenige Bytes faellt auf).
    int unaligned = below(4) == 0;
    guest_size = unaligned ? GUEST_SIZE - 1 - below(PAGE - 1) : GUEST_SIZE;
    guest_base = region + PAGE + GUEST_SIZE - guest_size;
    fail_mode = (int)below(5); save_enabled = (int)below(3) != 0; should_exit_after = 1 + (int)below(200);
    exit_calls = 0; next_handle = 0; sim_ns = 1000000000000ull;

    zehos_wasm_context context;
    prepare_context(&context, current_variant);
    fill_guest_memory();
    uint64_t args[9] = { 0 };
    make_arguments(function, args);
    memcpy(current_args, args, sizeof args);

    zehos_host_call call = { &context, guest_base, guest_size, ZEHOS_HOST_CONTINUE };
    alarm(10);
    uint64_t result = function->function(&call, args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]);
    alarm(0);
    zehos_host_context_close_files(&context);

    if (verbose) fprintf(stderr, "%s #%ld -> trap=%d ergebnis=%u\n", function->name, number, (int)call.trap, (unsigned)result);
    total_calls++;
    if (call.trap == ZEHOS_HOST_CONTINUE) total_continue++;
    else if (call.trap == ZEHOS_HOST_TRAP_MEMORY) total_memory++;
    else if (call.trap == ZEHOS_HOST_TRAP_EXIT) total_exit++;
    else { report_state("unbekannter Trap"); return 1; }
    // WASI-Funktionen melden nur Fehlernummern bis ENOTCAPABLE (76); alles andere ist ein zerstoerter Rueckgabewert.
    if (call.trap == ZEHOS_HOST_CONTINUE && strcmp(function->module, "wasi_snapshot_preview1") == 0 && function->result == 'i' &&
        (uint32_t)result > 76) { report_state("unzulaessige WASI-Fehlernummer"); return 1; }
    return 0;
}

int main(int argc, char** argv)
{
    long calls = argc > 1 ? strtol(argv[1], NULL, 0) : 10000;
    g_seed = argc > 2 ? strtoull(argv[2], NULL, 0) : DEFAULT_SEED_LOW;
    const char* only_function = argc > 3 && strcmp(argv[3], "-") != 0 ? argv[3] : NULL;
    long only_number = argc > 4 ? strtol(argv[4], NULL, 0) : -1;
    int guard_selftest = argc > 5 && strcmp(argv[5], "--guard-selbsttest") == 0;

    region = mmap(NULL, GUEST_SIZE + 2 * PAGE, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    if (region == MAP_FAILED || mprotect(region, PAGE, PROT_NONE) != 0 || mprotect(region + PAGE + GUEST_SIZE, PAGE, PROT_NONE) != 0)
    {
        perror("mmap/mprotect");
        return 4;
    }
    struct sigaction action;
    memset(&action, 0, sizeof action);
    action.sa_handler = on_signal;
    sigemptyset(&action.sa_mask);
    int signals[] = { SIGSEGV, SIGBUS, SIGABRT, SIGFPE, SIGILL, SIGALRM };
    for (size_t index = 0; index < sizeof signals / sizeof signals[0]; index++) sigaction(signals[index], &action, NULL);

    if (guard_selftest)
    {
        // Beweist, dass die Schutzseite jeden Zugriff knapp hinter dem Speicher meldet (Pruefung der Pruefung).
        guest_size = GUEST_SIZE; guest_base = region + PAGE;
        current = &functions[0]; current_number = -1;
        sink = guest_base[GUEST_SIZE];
        puts("FUZZ-FEHLER: Schutzseite hat nicht angeschlagen");
        return 1;
    }

    int matched = 0;
    for (int index = 0; index < FUNCTION_COUNT; index++)
    {
        const entry* function = &functions[index];
        if (only_function && strcmp(only_function, function->name) != 0) continue;
        matched++;
        // Die Signatur muss zur Argumentzahl passen, sonst liefe die Zeigerzuordnung falsch (Stubs mit 0 Argumenten ausgenommen).
        if (function->count > 9 || (function->count != 0 && (int)strlen(function->signature) != function->count))
        {
            fprintf(stderr, "FUZZ-FEHLER: Signatur %s passt nicht zu %d Argumenten (%s)\n", function->signature, function->count, function->name);
            return 1;
        }
        unsigned long long before = total_calls;
        for (long number = 0; number < calls; number++)
        {
            if (only_number >= 0 && number != only_number) continue;
            if (run_call(index, number, only_number >= 0)) return 1;
        }
        printf("FUNKTION %s aufrufe=%llu\n", function->name, total_calls - before);
    }
    if (matched == 0) { fprintf(stderr, "FUZZ-FEHLER: Funktion '%s' unbekannt\n", only_function); return 1; }
    printf("FUZZ-OK funktionen=%d aufrufe=%llu fortgesetzt=%llu trap_speicher=%llu trap_exit=%llu saat=0x%llX\n", matched,
        total_calls, total_continue, total_memory, total_exit, (unsigned long long)g_seed);
    return 0;
}
