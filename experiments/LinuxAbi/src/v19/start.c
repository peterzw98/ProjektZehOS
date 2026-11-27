/* V19 harte Startlücken (Ladybird #636): ppoll mit atomarer Signalmaske, close_range (auch
 * CLOSE_RANGE_CLOEXEC), /proc/self/fd als Verzeichnis, prctl PR_SET_PDEATHSIG im vfork-Kind
 * (über execve hinweg) und execve im Hauptprozess (Kinder, Dateien und Maske ziehen mit um).
 * Nur deterministischer Text; Kinder melden sich über Exitcode oder eine Pipe. */
#define _GNU_SOURCE
#include "../s3/util.h"
#include <dirent.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <time.h>

extern char **environ;
static volatile sig_atomic_t got;
static char self[512];

/* Eigener Pfad über /proc/self/exe (argv[0] ist im Gast kein Pfad, den posix_spawn findet). */
static char *self_path(char *argv0)
{
    ssize_t n = readlink("/proc/self/exe", self, sizeof self - 1);
    if (n <= 0) return argv0;
    self[n] = 0;
    return self;
}
static void on_sig(int s) { got = s; }

/* Enkel: wartet auf SIGUSR1 (PDEATHSIG, wenn der Elternprozess endet) und meldet es über fd 50. */
static int waiter(void)
{
    signal(SIGUSR1, on_sig);
    int sig = 0;
    CNEED(prctl(PR_GET_PDEATHSIG, &sig) == 0 && sig == SIGUSR1);
    for (int i = 0; i < 500 && !got; i++) {
        struct timespec t = { 0, 10 * 1000 * 1000 };
        nanosleep(&t, NULL);
    }
    char c = got == SIGUSR1 ? 'P' : 'x';
    CNEED(write(50, &c, 1) == 1);
    return 0;
}

/* Kind: startet den Enkel per vfork, setzt dort PDEATHSIG vor execve und endet sofort. */
static int middle(char *self)
{
    pid_t c = vfork();
    if (c == 0) {
        if (prctl(PR_SET_PDEATHSIG, SIGUSR1) != 0) _exit(4);
        char *av[] = { self, "--enkel", NULL };
        execve(self, av, environ);
        _exit(5);
    }
    if (c < 0) return 6;
    /* Der Enkel soll seinen Handler stehen haben, bevor dieser Prozess endet. */
    struct timespec t = { 0, 200 * 1000 * 1000 };
    nanosleep(&t, NULL);
    return 0;
}

static int second(int argc)
{
    INIT();
    printf("zweites Abbild argc=%d\n", argc);
    SHOW("fd 7 offen (ohne CLOEXEC)", fcntl(7, F_GETFD) == 0);
    errno = 0;
    SHOWI("fd 8 (CLOEXEC) errno", fcntl(8, F_GETFD) == -1 ? errno : 0);
    sigset_t m;
    sigprocmask(SIG_BLOCK, NULL, &m);
    SHOW("SIGTERM gesperrt geblieben", sigismember(&m, SIGTERM) == 1);
    int st = 0;
    pid_t w = waitpid(-1, &st, 0);
    SHOW("Kind von vor execve geerntet", w > 0 && WIFEXITED(st) && WEXITSTATUS(st) == 9);
    return 33;
}

