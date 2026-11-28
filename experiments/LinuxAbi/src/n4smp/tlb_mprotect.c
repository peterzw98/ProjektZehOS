/* N4-S2 TLB-Abschuss bei mprotect: Thread A schreibt in Schleife einen Zaehler in eine Seite, B setzt PROT_READ und liest
 * danach zweimal mit Abstand. Beide Lesungen muessen gleich sein (kein Schreiben ueber einen veralteten TLB-Eintrag nach
 * der Rueckkehr von mprotect), und A muss mit SIGSEGV enden. Aussagekraeftig erst mit A und B auf zwei CPUs. */
#define _GNU_SOURCE
#include "../s3/util.h"
#include <pthread.h>
#include <setjmp.h>
#include <signal.h>
#include <stdatomic.h>
#include <string.h>
#include <sys/mman.h>

#define ROUNDS 300

static _Atomic(long *) target;
static atomic_int round_no, writes_in_round, faults, stop;
static __thread sigjmp_buf jump;
static __thread volatile int armed;

static void on_segv(int sig)
{
    (void)sig;
    if (armed) { armed = 0; siglongjmp(jump, 1); }
    _exit(4);
}

static void *writer(void *arg)
{
    (void)arg;
    int seen = 0;
    while (!atomic_load(&stop)) {
        int r = atomic_load(&round_no);
        if (r == seen || atomic_load(&target) == NULL) { continue; }
        long *p = atomic_load(&target);
        if (sigsetjmp(jump, 1) == 0) {
            armed = 1;
            for (long n = 1;; n++) {
                *(volatile long *)p = n;
                atomic_fetch_add(&writes_in_round, 1);
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
    NEED(pthread_create(&t, NULL, writer, NULL) == 0);
    int differ = 0, timeouts = 0;
    for (int r = 1; r <= ROUNDS; r++) {
        long *p = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        NEED(p != MAP_FAILED);
        p[0] = 0;
        atomic_store(&writes_in_round, 0);
        int before = atomic_load(&faults);
        atomic_store(&target, p);
        atomic_store(&round_no, r);
        while (atomic_load(&writes_in_round) < 1000) { }
        NEED(mprotect(p, 4096, PROT_READ) == 0);
        long first = *(volatile long *)p;
        for (volatile long i = 0; i < 200000; i++) { }
        long second = *(volatile long *)p;
        if (first != second) differ++;
        long spins = 0;
        while (atomic_load(&faults) == before && spins < 2000000000L) spins++;
        if (atomic_load(&faults) == before) timeouts++;
        atomic_store(&target, NULL);
        NEED(munmap(p, 4096) == 0);
    }
    atomic_store(&stop, 1);
    pthread_join(t, NULL);
    SHOWI("rounds", ROUNDS);
    SHOW("every_round_faulted", atomic_load(&faults) == ROUNDS && timeouts == 0);
    SHOWI("reads_differ", differ);
    return 0;
}
