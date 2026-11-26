/* Ladybird V16 (IPC wie LibIPC): Unix-Sockets und Dateiweitergabe.
 * Teil 1: socketpair(AF_UNIX, SOCK_STREAM), poll und O_NONBLOCK auf einem Socket, posix_spawn eines Kindes mit dem zweiten Ende als
 * fd 3 (dup2 über die file actions), Datei per SCM_RIGHTS an das Kind (der Elternprozess schließt seine Nummer gleich danach; die
 * Datei lebt in der Nachricht weiter), das Kind liest sie über die empfangene Nummer und schickt den Inhalt zurück, danach ein Echo.
 * Nach dem Ende des Kindes: EOF, POLLHUP, EPIPE (MSG_NOSIGNAL).
 * Teil 2: socket/bind/listen/connect/accept4 mit abstraktem Namen und mit Pfadnamen im Arbeitsverzeichnis, getsockname/getpeername,
 * Fehlerfälle (EADDRINUSE, ECONNREFUSED, ENOENT nach unlink, EINVAL, EAGAIN), SOCK_SEQPACKET hält Nachrichtengrenzen.
 * Das Kind ist ein statisches Mini-ELF, das dieses Programm ins Arbeitsverzeichnis schreibt (der Gast hat die Programmdatei selbst
 * nicht im Dateisystem). Keine PIDs und keine Nummern außer festen in der Ausgabe. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <spawn.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <unistd.h>

extern char **environ;

/* Datenbereich des Kindes ab 0x400200 (Segment RWX): msghdr, iovec, Steuerpuffer, ein Byte, Puffer. */
#define BASE 0x400000u
#define DATA 0x200u
#define D (BASE + DATA)
#define MSG (D + 0)
#define IOV (D + 64)
#define CTRL (D + 80)
#define BYTE (D + 112)
#define BUF (D + 128)
#define FILESIZE 0x300u

static unsigned char img[FILESIZE];
static int len;

static void b1(int v) { img[0x78 + len++] = (unsigned char)v; }
static void b4(unsigned v) { for (int i = 0; i < 4; i++) b1((int)((v >> (8 * i)) & 0xff)); }
static void movr(int op, unsigned v) { b1(op); b4(v); } /* B8 eax, BF edi, BE esi, BA edx */
static void sys(void) { b1(0x0f); b1(0x05); }
/* Bedingung erfüllt (je rel8 über den Fehlerblock), sonst exit_group(fail). */
static void ok_or_exit(int fail) { b1(0x74); b1(12); movr(0xb8, 231); movr(0xbf, (unsigned)fail); sys(); }
static void put64(unsigned off, unsigned long v) { for (int i = 0; i < 8; i++) img[off + i] = (unsigned char)(v >> (8 * i)); }

/* Kind (fd 3 = Socket): recvmsg ein Byte mit SCM_RIGHTS (sonst exit 80), read über die empfangene Nummer, Inhalt zurück auf fd 3,
 * dann ein Echo (read fd 3, write fd 3), exit_group(0). */
static int write_child(const char *path)
{
    memset(img, 0, sizeof img);
    len = 0;
    movr(0xb8, 47); movr(0xbf, 3); movr(0xbe, MSG); b1(0x31); b1(0xd2); sys(); /* recvmsg(3, &msg, 0) */
    b1(0x48); b1(0x83); b1(0xf8); b1(1);                                       /* cmp rax, 1 */
    ok_or_exit(80);
    b1(0x8b); b1(0x3c); b1(0x25); b4(CTRL + 16);                               /* mov edi, [CMSG_DATA] */
    movr(0xb8, 0); movr(0xbe, BUF); movr(0xba, 128); sys();                    /* read(fd, buf, 128) */
    b1(0x89); b1(0xc2);                                                        /* mov edx, eax */
    movr(0xb8, 1); movr(0xbf, 3); movr(0xbe, BUF); sys();                      /* write(3, buf, n) */
    movr(0xb8, 0); movr(0xbf, 3); movr(0xbe, BUF); movr(0xba, 128); sys();     /* read(3, buf, 128) */
    b1(0x89); b1(0xc2);
    movr(0xb8, 1); movr(0xbf, 3); movr(0xbe, BUF); sys();                      /* write(3, buf, n) */
    movr(0xb8, 231); movr(0xbf, 0); sys();

    /* msghdr: name 0, iov, iovlen 1, control, controllen 32; iovec: BYTE, 1. */
    put64(DATA + 16, IOV); put64(DATA + 24, 1); put64(DATA + 32, CTRL); put64(DATA + 40, 32);
    put64(DATA + 64, BYTE); put64(DATA + 72, 1);

    memcpy(img, "\x7f" "ELF\x02\x01\x01", 7);
    img[16] = 2; img[18] = 62; img[20] = 1;                                     /* ET_EXEC, x86-64, Version */
    put64(24, BASE + 0x78);
    img[32] = 64; img[52] = 64; img[54] = 56; img[56] = 1;                      /* Programmköpfe ab 64, einer */
    img[64] = 1; img[68] = 7;                                                   /* PT_LOAD, RWX */
    put64(64 + 16, BASE); put64(64 + 24, BASE);
    put64(64 + 32, FILESIZE); put64(64 + 40, FILESIZE); put64(64 + 48, 0x1000);
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0755);
    if (fd < 0) return -1;
    int ok = write(fd, img, sizeof img) == (ssize_t)sizeof img;
    return close(fd) == 0 && ok ? 0 : -1;
}