int main(int argc, char **argv)
{
    if (argc == 2 && strcmp(argv[1], "--enkel") == 0) return waiter();
    if (argc == 2 && strcmp(argv[1], "--mitte") == 0) return middle(self_path(argv[0]));
    if (argc == 2 && strcmp(argv[1], "--neun") == 0) return 9;
    if (argc == 2 && strcmp(argv[1], "--zwei") == 0) return second(argc);
    INIT();
    char *exe = self_path(argv[0]);

    /* ppoll: Frist 0, bereite Pipe, Fehler. */
    int p[2];
    NEED(pipe(p) == 0);
    struct timespec zero = { 0, 0 };
    T("ppoll leer Frist 0", ppoll(NULL, 0, &zero, NULL));
    NEED(write(p[1], "a", 1) == 1);
    struct pollfd pf = { p[0], POLLIN, 0 };
    struct timespec sec = { 1, 0 };
    T("ppoll Pipe bereit", ppoll(&pf, 1, &sec, NULL));
    SHOW("revents POLLIN", pf.revents == POLLIN);
    sigset_t empty;
    sigemptyset(&empty);
    T("ppoll sigsetsize 4", syscall(SYS_ppoll, NULL, 0, &zero, &empty, 4));
    struct timespec bad = { 0, 1000000000L };
    T("ppoll nsec zu gross", ppoll(NULL, 0, &bad, NULL));
    T("ppoll Frist EFAULT", syscall(SYS_ppoll, NULL, 0, (void *)8, NULL, 8));

    /* Atomare Maske: SIGUSR2 gesperrt und anhängig; ppoll gibt es nur während des Wartens frei. */
    signal(SIGUSR2, on_sig);
    sigset_t m2;
    sigemptyset(&m2);
    sigaddset(&m2, SIGUSR2);
    sigprocmask(SIG_BLOCK, &m2, NULL);
    raise(SIGUSR2);
    SHOW("SIGUSR2 noch nicht zugestellt", got == 0);
    struct timespec five = { 5, 0 };
    T("ppoll mit freigebender Maske", ppoll(NULL, 0, &five, &empty));
    SHOW("Handler lief", got == SIGUSR2);
    sigset_t now;
    sigprocmask(SIG_BLOCK, NULL, &now);
    SHOW("SIGUSR2 danach wieder gesperrt", sigismember(&now, SIGUSR2) == 1);
    got = 0;
    raise(SIGUSR2);
    T("ppoll ohne Maske bleibt gesperrt", ppoll(NULL, 0, &zero, NULL));
    SHOW("Handler nicht gelaufen", got == 0);
    sigprocmask(SIG_UNBLOCK, &m2, NULL);
    SHOW("nach Freigabe gelaufen", got == SIGUSR2);
    got = 0;

    /* close_range */
    int f = open("datei", O_RDWR | O_CREAT | O_TRUNC, 0644);
    NEED(f >= 0 && write(f, "hallo", 5) == 5);
    NEED(dup2(f, 20) == 20 && dup2(f, 21) == 21 && dup2(f, 22) == 22);
    T("close_range first>last", syscall(SYS_close_range, 22, 21, 0));
    T("close_range Flag unbekannt", syscall(SYS_close_range, 20, 22, 1));
    T("close_range CLOEXEC 20..21", syscall(SYS_close_range, 20, 21, 4));
    SHOWI("fd 20 FD_CLOEXEC", fcntl(20, F_GETFD));
    SHOWI("fd 22 FD_CLOEXEC", fcntl(22, F_GETFD));
    T("close_range 21..~0", syscall(SYS_close_range, 21, ~0U, 0));
    SHOW("fd 20 offen", fcntl(20, F_GETFD) >= 0);
    errno = 0;
    SHOWI("fd 21 errno", fcntl(21, F_GETFD) == -1 ? errno : 0);
    SHOWI("fd 22 errno", fcntl(22, F_GETFD) == -1 ? errno : 0);

    /* /proc/self/fd */
    DIR *d = opendir("/proc/self/fd");
    NEED(d != NULL);
    int dots = 0, saw20 = 0, saw21 = 0, saw0 = 0, self = 0;
    for (struct dirent *e; (e = readdir(d)) != NULL;) {
        if (e->d_name[0] == '.') { dots++; continue; }
        int n = atoi(e->d_name);
        if (n == 0) saw0 = 1;
        if (n == 20) saw20 = 1;
        if (n == 21) saw21 = 1;
        if (n == dirfd(d)) self = 1;
    }
    SHOWI("/proc/self/fd Punkte", dots);
    SHOW("/proc/self/fd hat 0, 20, eigenen fd", saw0 && saw20 && self);
    SHOW("/proc/self/fd ohne 21", !saw21);
    closedir(d);
    T("/proc/self/fd schreibend", open("/proc/self/fd", O_RDWR));

    /* PDEATHSIG über vfork + execve: Mitte endet, der Enkel bekommt SIGUSR1 und schreibt 'P'. */
    int r[2];
    NEED(pipe(r) == 0 && dup2(r[1], 50) == 50);
    close(r[1]);
    T("prctl PDEATHSIG 65", prctl(PR_SET_PDEATHSIG, 65));
    int pd = -1;
    T("prctl GET_PDEATHSIG", prctl(PR_GET_PDEATHSIG, &pd));
    SHOWI("eigenes PDEATHSIG", pd);
    char *mid[] = { argv[0], "--mitte", NULL };
    pid_t m = 0;
    NEED(posix_spawn(&m, exe, NULL, NULL, mid, environ) == 0);
    int st = 0;
    NEED(waitpid(m, &st, 0) == m);
    SHOW("Mitte exit 0", WIFEXITED(st) && WEXITSTATUS(st) == 0);
    close(50);
    struct pollfd rf = { r[0], POLLIN, 0 };
    struct timespec ten = { 10, 0 };
    T("ppoll auf Enkel", ppoll(&rf, 1, &ten, NULL));
    char c = 0;
    T("Enkel meldet", read(r[0], &c, 1));
    SHOW("Enkel bekam SIGUSR1", c == 'P');

    /* execve im Hauptprozess: ein laufendes Kind, fd 7 ohne und fd 8 mit CLOEXEC, SIGTERM gesperrt. */
    NEED(dup2(f, 7) == 7 && fcntl(f, F_DUPFD_CLOEXEC, 8) == 8);
    sigset_t t;
    sigemptyset(&t);
    sigaddset(&t, SIGTERM);
    sigprocmask(SIG_BLOCK, &t, NULL);
    char *nine[] = { argv[0], "--neun", NULL };
    pid_t k = 0;
    NEED(posix_spawn(&k, exe, NULL, NULL, nine, environ) == 0);
    char *two[] = { argv[0], "--zwei", NULL };
    printf("jetzt execve im Hauptprozess\n");
    execve(exe, two, environ);
    printf("execve fehlgeschlagen errno=%d\n", errno);
    return 1;
}
