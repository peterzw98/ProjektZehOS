/* V33 (Ladybird): ioctl FIONBIO (O_NONBLOCK wie F_SETFL, LibCore set_blocking) und FIONREAD/TIOCINQ (ungelesene Bytes) auf Pipe,
 * Unix-Socketpaar (Stream, Seqpacket, Datagramm), lauschendem Socket, eventfd und inotify. Die Ausgabe ist mit Linux 6.12 gleich
 * (expected/v33/fion.txt). */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/eventfd.h>
#include <sys/inotify.h>
#include <sys/ioctl.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <termios.h>
#include <unistd.h>

#define T(label, expr) do { errno = 0; long r_ = (long)(expr); printf("%s: ret=%ld errno=%d\n", label, r_, errno); } while (0)
#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)

static int nonblock(int fd) { return (fcntl(fd, F_GETFL) & O_NONBLOCK) != 0; }

static void avail(const char *label, int fd)
{
    int n = -1;
    errno = 0;
    int r = ioctl(fd, FIONREAD, &n);
    printf("%s: ret=%d errno=%d n=%d\n", label, r, errno, n);
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    alarm(30);
    char buf[64];
    int on = 1, off = 0;

    int p[2];
    NEED(pipe(p) == 0);
    T("FIONBIO Pipe an", ioctl(p[0], FIONBIO, &on));
    printf("Pipe nonblock lesen=%d schreiben=%d\n", nonblock(p[0]), nonblock(p[1]));
    T("read leere Pipe", read(p[0], buf, 8));
    avail("FIONREAD leer", p[0]);
    NEED(write(p[1], "hallo!!", 7) == 7);
    avail("FIONREAD Lesende", p[0]);
    avail("FIONREAD Schreibende", p[1]);
    NEED(read(p[0], buf, 3) == 3);
    int n = -1;
    T("TIOCINQ Pipe", ioctl(p[0], TIOCINQ, &n));
    printf("TIOCINQ n=%d\n", n);
    T("FIONBIO Pipe aus", ioctl(p[0], FIONBIO, &off));
    printf("Pipe nonblock lesen=%d\n", nonblock(p[0]));
    T("FIONREAD Zeiger ungueltig", ioctl(p[0], FIONREAD, (int *)8));

    int s[2];
    NEED(socketpair(AF_UNIX, SOCK_STREAM, 0, s) == 0);
    T("FIONBIO Socket an", ioctl(s[0], FIONBIO, &on));
    printf("Socket nonblock=%d gegenseite=%d\n", nonblock(s[0]), nonblock(s[1]));
    T("recv ohne Daten", recv(s[0], buf, 8, 0));
    NEED(write(s[1], "abc", 3) == 3 && write(s[1], "de", 2) == 2);
    avail("FIONREAD Stream", s[0]);
    avail("FIONREAD Stream Gegenseite", s[1]);
    NEED(read(s[0], buf, 2) == 2);
    avail("FIONREAD Stream Rest", s[0]);

    int q[2];
    NEED(socketpair(AF_UNIX, SOCK_SEQPACKET, 0, q) == 0);
    NEED(write(q[1], "abc", 3) == 3 && write(q[1], "defg", 4) == 4);
    avail("FIONREAD Seqpacket", q[0]);
    int d[2];
    NEED(socketpair(AF_UNIX, SOCK_DGRAM, 0, d) == 0);
    NEED(write(d[1], "abc", 3) == 3 && write(d[1], "defg", 4) == 4);
    avail("FIONREAD Dgram", d[0]);

    int l = socket(AF_UNIX, SOCK_STREAM, 0);
    NEED(l >= 0);
    avail("FIONREAD unverbunden", l);
    struct sockaddr_un a;
    memset(&a, 0, sizeof a);
    a.sun_family = AF_UNIX;
    memcpy(a.sun_path, "\0v33fion", 8);
    NEED(bind(l, (struct sockaddr *)&a, (socklen_t)(offsetof(struct sockaddr_un, sun_path) + 8)) == 0);
    NEED(listen(l, 4) == 0);
    avail("FIONREAD lauschend", l);

    int e = eventfd(0, 0);
    NEED(e >= 0);
    T("FIONBIO eventfd", ioctl(e, FIONBIO, &on));
    T("read eventfd leer", read(e, buf, 8));
    avail("FIONREAD eventfd", e);

    int in = inotify_init1(0);
    NEED(in >= 0);
    int wd = inotify_add_watch(in, "/", IN_MODIFY);
    NEED(wd > 0);
    NEED(inotify_rm_watch(in, wd) == 0);
    avail("FIONREAD inotify", in);

    T("FIONBIO fd ungueltig", ioctl(99, FIONBIO, &on));
    T("FIONBIO Zeiger ungueltig", ioctl(s[1], FIONBIO, (int *)8));
    puts("fertig");
    return 0;
}
