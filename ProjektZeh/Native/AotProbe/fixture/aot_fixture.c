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
