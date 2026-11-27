/* Ladybird V18 (LibCore setzt Handler für SIGCHLD, SIGINT, SIGPIPE): Signalzustellung an Handler im Ring 3.
 * SIGCHLD: ein Handler mit SA_SIGINFO zählt die Enden von drei posix_spawn-Kindern (si_pid, si_code CLD_EXITED, si_status).
 * SIGINT per kill an sich selbst: Handler läuft vor dem Rücksprung von kill, das Signal ist im Handler blockiert, danach nicht.
 * SIGPIPE: write auf eine Pipe ohne Leser ruft den Handler und liefert EPIPE. SIGUSR1 von einem Kind während read: ohne SA_RESTART
 * EINTR, mit SA_RESTART liest read danach weiter. SIGUSR2 mit SA_ONSTACK läuft auf dem Alternativstack; SA_RESETHAND setzt zurück.
 * Register und FPU-Zustand bleiben über den Handler erhalten (rt_sigreturn über sa_restorer von musl).
 * Die Kinder sind statische Mini-ELFs, die dieses Programm ins Arbeitsverzeichnis schreibt (ein Segment R+X). */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

extern char **environ;

#define BASE 0x400000u
#define CODE 0x78u /* hinter einem Programmkopf */
#define TS 0x1c0u  /* timespec {0, 100 ms} */
#define BYTE 0x1d0u
#define FILESIZE 0x200u

static unsigned char img[FILESIZE];
static int len;

static void b1(int v) { img[CODE + len++] = (unsigned char)v; }
static void b4(unsigned v) { for (int i = 0; i < 4; i++) b1((int)((v >> (8 * i)) & 0xff)); }
static void movr(int op, unsigned v) { b1(op); b4(v); } /* B8 eax, BF edi, BE esi, BA edx */
static void sys(void) { b1(0x0f); b1(0x05); }
static void put64(unsigned off, unsigned long v) { for (int i = 0; i < 8; i++) img[off + i] = (unsigned char)(v >> (8 * i)); }
static void sleep100(void) { movr(0xb8, 35); movr(0xbf, BASE + TS); b1(0x31); b1(0xf6); sys(); } /* nanosleep(&ts, 0) */

/* mode 0: exit_group(code). mode 1: 100 ms, kill(getppid(), SIGUSR1), exit 0. mode 2: wie 1, dann 100 ms und ein Byte auf fd 3. */
static int write_child(const char *path, int mode, int code)
{
    memset(img, 0, sizeof img);
    len = 0;
    if (mode != 0) {
        sleep100();
        movr(0xb8, 110); sys();                       /* getppid */
        b1(0x89); b1(0xc7);                           /* mov edi, eax */
        movr(0xbe, SIGUSR1); movr(0xb8, 62); sys();   /* kill(ppid, SIGUSR1) */
        if (mode == 2) {
            sleep100();
            movr(0xb8, 1); movr(0xbf, 3); movr(0xbe, BASE + BYTE); movr(0xba, 1); sys(); /* write(3, byte, 1) */
        }
    }
    movr(0xb8, 231); movr(0xbf, (unsigned)code); sys(); /* exit_group(code) */
    put64(TS, 0); put64(TS + 8, 100000000);
    img[BYTE] = 'K';

    memcpy(img, "\x7f" "ELF\x02\x01\x01", 7);
    img[16] = 2; img[18] = 62; img[20] = 1;
    put64(24, BASE + CODE);
    img[32] = 64; img[52] = 64; img[54] = 56; img[56] = 1;
    img[64] = 1; img[68] = 5;
    put64(64 + 16, BASE); put64(64 + 24, BASE);
    put64(64 + 32, FILESIZE); put64(64 + 40, FILESIZE); put64(64 + 48, 0x1000);
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0755);
    if (fd < 0) return -1;
    int ok = write(fd, img, sizeof img) == (ssize_t)sizeof img;
    return close(fd) == 0 && ok ? 0 : -1;
}

static void show(const char *what, long r)
{
    printf("%s=%ld errno=%d\n", what, r, r < 0 ? errno : 0);
    errno = 0;
}

static volatile int chld_count, chld_pid_ok, chld_code_ok, chld_status_sum;
static pid_t kids[3];

static void on_chld(int sig, siginfo_t *si, void *uc)
{
    (void)uc;
    if (sig != SIGCHLD) return;
    chld_count++;
    for (int i = 0; i < 3; i++)
        if (si->si_pid == kids[i]) chld_pid_ok++;
    if (si->si_code == CLD_EXITED) chld_code_ok++;
    chld_status_sum += si->si_status;
}

