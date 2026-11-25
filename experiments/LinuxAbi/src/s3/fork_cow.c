/* fork/vfork/clone3: privater Speicher wird getrennt (Copy-on-Write), geteilter nicht;
 * wait4/waitpid-Zustände, ECHILD, WNOHANG, Eltern-/Kind-Beziehung, Dateideskriptoren und
 * Dateizeiger werden geteilt. */
#define _GNU_SOURCE
#include "util.h"
#include <fcntl.h>
#include <signal.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/resource.h>
#include <sys/syscall.h>
#include <sys/wait.h>

/* Kernel-Struktur clone_args (VER0, 64 Byte); musl-gcc bringt keine Kernel-Header mit. */
struct clone_args {
    unsigned long long flags, pidfd, child_tid, parent_tid, exit_signal, stack, stack_size, tls;
};

#define PAGES 64
#define PS 4096

static void report(const char *label, int st)
{
    if (WIFEXITED(st)) printf("%s: exit %d\n", label, WEXITSTATUS(st));
    else if (WIFSIGNALED(st)) printf("%s: signal %d\n", label, WTERMSIG(st));
    else printf("%s: anderer Zustand\n", label);
}

int main(void)
{
    INIT();
    signal(SIGPIPE, SIG_IGN);
    char *priv = mmap(NULL, PAGES * PS, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    char *shared = mmap(NULL, PS, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    NEED(priv != MAP_FAILED && shared != MAP_FAILED);
    for (int i = 0; i < PAGES; i++) priv[i * PS] = (char)(i + 1);
    static char data[100] = "vorher";
    int stack_var = 41;
    int pfd[2];
    NEED(pipe(pfd) == 0);
    pid_t parent = getpid();

    T("wait ohne Kinder", wait(NULL));
    pid_t c = fork();
    NEED(c >= 0);
    if (c == 0) {
        CNEED(getppid() == parent && getpid() != parent);
        for (int i = 0; i < PAGES; i++) CNEED(priv[i * PS] == (char)(i + 1));
        for (int i = 0; i < PAGES; i += 2) priv[i * PS] = 99;       /* löst COW aus */
        strcpy(data, "nachher");
        stack_var = 42;
        shared[0] = 'S';
        /* gemeinsamer Dateizeiger: Schreibende der Pipe bleibt offen, Kind schreibt hinein */
        CNEED(write(pfd[1], "kind", 4) == 4);
        _exit(17);
    }
    int st = 0;
    NEED(waitpid(c, &st, 0) == c);
    report("kind 1", st);
    int cow_ok = 1;
    for (int i = 0; i < PAGES; i++) if (priv[i * PS] != (char)(i + 1)) cow_ok = 0;
    SHOW("privat unverändert (COW)", cow_ok);
    SHOW("static unverändert", strcmp(data, "vorher") == 0);
    SHOW("stack unverändert", stack_var == 41);
    SHOW("geteilt sichtbar", shared[0] == 'S');
    char b[8] = {0};
    SHOWI("pipe vom kind", read(pfd[0], b, 4));
    SHOW("pipe inhalt", memcmp(b, "kind", 4) == 0);
    T("wait nach Ende", wait(NULL));

    /* Zustände: Signal-Tod, normaler Exit mit 255, WNOHANG auf laufendes Kind. */
    c = fork();
    NEED(c >= 0);
    if (c == 0) { raise(SIGTERM); _exit(0); }
    NEED(waitpid(c, &st, 0) == c);
    report("kind 2 (SIGTERM)", st);
    c = fork();
    NEED(c >= 0);
    if (c == 0) { char x; (void)!read(pfd[0], &x, 1); _exit(255); }
    T("WNOHANG auf laufendes Kind", waitpid(c, &st, WNOHANG));
    NEED(write(pfd[1], "x", 1) == 1);
    NEED(wait4(c, &st, 0, NULL) == c);
    report("kind 3", st);
    struct rusage ru;
    c = fork();
    NEED(c >= 0);
    if (c == 0) _exit(0);
    NEED(wait4(c, &st, 0, &ru) == c);
    report("kind 4 (wait4 mit rusage)", st);
    T("waitpid unbekannte PID", waitpid(c, &st, 0));
    T("waitpid ungültige Option", waitpid(-1, &st, 0x10));

    /* Kind wird Waise: Elternteil stirbt, getppid wechselt nicht verlässlich; nicht geprüft.
     * Mehrere Kinder, Warten auf beliebiges (-1) in beliebiger Reihenfolge: nur die Summe. */
    int sum = 0, n = 5;
    for (int i = 1; i <= n; i++) {
        c = fork();
        NEED(c >= 0);
        if (c == 0) _exit(i);
    }
    for (int i = 0; i < n; i++) {
        NEED(waitpid(-1, &st, 0) > 0);
        sum += WEXITSTATUS(st);
    }
    SHOWI("summe der Exitcodes", sum);
    T("wait danach", wait(NULL));

    /* vfork: Kind läuft im Speicher des Elternteils, Elternteil ruht bis _exit. */
    static volatile int vf = 0;
    c = vfork();
    NEED(c >= 0);
    if (c == 0) { vf = 5; _exit(9); }
    NEED(waitpid(c, &st, 0) == c);
    report("vfork-kind", st);
    SHOWI("vfork teilt Speicher", vf);

    /* clone3 wie fork (exit_signal=SIGCHLD, keine Flags). */
    struct clone_args ca;
    memset(&ca, 0, sizeof ca);
    ca.exit_signal = SIGCHLD;
    long r = syscall(SYS_clone3, &ca, sizeof ca);
    if (r == 0) { priv[0] = 77; _exit(21); }
    NEED(r > 0);
    NEED(waitpid((pid_t)r, &st, 0) == (pid_t)r);
    report("clone3-kind", st);
    SHOW("clone3: privat getrennt", priv[0] == 1);
    memset(&ca, 0, sizeof ca);
    ca.exit_signal = 0x7f;
    T("clone3 ungültiges exit_signal", syscall(SYS_clone3, &ca, sizeof ca));
    T("clone3 zu kleine Struktur", syscall(SYS_clone3, &ca, 8));
    return 0;
}
