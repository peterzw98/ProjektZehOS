/* Ladybird V15 (Warten): Prozess startet Prozess. musl posix_spawn (clone(CLONE_VM|CLONE_VFORK) + execve) startet kleine
 * Kindprogramme, danach wait4/waitpid/waitid. Geprüft: Exitcode, Tod durch Signal, WNOHANG, ECHILD, waitid-siginfo, geerbte
 * Deskriptoren (ohne CLOEXEC offen, mit CLOEXEC nach execve zu), EOF erst nach dem Ende des Kindes, getppid im Kind,
 * posix_spawn auf ein fehlendes Programm (ENOENT über die CLOEXEC-Pipe von musl).
 * Die Kinder sind statische Mini-ELFs, die dieses Programm ins Arbeitsverzeichnis schreibt (der Gast hat die Programmdatei
 * selbst nicht im Dateisystem); Nummern, Exitcode und Eltern-PID stehen als Konstanten im Code. Das Kind schreibt nichts nach
 * stdout; es meldet sich über Exitcode und eine Pipe. Keine PIDs in der Ausgabe. */
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

static unsigned char code[256];
static int len;

static void b1(int v) { code[len++] = (unsigned char)v; }
static void b4(unsigned v) { for (int i = 0; i < 4; i++) b1((int)((v >> (8 * i)) & 0xff)); }
static void movr(int op, unsigned v) { b1(op); b4(v); } /* B8 eax, BF edi, BE esi, BA edx */
static void sys(void) { b1(0x0f); b1(0x05); }
/* Bedingung erfüllt (je rel8 über den Fehlerblock), sonst exit_group(fail). */
static void ok_or_exit(int fail) { b1(0x74); b1(12); movr(0xb8, 231); movr(0xbf, (unsigned)fail); sys(); }

/* Kind: keep >= 0 schreibt ein Byte auf keep (sonst exit 90); gone >= 0 muss EBADF bei F_GETFD liefern (sonst 91);
 * getppid muss ppid sein (sonst 92); kill_self 1 beendet mit SIGKILL über kill(getpid()) (sonst 93), 2 dreht endlos im Ring 3, bis der
 * Elternprozess es mit kill beendet; sonst exit_group(exitcode). */
static int write_child(const char *path, int exitcode, int keep, int gone, int ppid, int kill_self)
{
    len = 0;
    if (keep >= 0) {
        b1(0x6a); b1('x');                     /* push 'x' */
        b1(0x48); b1(0x89); b1(0xe6);          /* mov rsi, rsp */
        movr(0xb8, 1); movr(0xbf, (unsigned)keep); movr(0xba, 1); sys();
        b1(0x48); b1(0x83); b1(0xf8); b1(1);   /* cmp rax, 1 */
        ok_or_exit(90);
    }
    if (gone >= 0) {
        movr(0xb8, 72); movr(0xbf, (unsigned)gone); movr(0xbe, F_GETFD); sys();
        b1(0x48); b1(0x83); b1(0xf8); b1(0xf7); /* cmp rax, -9 (EBADF) */
        ok_or_exit(91);
    }
    movr(0xb8, 110); sys();                    /* getppid */
    b1(0x3d); b4((unsigned)ppid);              /* cmp eax, ppid */
    ok_or_exit(92);
    if (kill_self == 1) {
        movr(0xb8, 39); sys();                 /* getpid */
        b1(0x89); b1(0xc7);                    /* mov edi, eax: kill(pid, SIGKILL) */
        movr(0xbe, SIGKILL); movr(0xb8, 62); sys();
        movr(0xb8, 231); movr(0xbf, 93); sys();
    }
    if (kill_self == 2) {
        b1(0xeb); b1(0xfe);                    /* jmp $ */
    }
    movr(0xb8, 231); movr(0xbf, (unsigned)exitcode); sys();

    unsigned char f[0x78 + sizeof code];
    memset(f, 0, sizeof f);
    memcpy(f, "\x7f" "ELF\x02\x01\x01", 7);
    f[16] = 2; f[18] = 62; f[20] = 1;                                   /* ET_EXEC, x86-64, Version */
    unsigned long entry = 0x400078, size = 0x78 + (unsigned long)len;
    for (int i = 0; i < 8; i++) f[24 + i] = (unsigned char)(entry >> (8 * i));
    f[32] = 64; f[52] = 64; f[54] = 56; f[56] = 1;                      /* Programmköpfe ab 64, einer */
    unsigned char *ph = f + 64;
    ph[0] = 1; ph[4] = 5;                                               /* PT_LOAD, R X */
    for (int i = 0; i < 8; i++) {
        ph[16 + i] = ph[24 + i] = (unsigned char)(0x400000UL >> (8 * i));
        ph[32 + i] = ph[40 + i] = (unsigned char)(size >> (8 * i));
        ph[48 + i] = (unsigned char)(0x1000UL >> (8 * i));
    }
    memcpy(f + 0x78, code, (size_t)len);
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0755);
    if (fd < 0) return -1;
    int ok = write(fd, f, size) == (ssize_t)size;
    return close(fd) == 0 && ok ? 0 : -1;
}

