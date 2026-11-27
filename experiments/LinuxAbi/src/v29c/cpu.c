/* V29c CPU-Zeit je Thread: Schleife, clock_gettime THREAD/PROCESS_CPUTIME_ID, getrusage (SELF, THREAD, CHILDREN, Fehlerfaelle)
 * und utime aus /proc/self/stat. Die Zeit zaehlt der Scheduler je Tick (10 ms), deshalb prueft das Programm nur Bereiche
 * (Untergrenze Schleifenzeit, Obergrenze Wanduhr, Gleichheit der Quellen auf Tickbreite) und gibt nur 0/1 aus. */
#define _GNU_SOURCE
#include "../s3/util.h"
#include <fcntl.h>
#include <pthread.h>
#include <string.h>
#include <sys/resource.h>
#include <time.h>

#define MS 1000000LL
static long long ns(clockid_t id)
{
    struct timespec t;
    return clock_gettime(id, &t) == 0 ? t.tv_sec * 1000000000LL + t.tv_nsec : -1;
}

static void *worker(void *arg)
{
    (void)arg;
    long long start = ns(CLOCK_THREAD_CPUTIME_ID), wall = ns(CLOCK_MONOTONIC);
    volatile unsigned long x = 0;
    while (ns(CLOCK_THREAD_CPUTIME_ID) - start < 60 * MS && ns(CLOCK_MONOTONIC) - wall < 10000 * MS) x++;
    return NULL;
}

static long long tv_ns(struct timeval tv) { return tv.tv_sec * 1000000000LL + tv.tv_usec * 1000LL; }

int main(void)
{
    INIT();
    long long w0 = ns(CLOCK_MONOTONIC), t0 = ns(CLOCK_THREAD_CPUTIME_ID), p0 = ns(CLOCK_PROCESS_CPUTIME_ID);
    NEED(w0 >= 0 && t0 >= 0 && p0 >= 0);
    volatile unsigned long x = 0;
    while (ns(CLOCK_THREAD_CPUTIME_ID) - t0 < 120 * MS && ns(CLOCK_MONOTONIC) - w0 < 10000 * MS) x++;
    long long w1 = ns(CLOCK_MONOTONIC), t1 = ns(CLOCK_THREAD_CPUTIME_ID), p1 = ns(CLOCK_PROCESS_CPUTIME_ID);
    long long wall = w1 - w0, thr = t1 - t0;
    SHOW("thread cpu >= 120ms", thr >= 120 * MS);
    SHOW("thread cpu <= wall+20ms", thr <= wall + 20 * MS);
    SHOW("process cpu >= thread cpu", p1 - p0 >= thr);
    SHOW("process cpu <= wall+20ms", p1 - p0 <= wall + 20 * MS);

    struct rusage ru, rt, rc;
    NEED(getrusage(RUSAGE_SELF, &ru) == 0 && getrusage(RUSAGE_THREAD, &rt) == 0 && getrusage(RUSAGE_CHILDREN, &rc) == 0);
    long long p2 = ns(CLOCK_PROCESS_CPUTIME_ID), t2 = ns(CLOCK_THREAD_CPUTIME_ID);
    SHOW("rusage self utime in clock range", tv_ns(ru.ru_utime) + 20 * MS >= p1 && tv_ns(ru.ru_utime) <= p2 + 20 * MS);
    SHOW("rusage thread utime in clock range", tv_ns(rt.ru_utime) + 20 * MS >= t1 && tv_ns(rt.ru_utime) <= t2 + 20 * MS);
    SHOW("rusage stime 0", ru.ru_stime.tv_sec == 0 && ru.ru_stime.tv_usec == 0);
    SHOW("rusage children 0", tv_ns(rc.ru_utime) == 0);
    T("getrusage EFAULT", getrusage(RUSAGE_SELF, (struct rusage *)1));
    T("getrusage who=5", getrusage(5, &ru));

    char buf[1024];
    int fd = open("/proc/self/stat", O_RDONLY | O_CLOEXEC);
    NEED(fd >= 0);
    ssize_t n = read(fd, buf, sizeof buf - 1);
    NEED(n > 0);
    buf[n] = 0;
    close(fd);
    char *rp = strrchr(buf, ')');
    NEED(rp != NULL);
    unsigned long long utime = 0, stime = 1;
    /* nach ")": state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt utime stime */
    NEED(sscanf(rp + 2, "%*c %*d %*d %*d %*d %*d %*u %*u %*u %*u %*u %llu %llu", &utime, &stime) == 2);
    long long up = (long long)utime * 10 * MS;
    SHOW("stat utime in clock range", up + 20 * MS >= p1 && up <= p2 + 20 * MS);
    SHOW("stat stime 0", stime == 0);

    /* Zweiter Thread rechnet 60 ms und endet; seine Zeit bleibt in der Prozesssumme, die des Hauptthreads waechst nicht mit. */
    long long pa = ns(CLOCK_PROCESS_CPUTIME_ID), ta = ns(CLOCK_THREAD_CPUTIME_ID);
    pthread_t th;
    NEED(pthread_create(&th, NULL, worker, NULL) == 0);
    NEED(pthread_join(th, NULL) == 0);
    long long pb = ns(CLOCK_PROCESS_CPUTIME_ID), tb = ns(CLOCK_THREAD_CPUTIME_ID);
    SHOW("beendeter Thread zaehlt im Prozess", pb - pa >= 60 * MS);
    SHOW("Hauptthread ohne Fremdzeit", tb - ta < 60 * MS);
    return 0;
}
