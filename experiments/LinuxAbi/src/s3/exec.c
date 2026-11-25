/* execve: Argumente und Umgebung, Signalbehandlung (ignoriert bleibt, behandelt wird
 * Standard, Maske bleibt), Dateideskriptoren (ohne O_CLOEXEC bleiben, mit CLOEXEC enden),
 * Fehlernummern, Aufruf aus einem fork-Kind, Elternteil sieht den Exitcode. */
#define _GNU_SOURCE
#include "util.h"
#include <fcntl.h>
#include <signal.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <string.h>
#include <sys/wait.h>

static void handler(int s) { (void)s; }

static int child_main(int argc, char **argv, char **envp)
{
    INIT();
    printf("kind argc=%d\n", argc);
    for (int i = 0; i < argc; i++) printf("kind argv[%d]=%s\n", i, i == 0 ? "<pfad>" : argv[i]);
    int n = 0;
    for (char **e = envp; *e; e++) { printf("kind envp[%d]=%s\n", n++, *e); }
    struct sigaction sa;
    sigaction(SIGUSR1, NULL, &sa);
    SHOW("SIGUSR1 ignoriert geblieben", sa.sa_handler == SIG_IGN);
    sigaction(SIGUSR2, NULL, &sa);
    SHOW("SIGUSR2 wieder Standard", sa.sa_handler == SIG_DFL);
    sigset_t m;
    sigprocmask(SIG_BLOCK, NULL, &m);
    SHOW("SIGTERM gesperrt geblieben", sigismember(&m, SIGTERM) == 1);
    SHOW("fd 7 offen (ohne CLOEXEC)", fcntl(7, F_GETFD) == 0);
    errno = 0;
    int r = fcntl(8, F_GETFD);
    printf("fd 8 (CLOEXEC): ret=%d errno=%d\n", r, errno);
    char b[8] = {0};
    SHOWI("fd 7 lesen (pread)", pread(7, b, 5, 0));
    SHOW("fd 7 Inhalt", memcmp(b, "hallo", 5) == 0);
    return 33;
}

int main(int argc, char **argv, char **envp)
{
    if (argc == 3 && strcmp(argv[1], "--kind") == 0) return child_main(argc, argv, envp);
    INIT();
    NEED(argc >= 1);
    int fd = open("datei", O_RDWR | O_CREAT | O_TRUNC, 0644);
    NEED(fd >= 0 && write(fd, "hallo", 5) == 5);
    NEED(dup2(fd, 7) == 7);
    NEED(fcntl(fd, F_DUPFD_CLOEXEC, 8) == 8);
    signal(SIGUSR1, SIG_IGN);
    signal(SIGUSR2, handler);
    sigset_t m;
    sigemptyset(&m);
    sigaddset(&m, SIGTERM);
    sigprocmask(SIG_BLOCK, &m, NULL);

    char *const av[] = { argv[0], "--kind", "mit Leerzeichen", NULL };
    char *const ev[] = { "E1=wert1", "E2=", "E3=a=b", NULL };
    char *noexec_av[] = { "x", NULL };
    char *no_env[] = { NULL };

    T("ENOENT", execve("gibt-es-nicht", noexec_av, no_env));
    NEED(close(open("leer", O_WRONLY | O_CREAT, 0644)) == 0);
    T("EACCES (nicht ausführbar)", execve("leer", noexec_av, no_env));
    NEED(close(open("leer-x", O_WRONLY | O_CREAT, 0755)) == 0);   /* umask 022 lässt 0755 stehen */
    T("ENOEXEC (leere Datei)", execve("leer-x", noexec_av, no_env));
    T("EACCES (Verzeichnis)", execve(".", noexec_av, no_env));
    T("EFAULT (Pfad)", syscall(SYS_execve, (char *)1, noexec_av, no_env));
    T("EFAULT (argv)", syscall(SYS_execve, argv[0], (char **)1, no_env));
    T("ENOTDIR", execve("datei/x", noexec_av, no_env));

    /* fork + exec: Elternteil wartet und sieht den Exitcode des neuen Programms. */
    fflush(stdout);
    pid_t c = fork();
    NEED(c >= 0);
    if (c == 0) {
        execve(argv[0], av, ev);
        _exit(127);
    }
    int st = 0;
    NEED(waitpid(c, &st, 0) == c);
    SHOW("Elternteil: exit 33", WIFEXITED(st) && WEXITSTATUS(st) == 33);

    /* Kind ohne exec-Fehler: execve mit nicht vorhandenem Programm gibt dem Kind -1 zurück. */
    printf("jetzt exec im selben Prozess\n");
    /* Das neue Programm ersetzt diesen Prozess; sein Exitcode ist der des Gesamtprogramms. */
    execve(argv[0], av, ev);
    printf("execve fehlgeschlagen errno=%d\n", errno);
    return 1;
}
