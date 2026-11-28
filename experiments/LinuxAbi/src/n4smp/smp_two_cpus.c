/* N4-S2/Tor N4-Z: Nutzt ein pthread-Programm zwei CPUs? Zwei Threads rechnen dieselbe feste Arbeit gleichzeitig; das
 * Verhaeltnis Prozess-CPU-Zeit zu Wandzeit und getcpu beider Threads entscheiden. Ausgabe "two-cpus ok" (Verhaeltnis
 * > 1,6 und zwei verschiedene CPUs) oder "one-cpu"; die Zahlen selbst stehen nicht in der Ausgabe (sie schwanken). */
#define _GNU_SOURCE
#include "../s3/util.h"
#include <pthread.h>
#include <sched.h>
#include <stdatomic.h>
#include <sys/syscall.h>
#include <time.h>

static atomic_int go;
static unsigned cpus_of[2];
static volatile unsigned long sink[2];

static double now(clockid_t c)
{
    struct timespec ts;
    clock_gettime(c, &ts);
    return (double)ts.tv_sec + (double)ts.tv_nsec / 1e9;
}

static void *work(void *arg)
{
    long id = (long)arg;
    while (!atomic_load(&go)) { }
    unsigned long x = (unsigned long)id + 1;
    double end = now(CLOCK_MONOTONIC) + 2.0;
    unsigned cpu = 0, node = 0;
    while (now(CLOCK_MONOTONIC) < end) {
        for (int i = 0; i < 100000; i++) x = x * 6364136223846793005UL + 1442695040888963407UL;
        if (syscall(SYS_getcpu, &cpu, &node, NULL) == 0) cpus_of[id] |= 1u << (cpu & 31);
    }
    sink[id] = x;
    return NULL;
}

int main(void)
{
    INIT();
    pthread_t t[2];
    for (long i = 0; i < 2; i++) NEED(pthread_create(&t[i], NULL, work, (void *)i) == 0);
    double wall0 = now(CLOCK_MONOTONIC), cpu0 = now(CLOCK_PROCESS_CPUTIME_ID);
    atomic_store(&go, 1);
    for (int i = 0; i < 2; i++) pthread_join(t[i], NULL);
    double ratio = (now(CLOCK_PROCESS_CPUTIME_ID) - cpu0) / (now(CLOCK_MONOTONIC) - wall0);
    /* Zwei verschiedene CPUs: jeder Thread sah eine CPU, die der andere nie sah. */
    int distinct = (cpus_of[0] & ~cpus_of[1]) != 0 && (cpus_of[1] & ~cpus_of[0]) != 0;
    if (ratio > 1.6 && distinct) {
        printf("two-cpus ok\n");
    } else {
        /* Nur im Fehlerfall: die schwankenden Werte gehoeren nicht in die verglichene Ausgabe. */
        printf("one-cpu ratio=%.2f cpus0=%#x cpus1=%#x\n", ratio, cpus_of[0], cpus_of[1]);
    }
    return 0;
}
