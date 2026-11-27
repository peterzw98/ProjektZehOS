/* V43/V43b (Ladybird): Speicherdruck-Probe für den Gast mit wenig Speicher (512M). Kein Vergleichsprogramm: Die Ausgabe hängt vom
 * Speicher ab, deshalb gibt es keine Erwartung unter expected/; scripts/test-linux-mem.py startet es im Desktop-Terminal.
 *
 * Aufruf: memprobe <szenario> [grenze-mib]   (grenze-mib begrenzt Füllszenarien, etwa für einen Probelauf auf dem Host)
 *
 * Jedes Szenario läuft zweimal hintereinander. Die Last liegt immer in Kindprozessen: Ein SIGKILL des Kernels (Seitenfehler ohne Frame)
 * trifft dann das Kind, und der Elternprozess meldet Ende, Signal und errno. Stirbt doch der Elternprozess, zeigt das Terminal
 * "signal 9" statt der Schlusszeile, und das Startskript wertet das als FAIL. Die Kinder melden ihren Fortschritt als Textzeilen
 * "schlüssel wert" über eine Pipe; der letzte Wert zählt, damit ein abgebrochenes Kind seinen Stand nicht verliert.
 *
 * Ausgabe je Prüfung eine Zeile "PASS|FAIL|INFO <szenario>.<lauf>.<prüfung> k=v ...", am Ende "SUMMARY <szenario> pass=<n> fail=<m>".
 * Nach jedem Lauf: Elternprozess lebt (er schreibt weiter), ein neuer Prozess ist startbar (fork und posix_spawn), und nach beiden
 * Läufen ist der Speicher wieder frei (sysinfo freeram, Toleranz FREE_SLACK_MIB). */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdarg.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/syscall.h>
#include <sys/sysinfo.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#define MIB (1024L * 1024L)
#define PAGE 4096L
#define CHUNK_MIB 4L
#define DEADLINE_S 240        // je Kind; der Kernel soll Last in Sekunden beenden, nicht in Minuten
#define FREE_SLACK_MIB 32L    // Kernel-Heap darf nach zwei Läufen so viel behalten, ohne dass es als Leck zählt
#define MAX_KIDS 8
#define MAX_KV 24
#define HEADROOM_PAGES 32L    // slotfull: freie Seiten über der Reserve, kleiner als KernelStackPages (64) in TakeSlot

extern char **environ;
// Pfad des eigenen Abbilds für posix_spawn und execve; main setzt ihn aus readlink(/proc/self/exe), weil der Gast den Pfad
// /proc/self/exe selbst nicht als Programm öffnet (posix_spawn ENOENT).
static char self_path[512] = "/proc/self/exe";
static int npass, nfail;
static const char *scen = "?";
static int rep;

/* ---------- Ausgabe ---------- */

static void result(int ok, const char *check, const char *fmt, ...) __attribute__((format(printf, 3, 4)));
static void result(int ok, const char *check, const char *fmt, ...)
{
    char extra[256];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(extra, sizeof extra, fmt, ap);
    va_end(ap);
    if (ok > 0) npass++;
    else if (ok == 0) nfail++;
    printf("%s %s.%d.%s %s\n", ok > 0 ? "PASS" : ok == 0 ? "FAIL" : "INFO", scen, rep, check, extra);
    fflush(stdout);
}

static long free_mib(void)
{
    struct sysinfo si;
    if (sysinfo(&si) != 0) return -1;
    return (long)((unsigned long long)si.freeram * si.mem_unit / MIB);
}

static long total_mib(void)
{
    struct sysinfo si;
    if (sysinfo(&si) != 0) return -1;
    return (long)((unsigned long long)si.totalram * si.mem_unit / MIB);
}

static void nap_ms(long ms)
{
    struct timespec ts = { ms / 1000, (ms % 1000) * 1000000L };
    nanosleep(&ts, NULL);
}

/* Kind -> Eltern: eine Zeile je Wert; write ist ein Systemaufruf ohne neue Nutzerseiten (Puffer liegt auf dem schon benutzten Stack). */
static int rep_fd = -1;
static void report(const char *key, long value)
{
    if (rep_fd < 0) return;
    char line[64];
    int n = snprintf(line, sizeof line, "%s %ld\n", key, value);
    if (write(rep_fd, line, (size_t)n) < 0) { /* Elternprozess weg: nichts zu tun */ }
    // Stufen und Abbildungszahl zusätzlich direkt ins Terminal (serieller Spiegel): Bricht der Kernel den ganzen Lauf ab (Ausnahme
    // im Systemaufruf beendet alle Prozesse, auch den Elternprozess), bleibt so der letzte Stand des Kinds sichtbar.
    if (strcmp(key, "stage") == 0 || strcmp(key, "maps") == 0) {
        char prog[80];
        int m = snprintf(prog, sizeof prog, "PROG %d %s %ld\n", (int)getpid(), key, value);
        if (write(2, prog, (size_t)m) < 0) { }
    }
}

/* Fasst den Stack vorab an: Später, bei vollem Speicher, bräuchte ein tieferer Aufruf (posix_spawn, snprintf) sonst einen neuen Frame
 * und der Kernel beendete das Kind mit SIGKILL, bevor die eigentliche Prüfung läuft. */