static int counter;

static pid_t start(int exitcode, int keep, int gone, int kill_self, int *err)
{
    char path[16];
    snprintf(path, sizeof path, "kind%d", counter++);
    if (write_child(path, exitcode, keep, gone, (int)getpid(), kill_self) != 0) { *err = -1; return -1; }
    char *args[] = { path, NULL };
    pid_t pid = -1;
    *err = posix_spawn(&pid, path, NULL, NULL, args, environ);
    return pid;
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    alarm(30);
    int err, st = 0;

    /* 1. Exitcode 7, geerbte Pipe ohne CLOEXEC, zweite mit CLOEXEC. */
    int p[2], c[2];
    if (pipe(p) != 0 || pipe2(c, O_CLOEXEC) != 0) { printf("pipe fehlgeschlagen\n"); return 2; }
    printf("cloexec_flag=%d\n", fcntl(c[1], F_GETFD) & FD_CLOEXEC);
    pid_t a = start(7, p[1], c[1], 0, &err);
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
    a = start(0, -1, -1, 0, &err);
    int spins = 0;
    while ((w = waitpid(a, &st, WNOHANG)) == 0 && spins < 3000) {
        struct timespec ts = { 0, 1000000 };
        nanosleep(&ts, NULL);
        spins++;
    }
    printf("wnohang_ok=%d code=%d\n", w == a, WEXITSTATUS(st));

    /* 3. waitid mit siginfo, WNOWAIT lässt den Zombie stehen. */
    a = start(5, -1, -1, 0, &err);
    siginfo_t si;
    memset(&si, 0, sizeof si);
    int r = waitid(P_PID, a, &si, WEXITED | WNOWAIT);
    printf("waitid=%d signo=%d code=%d status=%d pid_ok=%d\n", r, si.si_signo, si.si_code, si.si_status, si.si_pid == a);
    w = waitpid(a, &st, 0);
    printf("nach_wnowait_ok=%d code=%d\n", w == a, WEXITSTATUS(st));

    /* 4. Kind stirbt an SIGKILL. */
    a = start(0, -1, -1, 1, &err);
    w = waitpid(a, &st, 0);
    printf("kill_ok=%d signaled=%d sig=%d\n", w == a, WIFSIGNALED(st), WIFSIGNALED(st) ? WTERMSIG(st) : -1);

    /* 4b. Der Elternprozess beendet ein Kind, das im Ring 3 dreht, mit kill(pid, SIGTERM); kill(pid, 0) prüft vorher nur. */
    a = start(0, -1, -1, 2, &err);
    r = kill(a, 0);
    int r2 = kill(a, SIGTERM);
    w = waitpid(a, &st, 0);
    printf("kill_kind=%d %d ok=%d signaled=%d sig=%d\n", r, r2, w == a, WIFSIGNALED(st), WIFSIGNALED(st) ? WTERMSIG(st) : -1);
    errno = 0;
    r = kill(a, 0);
    printf("kill_geerntet=%d errno=%d\n", r, errno);

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
