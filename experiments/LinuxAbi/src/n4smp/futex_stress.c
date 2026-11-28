/* N4-S5 futex unter Last: Vier Threads wechseln sich mit pthread-Mutex und -Bedingung ab (musl baut beide auf futex).
 * Teil 1: jeder Thread erhoeht 25 000-mal einen Zaehler unter dem Mutex (umkaempfter Mutex, FUTEX_WAIT/WAKE).
 * Teil 2: 10^5 Runden im Ring 0 -> 1 -> 2 -> 3 -> 0: Ein Thread wartet auf der Bedingung, bis er an der Reihe ist, erhoeht
 * den Rundenzaehler und weckt die anderen (broadcast, musl haengt Wartende per FUTEX_REQUEUE um). Teil 3: dasselbe mit
 * pthread_cond_signal und je einer Bedingung pro Thread (gezieltes Einzelwecken).
 * Erfolg: Zaehler genau, Reihenfolge nie verletzt, kein Haenger (der Wecker aus INIT beendet einen Haenger mit Signal 14).
 * Ein verlorener Weckruf haelt den Ring an; ein fehlender Ausschluss verfaelscht den Zaehler. Die Laufzeit steht nicht in
 * der Ausgabe (sie schwankt), sondern in der Zeile LINUX_RUN time der Gastprobe.
 * Gegenprobe (nur Host, -DFUTEX_STRESS_NOLOCK): Teil 1 ohne Mutex zaehlt auf mehreren CPUs zu wenig. */
#define _GNU_SOURCE
#include "../s3/util.h"
#include <pthread.h>

#define THREADS 4
#define ADDS 25000
#define ROUNDS 100000

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t turn_changed = PTHREAD_COND_INITIALIZER;
static pthread_cond_t own_turn[THREADS];
static volatile long counter;
static int turn, rounds, order_errors, single;
static pthread_barrier_t start;

static void *adder(void *arg)
{
    (void)arg;
    pthread_barrier_wait(&start);
    for (int i = 0; i < ADDS; i++) {
#ifndef FUTEX_STRESS_NOLOCK
        pthread_mutex_lock(&lock);
#endif
        /* Lesen, kurz rechnen, schreiben: ohne Ausschluss gehen auf mehreren CPUs Erhoehungen verloren. */
        long v = counter;
        for (volatile int k = 0; k < 4; k++) { }
        counter = v + 1;
#ifndef FUTEX_STRESS_NOLOCK
        pthread_mutex_unlock(&lock);
#endif
    }
    return NULL;
}

static void *ring(void *arg)
{
    int id = (int)(long)arg;
    pthread_barrier_wait(&start);
    pthread_mutex_lock(&lock);
    for (;;) {
        while (turn != id && rounds < ROUNDS) {
            pthread_cond_wait(single ? &own_turn[id] : &turn_changed, &lock);
        }
        if (rounds >= ROUNDS) break;
        if (rounds % THREADS != id) order_errors++;
        rounds++;
        turn = (id + 1) % THREADS;
        if (single) {
            /* Nach der letzten Runde muessen alle aufwachen und enden. */
            if (rounds >= ROUNDS) {
                for (int i = 0; i < THREADS; i++) pthread_cond_signal(&own_turn[i]);
            } else {
                pthread_cond_signal(&own_turn[turn]);
            }
        } else {
            pthread_cond_broadcast(&turn_changed);
        }
    }
    pthread_mutex_unlock(&lock);
    return NULL;
}

static void phase(void *(*fn)(void *))
{
    pthread_t t[THREADS];
    NEED(pthread_barrier_init(&start, NULL, THREADS) == 0);
    for (long i = 0; i < THREADS; i++) NEED(pthread_create(&t[i], NULL, fn, (void *)i) == 0);
    for (int i = 0; i < THREADS; i++) NEED(pthread_join(t[i], NULL) == 0);
    pthread_barrier_destroy(&start);
}

int main(void)
{
    INIT();
    for (int i = 0; i < THREADS; i++) NEED(pthread_cond_init(&own_turn[i], NULL) == 0);

    phase(adder);
    SHOWI("mutex_counter", counter);

    phase(ring);
    SHOWI("broadcast_rounds", rounds);
    SHOWI("broadcast_order_errors", order_errors);

    turn = 0, rounds = 0, order_errors = 0, single = 1;
    phase(ring);
    SHOWI("signal_rounds", rounds);
    SHOWI("signal_order_errors", order_errors);
    return 0;
}