static int send_fd(int sock, int fd)
{
    char byte = 'F';
    struct iovec iov = { &byte, 1 };
    union { struct cmsghdr h; char b[CMSG_SPACE(sizeof(int))]; } u;
    memset(&u, 0, sizeof u);
    struct msghdr m = { 0 };
    m.msg_iov = &iov;
    m.msg_iovlen = 1;
    m.msg_control = u.b;
    m.msg_controllen = sizeof u.b;
    struct cmsghdr *c = CMSG_FIRSTHDR(&m);
    c->cmsg_level = SOL_SOCKET;
    c->cmsg_type = SCM_RIGHTS;
    c->cmsg_len = CMSG_LEN(sizeof(int));
    memcpy(CMSG_DATA(c), &fd, sizeof fd);
    return (int)sendmsg(sock, &m, 0);
}

static void show(const char *what, ssize_t r)
{
    printf("%s=%zd errno=%d\n", what, r, r < 0 ? errno : 0);
    errno = 0;
}

static socklen_t abstract(struct sockaddr_un *a, const char *name)
{
    memset(a, 0, sizeof *a);
    a->sun_family = AF_UNIX;
    size_t n = strlen(name);
    memcpy(a->sun_path + 1, name, n);
    return (socklen_t)(offsetof(struct sockaddr_un, sun_path) + 1 + n);
}

static socklen_t pathname(struct sockaddr_un *a, const char *name)
{
    memset(a, 0, sizeof *a);
    a->sun_family = AF_UNIX;
    strcpy(a->sun_path, name);
    return (socklen_t)sizeof *a;
}

/* Gibt einen Namen aus getsockname/getpeername aus: Länge, Art und Name ohne führendes Nullbyte. */
static void show_name(const char *what, int r, struct sockaddr_un *a, socklen_t l)
{
    int name = (int)l - (int)offsetof(struct sockaddr_un, sun_path);
    if (r != 0) { show(what, r); return; }
    if (name <= 0) { printf("%s len=%d unbenannt\n", what, (int)l); return; }
    if (a->sun_path[0] == 0) printf("%s len=%d abstrakt=%.*s\n", what, (int)l, name - 1, a->sun_path + 1);
    else printf("%s len=%d pfad=%s\n", what, (int)l, a->sun_path);
}

