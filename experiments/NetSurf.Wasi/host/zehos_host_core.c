// Prüfhost für Linux: zehos_v1-Dienste hinter dem Rückrufvertrag zehos_wasm_host_* des Kernels.
// Beide Prüfhosts nutzen diese Datei: zehos_host_wasm3.c (gleicher wasm3-Kern und Importbindung wie ZehOS) und
// zehos_host_linux.c (WAMR-Native-Bibliothek). Ersetzt nicht den ZehOS-Loader; er macht Bild und Eingabe ohne VM prüfbar.
//
// Umgebung:
//   ZEHOS_PPM=<datei>      jedes present als PPM (P6) dorthin schreiben
//   ZEHOS_EXIT_MS=<ms>     ab dieser App-Zeit meldet should_exit 1 (Vorgabe 10000)
//   ZEHOS_EVENTS=<datei>   Zeilen "<ms> <typ> <code> <x> <y>"; jedes Ereignis wird ab seiner Zeit geliefert
//   ZEHOS_NET=1            Netzwerk erlauben (ersetzt capability=net)
//   ZEHOS_TLS_CA=<datei>   PEM-Vertrauensanker für ZEHOS_NET_TLS; ohne sie meldet ein TLS-Handle ENOSYS
// net_* bildet das Netz-ABI mit nicht blockierenden POSIX-Sockets nach (DNS blockiert hier, im Kernel nicht),
// TLS mit OpenSSL statt BouncyCastle. Bauen mit -lssl -lcrypto.

#include <errno.h>
#include <fcntl.h>
#include <arpa/inet.h>
#include <netdb.h>
#include <poll.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#include <openssl/err.h>
#include <openssl/ssl.h>
#include <openssl/x509v3.h>

#include "zehos_host_core.h"

#define MAX_EVENTS 1024

struct scripted_event { uint32_t at; int32_t fields[4]; };

static struct scripted_event events[MAX_EVENTS];
static int event_count, next_event, loaded;
static uint64_t start_ms;
static uint32_t exit_ms = 10000, presents;

static uint64_t now_ms(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * 1000 + (uint64_t)ts.tv_nsec / 1000000;
}

static void load(void)
{
    if (loaded) return;
    loaded = 1;
    start_ms = now_ms();
    const char *limit = getenv("ZEHOS_EXIT_MS");
    if (limit != NULL) exit_ms = (uint32_t)strtoul(limit, NULL, 10);
    const char *path = getenv("ZEHOS_EVENTS");
    FILE *file = path != NULL ? fopen(path, "r") : NULL;
    if (path != NULL && file == NULL) { fprintf(stderr, "zehos-host: %s nicht lesbar\n", path); exit(2); }
    while (file != NULL && event_count < MAX_EVENTS) {
        struct scripted_event *e = &events[event_count];
        if (fscanf(file, "%u %d %d %d %d", &e->at, &e->fields[0], &e->fields[1], &e->fields[2], &e->fields[3]) != 5) break;
        event_count++;
    }
    if (file != NULL) fclose(file);
}

uint32_t zehos_wasm_host_ticks_ms(void) { load(); return (uint32_t)(now_ms() - start_ms); }

void zehos_wasm_host_present(const uint32_t *pixels, int32_t width, int32_t height, int32_t tic, int32_t test)
{
    (void)tic; (void)test;
    presents++;
    const char *path = getenv("ZEHOS_PPM");
    if (path == NULL) return;
    FILE *file = fopen(path, "wb");
    if (file == NULL) return;
    fprintf(file, "P6\n%d %d\n255\n", width, height);
    for (int64_t i = 0; i < (int64_t)width * height; i++) {
        uint8_t rgb[3] = { (uint8_t)(pixels[i] >> 16), (uint8_t)(pixels[i] >> 8), (uint8_t)pixels[i] };
        fwrite(rgb, 1, 3, file);
    }
    fclose(file);
}

