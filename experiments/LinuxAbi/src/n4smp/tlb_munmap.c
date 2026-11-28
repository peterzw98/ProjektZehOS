/* N4-S2 TLB-Abschuss bei munmap: Thread A liest eine Seite in Schleife, B entfernt sie und schreibt eine Marke in
 * frisch abgebildete Seiten (die den freigewordenen Frame bekommen koennen). A muss mit SIGSEGV enden und darf die
 * Marke nie ueber die alte Adresse lesen: Das hiesse, sein TLB zeigte nach munmap noch auf den neu vergebenen Frame.
 * Auf einer CPU trivial erfuellt; aussagekraeftig erst, wenn A und B gleichzeitig auf zwei CPUs laufen (zehos.linuxsmp=1). */
#define _GNU_SOURCE
#include "../s3/util.h"
#include <pthread.h>
#include <setjmp.h>
#include <signal.h>
#include <stdatomic.h>
#include <string.h>
#include <sys/mman.h>

#define ROUNDS 300
#define FRESH 16
#define MARK 0x4d41524b4d41524bL
#define OLD 0x1111111111111111L

static _Atomic(long *) target;         /* Seite der laufenden Runde, NULL zwischen den Runden */
static atomic_int round_no, reads_in_round, faults, marks, stale_values, stop;
static __thread sigjmp_buf jump;
static __thread volatile int armed;

static void on_segv(int sig)
{
    (void)sig;
    if (armed) { armed = 0; siglongjmp(jump, 1); }
    _exit(4);
}

static void *reader(void *arg)
{
    (void)arg;
    int seen = 0;
    while (!atomic_load(&stop)) {
        int r = atomic_load(&round_no);
        if (r == seen || atomic_load(&target) == NULL) { continue; }
        long *p = atomic_load(&target);
        if (sigsetjmp(jump, 1) == 0) {
            armed = 1;
            for (;;) {
                long v = *(volatile long *)p;
                if (v == MARK) atomic_fetch_add(&marks, 1);
                else if (v != OLD) atomic_fetch_add(&stale_values, 1);
                atomic_fetch_add(&reads_in_round, 1);
            }
        }
        atomic_fetch_add(&faults, 1);
        seen = r;
    }
    return NULL;
}

int main(void)
{
    INIT();
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_segv;
    sa.sa_flags = SA_NODEFER;
    NEED(sigaction(SIGSEGV, &sa, NULL) == 0);
    pthread_t t;
    NEED(pthread_create(&t, NULL, reader, NULL) == 0);
    int timeouts = 0;
    for (int r = 1; r <= ROUNDS; r++) {
        long *p = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        NEED(p != MAP_FAILED);
        for (int i = 0; i < 512; i++) p[i] = OLD;
        atomic_store(&reads_in_round, 0);
        int before = atomic_load(&faults);
        atomic_store(&target, p);
        atomic_store(&round_no, r);
        while (atomic_load(&reads_in_round) < 1000) { }
        NEED(munmap(p, 4096) == 0);
        atomic_store(&target, NULL);
        /* Die Adresse sperren (PROT_NONE, ohne Frame), sonst gaebe das naechste mmap sie wieder her und A laese die Marke zu Recht. */
        NEED(mmap(p, 4096, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED, -1, 0) == p);
        /* Der freie Frame geht an eine der neuen Seiten; jede bekommt die Marke. */
        long *fresh = mmap(NULL, FRESH * 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        NEED(fresh != MAP_FAILED);
        for (int i = 0; i < FRESH * 512; i++) fresh[i] = MARK;
        long spins = 0;
        while (atomic_load(&faults) == before && spins < 2000000000L) spins++;
        if (atomic_load(&faults) == before) timeouts++;
        NEED(munmap(fresh, FRESH * 4096) == 0);
        NEED(munmap(p, 4096) == 0);
    }
    atomic_store(&stop, 1);
    pthread_join(t, NULL);
    SHOWI("rounds", ROUNDS);
    SHOW("every_round_faulted", atomic_load(&faults) == ROUNDS && timeouts == 0);
    SHOWI("mark_seen", atomic_load(&marks));
    SHOWI("other_values", atomic_load(&stale_values));
    return 0;
}