static void part1(void)
{
    char buf[64];
    int sv[2];
    show("socketpair", socketpair(AF_UNIX, SOCK_STREAM, 0, sv));

    struct pollfd p = { sv[0], POLLIN | POLLOUT, 0 };
    int r = poll(&p, 1, 0);
    printf("poll_leer=%d revents=%d\n", r, p.revents);

    int fl = fcntl(sv[0], F_GETFL);
    show("setfl", fcntl(sv[0], F_SETFL, fl | O_NONBLOCK));
    printf("nonblock=%d\n", (fcntl(sv[0], F_GETFL) & O_NONBLOCK) != 0);
    show("read_nonblock", read(sv[0], buf, sizeof buf));
    fcntl(sv[0], F_SETFL, fl);

    int f = open("datei", O_RDWR | O_CREAT | O_TRUNC, 0644);
    if (f < 0 || write(f, "inhalt\n", 7) != 7 || lseek(f, 0, SEEK_SET) != 0) { printf("datei fehlgeschlagen\n"); return; }
    if (write_child("kind") != 0) { printf("kind fehlgeschlagen\n"); return; }

    posix_spawn_file_actions_t fa;
    posix_spawn_file_actions_init(&fa);
    posix_spawn_file_actions_adddup2(&fa, sv[1], 3);
    char *args[] = { "kind", NULL };
    pid_t pid = -1;
    int err = posix_spawn(&pid, "kind", &fa, NULL, args, environ);
    posix_spawn_file_actions_destroy(&fa);
    printf("spawn=%d\n", err);
    close(sv[1]);

    show("sendmsg", send_fd(sv[0], f));
    close(f); /* Die Datei lebt in der Nachricht weiter. */

    p.events = POLLIN;
    p.revents = 0;
    r = poll(&p, 1, 5000);
    printf("poll_daten=%d revents=%d\n", r, p.revents);
    ssize_t n = read(sv[0], buf, sizeof buf);
    printf("datei_vom_kind=%zd %.*s", n, n > 0 ? (int)n : 0, buf);
    if (n <= 0) printf("\n");

    show("echo_write", write(sv[0], "echo", 4));
    n = read(sv[0], buf, sizeof buf);
    printf("echo_read=%zd %.*s\n", n, n > 0 ? (int)n : 0, buf);

    int st = 0;
    pid_t w = waitpid(pid, &st, 0);
    printf("wait_ok=%d exited=%d code=%d\n", w == pid, WIFEXITED(st), WIFEXITED(st) ? WEXITSTATUS(st) : -1);

    p.revents = 0;
    r = poll(&p, 1, 0);
    printf("poll_ende=%d revents=%d\n", r, p.revents);
    show("eof", read(sv[0], buf, sizeof buf));
    show("epipe", send(sv[0], "x", 1, MSG_NOSIGNAL));
    close(sv[0]);
}

static void part2_abstract(void)
{
    char buf[16];
    int r;
    struct sockaddr_un a, got;
    socklen_t al = abstract(&a, "zehos-v16"), gl;

    int s = socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    printf("socket_ok=%d cloexec=%d\n", s >= 0, fcntl(s, F_GETFD) & FD_CLOEXEC);
    int u = socket(AF_UNIX, SOCK_STREAM, 0);
    show("listen_unbenannt", listen(u, 1));
    show("bind_abstrakt", bind(s, (struct sockaddr *)&a, al));
    show("bind_doppelt", bind(u, (struct sockaddr *)&a, al));
    show("listen", listen(s, 4));

    struct pollfd p = { s, POLLIN, 0 };
    r = poll(&p, 1, 0);
    printf("poll_ohne_verbindung=%d revents=%d\n", r, p.revents);

    int c = socket(AF_UNIX, SOCK_STREAM | SOCK_NONBLOCK, 0);
    show("connect", connect(c, (struct sockaddr *)&a, al));
    p.revents = 0;
    r = poll(&p, 1, 0);
    printf("poll_mit_verbindung=%d revents=%d\n", r, p.revents);

    gl = sizeof got;
    int x = accept4(s, (struct sockaddr *)&got, &gl, SOCK_CLOEXEC);
    printf("accept_ok=%d cloexec=%d\n", x >= 0, fcntl(x, F_GETFD) & FD_CLOEXEC);
    show_name("accept_name", 0, &got, gl);

    show("write_c", write(c, "hi", 2));
    ssize_t n = read(x, buf, sizeof buf);
    printf("read_x=%zd %.*s\n", n, n > 0 ? (int)n : 0, buf);
    show("write_x", write(x, "zu", 2));
    n = read(c, buf, sizeof buf);
    printf("read_c=%zd %.*s\n", n, n > 0 ? (int)n : 0, buf);
    show("read_c_leer", read(c, buf, sizeof buf));

    gl = sizeof got;
    r = getsockname(s, (struct sockaddr *)&got, &gl);
    show_name("sockname_s", r, &got, gl);
    gl = sizeof got;
    r = getpeername(c, (struct sockaddr *)&got, &gl);
    show_name("peername_c", r, &got, gl);
    gl = sizeof got;
    r = getpeername(x, (struct sockaddr *)&got, &gl);
    show_name("peername_x", r, &got, gl);
    gl = sizeof got;
    r = getpeername(s, (struct sockaddr *)&got, &gl);
    show_name("peername_s", r, &got, gl);

    fcntl(s, F_SETFL, O_NONBLOCK);
    show("accept_leer", accept(s, NULL, NULL));

    struct sockaddr_un none;
    socklen_t nl = abstract(&none, "zehos-fehlt");
    int d = socket(AF_UNIX, SOCK_STREAM, 0);
    show("connect_fehlt", connect(d, (struct sockaddr *)&none, nl));

    close(x);
    show("eof_nach_close", read(c, buf, sizeof buf));
    close(c);
    close(d);
    close(u);
    close(s);
    /* Nach dem Schließen ist der abstrakte Name frei. */
    s = socket(AF_UNIX, SOCK_STREAM, 0);
    show("bind_wieder", bind(s, (struct sockaddr *)&a, al));
    close(s);
}

