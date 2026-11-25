/* AF_INET nur auf 127.0.0.1: TCP-Echo zwischen Elternteil (Server) und Kind (Client), Sockoptionen,
 * Fehlernummern (EADDRINUSE, ECONNREFUSED, EADDRNOTAVAIL, EAGAIN), nichtblockierendes connect
 * mit poll, shutdown/EOF, UDP über Loopback. Ports werden nie ausgegeben. */
#define _GNU_SOURCE
#include "util.h"
#include <arpa/inet.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <signal.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/wait.h>

#define BULK 200000
#define CHUNK 4096

static struct sockaddr_in loop_addr(unsigned short port)
{
    struct sockaddr_in a;
    memset(&a, 0, sizeof a);
    a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    a.sin_port = htons(port);
    return a;
}

static int bound_socket(int type, unsigned short *port)
{
    int s = socket(AF_INET, type, 0);
    struct sockaddr_in a = loop_addr(0);
    if (s < 0 || bind(s, (struct sockaddr *)&a, sizeof a) != 0) return -1;
    socklen_t l = sizeof a;
    if (getsockname(s, (struct sockaddr *)&a, &l) != 0) return -1;
    *port = ntohs(a.sin_port);
    return s;
}

static int read_full(int s, char *b, int n)
{
    int got = 0;
    while (got < n) {
        ssize_t r = read(s, b + got, n - got);
        if (r <= 0) return -1;
        got += (int)r;
    }
    return got;
}

static int write_full(int s, const char *b, int n)
{
    int put = 0;
    while (put < n) {
        ssize_t r = write(s, b + put, n - put);
        if (r <= 0) return -1;
        put += (int)r;
    }
    return put;
}

static int client(unsigned short port)
{
    int s = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in a = loop_addr(port);
    CNEED(s >= 0 && connect(s, (struct sockaddr *)&a, sizeof a) == 0);
    int one = 1;
    CNEED(setsockopt(s, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one) == 0);
    char b[CHUNK], r[CHUNK];
    CNEED(write_full(s, "hallo", 5) == 5 && read_full(s, r, 5) == 5 && memcmp(r, "hallo", 5) == 0);
    CNEED(write_full(s, "!", 1) == 1 && read_full(s, r, 1) == 1 && r[0] == '!');
    unsigned long sum = 0;
    for (int off = 0; off < BULK; off += CHUNK) {
        int n = BULK - off < CHUNK ? BULK - off : CHUNK;
        for (int i = 0; i < n; i++) b[i] = (char)((off + i) * 31 + 7);
        CNEED(write_full(s, b, n) == n && read_full(s, r, n) == n && memcmp(b, r, n) == 0);
        sum += n;
    }
    CNEED(sum == BULK);
    CNEED(shutdown(s, SHUT_WR) == 0);
    CNEED(read(s, r, 1) == 0);   /* der Server schließt nach EOF */
    close(s);
    return 0;
}

