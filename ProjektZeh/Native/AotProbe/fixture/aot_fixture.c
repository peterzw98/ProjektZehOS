// ZehOS: Wasm-Quelle der AOT-Fixture fuer Probe B3/B4 (nicht Teil des Kernelbuilds, siehe build.sh).
// Jede Funktion prueft einen Fall: Rueckgabewert, Zugriff ausserhalb des Speichers, tiefe Rekursion, Hostaufruf.

__attribute__((import_module("zehos"), import_name("host_add"))) int host_add(int left, int right);

__attribute__((export_name("ret42"))) int ret42(void) { return 42; }

// Die Fixture hat genau eine 64-KiB-Seite; die Adresse liegt weit dahinter.
__attribute__((export_name("oob"))) int oob(void)
{
    volatile int* outside = (volatile int*)0x7ffffff0;
    return *outside;
}

// noinline und volatile verhindern, dass der Compiler die Rekursion in eine Schleife umformt.
__attribute__((noinline)) static int descend(int depth)
{
    volatile int pad[32];
    pad[depth & 31] = depth;
    if (depth < 0) return 0;
    return descend(depth + 1) + pad[depth & 31];
}

// Ueberlauf des Wasm-Stacks in linearem Speicher: endet als Speicherzugriff ausserhalb.
__attribute__((export_name("recurse"))) int recurse(void) { return descend(1); }

// Ohne Speicher-Stack waechst nur der native Stack; endet nur dank --stack-bounds-checks als Trap.
__attribute__((noinline)) static int climb(int depth)
{
    if (depth < 0) return 0;
    return climb(depth + 1) * 3 + depth;
}

__attribute__((export_name("recurse_native"))) int recurse_native(void) { return climb(1); }

__attribute__((export_name("call_host"))) int call_host(void) { return host_add(40, 2); }

__attribute__((import_module("zehos"), import_name("sleep_ms"))) void sleep_ms(int milliseconds);

// B4: f64-Zwischenwerte ueberleben Threadwechsel; sleep_ms gibt den Prozessor an andere Threads ab.
__attribute__((export_name("fsleep"))) int fsleep(void)
{
    volatile double value = 1.1;
    for (int step = 0; step < 8; step++)
    {
        value = value * 1.5 + 0.25;
        sleep_ms(5);
    }
    return (int)(value * 1000.0);
}

// Etappe-E-Vorbereitung (Review huang P3a): weitere Zugriffe ausserhalb des linearen Speichers. Basis und Laengen kommen
// aus einer globalen Variable, damit der Compiler nichts faltet und kein Speicher-Stack beteiligt ist (ein Trap setzt
// __stack_pointer nicht zurueck).
static volatile unsigned int probe_base = 0;

static unsigned int memory_end(void) { return (unsigned int)__builtin_wasm_memory_size(0) * 65536u; }

// Berechnete Adresse knapp hinter dem Ende: der Zugriff beginnt im Speicher und endet ausserhalb.
__attribute__((export_name("oob_computed"))) int oob_computed(void)
{
    return *(volatile int*)(probe_base + memory_end() - 2);
}

// i32.load mit statischem Offset 16 auf Adresse 0xfffffff8: addr+offset laeuft ueber 2^32 und darf nicht umbrechen.
// Inline-Assembler, weil clang base[4] als umbrechende i32.add-Adresse (= 8, im Speicher) uebersetzt.
__attribute__((export_name("oob_offset_wrap"))) int oob_offset_wrap(void)
{
    unsigned int address = probe_base + 0xfffffff8u;
    int value;
    __asm__ volatile("local.get %1\n\ti32.load 16\n\tlocal.set %0" : "=r"(value) : "r"(address));
    return value;
}

// memory.fill und memory.copy, die genau bis zum Ende reichen, muessen gelingen; ein Byte mehr muss abbrechen.
__attribute__((export_name("fill_edge_ok"))) int fill_edge_ok(void)
{
    __builtin_memset((void*)(probe_base + memory_end() - 16), 7, 16);
    return *(volatile unsigned char*)(probe_base + memory_end() - 1);
}

__attribute__((export_name("fill_edge_oob"))) int fill_edge_oob(void)
{
    __builtin_memset((void*)(probe_base + memory_end() - 16), 7, 17);
    return 0;
}

__attribute__((export_name("copy_edge_oob"))) int copy_edge_oob(void)
{
    __builtin_memcpy((void*)(probe_base + memory_end() - 8), (const void*)(probe_base + 1024), 16);
    return 0;
}