int32_t zehos_wasm_host_poll_event(int32_t *event)
{
    if (zehos_wasm_host_ticks_ms() < (next_event < event_count ? events[next_event].at : UINT32_MAX)) return 0;
    for (int i = 0; i < 4; i++) event[i] = events[next_event].fields[i];
    next_event++;
    return 1;
}

void zehos_wasm_host_sleep_ms(uint32_t ms)
{
    if (ms > 1000) ms = 1000;
    struct timespec ts = { ms / 1000, (long)(ms % 1000) * 1000000 };
    nanosleep(&ts, NULL);
}

int32_t zehos_wasm_host_should_exit(void)
{
    if (zehos_wasm_host_ticks_ms() < exit_ms) return 0;
    fprintf(stderr, "zehos-host: should_exit nach %u ms, %u Bilder\n", zehos_wasm_host_ticks_ms(), presents);
    return 1;
}

// WASI-errno-Werte des Netz-ABI.
enum { NET_OK = 0, NET_EACCES = 2, NET_EAGAIN = 6, NET_EBADF = 8, NET_ECONNREFUSED = 14, NET_ECONNRESET = 15,
       NET_EHOSTUNREACH = 23, NET_EINVAL = 28, NET_EIO = 29, NET_EMFILE = 33, NET_ENOENT = 44, NET_ENOSYS = 52,
       NET_ENOTSUP = 58, NET_EPERM = 63, NET_EPROTO = 65, NET_ETIMEDOUT = 73 };
#define NET_TLS 1
// Grenzen wie WasmNetworkSession im Kernel, damit der Prüfhost keine Fähigkeiten vortäuscht, die ZehOS nicht hat.
#define MAX_SOCKETS 4
#define MAX_TRANSFER (64 * 1024)
#define MAX_HOST 253

static int sockets[MAX_SOCKETS];
static int sockets_ready[MAX_SOCKETS];
// TCP steht, bei TLS fehlt eventuell noch der Handshake; sockets_ready gilt erst danach.
static int sockets_connected[MAX_SOCKETS];
static int sockets_tls[MAX_SOCKETS];
static SSL *sessions[MAX_SOCKETS];
static char names[MAX_SOCKETS][MAX_HOST + 1];
static SSL_CTX *tls_context;

static int32_t map_errno(int error)
{
    switch (error) {
    case EAGAIN: return NET_EAGAIN;
    case EINPROGRESS: return NET_EAGAIN;
    case ECONNREFUSED: return NET_ECONNREFUSED;
    case ECONNRESET: case EPIPE: return NET_ECONNRESET;
    case EHOSTUNREACH: case ENETUNREACH: return NET_EHOSTUNREACH;
    case ETIMEDOUT: return NET_ETIMEDOUT;
    default: return NET_EIO;
    }
}

// Abbildung wie im Kernel: abgelehntes Zertifikat EPERM, Ende ohne close_notify ECONNRESET, sonst EPROTO.
// close_notify ist nur nach dem Handshake ein reguläres Ende; davor meldet BouncyCastle im Kernel einen Protokollfehler.
static int32_t map_tls(SSL *session, int result, int handshake)
{
    int error = SSL_get_error(session, result);
    if (error == SSL_ERROR_WANT_READ || error == SSL_ERROR_WANT_WRITE) return NET_EAGAIN;
    if (error == SSL_ERROR_ZERO_RETURN) return handshake ? NET_EPROTO : NET_OK;
    if (SSL_get_verify_result(session) != X509_V_OK) return NET_EPERM;
    if (error == SSL_ERROR_SYSCALL) return errno == 0 ? NET_ECONNRESET : map_errno(errno);
    if (ERR_GET_REASON(ERR_peek_error()) == SSL_R_UNEXPECTED_EOF_WHILE_READING) return NET_ECONNRESET;
    return NET_EPROTO;
}

