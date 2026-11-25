/* Threads (musl: clone mit CLONE_VM|CLONE_THREAD|CLONE_SETTLS|CLONE_CHILD_CLEARTID), Mutex,
 * Bedingungsvariable, __thread je Thread, Rückgabewerte, gettid, Signalmaske je Thread. */
#define _GNU_SOURCE
#include "util.h"
#include <pthread.h>
#include <signal.h>
#include <sys/syscall.h>

#define N 4
#define ROUNDS 2000

static pthread_mutex_t mu = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t cv = PTHREAD_COND_INITIALIZER;
static long counter;
static int turn;                 /* Ping-Pong zwischen Hauptthread und einem Thread */
static __thread long tls_val = 7;
static __thread char tls_big[2000];
static pid_t tids[N];
static int mask_ok[N];

static void *worker(void *arg)
{
    long id = (long)arg;
    tids[id] = (pid_t)syscall(SYS_gettid);
    tls_val += id;
    tls_big[1999] = (char)id;
    sigset_t s;
    pthread_sigmask(SIG_BLOCK, NULL, &s);
    mask_ok[id] = sigismember(&s, SIGUSR1) == 1;   /* erbt die Maske des Erzeugers */
    for (int i = 0; i < ROUNDS; i++) {
        pthread_mutex_lock(&mu);
        counter++;
        pthread_mutex_unlock(&mu);
    }
    sched_yield();
    long ok = tls_val == 7 + id && tls_big[1999] == (char)id && tls_big[0] == 0;
    return (void *)(ok ? 100 + id : -1L);
}

static void *pong(void *arg)
{
    (void)arg;
    pthread_mutex_lock(&mu);
    for (int i = 0; i < 50; i++) {
        while (turn != 1) pthread_cond_wait(&cv, &mu);
        turn = 0;
        pthread_cond_signal(&cv);
    }
    pthread_mutex_unlock(&mu);
    return NULL;
}

static void *detached(void *arg)
{
    __atomic_store_n((int *)arg, 1, __ATOMIC_RELEASE);
    return NULL;
}

int main(void)
{
    INIT();
    sigset_t blk;
    sigemptyset(&blk);
    sigaddset(&blk, SIGUSR1);
    pthread_sigmask(SIG_BLOCK, &blk, NULL);

    pthread_t th[N];
    for (long i = 0; i < N; i++) NEED(pthread_create(&th[i], NULL, worker, (void *)i) == 0);
    long rets_ok = 0;
    for (long i = 0; i < N; i++) {
        void *r = NULL;
        NEED(pthread_join(th[i], &r) == 0);
        if ((long)r == 100 + i) rets_ok++;
    }
    SHOWI("joined", N);
    SHOWI("rueckgaben ok", rets_ok);
    SHOWI("zaehler", counter);
    SHOWI("tls Hauptthread", tls_val);
    int distinct = 1;
    for (int i = 0; i < N; i++) {
        if (tids[i] == getpid()) distinct = 0;
        for (int j = i + 1; j < N; j++) if (tids[i] == tids[j]) distinct = 0;
    }
    SHOW("tids verschieden und != pid", distinct);
    SHOW("gettid Hauptthread == getpid", syscall(SYS_gettid) == getpid());
    int masks = 0;
    for (int i = 0; i < N; i++) masks += mask_ok[i];
    SHOWI("signalmaske geerbt", masks);

    pthread_t p;
    NEED(pthread_create(&p, NULL, pong, NULL) == 0);
    pthread_mutex_lock(&mu);
    for (int i = 0; i < 50; i++) {
        turn = 1;
        pthread_cond_signal(&cv);
        while (turn != 0) pthread_cond_wait(&cv, &mu);
    }
    pthread_mutex_unlock(&mu);
    NEED(pthread_join(p, NULL) == 0);
    SHOWI("pingpong", 50);

    /* Abgekoppelter Thread: das Ende wird über eine Variable abgewartet (kein join). */
    static int flag;
    pthread_attr_t at;
    pthread_attr_init(&at);
    pthread_attr_setdetachstate(&at, PTHREAD_CREATE_DETACHED);
    pthread_attr_setstacksize(&at, 256 * 1024);
    pthread_t d;
    NEED(pthread_create(&d, &at, detached, &flag) == 0);
    while (!__atomic_load_n(&flag, __ATOMIC_ACQUIRE)) sched_yield();
    SHOW("detached gelaufen", flag == 1);

    /* Signal an einen bestimmten Thread (tgkill) mit Handler, der im Zielthread läuft. */
    T("tgkill eigener Thread, gesperrt", syscall(SYS_tgkill, getpid(), syscall(SYS_gettid), SIGUSR1));
    sigset_t pend;
    sigpending(&pend);
    SHOW("SIGUSR1 anhängig", sigismember(&pend, SIGUSR1) == 1);
    T("tgkill falscher Thread", syscall(SYS_tgkill, getpid(), 0x3fffffff, SIGUSR1));
    /* Das Programm endet mit exit_group, während der abgekoppelte Thread schon fertig ist. */
    printf("ende\n");
    return 0;
}
