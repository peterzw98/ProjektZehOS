/* V16 (Ladybird LibIPC): socketpair(AF_UNIX) Stream und Seqpacket in einem Prozess. Datei per SCM_RIGHTS (Sender schließt seine Nummer
 * vorher), MSG_TRUNC, MSG_CTRUNC, MSG_PEEK, shutdown je Richtung, EOF/EPIPE, poll nach dem Ende der Gegenseite, SOCK_NONBLOCK,
 * getsockopt SO_TYPE. Die Ausgabe ist mit Linux 6.12 gleich (expected/v16/pair.txt); Puffergrößen gibt das Programm nicht aus. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <unistd.h>

static void show(const char *what, long r)
{
    printf("%s=%ld errno=%d\n", what, r, r < 0 ? errno : 0);
}

/* sendmsg mit n Dateien (n = 0: ohne Hilfsdaten). */
static long send_fds(int s, const char *data, size_t len, const int *fds, int n)
{
    char ctrl[CMSG_SPACE(4 * sizeof(int))];
    struct iovec iov = { (void *)data, len };
    struct msghdr m = { .msg_iov = &iov, .msg_iovlen = 1 };
    if (n > 0) {
        memset(ctrl, 0, sizeof ctrl);
        m.msg_control = ctrl;
        m.msg_controllen = CMSG_SPACE(n * sizeof(int));
        struct cmsghdr *c = CMSG_FIRSTHDR(&m);
        c->cmsg_level = SOL_SOCKET;
        c->cmsg_type = SCM_RIGHTS;
        c->cmsg_len = CMSG_LEN(n * sizeof(int));
        memcpy(CMSG_DATA(c), fds, n * sizeof(int));
    }
    return sendmsg(s, &m, MSG_NOSIGNAL);
}

/* recvmsg in buf; Hilfsdatenpuffer für room Dateien; schreibt die empfangenen Nummern nach fds und ihre Anzahl nach *n. */
static long recv_fds(int s, char *buf, size_t len, int room, int *fds, int *n, int *flags, int how)
{
    char ctrl[CMSG_SPACE(4 * sizeof(int))];
    struct iovec iov = { buf, len };
    struct msghdr m = { .msg_iov = &iov, .msg_iovlen = 1 };
    if (room > 0) {
        m.msg_control = ctrl;
        m.msg_controllen = CMSG_SPACE(room * sizeof(int));
    }
    long r = recvmsg(s, &m, how);
    *n = 0;
    *flags = m.msg_flags;
    if (r >= 0 && room > 0) {
        for (struct cmsghdr *c = CMSG_FIRSTHDR(&m); c; c = CMSG_NXTHDR(&m, c)) {
            if (c->cmsg_level == SOL_SOCKET && c->cmsg_type == SCM_RIGHTS) {
                int k = (int)((c->cmsg_len - CMSG_LEN(0)) / sizeof(int));
                memcpy(fds + *n, CMSG_DATA(c), k * sizeof(int));
                *n += k;
            }
        }
    }
    return r;
}

