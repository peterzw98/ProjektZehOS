/* V22b (Ladybird): timerfd_create, timerfd_settime (relativ, TFD_TIMER_ABSTIME, periodisch, entschärfen, alter Stand), timerfd_gettime,
 * read (blockierend und O_NONBLOCK), poll und epoll auf CLOCK_MONOTONIC und CLOCK_REALTIME. Nur Ausgaben, die unter Last stabil sind
 * (Abläufe >= 1, Untergrenzen der Zeit). Die Ausgabe ist mit Linux 6.12 gleich (expected/v22/timerfd.txt). */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/epoll.h>
#include <sys/eventfd.h>
#include <sys/timerfd.h>
#include <time.h>
#include <unistd.h>

#define T(label, expr) do { errno = 0; long r_ = (long)(expr); printf("%s: ret=%ld errno=%d\n", label, r_, errno); } while (0)
#define SHOW(label, cond) printf("%s: %s\n", label, (cond) ? "ja" : "nein")
#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)

static long ms_since(const struct timespec *t0)
{
    struct timespec t1;
    clock_gettime(CLOCK_MONOTONIC, &t1);
    return (t1.tv_sec - t0->tv_sec) * 1000 + (t1.tv_nsec - t0->tv_nsec) / 1000000;
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    alarm(30);
    uint64_t v = 0;
    struct itimerspec its = { { 0, 0 }, { 0, 0 } }, cur;
    struct timespec t0;

    T("create Uhr ungueltig", timerfd_create(9999, 0));
    T("create CPU-Uhr", timerfd_create(CLOCK_PROCESS_CPUTIME_ID, 0));
    T("create Flag ungueltig", timerfd_create(CLOCK_MONOTONIC, 1));
    int tf = timerfd_create(CLOCK_MONOTONIC, TFD_NONBLOCK | TFD_CLOEXEC);
    NEED(tf >= 0);
    T("FD_CLOEXEC", fcntl(tf, F_GETFD));
    T("F_GETFL", fcntl(tf, F_GETFL) & (O_ACCMODE | O_NONBLOCK));
    T("read nicht gestellt", read(tf, &v, 8));
    T("read 4 Byte", read(tf, &v, 4));
    T("write", write(tf, &v, 8));
    NEED(timerfd_gettime(tf, &cur) == 0);
    SHOW("gettime nicht gestellt 0", cur.it_value.tv_sec == 0 && cur.it_value.tv_nsec == 0 && cur.it_interval.tv_nsec == 0);

    its.it_value.tv_nsec = 1000000000L;
    T("settime tv_nsec zu gross", timerfd_settime(tf, 0, &its, NULL));
    its.it_value.tv_nsec = 1;
    T("settime Flag ungueltig", timerfd_settime(tf, 4, &its, NULL));
    T("settime fd ungueltig", timerfd_settime(99, 0, &its, NULL));
    int ef = eventfd(0, 0);
    T("settime kein timerfd", timerfd_settime(ef, 0, &its, NULL));
    T("gettime kein timerfd", timerfd_gettime(ef, &cur));
    T("settime new NULL", timerfd_settime(tf, 0, NULL, NULL));
    close(ef);

    /* Einmalig 30 ms, gewartet mit poll. */
    its.it_value.tv_nsec = 30 * 1000000L;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    NEED(timerfd_settime(tf, 0, &its, NULL) == 0);
    NEED(timerfd_gettime(tf, &cur) == 0);
    SHOW("gettime Restzeit <= 30 ms", cur.it_value.tv_sec == 0 && cur.it_value.tv_nsec > 0 && cur.it_value.tv_nsec <= 30 * 1000000L);
    struct pollfd pf = { tf, POLLIN | POLLOUT, 0 };
    T("poll sofort", poll(&pf, 1, 0));
    T("poll mit Frist", poll(&pf, 1, 2000));
    printf("  revents=%d\n", pf.revents);
    SHOW("  mindestens 25 ms", ms_since(&t0) >= 25);
    T("read", read(tf, &v, 8));
    SHOW("  Ablaeufe == 1", v == 1);
    T("read danach", read(tf, &v, 8));
    NEED(timerfd_gettime(tf, &cur) == 0);
    SHOW("einmalig: danach 0", cur.it_value.tv_sec == 0 && cur.it_value.tv_nsec == 0);

    /* Periodisch 10 ms über epoll, dann alter Stand beim Entschärfen. */
    int ep = epoll_create1(0);
    struct epoll_event ev = { EPOLLIN, { .u64 = 0x6666 } }, out[2];
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, tf, &ev) == 0);
    its.it_interval.tv_nsec = 10 * 1000000L;
    its.it_value.tv_nsec = 10 * 1000000L;
    NEED(timerfd_settime(tf, 0, &its, NULL) == 0);
    for (int i = 0; i < 3; i++) {
        int n = epoll_wait(ep, out, 2, 2000);
        printf("periodisch: n=%d events=%u data=%llx\n", n, n > 0 ? out[0].events : 0, n > 0 ? (unsigned long long)out[0].data.u64 : 0ULL);
        NEED(read(tf, &v, 8) == 8 && v >= 1);
    }
    struct itimerspec off = { { 0, 0 }, { 0, 0 } }, old;
    NEED(timerfd_settime(tf, 0, &off, &old) == 0);
    SHOW("alter Stand: Intervall 10 ms", old.it_interval.tv_sec == 0 && old.it_interval.tv_nsec == 10 * 1000000L);
    SHOW("alter Stand: Restzeit <= 10 ms", old.it_value.tv_sec == 0 && old.it_value.tv_nsec > 0 && old.it_value.tv_nsec <= 10 * 1000000L);
    T("epoll entschaerft", epoll_wait(ep, out, 2, 50));

    /* Ablauf ausstehend, dann neu gestellt: verworfen. */
    its.it_interval.tv_nsec = 0;
    its.it_value.tv_nsec = 1000000L;
    NEED(timerfd_settime(tf, 0, &its, NULL) == 0);
    struct timespec d = { 0, 20 * 1000000L };
    nanosleep(&d, NULL);
    its.it_value.tv_sec = 10;
    NEED(timerfd_settime(tf, 0, &its, NULL) == 0);
    T("neu gestellt verwirft", read(tf, &v, 8));

    /* Absolut in der Vergangenheit: läuft gleich ab (Linux über den hrtimer, also erst nach poll sicher lesbar). */
    its.it_value.tv_sec = 0;
    its.it_value.tv_nsec = 1;
    NEED(timerfd_settime(tf, TFD_TIMER_ABSTIME, &its, NULL) == 0);
    clock_gettime(CLOCK_MONOTONIC, &t0);
    T("absolut vergangen poll", poll(&pf, 1, 2000));
    SHOW("  ohne Warten (< 500 ms)", ms_since(&t0) < 500);
    T("absolut vergangen read", read(tf, &v, 8));
    printf("  Ablaeufe=%llu\n", (unsigned long long)v);
    close(ep);
    close(tf);

    /* CLOCK_REALTIME absolut +30 ms, blockierendes read. */
    int rt = timerfd_create(CLOCK_REALTIME, 0);
    NEED(rt >= 0);
    struct timespec now;
    clock_gettime(CLOCK_REALTIME, &now);
    long ns = now.tv_nsec + 30 * 1000000L;
    its.it_value.tv_sec = now.tv_sec + ns / 1000000000L;
    its.it_value.tv_nsec = ns % 1000000000L;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    NEED(timerfd_settime(rt, TFD_TIMER_ABSTIME | TFD_TIMER_CANCEL_ON_SET, &its, NULL) == 0);
    T("realtime blockierend", read(rt, &v, 8));
    SHOW("  Ablaeufe == 1", v == 1);
    SHOW("  mindestens 25 ms", ms_since(&t0) >= 25);
    close(rt);

    int bt = timerfd_create(CLOCK_BOOTTIME, 0);
    SHOW("CLOCK_BOOTTIME", bt >= 0);
    printf("fertig\n");
    return 0;
}
