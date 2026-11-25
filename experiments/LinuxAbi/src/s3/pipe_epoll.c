/* pipe2, epoll (Level- und Edge-Trigger, ONESHOT, MOD/DEL), eventfd2, timerfd, poll/ppoll auf
 * Pipes, EPIPE/SIGPIPE, Aufwecken durch ein anderes Programm (Kindprozess). */
#define _GNU_SOURCE
#include "util.h"
#include <fcntl.h>
#include <stdint.h>
#include <poll.h>
#include <signal.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/eventfd.h>
#include <sys/timerfd.h>
#include <sys/wait.h>
#include <time.h>

#define BLOCK_MS 10000   /* Obergrenze für Wartezeiten, die nie erreicht werden darf */

static int wait_ev(int ep, struct epoll_event *ev, int max, int ms)
{
    int n;
    do n = epoll_wait(ep, ev, max, ms); while (n < 0 && errno == EINTR);
    return n;
}

int main(void)
{
    INIT();
    int p[2];
    NEED(pipe2(p, O_NONBLOCK | O_CLOEXEC) == 0);
    char buf[64];
    T("read leere Pipe (nonblock)", read(p[0], buf, sizeof buf));
    SHOW("O_NONBLOCK lesbar", (fcntl(p[0], F_GETFL) & O_NONBLOCK) != 0);
    SHOW("CLOEXEC gesetzt", (fcntl(p[0], F_GETFD) & FD_CLOEXEC) != 0);
    int q[2];
    T("pipe2 ungültige Flags", pipe2(q, 0x40000000));
    close(p[0]); close(p[1]);
    NEED(pipe2(p, O_NONBLOCK) == 0);

    struct pollfd pf[2] = { { p[0], POLLIN, 0 }, { p[1], POLLOUT, 0 } };
    T("poll leere Pipe", poll(pf, 2, 0));
    SHOW("  schreibbar", (pf[1].revents & POLLOUT) != 0);
    SHOW("  nicht lesbar", pf[0].revents == 0);
    NEED(write(p[1], "abc", 3) == 3);
    T("poll mit Daten", poll(pf, 2, 0));
    SHOW("  lesbar", (pf[0].revents & POLLIN) != 0);
    struct timespec z = { 0, 0 };
    T("ppoll Zeit 0", ppoll(pf, 2, &z, NULL));
    SHOWI("lesen", read(p[0], buf, sizeof buf));

    int ep = epoll_create1(EPOLL_CLOEXEC);
    NEED(ep >= 0);
    struct epoll_event ev, out[4];
    ev.events = EPOLLIN;
    ev.data.u64 = 0x1111;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, p[0], &ev) == 0);
    T("ctl ADD doppelt", epoll_ctl(ep, EPOLL_CTL_ADD, p[0], &ev));
    T("ctl DEL unbekannt", epoll_ctl(ep, EPOLL_CTL_DEL, p[1], NULL));
    T("ctl ungültiger fd", epoll_ctl(ep, EPOLL_CTL_ADD, 99, &ev));
    T("ctl epoll auf sich selbst", epoll_ctl(ep, EPOLL_CTL_ADD, ep, &ev));
    T("wait ohne Ereignis, 10 ms", wait_ev(ep, out, 4, 10));
    NEED(write(p[1], "xy", 2) == 2);
    int n = wait_ev(ep, out, 4, BLOCK_MS);
    SHOWI("wait mit Daten", n);
    SHOW("  Daten gleich", n == 1 && out[0].data.u64 == 0x1111 && (out[0].events & EPOLLIN));
    n = wait_ev(ep, out, 4, 0);
    SHOWI("level: noch einmal gemeldet", n);   /* nicht gelesen */
    SHOWI("lesen", read(p[0], buf, 1));
    n = wait_ev(ep, out, 4, 0);
    SHOWI("level: ein Byte übrig", n);
    SHOWI("lesen", read(p[0], buf, sizeof buf));
    T("wait nichts mehr", wait_ev(ep, out, 4, 0));
    T("wait maxevents 0", epoll_wait(ep, out, 0, 0));

    /* Edge-Trigger: nur bei neuen Daten. */
    ev.events = EPOLLIN | EPOLLET;
    ev.data.u64 = 0x2222;
    NEED(epoll_ctl(ep, EPOLL_CTL_MOD, p[0], &ev) == 0);
    NEED(write(p[1], "12", 2) == 2);
    SHOWI("edge: erste Meldung", wait_ev(ep, out, 4, BLOCK_MS));
    SHOW("  neue Daten gleich", out[0].data.u64 == 0x2222);
    SHOWI("edge: zweite Meldung ohne Lesen", wait_ev(ep, out, 4, 0));
    NEED(write(p[1], "3", 1) == 1);
    SHOWI("edge: nach weiteren Daten", wait_ev(ep, out, 4, BLOCK_MS));
    SHOWI("lesen", read(p[0], buf, sizeof buf));

    /* ONESHOT: nach der Meldung stumm bis MOD. */
    ev.events = EPOLLIN | EPOLLONESHOT;
    ev.data.u64 = 0x3333;
    NEED(epoll_ctl(ep, EPOLL_CTL_MOD, p[0], &ev) == 0);
    NEED(write(p[1], "z", 1) == 1);
    SHOWI("oneshot: Meldung", wait_ev(ep, out, 4, BLOCK_MS));
    SHOWI("oneshot: danach", wait_ev(ep, out, 4, 0));
    NEED(epoll_ctl(ep, EPOLL_CTL_MOD, p[0], &ev) == 0);
    SHOWI("oneshot: nach MOD", wait_ev(ep, out, 4, BLOCK_MS));
    SHOWI("lesen", read(p[0], buf, sizeof buf));
    NEED(epoll_ctl(ep, EPOLL_CTL_DEL, p[0], NULL) == 0);
    NEED(write(p[1], "q", 1) == 1);
    T("nach DEL keine Meldung", wait_ev(ep, out, 4, 0));
    SHOWI("lesen", read(p[0], buf, sizeof buf));

    /* HUP und EOF: Schreibende zu. */
    ev.events = EPOLLIN;
    ev.data.u64 = 0x4444;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, p[0], &ev) == 0);
    close(p[1]);
    n = wait_ev(ep, out, 4, BLOCK_MS);
    SHOWI("hup: Meldung", n);
    SHOW("  EPOLLHUP", n == 1 && (out[0].events & EPOLLHUP));
    T("lesen: EOF", read(p[0], buf, sizeof buf));
    close(p[0]);

    /* EPIPE (SIGPIPE ignoriert) und SIGPIPE mit Standardverhalten wird in sigsegv_status nicht geprüft. */
    signal(SIGPIPE, SIG_IGN);
    NEED(pipe(p) == 0);
    close(p[0]);
    T("write ohne Leser", write(p[1], "x", 1));
    close(p[1]);

    /* Pipe voll: nonblocking schreiben bis EAGAIN; die Größe ist Sache des Kerns. */
    NEED(pipe2(p, O_NONBLOCK) == 0);
    char big[4096];
    memset(big, 'x', sizeof big);
    long total = 0;
    for (;;) {
        ssize_t w = write(p[1], big, sizeof big);
        if (w < 0) { SHOW("voll: EAGAIN", errno == EAGAIN); break; }
        total += w;
        NEED(total < (1 << 24));
    }
    SHOW("voll: mindestens 4096 Byte", total >= 4096);
    pf[0].fd = p[1]; pf[0].events = POLLOUT; pf[0].revents = 0;
    T("poll voll, nicht schreibbar", poll(pf, 1, 0));
    NEED(read(p[0], big, sizeof big) == (ssize_t)sizeof big);
    long drained = 4096;
    while (read(p[0], big, sizeof big) > 0) drained += 4096;
    SHOW("alles wieder gelesen", drained == total);
    close(p[0]); close(p[1]);

    /* eventfd. */
    int ef = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
    NEED(ef >= 0);
    uint64_t v = 0;
    T("eventfd lesen (0)", read(ef, &v, 8));
    v = 5;
    NEED(write(ef, &v, 8) == 8);
    v = 7;
    NEED(write(ef, &v, 8) == 8);
    v = 0;
    SHOWI("eventfd lesen (Summe)", read(ef, &v, 8));
    SHOWI("  Wert", v);
    T("eventfd zu kurz", read(ef, buf, 4));
    v = 0xffffffffffffffffULL;
    T("eventfd Wert -1", write(ef, &v, 8));
    close(ef);
    ef = eventfd(3, EFD_SEMAPHORE | EFD_NONBLOCK);
    NEED(ef >= 0);
    int taken = 0;
    while (read(ef, &v, 8) == 8) { taken += (int)v; NEED(taken < 100); }
    SHOWI("semaphor: entnommen", taken);
    SHOW("semaphor: danach EAGAIN", errno == EAGAIN);
    ev.events = EPOLLIN; ev.data.u64 = 0x5555;
    ep = epoll_create1(0);
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, ef, &ev) == 0);
    T("eventfd leer, epoll", wait_ev(ep, out, 4, 0));
    v = 1;
    NEED(write(ef, &v, 8) == 8);
    T("eventfd gesetzt, epoll", wait_ev(ep, out, 4, BLOCK_MS));
    close(ef);

    /* timerfd: einmalig 20 ms, danach periodisch; Anzahl der Abläufe nur >= 1. */
    int tf = timerfd_create(CLOCK_MONOTONIC, TFD_NONBLOCK | TFD_CLOEXEC);
    NEED(tf >= 0);
    T("timerfd noch nicht abgelaufen", read(tf, &v, 8));
    struct itimerspec its = { { 0, 0 }, { 0, 20 * 1000000L } };
    NEED(timerfd_settime(tf, 0, &its, NULL) == 0);
    struct timespec t0, t1;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    ev.events = EPOLLIN; ev.data.u64 = 0x6666;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, tf, &ev) == 0);
    n = wait_ev(ep, out, 4, BLOCK_MS);
    clock_gettime(CLOCK_MONOTONIC, &t1);
    long ms = (t1.tv_sec - t0.tv_sec) * 1000 + (t1.tv_nsec - t0.tv_nsec) / 1000000;
    SHOWI("timerfd: Meldung", n);
    SHOW("  mindestens 15 ms", ms >= 15);
    v = 0;
    SHOWI("timerfd lesen", read(tf, &v, 8));
    SHOW("  Abläufe >= 1", v >= 1);
    struct itimerspec cur;
    NEED(timerfd_gettime(tf, &cur) == 0);
    SHOW("einmalig: danach abgelaufen", cur.it_value.tv_sec == 0 && cur.it_value.tv_nsec == 0);
    T("timerfd ungültiger Zeitgeber", timerfd_create(9999, 0));
    its.it_interval.tv_nsec = 10 * 1000000L;
    its.it_value.tv_nsec = 10 * 1000000L;
    NEED(timerfd_settime(tf, 0, &its, NULL) == 0);
    for (int i = 0; i < 3; i++) {
        SHOWI("periodisch: Meldung", wait_ev(ep, out, 4, BLOCK_MS));
        NEED(read(tf, &v, 8) == 8 && v >= 1);
    }
    its.it_value.tv_nsec = 0;
    its.it_interval.tv_nsec = 0;
    NEED(timerfd_settime(tf, 0, &its, NULL) == 0);
    close(tf);

    /* Ein Kindprozess weckt epoll_wait im Elternteil durch Schreiben in die Pipe. */
    NEED(pipe(p) == 0);
    ev.events = EPOLLIN; ev.data.u64 = 0x7777;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, p[0], &ev) == 0);
    pid_t c = fork();
    NEED(c >= 0);
    if (c == 0) {
        struct timespec d = { 0, 30 * 1000000L };
        nanosleep(&d, NULL);
        CNEED(write(p[1], "w", 1) == 1);
        _exit(0);
    }
    n = wait_ev(ep, out, 4, BLOCK_MS);
    SHOWI("kind weckt epoll_wait", n);
    SHOW("  Daten gleich", n == 1 && out[0].data.u64 == 0x7777);
    int st = 0;
    NEED(waitpid(c, &st, 0) == c);
    SHOW("kind beendet", WIFEXITED(st) && WEXITSTATUS(st) == 0);
    return 0;
}