static void __attribute__((noinline)) touch_stack(void)
{
    volatile char pad[96 * 1024];
    for (size_t i = 0; i < sizeof pad; i += PAGE) pad[i] = 1;
    pad[sizeof pad - 1] = 1;
}

static int memfd(const char *name)
{
    return (int)syscall(SYS_memfd_create, name, 0);
}

/* ---------- Kindprozesse und ihre Auswertung ---------- */

struct kid {
    pid_t pid;
    int fd;
    char buf[1024];
    int blen;
    int nkv;
    char key[MAX_KV][24];
    long val[MAX_KV];
    int status;
    int reaped;
    int timeout;
};

static void kv_set(struct kid *k, const char *key, long v)
{
    for (int i = 0; i < k->nkv; i++)
        if (strcmp(k->key[i], key) == 0) { k->val[i] = v; return; }
    if (k->nkv < MAX_KV) {
        snprintf(k->key[k->nkv], sizeof k->key[0], "%s", key);
        k->val[k->nkv++] = v;
    }
}

static long kv(const struct kid *k, const char *key, long dflt)
{
    for (int i = 0; i < k->nkv; i++)
        if (strcmp(k->key[i], key) == 0) return k->val[i];
    return dflt;
}

static void kid_parse(struct kid *k)
{
    int start = 0;
    for (int i = 0; i < k->blen; i++) {
        if (k->buf[i] != '\n') continue;
        k->buf[i] = 0;
        char *sp = strchr(k->buf + start, ' ');
        if (sp) { *sp = 0; kv_set(k, k->buf + start, strtol(sp + 1, NULL, 10)); }
        start = i + 1;
    }
    memmove(k->buf, k->buf + start, (size_t)(k->blen - start));
    k->blen -= start;
}

static void kid_read(struct kid *k)
{
    if (k->fd < 0) return;
    for (;;) {
        ssize_t n = read(k->fd, k->buf + k->blen, sizeof k->buf - 1 - (size_t)k->blen);
        if (n > 0) { k->blen += (int)n; kid_parse(k); continue; }
        if (n == 0) { close(k->fd); k->fd = -1; }
        return; // n < 0: EAGAIN, später weiter
    }
}

typedef int (*kidfn)(long arg);

static int kid_start(struct kid *k, kidfn fn, long arg)
{
    memset(k, 0, sizeof *k);
    int p[2];
    if (pipe2(p, O_CLOEXEC) != 0) return -errno;
    fflush(stdout);
    pid_t pid = fork();
    if (pid < 0) { int e = errno; close(p[0]); close(p[1]); return -e; }
    if (pid == 0) {
        close(p[0]);
        rep_fd = p[1];
        touch_stack();
        _exit(fn(arg));
    }
    close(p[1]);
    fcntl(p[0], F_SETFL, O_NONBLOCK);
    k->pid = pid;
    k->fd = p[0];
    return 0;
}

/* Wartet auf alle Kinder; nach DEADLINE_S SIGKILL und timeout=1 (ein hängendes Kind ist ein Befund, kein stilles Warten). */
static void kids_wait(struct kid *ks, int n)
{
    time_t end = time(NULL) + DEADLINE_S;
    for (;;) {
        int left = 0;
        struct pollfd pf[MAX_KIDS];
        int np = 0;
        for (int i = 0; i < n; i++) {
            if (!ks[i].reaped) {
                int st;
                pid_t r = waitpid(ks[i].pid, &st, WNOHANG);
                if (r == ks[i].pid) { ks[i].reaped = 1; ks[i].status = st; }
            }
            kid_read(&ks[i]);
            if (ks[i].reaped && ks[i].fd >= 0) {
                // Enkel könnten die Pipe noch halten; nach dem Ende des Kinds zählt nur, was schon da ist.
                kid_read(&ks[i]);
                close(ks[i].fd);
                ks[i].fd = -1;
            }
            if (!ks[i].reaped) left++;
            if (ks[i].fd >= 0) { pf[np].fd = ks[i].fd; pf[np].events = POLLIN; np++; }
        }
        if (!left) return;
        if (time(NULL) > end) {
            for (int i = 0; i < n; i++)
                if (!ks[i].reaped) { ks[i].timeout = 1; kill(ks[i].pid, SIGKILL); }
            for (int i = 0; i < n; i++)
                if (!ks[i].reaped) { int st; if (waitpid(ks[i].pid, &st, 0) == ks[i].pid) ks[i].status = st; ks[i].reaped = 1; }
            return;
        }
        if (np) poll(pf, (nfds_t)np, 200); else nap_ms(200);
    }
}

static int kid_sig(const struct kid *k) { return WIFSIGNALED(k->status) ? WTERMSIG(k->status) : 0; }
static int kid_exit(const struct kid *k) { return WIFEXITED(k->status) ? WEXITSTATUS(k->status) : -1; }

static void kid_describe(const struct kid *k, char *out, size_t len)
{
    snprintf(out, len, "pid=%d exit=%d sig=%d timeout=%d", (int)k->pid, kid_exit(k), kid_sig(k), k->timeout);
}

