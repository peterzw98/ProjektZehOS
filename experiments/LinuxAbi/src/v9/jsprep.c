/* V9 js-Vorbereitung: was musl und Ladybird (AK/StackInfo.cpp, AK/Rust/src/stack.rs) vor dem ersten Skript abfragen.
 * Ausgabe nur Ja/Nein-Zeilen, damit Linux und ZehOS dieselbe Referenz haben (Stackgrößen und CPU-Zahl sind rechnerabhängig). */
#define _GNU_SOURCE
#include <pthread.h>
#include <stdio.h>
#include <sys/mman.h>
#include <sys/resource.h>
#include <unistd.h>

int main(void)
{
    pthread_attr_t attr;
    void *addr = 0;
    size_t size = 0;
    int rc = pthread_getattr_np(pthread_self(), &attr);
    if (rc == 0)
        rc = pthread_attr_getstack(&attr, &addr, &size);
    char here;
    unsigned long sp = (unsigned long)&here, low = (unsigned long)addr;
    printf("getattr rc %d\n", rc);
    /* musl misst den Hauptstack mit mremap; ohne mremap wäre es eine Seite und die Rust-Stackgrenze läge über dem Stackzeiger. */
    printf("stack mehr als 64 KiB: %s\n", size > 64 * 1024 ? "ja" : "nein");
    printf("stackzeiger im stack: %s\n", sp > low && sp < low + size ? "ja" : "nein");

    struct rlimit rl = { 0, 0 };
    rc = getrlimit(RLIMIT_STACK, &rl);
    printf("getrlimit stack rc %d, cur mindestens 1 MiB: %s\n", rc, rc == 0 && rl.rlim_cur >= (1u << 20) ? "ja" : "nein");
    rc = getrlimit(RLIMIT_NOFILE, &rl);
    printf("getrlimit nofile rc %d, cur mindestens 64: %s\n", rc, rc == 0 && rl.rlim_cur >= 64 ? "ja" : "nein");

    long cpus = sysconf(_SC_NPROCESSORS_ONLN);
    printf("cpus mindestens 1: %s\n", cpus >= 1 ? "ja" : "nein");
    printf("setname rc %d\n", pthread_setname_np(pthread_self(), "jsprep"));

    /* mimalloc und musl-realloc: Verkleinern an Ort und Stelle; Vergrößern darf scheitern (dann kopieren sie). */
    char *m = mmap(0, 3 * 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    void *r = m == MAP_FAILED ? MAP_FAILED : mremap(m, 3 * 4096, 4096, 0);
    printf("mremap verkleinert: %s\n", r == m ? "ja" : "nein");

    /* Ladybird GC: große Reservierung PROT_NONE + MAP_NORESERVE (BlockAllocator 2 x 32 GiB, PrimitiveStorage 4 TiB + Rand). */
    void *big = mmap(0, 64ull << 30, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    printf("reservierung 64 GiB: %s\n", big != MAP_FAILED ? "ja" : "nein");
    void *cage = mmap(0, (4ull << 40) + (32ull << 30), PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    printf("reservierung 4 TiB: %s\n", cage != MAP_FAILED ? "ja" : "nein");
    int ok = big != MAP_FAILED && mprotect(big, 1 << 20, PROT_READ | PROT_WRITE) == 0;
    if (ok)
        ((volatile char *)big)[4096] = 1;
    printf("reservierung beschreibbar nach mprotect: %s\n", ok ? "ja" : "nein");
    return 0;
}
