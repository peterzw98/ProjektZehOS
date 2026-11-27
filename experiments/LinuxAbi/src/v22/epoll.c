/* V22 (Ladybird): epoll_create1, epoll_ctl ADD/MOD/DEL, epoll_wait/epoll_pwait (LT, EPOLLET, EPOLLONESHOT) auf Pipes, eventfd,
 * Unix-Socketpaar und Terminal (stdout), Wecken aus einem anderen Thread. Ohne timerfd und fork (s3 pipe_epoll braucht beides).
 * Die Ausgabe ist mit Linux 6.12 gleich (expected/v22/epoll.txt). */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/epoll.h>
#include <sys/eventfd.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define T(label, expr) do { errno = 0; long r_ = (long)(expr); printf("%s: ret=%ld errno=%d\n", label, r_, errno); } while (0)
#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)

static int wfd;

static void *writer(void *arg)
{
    (void)arg;
    struct timespec d = { 0, 30 * 1000000L };
    nanosleep(&d, NULL);
    if (write(wfd, "w", 1) != 1) _exit(3);
    return NULL;
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    alarm(30);
    struct epoll_event ev, out[4];
    T("create1 Flag ungueltig", epoll_create1(1));
    T("create size 0", epoll_create(0));
    int ep = epoll_create1(EPOLL_CLOEXEC);
    NEED(ep >= 0);
    printf("CLOEXEC=%d\n", (fcntl(ep, F_GETFD) & FD_CLOEXEC) != 0);
    char buf[64];
    T("read auf epoll", read(ep, buf, 8));
    int p[2];
    NEED(pipe2(p, O_NONBLOCK) == 0);
    T("ctl ADD Eventzeiger NULL", epoll_ctl(ep, EPOLL_CTL_ADD, p[0], NULL));
    T("ctl kein epoll", epoll_ctl(p[0], EPOLL_CTL_ADD, p[1], &ev));
    ev.events = EPOLLOUT | EPOLLEXCLUSIVE | EPOLLONESHOT;
    T("ctl EXCLUSIVE mit ONESHOT", epoll_ctl(ep, EPOLL_CTL_ADD, p[1], &ev));
    T("wait epfd ungueltig", epoll_wait(99, out, 4, 0));
    T("wait kein epoll", epoll_wait(p[0], out, 4, 0));
    T("wait Puffer ungueltig", epoll_wait(ep, (struct epoll_event *)16, 4, 0));

    /* Ohne angefragte Ereignisse: nur HUP und ERR. */
    ev.events = 0; ev.data.u64 = 0x44;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, p[0], &ev) == 0);
    T("ohne Ereignisse", epoll_wait(ep, out, 4, 0));
    close(p[1]);
    int n = epoll_wait(ep, out, 4, 0);
    printf("HUP ungefragt=%d events=%#x\n", n, n == 1 ? out[0].events : 0);
    close(p[0]);
    T("nach close keine Meldung", epoll_wait(ep, out, 4, 0));

    /* Schreibende ohne Leser: EPOLLERR. */
    NEED(pipe2(p, O_NONBLOCK) == 0);
    ev.events = EPOLLOUT; ev.data.u64 = 0x55;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, p[1], &ev) == 0);
    n = epoll_wait(ep, out, 4, 0);
    printf("schreibbar=%d events=%#x\n", n, n == 1 ? out[0].events : 0);
    close(p[0]);
    signal(SIGPIPE, SIG_IGN);
    n = epoll_wait(ep, out, 4, 0);
    printf("ohne Leser=%d events=%#x\n", n, n == 1 ? out[0].events : 0);
    NEED(epoll_ctl(ep, EPOLL_CTL_DEL, p[1], NULL) == 0);
    close(p[1]);

    /* maxevents 1 mit zwei bereiten Pipes: reihum. */
    int a[2], b[2];
    NEED(pipe2(a, O_NONBLOCK) == 0 && pipe2(b, O_NONBLOCK) == 0);
    ev.events = EPOLLIN; ev.data.u64 = 1;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, a[0], &ev) == 0);
    ev.data.u64 = 2;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, b[0], &ev) == 0);
    NEED(write(a[1], "a", 1) == 1 && write(b[1], "b", 1) == 1);
    uint64_t first = 0, second = 0;
    NEED(epoll_wait(ep, out, 1, 0) == 1); first = out[0].data.u64;
    NEED(epoll_wait(ep, out, 1, 0) == 1); second = out[0].data.u64;
    printf("reihum verschieden=%d\n", first != second);
    T("beide bereit", epoll_wait(ep, out, 4, 0));
    close(a[0]); close(a[1]); close(b[0]); close(b[1]);

    /* eventfd flankengesteuert. */
    int ef = eventfd(0, EFD_NONBLOCK);
    NEED(ef >= 0);
    ev.events = EPOLLIN | EPOLLET; ev.data.u64 = 5;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, ef, &ev) == 0);
    uint64_t v = 1;
    T("eventfd leer", epoll_wait(ep, out, 4, 0));
    NEED(write(ef, &v, 8) == 8);
    T("eventfd Flanke", epoll_wait(ep, out, 4, 0));
    T("eventfd ohne neues write", epoll_wait(ep, out, 4, 0));
    NEED(write(ef, &v, 8) == 8);
    T("eventfd neues write", epoll_wait(ep, out, 4, 0));
    close(ef);

    /* Unix-Socketpaar. */
    int sv[2];
    NEED(socketpair(AF_UNIX, SOCK_STREAM, 0, sv) == 0);
    ev.events = EPOLLIN | EPOLLOUT; ev.data.u64 = 7;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, sv[0], &ev) == 0);
    n = epoll_wait(ep, out, 4, 0);
    printf("socket nur schreibbar=%d events=%#x\n", n, n == 1 ? out[0].events : 0);
    ev.events = EPOLLIN | EPOLLET; ev.data.u64 = 8;
    NEED(epoll_ctl(ep, EPOLL_CTL_MOD, sv[0], &ev) == 0);
    NEED(write(sv[1], "hi", 2) == 2);
    n = epoll_wait(ep, out, 4, 0);
    printf("socket Flanke=%d data=%llu\n", n, n == 1 ? (unsigned long long)out[0].data.u64 : 0ULL);
    T("socket ohne neue Daten", epoll_wait(ep, out, 4, 0));
    NEED(write(sv[1], "!", 1) == 1);
    T("socket neue Daten", epoll_wait(ep, out, 4, 0));
    close(sv[1]);
    n = epoll_wait(ep, out, 4, 0);
    printf("socket Gegenseite zu=%d hup=%d\n", n, n == 1 && (out[0].events & EPOLLHUP) != 0);
    close(sv[0]);

    /* Terminal (stdout) ist schreibbar. */
    ev.events = EPOLLOUT; ev.data.u64 = 9;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, 1, &ev) == 0);
    n = epoll_wait(ep, out, 4, 0);
    printf("stdout schreibbar=%d data=%llu\n", n, n == 1 ? (unsigned long long)out[0].data.u64 : 0ULL);
    NEED(epoll_ctl(ep, EPOLL_CTL_DEL, 1, NULL) == 0);

    /* Frist und epoll_pwait mit Maske. */
    NEED(pipe(p) == 0);
    ev.events = EPOLLIN; ev.data.u64 = 0x77;
    NEED(epoll_ctl(ep, EPOLL_CTL_ADD, p[0], &ev) == 0);
    struct timespec t0, t1;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    T("wait Frist 50 ms", epoll_wait(ep, out, 4, 50));
    clock_gettime(CLOCK_MONOTONIC, &t1);
    long ms = (t1.tv_sec - t0.tv_sec) * 1000 + (t1.tv_nsec - t0.tv_nsec) / 1000000;
    printf("  mindestens 45 ms=%d\n", ms >= 45);
    sigset_t m;
    sigemptyset(&m);
    T("pwait Maske, Frist 0", epoll_pwait(ep, out, 4, 0, &m));

    /* Ein anderer Thread weckt epoll_wait ohne Frist. */
    wfd = p[1];
    pthread_t th;
    NEED(pthread_create(&th, NULL, writer, NULL) == 0);
    n = epoll_wait(ep, out, 4, -1);
    printf("thread weckt=%d data=%#llx\n", n, n == 1 ? (unsigned long long)out[0].data.u64 : 0ULL);
    NEED(pthread_join(th, NULL) == 0);
    close(ep);
    printf("ende\n");
    return 0;
}