int main(void)
{
    INIT();
    signal(SIGPIPE, SIG_IGN);
    unsigned short port = 0;
    int ls = socket(AF_INET, SOCK_STREAM, 0);
    NEED(ls >= 0);
    int one = 1;
    NEED(setsockopt(ls, SOL_SOCKET, SO_REUSEADDR, &one, sizeof one) == 0);
    int v = 0;
    socklen_t vl = sizeof v;
    NEED(getsockopt(ls, SOL_SOCKET, SO_REUSEADDR, &v, &vl) == 0);
    SHOW("SO_REUSEADDR gesetzt", v != 0);
    struct sockaddr_in a = loop_addr(0);
    NEED(bind(ls, (struct sockaddr *)&a, sizeof a) == 0);
    socklen_t al = sizeof a;
    NEED(getsockname(ls, (struct sockaddr *)&a, &al) == 0);
    port = ntohs(a.sin_port);
    SHOW("Port vergeben, Adresse 127.0.0.1", port != 0 && a.sin_addr.s_addr == htonl(INADDR_LOOPBACK) && a.sin_family == AF_INET);
    SHOWI("Länge der Adresse", al);
    NEED(listen(ls, 8) == 0);
    vl = sizeof v;
    NEED(getsockopt(ls, SOL_SOCKET, SO_ACCEPTCONN, &v, &vl) == 0);
    SHOW("SO_ACCEPTCONN", v == 1);

    struct sockaddr_in b = loop_addr(port);
    int dup = socket(AF_INET, SOCK_STREAM, 0);
    T("zweites bind auf denselben Port", bind(dup, (struct sockaddr *)&b, sizeof b));
    close(dup);
    struct sockaddr_in fremd;
    memset(&fremd, 0, sizeof fremd);
    fremd.sin_family = AF_INET;
    inet_pton(AF_INET, "192.0.2.1", &fremd.sin_addr);   /* TEST-NET-1, nie lokal */
    dup = socket(AF_INET, SOCK_STREAM, 0);
    T("bind auf nicht lokale Adresse", bind(dup, (struct sockaddr *)&fremd, sizeof fremd));
    close(dup);
    T("socket ungültiger Typ", socket(AF_INET, 99, 0));
    T("socket ungültige Familie", socket(99, SOCK_STREAM, 0));

    /* nichtblockierendes accept ohne Verbindung */
    int fl = fcntl(ls, F_GETFL);
    NEED(fcntl(ls, F_SETFL, fl | O_NONBLOCK) == 0);
    T("accept ohne Verbindung (nonblock)", accept(ls, NULL, NULL));
    NEED(fcntl(ls, F_SETFL, fl) == 0);

    /* Verbindung verweigert: Port eines geschlossenen Sockets. */
    unsigned short dead;
    int d = bound_socket(SOCK_STREAM, &dead);
    NEED(d >= 0);
    close(d);
    int cs = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in da = loop_addr(dead);
    T("connect ohne Server", connect(cs, (struct sockaddr *)&da, sizeof da));
    close(cs);
    cs = socket(AF_INET, SOCK_STREAM | SOCK_NONBLOCK, 0);
    int r = connect(cs, (struct sockaddr *)&da, sizeof da);
    SHOW("nonblock connect: EINPROGRESS oder sofortiger Fehler", (r == -1 && (errno == EINPROGRESS || errno == ECONNREFUSED)));
    struct pollfd pf = { cs, POLLOUT, 0 };
    NEED(poll(&pf, 1, 10000) == 1);
    vl = sizeof v;
    NEED(getsockopt(cs, SOL_SOCKET, SO_ERROR, &v, &vl) == 0);
    SHOWI("SO_ERROR nach verweigertem connect", v);
    close(cs);

    /* nichtblockierendes connect zum Server: wird fertig, SO_ERROR 0, Echo später. */
    cs = socket(AF_INET, SOCK_STREAM | SOCK_NONBLOCK, 0);
    r = connect(cs, (struct sockaddr *)&b, sizeof b);
    SHOW("nonblock connect: EINPROGRESS oder 0", r == 0 || (r == -1 && errno == EINPROGRESS));
    pf.fd = cs; pf.events = POLLOUT; pf.revents = 0;
    NEED(poll(&pf, 1, 10000) == 1);
    vl = sizeof v;
    NEED(getsockopt(cs, SOL_SOCKET, SO_ERROR, &v, &vl) == 0);
    SHOWI("SO_ERROR nach gelungenem connect", v);
    int as = accept(ls, NULL, NULL);
    NEED(as >= 0);
    struct sockaddr_in pa;
    al = sizeof pa;
    NEED(getpeername(as, (struct sockaddr *)&pa, &al) == 0);
    SHOW("getpeername des Servers: 127.0.0.1", pa.sin_addr.s_addr == htonl(INADDR_LOOPBACK));
    NEED(write(cs, "n", 1) == 1);
    char c1;
    SHOWI("Daten über nonblock-Verbindung", read(as, &c1, 1));
    close(cs); close(as);

    /* Echo: Kind als Client, Elternteil als Server. */
    pid_t c = fork();
    NEED(c >= 0);
    if (c == 0) {
        close(ls);
        _exit(client(port));
    }
    struct sockaddr_in peer;
    al = sizeof peer;
    as = accept(ls, (struct sockaddr *)&peer, &al);
    NEED(as >= 0);
    SHOW("accept: Peer 127.0.0.1, Port != 0", peer.sin_addr.s_addr == htonl(INADDR_LOOPBACK) && peer.sin_port != 0 && peer.sin_port != htons(port));
    struct sockaddr_in me;
    al = sizeof me;
    NEED(getsockname(as, (struct sockaddr *)&me, &al) == 0);
    SHOW("getsockname der Verbindung: Server-Port", me.sin_port == htons(port));
    char buf[CHUNK];
    long total = 0;
    for (;;) {
        ssize_t n = read(as, buf, sizeof buf);
        if (n <= 0) { SHOWI("EOF nach shutdown des Clients", n); break; }
        NEED(write_full(as, buf, (int)n) == (int)n);
        total += n;
    }
    SHOWI("Server: Bytes zurückgesendet", total);
    close(as);
    int st = 0;
    NEED(waitpid(c, &st, 0) == c);
    SHOW("Client: Echo vollständig, exit 0", WIFEXITED(st) && WEXITSTATUS(st) == 0);
    close(ls);

    /* UDP über Loopback. */
    unsigned short p1, p2;
    int u1 = bound_socket(SOCK_DGRAM, &p1), u2 = bound_socket(SOCK_DGRAM, &p2);
    NEED(u1 >= 0 && u2 >= 0);
    struct sockaddr_in to = loop_addr(p2);
    SHOWI("sendto", sendto(u1, "udp", 3, 0, (struct sockaddr *)&to, sizeof to));
    struct sockaddr_in from;
    al = sizeof from;
    SHOWI("recvfrom", recvfrom(u2, buf, sizeof buf, 0, (struct sockaddr *)&from, &al));
    SHOW("  Absender-Port", from.sin_port == htons(p1) && from.sin_addr.s_addr == htonl(INADDR_LOOPBACK));
    T("recvfrom leer (DONTWAIT)", recv(u2, buf, sizeof buf, MSG_DONTWAIT));
    close(u1); close(u2);
    return 0;
}
