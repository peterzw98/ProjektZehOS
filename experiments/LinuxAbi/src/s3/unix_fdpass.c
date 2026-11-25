/* AF_UNIX: socketpair (STREAM, DGRAM, SEQPACKET), benannter Socket (bind/listen/accept/connect),
 * SCM_RIGHTS (Dateideskriptoren über sendmsg/recvmsg, auch mehrere, auch an ein Kind),
 * MSG_CMSG_CLOEXEC, MSG_PEEK, MSG_CTRUNC, SO_PEERCRED, shutdown. */
#define _GNU_SOURCE
#include "util.h"
#include <fcntl.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <sys/wait.h>

static int send_fds(int s, const int *fds, int n, const char *data, int len)
{
    char cbuf[CMSG_SPACE(sizeof(int) * 4)];
    memset(cbuf, 0, sizeof cbuf);
    struct iovec iov = { (void *)data, (size_t)len };
    struct msghdr m;
    memset(&m, 0, sizeof m);
    m.msg_iov = &iov;
    m.msg_iovlen = 1;
    m.msg_control = cbuf;
    m.msg_controllen = CMSG_SPACE(sizeof(int) * n);
    struct cmsghdr *c = CMSG_FIRSTHDR(&m);
    c->cmsg_level = SOL_SOCKET;
    c->cmsg_type = SCM_RIGHTS;
    c->cmsg_len = CMSG_LEN(sizeof(int) * n);
    memcpy(CMSG_DATA(c), fds, sizeof(int) * n);
    return (int)sendmsg(s, &m, 0);
}

/* Liefert die Zahl empfangener Deskriptoren (in fds), -1 bei Fehler; *flags = msg_flags. */
static int recv_fds(int s, int *fds, int max, char *data, int len, int rflags, int *flags, int *got)
{
    char cbuf[CMSG_SPACE(sizeof(int) * 4)];
    memset(cbuf, 0, sizeof cbuf);
    struct iovec iov = { data, (size_t)len };
    struct msghdr m;
    memset(&m, 0, sizeof m);
    m.msg_iov = &iov;
    m.msg_iovlen = 1;
    if (max > 0) {
        m.msg_control = cbuf;
        m.msg_controllen = CMSG_SPACE(sizeof(int) * max);
    }
    ssize_t r = recvmsg(s, &m, rflags);
    if (r < 0) return -1;
    *got = (int)r;
    *flags = m.msg_flags;
    int n = 0;
    for (struct cmsghdr *c = CMSG_FIRSTHDR(&m); c; c = CMSG_NXTHDR(&m, c)) {
        if (c->cmsg_level == SOL_SOCKET && c->cmsg_type == SCM_RIGHTS) {
            int k = (int)((c->cmsg_len - CMSG_LEN(0)) / sizeof(int));
            memcpy(fds + n, CMSG_DATA(c), sizeof(int) * k);
            n += k;
        }
    }
    return n;
}

