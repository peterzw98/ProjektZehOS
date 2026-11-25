/* futex: WAIT/WAKE (privat und geteilt), EAGAIN bei anderem Wert, Zeitüberschreitung relativ
 * und absolut (WAIT_BITSET), Bitmasken, REQUEUE, Aufwecken mehrerer Wartender. */
#define _GNU_SOURCE
#include "util.h"
#include <pthread.h>
#include <sched.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <time.h>

/* Konstanten aus linux/futex.h (musl-gcc bringt keine Kernel-Header mit). */
#define FUTEX_WAIT 0
#define FUTEX_WAKE 1
#define FUTEX_REQUEUE 3
#define FUTEX_CMP_REQUEUE 4
#define FUTEX_WAIT_BITSET 9
#define FUTEX_WAKE_BITSET 10
#define FUTEX_PRIVATE_FLAG 128
#define FUTEX_WAIT_PRIVATE (FUTEX_WAIT | FUTEX_PRIVATE_FLAG)
#define FUTEX_WAKE_PRIVATE (FUTEX_WAKE | FUTEX_PRIVATE_FLAG)
#define FUTEX_REQUEUE_PRIVATE (FUTEX_REQUEUE | FUTEX_PRIVATE_FLAG)
#define FUTEX_CMP_REQUEUE_PRIVATE (FUTEX_CMP_REQUEUE | FUTEX_PRIVATE_FLAG)
#define FUTEX_WAIT_BITSET_PRIVATE (FUTEX_WAIT_BITSET | FUTEX_PRIVATE_FLAG)
#define FUTEX_WAKE_BITSET_PRIVATE (FUTEX_WAKE_BITSET | FUTEX_PRIVATE_FLAG)
#define FUTEX_BITSET_MATCH_ANY 0xffffffff

static long fu(int *uaddr, int op, int val, const struct timespec *to, int *uaddr2, int val3)
{
    return syscall(SYS_futex, uaddr, op, val, to, uaddr2, val3);
}

static int word, done_count, ready_count;
#define W 3

static void *waiter(void *arg)
{
    int *w = arg;
    __atomic_add_fetch(&ready_count, 1, __ATOMIC_SEQ_CST);
    while (__atomic_load_n(w, __ATOMIC_ACQUIRE) == 0)
        fu(w, FUTEX_WAIT_PRIVATE, 0, NULL, NULL, 0);
    __atomic_add_fetch(&done_count, 1, __ATOMIC_SEQ_CST);
    return NULL;
}

int main(void)
{
    INIT();
    int v = 5;
    T("wake ohne Wartende", fu(&v, FUTEX_WAKE_PRIVATE, 10, NULL, NULL, 0));
    T("wait falscher Wert", fu(&v, FUTEX_WAIT_PRIVATE, 4, NULL, NULL, 0));
    struct timespec rel = { 0, 20 * 1000000L };
    T("wait relative Zeit", fu(&v, FUTEX_WAIT_PRIVATE, 5, &rel, NULL, 0));
    struct timespec now, past;
    clock_gettime(CLOCK_MONOTONIC, &now);
    past = now;
    T("wait_bitset absolut (Vergangenheit)", fu(&v, FUTEX_WAIT_BITSET_PRIVATE, 5, &past, NULL, FUTEX_BITSET_MATCH_ANY));
    T("wait_bitset Maske 0", fu(&v, FUTEX_WAIT_BITSET_PRIVATE, 5, NULL, NULL, 0));
    T("wake_bitset Maske 0", fu(&v, FUTEX_WAKE_BITSET_PRIVATE, 1, NULL, NULL, 0));
    T("unbekannte Operation", fu(&v, 0x7f, 0, NULL, NULL, 0));
    T("nicht ausgerichtet", fu((int *)((char *)&v + 1), FUTEX_WAKE_PRIVATE, 1, NULL, NULL, 0));
    T("wait ungültige Adresse", fu((int *)8, FUTEX_WAIT_PRIVATE, 0, NULL, NULL, 0));

    /* W Wartende; der Hauptthread setzt das Wort und weckt, bis alle fertig sind.
     * Wie viele ein einzelner Aufruf weckt, hängt vom Zeitpunkt ab und wird nicht gezeigt. */
    pthread_t th[W];
    for (int i = 0; i < W; i++) NEED(pthread_create(&th[i], NULL, waiter, &word) == 0);
    while (__atomic_load_n(&ready_count, __ATOMIC_SEQ_CST) < W) sched_yield();
    __atomic_store_n(&word, 1, __ATOMIC_RELEASE);
    while (__atomic_load_n(&done_count, __ATOMIC_SEQ_CST) < W) {
        fu(&word, FUTEX_WAKE_PRIVATE, 1000, NULL, NULL, 0);
        sched_yield();
    }
    for (int i = 0; i < W; i++) pthread_join(th[i], NULL);
    SHOWI("wartende fertig", done_count);

    /* Geteilter Speicher zwischen Prozessen (ohne PRIVATE): Kind wartet, Elternteil weckt. */
    int *sh = mmap(NULL, 4096, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    NEED(sh != MAP_FAILED);
    pid_t c = fork();
    NEED(c >= 0);
    if (c == 0) {
        __atomic_store_n(&sh[1], 1, __ATOMIC_SEQ_CST);
        while (__atomic_load_n(&sh[0], __ATOMIC_ACQUIRE) == 0) fu(&sh[0], FUTEX_WAIT, 0, NULL, NULL, 0);
        _exit(sh[0] == 1 ? 0 : 4);
    }
    while (__atomic_load_n(&sh[1], __ATOMIC_SEQ_CST) == 0) sched_yield();
    __atomic_store_n(&sh[0], 1, __ATOMIC_RELEASE);
    int st = 0;
    for (;;) {
        fu(&sh[0], FUTEX_WAKE, 1, NULL, NULL, 0);
        pid_t r = waitpid(c, &st, WNOHANG);
        if (r == c) break;
        NEED(r == 0);
        sched_yield();
    }
    SHOW("geteilt: Kind beendet mit 0", WIFEXITED(st) && WEXITSTATUS(st) == 0);

    /* Requeue: Vergleichswert wird geprüft (CMP_REQUEUE -> EAGAIN bei Abweichung). */
    int a = 1, b = 2;
    T("cmp_requeue falscher Wert", fu(&a, FUTEX_CMP_REQUEUE_PRIVATE, 1, (void *)1, &b, 99));
    T("cmp_requeue ohne Wartende", fu(&a, FUTEX_CMP_REQUEUE_PRIVATE, 1, (void *)1, &b, 1));
    T("requeue ohne Wartende", fu(&a, FUTEX_REQUEUE_PRIVATE, 1, (void *)1, &b, 0));
    return 0;
}
