/* Ladybird V15 (Warten): Prozess startet Prozess. musl posix_spawn (clone(CLONE_VM|CLONE_VFORK) + execve) startet dieses Programm
 * erneut als Kind ("kind <modus> ..."), danach wait4/waitpid/waitid. Geprüft: Exitcode, Tod durch Signal, WNOHANG, ECHILD,
 * waitid-siginfo, geerbte Deskriptoren (ohne CLOEXEC offen, mit CLOEXEC nach execve zu), EOF erst nach dem Ende des Kindes,
 * getppid im Kind, posix_spawn auf ein fehlendes Programm (ENOENT über die CLOEXEC-Pipe von musl).
 * Das Kind schreibt nichts nach stdout; es meldet sich über Exitcode und eine Pipe. Keine PIDs in der Ausgabe. */
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

extern char **environ;

static int kind(int argc, char **argv)
{
    if (argc < 6) return 80;
    int code = atoi(argv[3]);
    int keep = atoi(argv[4]), gone = atoi(argv[5]);
    if (argc > 6 && getppid() != atoi(argv[6])) return 92;
    if (keep >= 0 && write(keep, "x", 1) != 1) return 90;
    if (gone >= 0 && (fcntl(gone, F_GETFD) != -1 || errno != EBADF)) return 91;
    if (strcmp(argv[2], "kill") == 0) {
        kill(getpid(), SIGKILL);
        return 93;
    }
    return code;
}

static pid_t start(char *self, const char *mode, const char *code, int keep, int gone, int *err)
{
    char k[16], g[16], pp[16];
    snprintf(k, sizeof k, "%d", keep);
    snprintf(g, sizeof g, "%d", gone);
    snprintf(pp, sizeof pp, "%d", (int)getpid());
    char *args[] = { self, "kind", (char *)mode, (char *)code, k, g, pp, NULL };
    pid_t pid = -1;
    *err = posix_spawn(&pid, self, NULL, NULL, args, environ);
    return pid;
}

int main(int argc, char **argv)
{
    if (argc > 1 && strcmp(argv[1], "kind") == 0) return kind(argc, argv);
    setvbuf(stdout, NULL, _IONBF, 0);
    alarm(30);
    int err, st = 0;

    /* 1. Exitcode 7, geerbte Pipe ohne CLOEXEC, zweite mit CLOEXEC. */
    int p[2], c[2];
    if (pipe(p) != 0 || pipe2(c, O_CLOEXEC) != 0) { printf("pipe fehlgeschlagen\n"); return 2; }
    printf("cloexec_flag=%d\n", fcntl(c[1], F_GETFD) & FD_CLOEXEC);
    pid_t a = start(argv[0], "exit", "7", p[1], c[1], &err);
    printf("spawn=%d pid_ok=%d\n", err, a > 0);
    close(p[1]);
    close(c[1]);
    char buf[8];
    ssize_t n = read(p[0], buf, sizeof buf);
    printf("pipe_read=%zd byte=%c\n", n, n > 0 ? buf[0] : '-');
    n = read(p[0], buf, sizeof buf);
    printf("pipe_eof=%zd\n", n);
    n = read(c[0], buf, sizeof buf);
    printf("cloexec_pipe_eof=%zd\n", n);
    pid_t w = waitpid(a, &st, 0);
    printf("waitpid_ok=%d exited=%d code=%d\n", w == a, WIFEXITED(st), WIFEXITED(st) ? WEXITSTATUS(st) : -1);
    errno = 0;
    w = waitpid(-1, &st, WNOHANG);
    printf("waitpid_none=%d errno=%d\n", (int)w, errno);
    close(p[0]);
    close(c[0]);

    /* 2. WNOHANG, bis das Kind fertig ist (erster Versuch 0 oder schon fertig; gezeigt wird nur das Ende). */
    a = start(argv[0], "exit", "0", -1, -1, &err);
    int spins = 0;
    while ((w = waitpid(a, &st, WNOHANG)) == 0 && spins < 3000) {
        struct timespec ts = { 0, 1000000 };
        nanosleep(&ts, NULL);
        spins++;
    }
    printf("wnohang_ok=%d code=%d\n", w == a, WEXITSTATUS(st));

    /* 3. waitid mit siginfo, WNOWAIT lässt den Zombie stehen. */
    a = start(argv[0], "exit", "5", -1, -1, &err);
    siginfo_t si;
    memset(&si, 0, sizeof si);
    int r = waitid(P_PID, a, &si, WEXITED | WNOWAIT);
    printf("waitid=%d signo=%d code=%d status=%d pid_ok=%d\n", r, si.si_signo, si.si_code, si.si_status, si.si_pid == a);
    w = waitpid(a, &st, 0);
    printf("nach_wnowait_ok=%d code=%d\n", w == a, WEXITSTATUS(st));

    /* 4. Kind stirbt an SIGKILL. */
    a = start(argv[0], "kill", "0", -1, -1, &err);
    w = waitpid(a, &st, 0);
    printf("kill_ok=%d signaled=%d sig=%d\n", w == a, WIFSIGNALED(st), WIFSIGNALED(st) ? WTERMSIG(st) : -1);

    /* 5. Fehlendes Programm: musl meldet den execve-Fehler des Kindes über seine CLOEXEC-Pipe. */
    pid_t x = -1;
    char *nargs[] = { "fehlt", NULL };
    err = posix_spawn(&x, "/gibt/es/nicht", NULL, NULL, nargs, environ);
    printf("spawn_fehlt=%d\n", err);
    errno = 0;
    w = waitpid(-1, &st, WNOHANG);
    printf("danach_keine_kinder=%d errno=%d\n", (int)w, errno);
    return 0;
}