static void part_chld(void)
{
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = on_chld;
    sa.sa_flags = SA_SIGINFO | SA_RESTART;
    show("sigaction chld", sigaction(SIGCHLD, &sa, NULL));
    char name[16];
    for (int i = 0; i < 3; i++) {
        snprintf(name, sizeof name, "kind%d", i);
        show("kind schreiben", write_child(name, 0, i + 1));
        char *argv[] = { name, NULL };
        show("posix_spawn", posix_spawn(&kids[i], name, NULL, NULL, argv, environ));
        /* Je Kind warten, bis sein SIGCHLD gezählt ist: Standardsignale reihen sich nicht ein, zwei Enden während eines Handlers
         * zählten sonst als eins (Linux wie ZehOS). poll mit kurzer Frist verpasst kein Signal (höchstens 3 s). */
        for (int t = 0; t < 300 && chld_count < i + 1; t++) poll(NULL, 0, 10);
    }
    printf("sigchld gezaehlt=%d pid=%d cld_exited=%d status summe=%d\n", chld_count, chld_pid_ok, chld_code_ok, chld_status_sum);
    int sum = 0;
    for (int i = 0; i < 3; i++) {
        int st = 0;
        pid_t r = waitpid(kids[i], &st, 0);
        if (r == kids[i] && WIFEXITED(st)) sum += WEXITSTATUS(st);
    }
    printf("waitpid summe=%d\n", sum);
    signal(SIGCHLD, SIG_DFL);
}

static volatile int int_count, int_blocked_inside;

static void on_int(int sig)
{
    sigset_t cur;
    sigprocmask(SIG_BLOCK, NULL, &cur);
    int_blocked_inside = sigismember(&cur, sig);
    int_count++;
}

static void part_int(void)
{
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_int;
    show("sigaction int", sigaction(SIGINT, &sa, NULL));
    volatile double x = 1.25;
    double y = x * 3.0;
    show("kill int", kill(getpid(), SIGINT));
    sigset_t cur;
    sigprocmask(SIG_BLOCK, NULL, &cur);
    printf("sigint gezaehlt=%d im handler blockiert=%d danach blockiert=%d fpu=%.3f\n", int_count, int_blocked_inside,
           sigismember(&cur, SIGINT), y + x);
}

static volatile int pipe_count;
static void on_pipe(int sig) { if (sig == SIGPIPE) pipe_count++; }

static void part_pipe(void)
{
    int p[2];
    show("pipe", pipe(p));
    close(p[0]);
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_pipe;
    show("sigaction pipe", sigaction(SIGPIPE, &sa, NULL));
    show("write ohne leser", write(p[1], "x", 1));
    printf("sigpipe gezaehlt=%d\n", pipe_count);
    signal(SIGPIPE, SIG_IGN);
    show("write ignoriert", write(p[1], "x", 1));
    printf("sigpipe gezaehlt=%d\n", pipe_count);
    close(p[1]);
}

static volatile int usr1_count;
static void on_usr1(int sig) { if (sig == SIGUSR1) usr1_count++; }

static void part_eintr(int restart)
{
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_usr1;
    sa.sa_flags = restart ? SA_RESTART : 0;
    show(restart ? "sigaction usr1 restart" : "sigaction usr1", sigaction(SIGUSR1, &sa, NULL));
    int p[2];
    show("pipe", pipe(p));
    show("kind schreiben", write_child("kindusr", restart ? 2 : 1, 0));
    posix_spawn_file_actions_t fa;
    posix_spawn_file_actions_init(&fa);
    posix_spawn_file_actions_adddup2(&fa, p[1], 3);
    pid_t pid;
    char *argv[] = { "kindusr", NULL };
    show("posix_spawn", posix_spawn(&pid, "kindusr", &fa, NULL, argv, environ));
    posix_spawn_file_actions_destroy(&fa);
    usr1_count = 0;
    char c = 0;
    show(restart ? "read mit restart" : "read ohne restart", read(p[0], &c, 1));
    printf("sigusr1 gezaehlt=%d byte=%c\n", usr1_count, c ? c : '-');
    int st = 0;
    show("waitpid", waitpid(pid, &st, 0) == pid ? 0 : -1);
    close(p[0]);
    close(p[1]);
}

static char altstack[65536];
static volatile int usr2_count, usr2_on_alt;

static void on_usr2(int sig)
{
    char here;
    if (sig == SIGUSR2) usr2_count++;
    usr2_on_alt = &here > altstack && &here < altstack + sizeof altstack;
}

static void part_alt(void)
{
    stack_t ss = { .ss_sp = altstack, .ss_size = sizeof altstack, .ss_flags = 0 };
    show("sigaltstack", sigaltstack(&ss, NULL));
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_usr2;
    sa.sa_flags = SA_ONSTACK | SA_RESETHAND;
    show("sigaction usr2", sigaction(SIGUSR2, &sa, NULL));
    show("kill usr2", kill(getpid(), SIGUSR2));
    struct sigaction old;
    sigaction(SIGUSR2, NULL, &old);
    printf("sigusr2 gezaehlt=%d auf altstack=%d zurueckgesetzt=%d\n", usr2_count, usr2_on_alt, old.sa_handler == SIG_DFL);
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    part_chld();
    part_int();
    part_pipe();
    part_eintr(0);
    part_eintr(1);
    part_alt();
    printf("ende\n");
    return 0;
}