// Wie TlsTransport: TLS 1.2/1.3 nur mit ECDHE/AEAD, Kette bis 8 Zertifikate, Name ohne Teil-Platzhalter gegen
// ZEHOS_TLS_CA, kein Übergehen. Fehlen die Anker, meldet
// net_open wie der Kernel sofort ENOSYS.
static int tls_available(void)
{
    if (tls_context != NULL) return 1;
    const char *anchors = getenv("ZEHOS_TLS_CA");
    if (anchors == NULL || *anchors == '\0') return 0;
    SSL_CTX *context = SSL_CTX_new(TLS_client_method());
    if (context == NULL) return 0;
    SSL_CTX_set_min_proto_version(context, TLS1_2_VERSION);
    SSL_CTX_set_verify(context, SSL_VERIFY_PEER, NULL);
    // Tiefe zählt Zwischenzertifikate ohne Blatt; 7 entspricht höchstens 8 Zertifikaten wie im Kernel.
    SSL_CTX_set_verify_depth(context, 7);
    if (SSL_CTX_set_cipher_list(context, "ECDHE+AESGCM:ECDHE+CHACHA20") != 1) { SSL_CTX_free(context); return 0; }
    SSL_CTX_set_mode(context, SSL_MODE_ENABLE_PARTIAL_WRITE | SSL_MODE_ACCEPT_MOVING_WRITE_BUFFER);
    if (SSL_CTX_load_verify_locations(context, anchors, NULL) != 1) { SSL_CTX_free(context); return 0; }
    tls_context = context;
    return 1;
}

static SSL *tls_session(int fd, const char *name)
{
    SSL *session = SSL_new(tls_context);
    if (session == NULL) return NULL;
    struct in_addr literal;
    // IPv4-Literale ohne SNI gegen die IP-SAN prüfen, Namen mit SNI gegen den DNS-Namen.
    int named = inet_pton(AF_INET, name, &literal) != 1;
    int ok = SSL_set_fd(session, fd) == 1 &&
             (named ? (SSL_set_hostflags(session, X509_CHECK_FLAG_NO_PARTIAL_WILDCARDS),
                       SSL_set_tlsext_host_name(session, name) == 1 && SSL_set1_host(session, name) == 1)
                    : X509_VERIFY_PARAM_set1_ip_asc(SSL_get0_param(session), name) == 1);
    if (!ok) { SSL_free(session); return NULL; }
    return session;
}

// Handles sind 1-basiert, damit 0 nie ein gültiges Handle ist.
static int socket_of(int32_t handle)
{
    return handle >= 1 && handle <= MAX_SOCKETS && sockets[handle - 1] > 0 ? sockets[handle - 1] : -1;
}

// Ohne capability=net sperrt der Kernel net_open; der Prüfhost erlaubt Netz nur mit ZEHOS_NET=1.
static int network_allowed(void)
{
    const char *value = getenv("ZEHOS_NET");
    return value != NULL && strcmp(value, "1") == 0;
}

// Prüfreihenfolge wie HostNetOpen/WasmNetworkSession.Open im Kernel.
int32_t zehos_wasm_host_net_open(const uint8_t *host, uint32_t host_len, int32_t port, int32_t flags, int32_t *handle)
{
    if (host_len > MAX_HOST) return NET_EINVAL;
    char name[MAX_HOST + 1], service[8];
    memcpy(name, host, host_len);
    name[host_len] = '\0';
    for (uint32_t i = 0; i < host_len; i++)
        if ((unsigned char)name[i] > 127) return NET_EINVAL;
    if (!network_allowed()) return NET_EACCES;
    if ((flags & ~NET_TLS) != 0) return NET_ENOTSUP;
    if (port < 1 || port > 65535 || host_len == 0 || memchr(name, '\0', host_len) != NULL) return NET_EINVAL;
    int slot = 0;
    while (slot < MAX_SOCKETS && sockets[slot] > 0) slot++;
    if (slot == MAX_SOCKETS) return NET_EMFILE;
    if ((flags & NET_TLS) != 0 && !tls_available()) return NET_ENOSYS;
    snprintf(service, sizeof(service), "%d", port);
    struct addrinfo hints = { .ai_family = AF_INET, .ai_socktype = SOCK_STREAM }, *info;
    if (getaddrinfo(name, service, &hints, &info) != 0) return NET_ENOENT;
    int fd = socket(AF_INET, SOCK_STREAM | SOCK_NONBLOCK | SOCK_CLOEXEC, 0);
    if (fd < 0) { freeaddrinfo(info); return NET_EIO; }
    int result = connect(fd, info->ai_addr, info->ai_addrlen);
    int error = errno;
    freeaddrinfo(info);
    if (result != 0 && error != EINPROGRESS) { close(fd); return map_errno(error); }
    sockets[slot] = fd;
    sockets_connected[slot] = result == 0;
    sockets_tls[slot] = (flags & NET_TLS) != 0;
    sockets_ready[slot] = result == 0 && !sockets_tls[slot];
    memcpy(names[slot], name, host_len + 1);
    *handle = slot + 1;
    return NET_OK;
}