/* ---------- Lasten (laufen im Kind) ---------- */

/* Exitcodes der Lasten */
#define EX_DONE 0     // planmäßig fertig
#define EX_ERRNO 10   // Systemaufruf scheiterte wie erwartet (errno gemeldet)
#define EX_CAP 11     // grenze-mib erreicht
#define EX_BAD 20     // unerwarteter Zustand

/* anonymes mmap in Stücken, jede Seite anfassen; Ende durch SIGKILL (Seitenfehler ohne Frame) oder mmap-ENOMEM. */
static int load_anon(long cap)
{
    long mib = 0, minfree = free_mib();
    for (;;) {
        if (cap > 0 && mib >= cap) return EX_CAP;
        char *p = mmap(NULL, CHUNK_MIB * MIB, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED) { report("errno", errno); return EX_ERRNO; }
        for (long o = 0; o < CHUNK_MIB * MIB; o += PAGE) p[o] = 1;
        mib += CHUNK_MIB;
        long f = free_mib();
        if (f < minfree) minfree = f;
        report("mib", mib);
        report("minfree", minfree);
    }
}

/* memfd mit write füllen bis ENOSPC (LinuxMemFile.CopyIn); danach schließen und prüfen, dass die Frames zurückkommen. */
static int fill_memfd(int fd, long cap, long *mib_out, int *err_out)
{
    // Der Puffer bleibt bis zum Prozessende: Gäbe ihn munmap nach dem Füllen frei, hätte slotfull 256 freie Seiten über der Reserve
    // statt HEADROOM_PAGES, und TakeSlot ließe den Thread zu.
    static char *buf;
    if (!buf) {
        buf = mmap(NULL, MIB, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (buf == MAP_FAILED) { buf = NULL; *err_out = errno; return -1; }
        memset(buf, 0x5a, MIB);
    }
    long mib = 0, minfree = free_mib();
    *err_out = 0;
    for (;;) {
        if (cap > 0 && mib >= cap) break;
        ssize_t n = write(fd, buf, MIB);
        if (n < 0) { *err_out = errno; break; }
        if (n < MIB) { mib += 0; *err_out = ENOSPC; report("short", n); break; }
        mib++;
        if ((mib & 3) == 0) {
            long f = free_mib();
            if (f < minfree) minfree = f;
            report("mib", mib);
            report("minfree", minfree);
        }
    }
    long f = free_mib();
    if (f < minfree) minfree = f;
    report("mib", mib);
    report("minfree", minfree);
    *mib_out = mib;
    return 0;
}

static int load_memfd(long cap)
{
    int fd = memfd("probe");
    if (fd < 0) { report("errno", errno); return EX_BAD; }
    long mib; int err;
    if (fill_memfd(fd, cap, &mib, &err) != 0) { report("errno", err); return EX_BAD; }
    report("errno", err);
    long before = free_mib();
    close(fd);
    nap_ms(200);
    report("freed", free_mib() - before);
    return err ? EX_ERRNO : EX_CAP;
}

/* memfd groß abschneiden, MAP_SHARED abbilden und anfassen: Seitenfehler über LinuxUserProcessShared (V43-Review P2: SIGKILL). */
static int load_memfdmap(long cap)
{
    int fd = memfd("probemap");
    if (fd < 0) { report("errno", errno); return EX_BAD; }
    long size = (cap > 0 ? cap : 2048) * MIB;
    if (ftruncate(fd, size) != 0) {
        // Obergrenze des Erzeugers (EFBIG): kleiner versuchen
        size = 448 * MIB;
        if (ftruncate(fd, size) != 0) { report("errno", errno); return EX_BAD; }
    }
    report("size", size / MIB);
    char *p = mmap(NULL, (size_t)size, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    if (p == MAP_FAILED) { report("errno", errno); return EX_ERRNO; }
    long mib = 0, minfree = free_mib();
    for (long o = 0; o < size; o += PAGE) {
        p[o] = 1;
        if (((o + PAGE) % (CHUNK_MIB * MIB)) == 0) {
            mib += CHUNK_MIB;
            long f = free_mib();
            if (f < minfree) minfree = f;
            report("mib", mib);
            report("minfree", minfree);
        }
    }
    return EX_CAP;
}

static int block_fd = -1;
static void *thread_wait(void *a)
{
    (void)a;
    char c;
    while (read(block_fd, &c, 1) > 0) { }
    return NULL;
}

/* pthread_create bis zum Fehler (LinuxUserProcess.MaxThreads 64 oder TakeSlot), dann alle lösen und einen neuen anlegen. */
static int load_threads(long cap)
{
    int p[2];
    if (pipe(p) != 0) return EX_BAD;
    block_fd = p[0];
    static pthread_t t[1024];
    long max = cap > 0 ? cap : 1024, n = 0;
    int err = 0;
    pthread_attr_t at;
    pthread_attr_init(&at);
    pthread_attr_setstacksize(&at, 64 * 1024);
    while (n < max) {
        err = pthread_create(&t[n], &at, thread_wait, NULL);
        if (err) break;
        n++;
    }
    report("count", n);
    report("errno", err);
    close(p[1]);
    for (long i = 0; i < n; i++) pthread_join(t[i], NULL);
    pthread_t again;
    int e2 = pipe(p) == 0 ? (block_fd = p[0], close(p[1]), pthread_create(&again, &at, thread_wait, NULL)) : errno;
    if (e2 == 0) pthread_join(again, NULL);
    report("again", e2);
    return err ? EX_ERRNO : EX_CAP;
}

/* fork bis zum Fehler (MaxRunThreads 256, TakeSlot-Reserve oder ENOMEM). Die Enkel warten auf EOF einer Pipe und enden mit ihr. */
static int load_forkmany(long cap)
{
    int p[2];
    if (pipe(p) != 0) return EX_BAD;
    static pid_t kids[1024];
    long max = cap > 0 ? cap : 1024, n = 0;
    int err = 0;
    while (n < max) {
        pid_t pid = fork();
        if (pid < 0) { err = errno; break; }
        if (pid == 0) {
            close(p[1]);
            if (rep_fd >= 0) close(rep_fd);
            char c;
            while (read(p[0], &c, 1) > 0) { }
            _exit(0);
        }
        kids[n++] = pid;
        if ((n & 15) == 0) { report("count", n); report("minfree", free_mib()); }
    }
    report("count", n);
    report("errno", err);
    report("minfree", free_mib());
    close(p[1]);
    long bad = 0;
    for (long i = 0; i < n; i++) {
        int st;
        if (waitpid(kids[i], &st, 0) != kids[i] || !WIFEXITED(st) || WEXITSTATUS(st) != 0) bad++;
    }
    report("bad", bad);
    pid_t pid = fork();
    if (pid == 0) _exit(7);
    int st = 0;
    report("again", pid > 0 && waitpid(pid, &st, 0) == pid && WIFEXITED(st) && WEXITSTATUS(st) == 7);
    return err ? EX_ERRNO : EX_CAP;
}

/* slotfull: memfd bis ENOSPC (freie Seiten = Reserve), dann HEADROOM_PAGES zurückgeben. Danach dürfen Nutzer-Frames noch kommen, aber
 * TakeSlot lehnt neue Threads und Prozesse ab (FreePages < Reserve + 64): pthread_create EAGAIN, fork und posix_spawn EAGAIN oder ENOMEM.
 * Der Thread bekommt einen vorab angefassten Stack, damit musl keine neue Seite braucht (TLS liegt oben im Stack). */
static char tstack[256 * 1024] __attribute__((aligned(4096)));
static int load_slotfull(long cap)
{
    memset(tstack, 1, sizeof tstack);
    int fd = memfd("slotfull");
    if (fd < 0) { report("errno", errno); return EX_BAD; }
    long mib; int err;
    report("stage", 1);
    if (fill_memfd(fd, cap, &mib, &err) != 0) return EX_BAD;
    report("fillerr", err);
    if (err == 0) return EX_CAP; // Grenze erreicht, ohne voll zu sein: Prüfung sinnlos
    off_t sz = lseek(fd, 0, SEEK_END);
    report("stage", 2);
    if (ftruncate(fd, sz - HEADROOM_PAGES * PAGE) != 0) { report("errno", errno); return EX_BAD; }
    report("free_kib", free_mib() * 1024);

    pthread_attr_t at;
    pthread_attr_init(&at);
    pthread_attr_setstack(&at, tstack, sizeof tstack);
    int p[2];
    if (pipe(p) != 0) return EX_BAD;
    block_fd = p[0];
    close(p[1]); // Thread endet sofort, falls er doch startet
    pthread_t t;
    report("stage", 3);
    int te = pthread_create(&t, &at, thread_wait, NULL);
    if (te == 0) pthread_join(t, NULL);
    report("thread", te);

    report("stage", 4);
    pid_t pid = fork();
    if (pid == 0) _exit(7);
    int fe = 0;
    if (pid < 0) fe = errno;
    else { int st; waitpid(pid, &st, 0); }
    report("fork", fe);

    report("stage", 5);
    char *argv[] = { "memprobe", "ping", "quiet", NULL };
    pid_t sp;
    int se = posix_spawn(&sp, self_path, NULL, NULL, argv, environ);
    if (se == 0) { int st; waitpid(sp, &st, 0); }
    report("spawn", se);

    report("stage", 6);
    close(fd);
    nap_ms(200);
    report("free_after", free_mib());
    if (pipe(p) != 0) return EX_BAD;
    block_fd = p[0];
    close(p[1]);
    int te2 = pthread_create(&t, &at, thread_wait, NULL);
    if (te2 == 0) pthread_join(t, NULL);
    report("thread2", te2);
    pid = fork();
    if (pid == 0) _exit(7);
    int st = 0;
    report("fork2", pid > 0 && waitpid(pid, &st, 0) == pid && WIFEXITED(st) && WEXITSTATUS(st) == 7);
    report("stage", 7);
    return EX_DONE;
}

/* kheap: Speicher mit memfd voll machen, dann Kernelobjekte anlegen, die nicht über AllocateFrame laufen (Pipes, Socketpaare, memfds,
 * viele kleine Abbildungen). Sie kommen aus dem Kernel-Heap und damit aus der Reserve. Ein Kernel-Halt hier ist der offene Restpunkt
 * "große Kernel-Heapobjekte" aus der Nachprüfung V43. arg = Höchstzahl der Abbildungen (Linux vm.max_map_count 65530). */
static int load_kheap(long maps_max)
{
    static int fds[2048];
    memset(fds, 0, sizeof fds); // vor dem Füllen anfassen: danach gäbe es für die Seite keinen Frame mehr (SIGKILL)
    int full = memfd("kheapfill");
    long mib = 0; int err = 0;
    if (full >= 0 && maps_max > 0) fill_memfd(full, 0, &mib, &err);
    report("fillerr", err);
    report("stage", 1);
    int n = 0, e = 0;
    while (n + 2 <= 2000) {
        if (pipe(fds + n) != 0) { e = errno; break; }
        n += 2;
    }
    report("pipes", n / 2);
    report("pipeerr", e);
    for (int i = 0; i < n; i++) close(fds[i]);
    report("stage", 2);
    n = 0; e = 0;
    while (n + 2 <= 2000) {
        if (socketpair(AF_UNIX, SOCK_STREAM, 0, fds + n) != 0) { e = errno; break; }
        n += 2;
    }
    report("socks", n / 2);
    report("sockerr", e);
    for (int i = 0; i < n; i++) close(fds[i]);
    report("stage", 3);
    n = 0; e = 0;
    while (n < 2000) {
        int f = memfd("k");
        if (f < 0) { e = errno; break; }
        fds[n++] = f;
    }
    report("memfds", n);
    report("memfderr", e);
    for (int i = 0; i < n; i++) close(fds[i]);
    report("stage", 4);
    // Abbildungen abwechselnd PROT_READ und PROT_NONE mit festen Adressen, damit keine zwei Nachbarn zu einem Bereich verschmelzen.
    long max = maps_max > 0 ? maps_max : -maps_max;
    char *base = mmap(NULL, (size_t)max * PAGE, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    long m = 0; e = 0;
    if (base == MAP_FAILED) e = errno;
    else {
        for (m = 0; m < max; m += 2) {
            if (mmap(base + m * PAGE, PAGE, PROT_READ, MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED, -1, 0) == MAP_FAILED) { e = errno; break; }
            if ((m & 8191) == 0) report("maps", m / 2);
        }
    }
    report("maps", m / 2);
    report("maperr", e);
    report("minfree", free_mib());
    report("stage", 5);
    if (base != MAP_FAILED) munmap(base, (size_t)max * PAGE);
    if (full >= 0) close(full);
    report("stage", 6);
    return EX_DONE;
}

/* sockloop/pipeloop: Speicher voll (memfd bis ENOSPC), dann wiederholt Socketpaare bzw. Pipes bis EMFILE anlegen und schließen.
 * Wiederholung des Kernel-Halts aus maps (LinuxUnixSocket.Close: lock auf Link.Gate == null). pipeloop ist die Gegenprobe mit einem
 * anderen Objekttyp: hält auch sie an, liegt es am Kernel-Heap unter Druck, nicht an den Sockets. arg = Wiederholungen. Nur im Gast:
 * Das Füllen hat keine Grenze. */
static int load_objloop(long iters, int sockets)
{
    static int fds[2048];
    memset(fds, 0, sizeof fds);
    int full = memfd("loopfill");
    long mib = 0; int err = 0;
    if (full >= 0) fill_memfd(full, 0, &mib, &err);
    report("fillerr", err);
    long total = 0;
    for (long it = 1; it <= iters; it++) {
        int n = 0;
        while (n + 2 <= 2000) {
            if ((sockets ? socketpair(AF_UNIX, SOCK_STREAM, 0, fds + n) : pipe(fds + n)) != 0) { report("err", errno); break; }
            n += 2;
        }
        total += n / 2;
        for (int i = 0; i < n; i++) close(fds[i]);
        report("stage", it);
        report("objs", total);
    }
    if (full >= 0) close(full);
    return EX_DONE;
}
static int load_sockloop(long iters) { return load_objloop(iters > 0 ? iters : 40, 1); }
static int load_pipeloop(long iters) { return load_objloop(iters > 0 ? iters : 40, 0); }

/* execchain (Nachprüfung V43, P3 2): Budget hängt am Abbild. Stufe 1 füllt ein memfd bis cap und behält es über execve; Stufe 2
 * (neues Abbild, frisches Budget) füllt ein zweites bis zum Fehler. Summe > 512 MiB belegt, dass execve die Prozessgrenze umgeht. */
static int load_execchain(long cap)
{
    int fd = memfd("chain1");
    if (fd < 0) return EX_BAD;
    long mib; int err;
    fill_memfd(fd, cap > 0 ? cap : 384, &mib, &err);
    report("first", mib);
    char a1[16], a2[16], a3[16];
    snprintf(a1, sizeof a1, "%d", rep_fd);
    snprintf(a2, sizeof a2, "%ld", mib);
    snprintf(a3, sizeof a3, "%d", fd);
    char *argv[] = { "memprobe", "execchain2", a1, a2, a3, NULL };
    int fl = fcntl(rep_fd, F_GETFD);
    fcntl(rep_fd, F_SETFD, fl & ~FD_CLOEXEC);
    execve(self_path, argv, environ);
    report("execerr", errno);
    return EX_BAD;
}

static int stage2_execchain(int argc, char **argv)
{
    if (argc < 5) return EX_BAD;
    rep_fd = atoi(argv[2]);
    long first = atol(argv[3]);
    int fd = memfd("chain2");
    if (fd < 0) { report("errno", errno); return EX_BAD; }
    long mib; int err;
    fill_memfd(fd, 0, &mib, &err);
    report("second", mib);
    report("total", first + mib);
    report("errno", err);
    return EX_ERRNO;
}

/* ---------- Szenarien (Elternprozess) ---------- */

static void alive_checks(void)
{
    // fork: ein neuer Prozess ist startbar
    fflush(stdout);
    pid_t pid = fork();
    if (pid == 0) _exit(7);
    int st = 0;
    int ok = pid > 0 && waitpid(pid, &st, 0) == pid && WIFEXITED(st) && WEXITSTATUS(st) == 7;
    result(ok, "fork_danach", "pid=%d status=0x%x errno=%d", (int)pid, st, pid < 0 ? errno : 0);
    // posix_spawn eines neuen Abbilds (execve-Weg, ElfLoader)
    char *argv[] = { "memprobe", "ping", "quiet", NULL };
    pid_t sp = 0;
    int se = posix_spawn(&sp, self_path, NULL, NULL, argv, environ);
    st = 0;
    ok = se == 0 && waitpid(sp, &st, 0) == sp && WIFEXITED(st) && WEXITSTATUS(st) == 0;
    result(ok, "spawn_danach", "ret=%d status=0x%x", se, st);
}

/* OOM-Ende einer Füllast: SIGKILL, ENOMEM/ENOSPC/EFBIG gemeldet, oder Grenze erreicht. */
static int oom_end_ok(const struct kid *k)
{
    if (k->timeout) return 0;
    if (kid_sig(k) == SIGKILL) return 1;
    long e = kv(k, "errno", 0);
    if (kid_exit(k) == EX_ERRNO && (e == ENOMEM || e == ENOSPC || e == EFBIG || e == EAGAIN)) return 1;
    return kid_exit(k) == EX_CAP;
}

static void eval_fill(const char *name, const struct kid *k, long *mib_out)
{
    char d[128];
    kid_describe(k, d, sizeof d);
    long mib = kv(k, "mib", 0), minfree = kv(k, "minfree", -1);
    *mib_out = mib;
    result(oom_end_ok(k), name, "%s mib=%ld minfree=%ld errno=%ld", d, mib, minfree, kv(k, "errno", 0));
    // Die Reserve (64 MiB bei 512M) bleibt dem Kernel: zuletzt gesehen frei >= 16 MiB. Nur sinnvoll, wenn der Speicher wirklich voll wurde.
    if (kid_exit(k) != EX_CAP)
        result(minfree >= 16, "reserve_frei", "minfree=%ld", minfree);
}

static long run_one(const char *s, long cap)
{
    struct kid ks[MAX_KIDS];
    char d[128];
    long mib = 0;
    if (strcmp(s, "anon") == 0 || strcmp(s, "memfd") == 0 || strcmp(s, "memfdmap") == 0) {
        kidfn f = s[0] == 'a' ? load_anon : strcmp(s, "memfd") == 0 ? load_memfd : load_memfdmap;
        int e = kid_start(&ks[0], f, cap);
        if (e) { result(0, "start", "errno=%d", -e); return 0; }
        kids_wait(ks, 1);
        eval_fill("ende", &ks[0], &mib);
        if (strcmp(s, "memfd") == 0 && kid_exit(&ks[0]) == EX_ERRNO)
            result(kv(&ks[0], "freed", 0) >= mib - FREE_SLACK_MIB, "close_gibt_frei", "freed=%ld mib=%ld", kv(&ks[0], "freed", 0), mib);
        if (strcmp(s, "memfdmap") == 0)
            result(kid_sig(&ks[0]) == SIGKILL || kid_exit(&ks[0]) == EX_CAP, "sigkill", "sig=%d size=%ld", kid_sig(&ks[0]), kv(&ks[0], "size", 0));
    } else if (strcmp(s, "threads") == 0 || strcmp(s, "forkmany") == 0) {
        int th = s[0] == 't';
        int e = kid_start(&ks[0], th ? load_threads : load_forkmany, cap);
        if (e) { result(0, "start", "errno=%d", -e); return 0; }
        kids_wait(ks, 1);
        kid_describe(&ks[0], d, sizeof d);
        long err = kv(&ks[0], "errno", -1);
        mib = kv(&ks[0], "count", 0);
        int okend = kid_exit(&ks[0]) == EX_ERRNO && (err == EAGAIN || err == ENOMEM);
        if (cap > 0 && kid_exit(&ks[0]) == EX_CAP) okend = 1;
        result(okend, "grenze", "%s count=%ld errno=%ld minfree=%ld", d, mib, err, kv(&ks[0], "minfree", -1));
        if (th) result(kv(&ks[0], "again", -1) == 0, "neuer_thread", "ret=%ld", kv(&ks[0], "again", -1));
        else {
            result(kv(&ks[0], "again", 0) == 1, "neuer_fork", "ok=%ld", kv(&ks[0], "again", 0));
            result(kv(&ks[0], "bad", -1) == 0, "enkel_sauber", "bad=%ld", kv(&ks[0], "bad", -1));
        }
    } else if (strcmp(s, "slotfull") == 0) {
        int e = kid_start(&ks[0], load_slotfull, cap);
        if (e) { result(0, "start", "errno=%d", -e); return 0; }
        kids_wait(ks, 1);
        kid_describe(&ks[0], d, sizeof d);
        if (kid_exit(&ks[0]) == EX_CAP) {
            // grenze-mib erreicht, ohne dass der Speicher voll war (Probelauf): die Ablehnungen sind dann nicht zu erwarten.
            result(-1, "ende", "%s grenze erreicht, keine Pruefung", d);
            return 0;
        }
        result(kid_exit(&ks[0]) == EX_DONE, "ende", "%s stage=%ld fillerr=%ld mib=%ld free_kib=%ld", d, kv(&ks[0], "stage", 0),
               kv(&ks[0], "fillerr", -1), kv(&ks[0], "mib", 0), kv(&ks[0], "free_kib", -1));
        mib = kv(&ks[0], "mib", 0);
        long te = kv(&ks[0], "thread", -1), fe = kv(&ks[0], "fork", -1), se = kv(&ks[0], "spawn", -1);
        result(te == EAGAIN, "thread_eagain", "ret=%ld", te);
        result(fe == EAGAIN || fe == ENOMEM, "fork_abgelehnt", "errno=%ld", fe);
        result(se == EAGAIN || se == ENOMEM, "spawn_abgelehnt", "ret=%ld", se);
        result(kv(&ks[0], "thread2", -1) == 0 && kv(&ks[0], "fork2", 0) == 1, "erholt", "thread2=%ld fork2=%ld free_after=%ld",
               kv(&ks[0], "thread2", -1), kv(&ks[0], "fork2", 0), kv(&ks[0], "free_after", -1));
    } else if (strcmp(s, "concurrent") == 0 || strcmp(s, "concmix") == 0) {
        // concurrent: vier Füllasten gleichzeitig (smp4: echte Konkurrenz um die letzten Frames, TryCharge nicht atomar).
        // concmix: Füllasten, Threads und fork gleichzeitig.
        int mix = s[4] == 'm';
        kidfn fs[] = { load_anon, load_memfd, load_anon, load_memfdmap, load_threads, load_forkmany };
        int n = mix ? 6 : 4;
        int started = 0;
        for (int i = 0; i < n; i++) {
            kidfn f = mix ? fs[i] : (i & 1) ? load_memfd : load_anon;
            if (kid_start(&ks[started], f, (f == load_threads || f == load_forkmany) ? 0 : cap) == 0) started++;
        }
        // fork kann scheitern, sobald die ersten Kinder den Speicher gefüllt haben (TakeSlot: EAGAIN); das ist gewollt, nur Information.
        result(-1, "gestartet", "n=%d von %d", started, n);
        kids_wait(ks, started);
        int all = 1, killed = 0;
        for (int i = 0; i < started; i++) {
            kid_describe(&ks[i], d, sizeof d);
            int ok = oom_end_ok(&ks[i]);
            all &= ok;
            killed += kid_sig(&ks[i]) == SIGKILL;
            mib += kv(&ks[i], "mib", 0);
            printf("INFO %s.%d.kind%d %s mib=%ld count=%ld errno=%ld minfree=%ld\n", scen, rep, i, d, kv(&ks[i], "mib", 0),
                   kv(&ks[i], "count", 0), kv(&ks[i], "errno", 0), kv(&ks[i], "minfree", -1));
        }
        result(all, "alle_beendet", "n=%d sigkill=%d summe_mib=%ld", started, killed, mib);
    } else if (strcmp(s, "kheap") == 0 || strcmp(s, "maps") == 0 || strcmp(s, "kheapfrei") == 0 || strcmp(s, "mapsfrei") == 0) {
        long maps = strncmp(s, "maps", 4) == 0 ? 2000000 : 131060; // 65530 Abbildungen (jede zweite Seite) bzw. 1 Million
        if (strstr(s, "frei")) maps = -maps;                      // negativ: ohne Füllen vorab
        if (cap > 0) maps = maps > 0 ? cap : -cap;
        int e = kid_start(&ks[0], load_kheap, maps);
        if (e) { result(0, "start", "errno=%d", -e); return 0; }
        kids_wait(ks, 1);
        kid_describe(&ks[0], d, sizeof d);
        result(kid_exit(&ks[0]) == EX_DONE, "ende", "%s stage=%ld fillerr=%ld mib=%ld", d, kv(&ks[0], "stage", 0), kv(&ks[0], "fillerr", -1),
               kv(&ks[0], "mib", 0));
        result(-1, "objekte", "pipes=%ld/%ld socks=%ld/%ld memfds=%ld/%ld maps=%ld/%ld minfree=%ld", kv(&ks[0], "pipes", -1),
               kv(&ks[0], "pipeerr", -1), kv(&ks[0], "socks", -1), kv(&ks[0], "sockerr", -1), kv(&ks[0], "memfds", -1),
               kv(&ks[0], "memfderr", -1), kv(&ks[0], "maps", -1), kv(&ks[0], "maperr", -1), kv(&ks[0], "minfree", -1));
        mib = kv(&ks[0], "maps", 0);
    } else if (strcmp(s, "sockloop") == 0 || strcmp(s, "pipeloop") == 0) {
        int e = kid_start(&ks[0], s[0] == 's' ? load_sockloop : load_pipeloop, cap);
        if (e) { result(0, "start", "errno=%d", -e); return 0; }
        kids_wait(ks, 1);
        kid_describe(&ks[0], d, sizeof d);
        mib = kv(&ks[0], "objs", 0);
        result(kid_exit(&ks[0]) == EX_DONE, "ende", "%s runden=%ld objekte=%ld err=%ld fillerr=%ld mib=%ld", d, kv(&ks[0], "stage", 0), mib,
               kv(&ks[0], "err", 0), kv(&ks[0], "fillerr", -1), kv(&ks[0], "mib", 0));
    } else if (strcmp(s, "execchain") == 0) {
        int e = kid_start(&ks[0], load_execchain, cap);
        if (e) { result(0, "start", "errno=%d", -e); return 0; }
        kids_wait(ks, 1);
        kid_describe(&ks[0], d, sizeof d);
        mib = kv(&ks[0], "total", 0);
        result(oom_end_ok(&ks[0]), "ende", "%s first=%ld second=%ld total=%ld errno=%ld execerr=%ld", d, kv(&ks[0], "first", 0),
               kv(&ks[0], "second", 0), mib, kv(&ks[0], "errno", 0), kv(&ks[0], "execerr", 0));
        // Nur Information: Summe über 512 MiB belegt den bekannten P3 (Budget nach execve frisch), ist hier kein Fehler der Probe.
        result(-1, "budget_umgangen", "%d total=%ld grenze=512", mib > 512, mib);
    } else {
        result(0, "unbekannt", "szenario=%s", s);
    }
    return mib;
}

int main(int argc, char **argv)
{
    if (argc >= 2 && strcmp(argv[1], "execchain2") == 0) return stage2_execchain(argc, argv);
    if (argc >= 2 && strcmp(argv[1], "ping") == 0) {
        if (argc < 3) printf("PONG frei=%ld gesamt=%ld\n", free_mib(), total_mib());
        return 0;
    }
    if (argc < 2) {
        fprintf(stderr, "Aufruf: memprobe ping|anon|memfd|memfdmap|threads|forkmany|slotfull|concurrent|concmix|kheap|kheapfrei|maps|mapsfrei|sockloop|pipeloop|execchain "
                        "[grenze-mib]\n");
        return 2;
    }
    setvbuf(stdout, NULL, _IOLBF, 0);
    ssize_t sl = readlink("/proc/self/exe", self_path, sizeof self_path - 1);
    if (sl > 0) self_path[sl] = 0;
    else snprintf(self_path, sizeof self_path, "%s", argv[0]);
    signal(SIGPIPE, SIG_IGN);
    touch_stack();
    scen = argv[1];
    long cap = argc > 2 ? atol(argv[2]) : 0;
    long total = total_mib();
    long free0 = free_mib();
    printf("START %s gesamt=%ld frei=%ld grenze=%ld\n", scen, total, free0, cap);
    long amount[3] = { 0, 0, 0 };
    for (rep = 1; rep <= 2; rep++) {
        long before = free_mib();
        amount[rep] = run_one(scen, cap);
        nap_ms(500);
        alive_checks();
        result(-1, "frei", "vorher=%ld nachher=%ld", before, free_mib());
    }
    rep = 0;
    // Zweiter Lauf erreicht dasselbe (kein Leck, das die Menge schrumpfen lässt); Toleranz wie beim Freigeben.
    // Bei gleichzeitigen Lasten hängt die Menge von der Reihenfolge ab (wer zuerst schließt, gibt Frames frei): nur Information.
    int conc = strncmp(scen, "conc", 4) == 0;
    if (amount[1] > 0)
        result(conc ? -1 : (amount[2] >= amount[1] - FREE_SLACK_MIB && amount[2] >= amount[1] * 9 / 10), "wiederholbar", "lauf1=%ld lauf2=%ld",
               amount[1], amount[2]);
    long free1 = free_mib();
    // Nur Information: Der Kernel-Heap gibt Seiten erst nach einer späteren Sammlung zurück (im Gast kamen 30-80 MiB erst im nächsten
    // Szenario wieder). Die harte Prüfung macht test-linux-mem.py über den freien Speicher am Ende aller Szenarien.
    result(free1 >= free0 - FREE_SLACK_MIB ? 1 : -1, "speicher_zurueck", "start=%ld ende=%ld diff=%ld", free0, free1, free1 - free0);
    printf("SUMMARY %s pass=%d fail=%d\n", scen, npass, nfail);
    return nfail ? 1 : 0;
}