int main(void)
{
    int sv[2], p[2], q[2], fds[4], n, fl;
    char buf[64];
    struct stat st;

    setvbuf(stdout, NULL, _IONBF, 0); /* jede Zeile sofort: ein Halt zeigt die Stelle */
    show("socketpair_stream", socketpair(AF_UNIX, SOCK_STREAM, 0, sv));
    show("write", write(sv[0], "hal", 3));
    show("write", write(sv[0], "lo", 2));
    long r = read(sv[1], buf, sizeof buf);
    printf("read=%ld text=%.*s\n", r, (int)(r > 0 ? r : 0), buf);
    show("fstat", fstat(sv[0], &st));
    printf("issock=%d\n", S_ISSOCK(st.st_mode));
    int type = 0;
    socklen_t tl = sizeof type;
    show("getsockopt", getsockopt(sv[0], SOL_SOCKET, SO_TYPE, &type, &tl));
    printf("so_type=%d len=%u\n", type, (unsigned)tl);

    /* Datei per SCM_RIGHTS: der Sender schließt sofort, die Nachricht hält die Pipe offen. */
    show("pipe", pipe(p));
    show("sendmsg_rights", send_fds(sv[0], "F", 1, &p[1], 1));
    show("close_sender", close(p[1]));
    r = recv_fds(sv[1], buf, sizeof buf, 2, fds, &n, &fl, 0);
    printf("recvmsg=%ld fds=%d flags=%d neu=%d\n", r, n, fl, n == 1 && fds[0] != p[1] ? 1 : n == 1);
    show("write_via", write(fds[0], "via", 3));
    show("close_via", close(fds[0]));
    r = read(p[0], buf, sizeof buf);
    printf("pipe_read=%ld text=%.*s\n", r, (int)(r > 0 ? r : 0), buf);
    show("pipe_eof", read(p[0], buf, sizeof buf));
    close(p[0]);

    /* Stream liest über eine Nachricht mit Dateien hinweg und hält danach an (eine Dateiliste je Aufruf). */
    show("pipe", pipe(p));
    show("send_ab", send_fds(sv[0], "ab", 2, NULL, 0));
    show("send_cd_rights", send_fds(sv[0], "cd", 2, &p[0], 1));
    show("send_ef_rights", send_fds(sv[0], "ef", 2, &p[0], 1));
    show("send_gh", send_fds(sv[0], "gh", 2, NULL, 0));
    for (int i = 1; i <= 3; i++) {
        r = recv_fds(sv[1], buf, sizeof buf, 1, fds, &n, &fl, 0);
        printf("recv%d=%ld fds=%d text=%.*s\n", i, r, n, (int)(r > 0 ? r : 0), buf);
        if (n == 1)
            close(fds[0]);
    }

    /* MSG_CTRUNC: drei Dateien, Platz für zwei (CMSG_SPACE(sizeof(int)) = 24 Bytes); die dritte wird geschlossen. */
    show("pipe", pipe(q));
    int three[3] = { p[1], p[1], q[1] };
    show("send_three", send_fds(sv[0], "3", 1, three, 3));
    close(p[1]);
    close(q[1]);
    r = recv_fds(sv[1], buf, sizeof buf, 1, fds, &n, &fl, 0);
    printf("recv_ctrunc=%ld fds=%d ctrunc=%d\n", r, n, (fl & MSG_CTRUNC) != 0);
    show("q_eof", read(q[0], buf, sizeof buf));
    for (int i = 0; i < n; i++)
        close(fds[i]);
    show("p_eof", read(p[0], buf, sizeof buf));
    close(p[0]);
    close(q[0]);

    /* MSG_PEEK auf dem Stream. */
    show("write_peek", write(sv[0], "pk", 2));
    show("recv_peek", recv(sv[1], buf, sizeof buf, MSG_PEEK));
    show("recv_after_peek", recv(sv[1], buf, sizeof buf, 0));

    /* shutdown: SHUT_WR beendet nur die eigene Senderichtung. */
    show("write_1", write(sv[0], "1", 1));
    show("shutdown_wr", shutdown(sv[0], SHUT_WR));
    show("send_after_shut", send(sv[0], "2", 1, MSG_NOSIGNAL));
    show("read_rest", read(sv[1], buf, sizeof buf));
    show("read_eof", read(sv[1], buf, sizeof buf));
    show("write_back", write(sv[1], "ok", 2));
    show("read_back", read(sv[0], buf, sizeof buf));
    show("shutdown_bad", shutdown(sv[0], 7));

    /* close der einen Seite: Gegenseite POLLIN|POLLHUP|POLLRDHUP, EOF, EPIPE. */
    show("close_a", close(sv[0]));
    struct pollfd pf = { sv[1], POLLIN | POLLRDHUP, 0 };
    show("poll", poll(&pf, 1, 1000));
    printf("revents in=%d hup=%d rdhup=%d\n", (pf.revents & POLLIN) != 0, (pf.revents & POLLHUP) != 0, (pf.revents & POLLRDHUP) != 0);
    show("read_closed", read(sv[1], buf, sizeof buf));
    show("send_closed", send(sv[1], "x", 1, MSG_NOSIGNAL));
    close(sv[1]);

    /* Seqpacket: Grenzen und MSG_TRUNC. */
    show("socketpair_seq", socketpair(AF_UNIX, SOCK_SEQPACKET | SOCK_CLOEXEC, 0, sv));
    printf("cloexec=%d\n", fcntl(sv[0], F_GETFD) & FD_CLOEXEC);
    show("send_hallo", send(sv[0], "hallo", 5, 0));
    show("send_du", send(sv[0], "du", 2, 0));
    r = recv_fds(sv[1], buf, 3, 0, fds, &n, &fl, 0);
    printf("recv_trunc=%ld trunc=%d text=%.*s\n", r, (fl & MSG_TRUNC) != 0, (int)(r > 0 ? r : 0), buf);
    show("recv_next", recv(sv[1], buf, sizeof buf, 0));
    show("send_ok", send(sv[0], "ok", 2, 0));
    show("recv_trunc_len", recv(sv[1], buf, 1, MSG_TRUNC));

    /* Datei über Seqpacket, ungelesen verworfen: close des Empfängers schließt sie. */
    show("pipe", pipe(p));
    show("send_rights_seq", send_fds(sv[0], "x", 1, &p[1], 1));
    close(p[1]);
    show("close_receiver", close(sv[1]));
    show("dropped_eof", read(p[0], buf, sizeof buf));
    show("send_no_peer", send(sv[0], "y", 1, MSG_NOSIGNAL));
    close(p[0]);
    close(sv[0]);

    /* SOCK_NONBLOCK: leer EAGAIN, voll EAGAIN. */
    show("socketpair_nb", socketpair(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK, 0, sv));
    show("recv_empty", recv(sv[1], buf, sizeof buf, 0));
    static char big[4096];
    long total = 0, w;
    while ((w = send(sv[0], big, sizeof big, 0)) > 0)
        total += w;
    printf("full errno=%d some=%d\n", errno, total > 0);
    struct pollfd po = { sv[0], POLLOUT, 0 };
    show("poll_full", poll(&po, 1, 0));
    close(sv[0]);
    close(sv[1]);
    printf("ende\n");
    return 0;
}