// Treibt Verbindungsaufbau und Handshake voran; der Kernel erledigt das im Worker, hier jeder Aufruf.
static int32_t progress(int slot)
{
    int fd = sockets[slot];
    if (sockets_ready[slot]) return NET_OK;
    if (!sockets_connected[slot]) {
        struct pollfd p = { .fd = fd, .events = POLLOUT };
        if (poll(&p, 1, 0) == 0) return NET_EAGAIN;
        int error = 0;
        socklen_t len = sizeof(error);
        if (getsockopt(fd, SOL_SOCKET, SO_ERROR, &error, &len) != 0) error = errno;
        if (error != 0) return map_errno(error);
        sockets_connected[slot] = 1;
        sockets_ready[slot] = !sockets_tls[slot];
        if (sockets_ready[slot]) return NET_OK;
    }
    if (sessions[slot] == NULL && (sessions[slot] = tls_session(fd, names[slot])) == NULL) return NET_EIO;
    ERR_clear_error();
    errno = 0;
    int result = SSL_connect(sessions[slot]);
    if (result != 1) return map_tls(sessions[slot], result, 1);
    sockets_ready[slot] = 1;
    return NET_OK;
}

int32_t zehos_wasm_host_net_status(int32_t handle)
{
    if (socket_of(handle) < 0) return NET_EBADF;
    return progress(handle - 1);
}

static int32_t transfer(int32_t handle, void *buffer, uint32_t len, int32_t *count, int sending)
{
    if (len > MAX_TRANSFER) return NET_EINVAL;
    int fd = socket_of(handle);
    if (fd < 0) return NET_EBADF;
    int32_t state = progress(handle - 1);
    if (state != NET_OK) return state;
    SSL *session = sessions[handle - 1];
    if (session != NULL) {
        *count = 0;
        if (len == 0) return NET_OK;
        ERR_clear_error();
        errno = 0;
        int n = sending ? SSL_write(session, buffer, (int)len) : SSL_read(session, buffer, (int)len);
        if (n > 0) *count = n;
        return n > 0 ? NET_OK : map_tls(session, n, 0);
    }
    ssize_t n = sending ? send(fd, buffer, (size_t)len, MSG_NOSIGNAL) : recv(fd, buffer, (size_t)len, 0);
    if (n < 0) return map_errno(errno);
    *count = (int32_t)n;
    return NET_OK;
}

int32_t zehos_wasm_host_net_send(int32_t handle, const uint8_t *data, uint32_t len, int32_t *count)
{
    return transfer(handle, (void *)data, len, count, 1);
}

int32_t zehos_wasm_host_net_recv(int32_t handle, uint8_t *data, uint32_t len, int32_t *count)
{
    return transfer(handle, data, len, count, 0);
}

int32_t zehos_wasm_host_net_close(int32_t handle)
{
    int fd = socket_of(handle);
    if (fd < 0) return NET_EBADF;
    SSL *session = sessions[handle - 1];
    if (session != NULL) {
        // close_notify nur nach abgeschlossenem Handshake und ohne Rückmeldung, wie im Kernel.
        if (sockets_ready[handle - 1]) SSL_shutdown(session);
        SSL_free(session);
        sessions[handle - 1] = NULL;
    }
    close(fd);
    sockets[handle - 1] = 0;
    return NET_OK;
}
