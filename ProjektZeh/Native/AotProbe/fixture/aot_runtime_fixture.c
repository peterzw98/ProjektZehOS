// ZehOS: Wasm-Quelle der AOT-Fixtures fuer Etappe D (nicht Teil des Kernelbuilds, siehe build-runtime.sh).
// Prueft die WAMR-Bindung der gemeinsamen Hostfunktionstabelle (zehos_host_wamr.c) ueber zehos_wasm_run_aot:
// WASI- und zehos_v1-Importe, Trap einer Hostfunktion, proc_exit, offene Datei nach Trap und die Speicherobergrenze.
// Ohne C-Bibliothek, damit jeder Import im Quelltext sichtbar ist.

typedef struct { const void* buffer; unsigned length; } ciovec;

#define WASI(name) __attribute__((import_module("wasi_snapshot_preview1"), import_name(#name)))
#define ZEHOS(name) __attribute__((import_module("zehos_v1"), import_name(#name)))

WASI(fd_write) int fd_write(int fd, const ciovec* iovs, int count, unsigned* written);
WASI(proc_exit) _Noreturn void proc_exit(int code);
WASI(clock_time_get) int clock_time_get(int clock, unsigned long long precision, unsigned long long* time);
WASI(args_sizes_get) int args_sizes_get(unsigned* count, unsigned* bytes);
WASI(random_get) int random_get(void* buffer, unsigned length);
WASI(path_open) int path_open(int dirFd, int lookupFlags, const char* path, unsigned pathLength, int openFlags,
    unsigned long long rightsBase, unsigned long long rightsInheriting, int fdFlags, int* openedFd);
ZEHOS(ticks_ms) unsigned ticks_ms(void);
ZEHOS(sleep_ms) void sleep_ms(int milliseconds);

static const char greeting[] = "AOT_HOST hallo\n";
// Legt der Probenlauf auf das Datenlaufwerk (scripts/test-wasm-loader.py --probe aot).
static const char probe_file[] = "aotprobe.txt";

// Ohne Wasm-Stack: Ergebnisse liegen in globalen Variablen (ein Trap setzt __stack_pointer nicht zurueck).
static unsigned written;
static unsigned long long now;
static unsigned argument_count, argument_bytes;
static unsigned char random_bytes[16];
static int opened;
static volatile unsigned probe_base = 0;

static void say(const char* text, unsigned length)
{
    static ciovec vector;
    vector.buffer = text;
    vector.length = length;
    fd_write(1, &vector, 1, &written);
}

// WASI-Einstieg: Ausgabe und Exitcode 7 ueber proc_exit.
__attribute__((export_name("_start"))) void start(void)
{
    say(greeting, sizeof(greeting) - 1);
    proc_exit(7);
}

// Je erfolgreichem Hostaufruf ein Bit; erwartet 31.
__attribute__((export_name("hostcalls"))) int hostcalls(void)
{
    int bits = 0;
    if (clock_time_get(1, 1, &now) == 0 && now > 0) bits |= 1;
    if (args_sizes_get(&argument_count, &argument_bytes) == 0 && argument_count == 1) bits |= 2;
    unsigned before = ticks_ms();
    sleep_ms(20);
    if (ticks_ms() - before >= 10) bits |= 4;
    // fd 9 ist nie offen: EBADF (8), kein Trap.
    static ciovec empty;
    if (fd_write(9, &empty, 1, &written) == 8) bits |= 8;
    // Ohne Hardwarezufall liefert der Kernel EIO (29) statt Nullen.
    int random = random_get(random_bytes, sizeof(random_bytes));
    if (random == 0 || random == 29) bits |= 16;
    return bits;
}

// Gastadresse ausserhalb des linearen Speichers: die Kernfunktion meldet einen Trap, die Bindung eine WAMR-Ausnahme.
__attribute__((export_name("host_oob"))) int host_oob(void)
{
    return fd_write(1, (const ciovec*)(probe_base + 0xfffffff0u), 1, &written);
}

// Waechst ueber 256 MiB: memory.grow muss -1 liefern, nicht trappen.
__attribute__((export_name("grow"))) int grow(void)
{
    return (int)__builtin_wasm_memory_grow(0, 4200);
}

// Offene Datei, danach Trap bzw. proc_exit; der Kernel schliesst sie in beiden Faellen (Gegenstueck zu opentrap/openexit
// der wasm3-Probe). 99 heisst: Datei nicht geoeffnet.
__attribute__((export_name("opentrap"))) int opentrap(void)
{
    if (path_open(3, 0, probe_file, sizeof(probe_file) - 1, 0, 0, 0, 0, &opened) == 0) __builtin_trap();
    return 99;
}

__attribute__((export_name("openexit"))) int openexit(void)
{
    if (path_open(3, 0, probe_file, sizeof(probe_file) - 1, 0, 0, 0, 0, &opened) == 0) proc_exit(7);
    return 99;
}