static void part2_path(void)
{
    char buf[16];
    int r;
    struct sockaddr_un a, a2, got;
    socklen_t al = pathname(&a, "sock"), a2l = pathname(&a2, "./sock"), gl;

    int s = socket(AF_UNIX, SOCK_STREAM, 0);
    show("bind_pfad", bind(s, (struct sockaddr *)&a, al));
    show("listen_pfad", listen(s, 2));
    int u = socket(AF_UNIX, SOCK_STREAM, 0);
    show("bind_pfad_doppelt", bind(u, (struct sockaddr *)&a2, a2l));
    printf("access_sock=%d\n", access("sock", F_OK));

    int c = socket(AF_UNIX, SOCK_STREAM, 0);
    show("connect_pfad", connect(c, (struct sockaddr *)&a2, a2l));
    int x = accept(s, NULL, NULL);
    printf("accept_pfad_ok=%d\n", x >= 0);
    show("write_pfad", write(x, "pf", 2));
    ssize_t n = read(c, buf, sizeof buf);
    printf("read_pfad=%zd %.*s\n", n, n > 0 ? (int)n : 0, buf);
    gl = sizeof got;
    r = getsockname(s, (struct sockaddr *)&got, &gl);
    show_name("sockname_pfad", r, &got, gl);

    struct sockaddr_un df;
    socklen_t dl = pathname(&df, "datei");
    int d = socket(AF_UNIX, SOCK_STREAM, 0);
    show("connect_datei", connect(d, (struct sockaddr *)&df, dl));
    close(d);

    show("unlink_sock", unlink("sock"));
    d = socket(AF_UNIX, SOCK_STREAM, 0);
    show("connect_nach_unlink", connect(d, (struct sockaddr *)&a, al));
    close(d);
    close(x);
    close(c);
    close(u);
    close(s);
}

static void part2_seqpacket(void)
{
    char buf[16];
    struct sockaddr_un a;
    socklen_t al = abstract(&a, "zehos-seq");
    int s = socket(AF_UNIX, SOCK_SEQPACKET, 0);
    show("seq_bind", bind(s, (struct sockaddr *)&a, al));
    show("seq_listen", listen(s, 1));
    int c = socket(AF_UNIX, SOCK_SEQPACKET, 0);
    show("seq_connect", connect(c, (struct sockaddr *)&a, al));
    int x = accept(s, NULL, NULL);
    printf("seq_accept_ok=%d\n", x >= 0);
    show("seq_send1", send(c, "a", 1, 0));
    show("seq_send2", send(c, "bc", 2, 0));
    show("seq_recv1", recv(x, buf, sizeof buf, 0));
    show("seq_recv2", recv(x, buf, sizeof buf, 0));
    int t = socket(AF_UNIX, SOCK_STREAM, 0);
    show("connect_falscher_typ", connect(t, (struct sockaddr *)&a, al));
    close(t);
    close(x);
    close(c);
    close(s);
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    alarm(30);
    part1();
    part2_abstract();
    part2_path();
    part2_seqpacket();
    unlink("datei");
    unlink("kind");
    printf("ende\n");
    return 0;
}
