/* Ladybird V10: Threads (clone, futex zwischen Threads, CLEARTID beim Threadende, TLS je Thread, gettid).
 * Ausgabe nur aus main nach den joins, damit sie unter Linux und ZehOS gleich ist. */
#define _GNU_SOURCE
#include <pthread.h>
#include <semaphore.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

static __thread int tls = 1;
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t cond = PTHREAD_COND_INITIALIZER;
static long counter;
static int turn, rounds_done;
static sem_t done;

static long tid(void) { return syscall(SYS_gettid); }

static void *adder(void *arg)
{
    long me = (long)arg;
    tls = 100 + (int)me;
    for (int i = 0; i < 20000; i++) {
        pthread_mutex_lock(&lock);
        counter++;
        pthread_mutex_unlock(&lock);
        if (i % 4096 == 0) sched_yield();
    }
    /* TLS bleibt je Thread, die TID unterscheidet sich von der PID. */
    return (void *)(long)(tls == 100 + (int)me && tid() != getpid());
}

static void *pong(void *arg)
{
    int who = (int)(long)arg;
    for (int i = 0; i < 500; i++) {
        pthread_mutex_lock(&lock);
        while (turn != who) pthread_cond_wait(&cond, &lock);
        turn = 1 - who;
        rounds_done++;
        pthread_cond_broadcast(&cond);
        pthread_mutex_unlock(&lock);
    }
    return NULL;
}

static void *sleeper(void *arg)
{
    (void)arg;
    struct timespec ts = { 0, 20 * 1000 * 1000 };
    nanosleep(&ts, NULL);
    return (void *)42;
}

static void *detached(void *arg)
{
    (void)arg;
    sem_post(&done);
    return NULL;
}

int main(void)
{
    pthread_t t[4], p[2], s, d;
    void *r;
    int ok = 0;
    for (long i = 0; i < 4; i++) pthread_create(&t[i], NULL, adder, (void *)i);
    for (int i = 0; i < 4; i++) { pthread_join(t[i], &r); ok += (int)(long)r; }
    printf("adder: counter=%ld ok=%d tls-main=%d\n", counter, ok, tls);

    for (long i = 0; i < 2; i++) pthread_create(&p[i], NULL, pong, (void *)i);
    for (int i = 0; i < 2; i++) pthread_join(p[i], NULL);
    printf("pingpong: rounds=%d\n", rounds_done);

    pthread_create(&s, NULL, sleeper, NULL);
    pthread_join(s, &r);
    printf("join: %ld\n", (long)r);

    sem_init(&done, 0, 0);
    pthread_attr_t a;
    pthread_attr_init(&a);
    pthread_attr_setdetachstate(&a, PTHREAD_CREATE_DETACHED);
    for (int i = 0; i < 8; i++) pthread_create(&d, &a, detached, NULL);
    for (int i = 0; i < 8; i++) sem_wait(&done);
    printf("detached: 8\n");
    printf("main: tid==pid %d\n", tid() == getpid());
    return 0;
}