int main(void)
{
    INIT();
    int sv[2];
    char buf[64];

    /* STREAM: Reihenfolge, Halbschließen, EOF. */
    NEED(socketpair(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0, sv) == 0);
    NEED(write(sv[0], "hallo", 5) == 5);
    SHOWI("stream lesen", read(sv[1], buf, sizeof buf));
    NEED(write(sv[1], "zurueck", 7) == 7);
    SHOWI("peek", recv(sv[0], buf, 3, MSG_PEEK));
    SHOWI("lesen nach peek", recv(sv[0], buf, sizeof buf, 0));
    NEED(shutdown(sv[0], SHUT_WR) == 0);
    T("EOF nach shutdown", read(sv[1], buf, sizeof buf));
    SHOWI("Gegenrichtung noch offen", write(sv[1], "x", 1));
    T("write nach shutdown", send(sv[0], "x", 1, MSG_NOSIGNAL));
    close(sv[0]); close(sv[1]);

    /* DGRAM und SEQPACKET: Nachrichtengrenzen. */
    NEED(socketpair(AF_UNIX, SOCK_DGRAM, 0, sv) == 0);
    NEED(write(sv[0], "eins", 4) == 4 && write(sv[0], "zwei22", 6) == 6);
    SHOWI("dgram 1", read(sv[1], buf, sizeof buf));
    SHOWI("dgram 2 (abgeschnitten auf 3)", recv(sv[1], buf, 3, 0));
    T("dgram leer, nonblocking", recv(sv[1], buf, sizeof buf, MSG_DONTWAIT));
    close(sv[0]); close(sv[1]);
    NEED(socketpair(AF_UNIX, SOCK_SEQPACKET, 0, sv) == 0);
    NEED(write(sv[0], "ab", 2) == 2 && write(sv[0], "cdef", 4) == 4);
    SHOWI("seqpacket 1", read(sv[1], buf, sizeof buf));
    SHOWI("seqpacket 2", read(sv[1], buf, sizeof buf));
    close(sv[0]);
    T("seqpacket EOF", read(sv[1], buf, sizeof buf));
    close(sv[1]);
    T("socketpair ungültiger Typ", socketpair(AF_UNIX, 99, 0, sv));
    T("socketpair ungültiges Protokoll", socketpair(AF_UNIX, SOCK_STREAM, 5, sv));

    /* SCM_RIGHTS: Datei mit geteiltem Dateizeiger. */
    NEED(socketpair(AF_UNIX, SOCK_STREAM, 0, sv) == 0);
    int f = open("datei", O_RDWR | O_CREAT | O_TRUNC, 0600);
    NEED(f >= 0 && write(f, "0123456789", 10) == 10 && lseek(f, 2, SEEK_SET) == 2);
    SHOWI("senden (1 fd)", send_fds(sv[0], &f, 1, "F", 1));
    int fds[4], flags = 0, got = 0;
    int n = recv_fds(sv[1], fds, 1, buf, 1, 0, &flags, &got);
    SHOWI("empfangen: Zahl der fd", n);
    SHOW("  neue Nummer", n == 1 && fds[0] != f && fds[0] > sv[1]);
    SHOW("  kein CTRUNC", (flags & MSG_CTRUNC) == 0);
    SHOWI("  lesen über fremden fd (Zeiger bei 2)", read(fds[0], buf, 3));
    SHOW("  Inhalt", memcmp(buf, "234", 3) == 0);
    SHOWI("  Zeiger beim Absender weitergerückt", lseek(f, 0, SEEK_CUR));
    SHOW("  CLOEXEC nicht gesetzt", (fcntl(fds[0], F_GETFD) & FD_CLOEXEC) == 0);
    close(fds[0]);

    /* mehrere Deskriptoren, MSG_CMSG_CLOEXEC, Pipe-Ende. */
    int p[2];
    NEED(pipe(p) == 0);
    int two[2] = { p[1], f };
    SHOWI("senden (2 fd)", send_fds(sv[0], two, 2, "ZZ", 2));
    n = recv_fds(sv[1], fds, 2, buf, 2, MSG_CMSG_CLOEXEC, &flags, &got);
    SHOWI("empfangen: Zahl der fd", n);
    SHOW("  CLOEXEC gesetzt", n == 2 && (fcntl(fds[0], F_GETFD) & FD_CLOEXEC) && (fcntl(fds[1], F_GETFD) & FD_CLOEXEC));
    SHOWI("  schreiben über empfangenes Pipe-Ende", write(fds[0], "pp", 2));
    SHOWI("  lesen am Original", read(p[0], buf, 2));
    close(fds[0]); close(fds[1]);

    /* Kein Platz für Steuerdaten: die Deskriptoren gehen verloren, MSG_CTRUNC. */
    SHOWI("senden (2 fd) für CTRUNC", send_fds(sv[0], two, 2, "T", 1));
    n = recv_fds(sv[1], fds, 0, buf, 1, 0, &flags, &got);
    SHOW("  CTRUNC", (flags & MSG_CTRUNC) != 0);
    SHOWI("  empfangene fd", n);
    for (int i = 0; i < n; i++) close(fds[i]);

    /* Ungültiger fd und falscher Typ. */
    int bad = 999;
    T("senden: ungültiger fd", send_fds(sv[0], &bad, 1, "B", 1));

    /* Peer-Daten. */
    struct ucred cr;
    socklen_t cl = sizeof cr;
    NEED(getsockopt(sv[0], SOL_SOCKET, SO_PEERCRED, &cr, &cl) == 0);
    SHOW("SO_PEERCRED: pid/uid/gid des Prozesses", cr.pid == getpid() && cr.uid == getuid() && cr.gid == getgid());
    int type = 0;
    cl = sizeof type;
    NEED(getsockopt(sv[0], SOL_SOCKET, SO_TYPE, &type, &cl) == 0);
    SHOW("SO_TYPE STREAM", type == SOCK_STREAM);
    close(sv[0]); close(sv[1]);

    /* Benannter Socket im Arbeitsverzeichnis; Deskriptor an ein Kind per fork. */
    struct sockaddr_un a;
    memset(&a, 0, sizeof a);
    a.sun_family = AF_UNIX;
    strcpy(a.sun_path, "s.sock");
    int ls = socket(AF_UNIX, SOCK_STREAM, 0);
    NEED(ls >= 0 && bind(ls, (struct sockaddr *)&a, sizeof a) == 0);
    T("bind erneut (EADDRINUSE)", ({ int t = socket(AF_UNIX, SOCK_STREAM, 0); bind(t, (struct sockaddr *)&a, sizeof a); }));
    NEED(listen(ls, 4) == 0);
    int cs = socket(AF_UNIX, SOCK_STREAM, 0);
    NEED(connect(cs, (struct sockaddr *)&a, sizeof a) == 0);
    int as = accept4(ls, NULL, NULL, SOCK_CLOEXEC);
    NEED(as >= 0);
    SHOW("accept4: CLOEXEC", (fcntl(as, F_GETFD) & FD_CLOEXEC) != 0);
    NEED(write(cs, "ping", 4) == 4);
    SHOWI("benannter Socket lesen", read(as, buf, sizeof buf));
    struct sockaddr_un pa;
    socklen_t pl = sizeof pa;
    NEED(getsockname(ls, (struct sockaddr *)&pa, &pl) == 0);
    SHOW("getsockname: Pfad", pa.sun_family == AF_UNIX && strcmp(pa.sun_path, "s.sock") == 0);
    int one = socket(AF_UNIX, SOCK_STREAM, 0);
    struct sockaddr_un na;
    memset(&na, 0, sizeof na);
    na.sun_family = AF_UNIX;
    strcpy(na.sun_path, "gibt-es-nicht");
    T("connect: kein Socket", connect(one, (struct sockaddr *)&na, sizeof na));
    NEED(close(open("datei2", O_CREAT | O_WRONLY, 0600)) == 0);
    strcpy(na.sun_path, "datei2");
    T("connect: Datei statt Socket", connect(one, (struct sockaddr *)&na, sizeof na));
    close(one);

    pid_t c = fork();
    NEED(c >= 0);
    if (c == 0) {
        /* Kind: nimmt über die Verbindung einen Deskriptor an, schreibt hinein, meldet Erfolg. */
        int cf[1], fl, g;
        CNEED(recv_fds(as, cf, 1, buf, 1, 0, &fl, &g) == 1);
        CNEED(write(cf[0], "kind", 4) == 4);
        _exit(0);
    }
    SHOWI("senden an Kind", send_fds(cs, &p[1], 1, "K", 1));
    int st = 0;
    NEED(waitpid(c, &st, 0) == c);
    SHOW("Kind fertig", WIFEXITED(st) && WEXITSTATUS(st) == 0);
    SHOWI("Kind hat in die Pipe geschrieben", read(p[0], buf, sizeof buf));
    close(as); close(cs); close(ls);
    T("unlink Socket-Datei", unlink("s.sock"));
    return 0;
}
