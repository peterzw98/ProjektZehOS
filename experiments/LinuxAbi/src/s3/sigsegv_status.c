/* Signale: SIGSEGV mit Handler (si_code, si_addr), Rückkehr per siglongjmp, Handler auf eigenem
 * Stapel (sigaltstack), raise/kill/sigprocmask/sigpending, Unterbrechung eines read (EINTR);
 * ein Kind stirbt an SIGSEGV (wait4-Status); am Ende stürzt das Programm selbst mit
 * Standardbehandlung ab: Status `signal 11`, Ausgabe davor vollständig. */
#define _GNU_SOURCE
#include "util.h"
#include <setjmp.h>
#include <signal.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/wait.h>
#include <time.h>

static sigjmp_buf jb;
static volatile sig_atomic_t seg_code, seg_signo, seg_addr_ok, seg_on_alt, usr1_count, usr1_blocked_in_handler;
static void *volatile expect_addr;
static char *volatile alt_lo, *volatile alt_hi;

static void on_segv(int sig, siginfo_t *si, void *uc)
{
    (void)uc;
    seg_signo = sig;
    seg_code = si->si_code;
    seg_addr_ok = si->si_addr == expect_addr;
    char here;
    seg_on_alt = alt_lo && &here >= alt_lo && &here < alt_hi;
    siglongjmp(jb, 1);
}

static void on_usr1(int sig)
{
    (void)sig;
    usr1_count++;
    sigset_t cur;
    sigprocmask(SIG_BLOCK, NULL, &cur);
    usr1_blocked_in_handler = sigismember(&cur, SIGUSR1) == 1;   /* der behandelte Signal ist während des Handlers gesperrt */
}

static void on_alarm(int sig) { (void)sig; }

static void fault(volatile char *p, int write)
{
    expect_addr = (void *)p;
    if (sigsetjmp(jb, 1) == 0) {
        if (write) *p = 1; else (void)*p;
        printf("kein Fehler\n");
    }
}

int main(void)
{
    INIT();
    long ps = sysconf(_SC_PAGESIZE);
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = on_segv;
    sa.sa_flags = SA_SIGINFO;
    sigemptyset(&sa.sa_mask);
    NEED(sigaction(SIGSEGV, &sa, NULL) == 0);

    char *page = mmap(NULL, 3 * ps, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    NEED(page != MAP_FAILED);
    NEED(mprotect(page + ps, ps, PROT_READ) == 0);
    fault(page, 0);
    SHOWI("PROT_NONE lesen: signo", seg_signo);
    SHOWI("  si_code (SEGV_ACCERR=2)", seg_code);
    SHOW("  si_addr", seg_addr_ok);
    fault(page + ps + 17, 1);
    SHOWI("PROT_READ schreiben: si_code", seg_code);
    SHOW("  si_addr", seg_addr_ok);
    munmap(page + 2 * ps, ps);
    fault(page + 2 * ps + 5, 0);
    SHOWI("ungemappt lesen: si_code (SEGV_MAPERR=1)", seg_code);
    SHOW("  si_addr", seg_addr_ok);
    fault((volatile char *)0, 0);
    SHOWI("Adresse 0: si_code", seg_code);
    SHOW("  si_addr", seg_addr_ok);

    /* Handler auf eigenem Stapel. */
    stack_t ss;
    ss.ss_size = 64 * 1024;
    ss.ss_sp = mmap(NULL, ss.ss_size, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    NEED(ss.ss_sp != MAP_FAILED);
    ss.ss_flags = 0;
    NEED(sigaltstack(&ss, NULL) == 0);
    alt_lo = ss.ss_sp;
    alt_hi = alt_lo + ss.ss_size;
    sa.sa_flags = SA_SIGINFO | SA_ONSTACK;
    NEED(sigaction(SIGSEGV, &sa, NULL) == 0);
    fault(page, 0);
    SHOW("sigaltstack: Handler lief auf dem Ersatzstapel", seg_on_alt);
    stack_t old;
    NEED(sigaltstack(NULL, &old) == 0);
    SHOW("  sigaltstack abfragen", old.ss_sp == ss.ss_sp && old.ss_size == ss.ss_size && !(old.ss_flags & SS_DISABLE));
    T("sigaltstack zu klein", ({ stack_t t = { .ss_sp = ss.ss_sp, .ss_size = 100, .ss_flags = 0 }; sigaltstack(&t, NULL); }));

    /* Signal an sich selbst: Handler, Sperre, anhängig. */
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_usr1;
    sigemptyset(&sa.sa_mask);
    NEED(sigaction(SIGUSR1, &sa, NULL) == 0);
    raise(SIGUSR1);
    SHOWI("raise: Handler einmal gelaufen", usr1_count);
    SHOW("  im Handler gesperrt", usr1_blocked_in_handler);
    sigset_t blk, pend, cur;
    sigemptyset(&blk);
    sigaddset(&blk, SIGUSR1);
    NEED(sigprocmask(SIG_BLOCK, &blk, NULL) == 0);
    raise(SIGUSR1);
    raise(SIGUSR1);   /* Standardsignale reihen sich nicht ein */
    SHOWI("gesperrt: Handler nicht gelaufen", usr1_count);
    sigpending(&pend);
    SHOW("  anhängig", sigismember(&pend, SIGUSR1) == 1);
    NEED(sigprocmask(SIG_UNBLOCK, &blk, &cur) == 0);
    SHOW("  alte Maske enthielt SIGUSR1", sigismember(&cur, SIGUSR1) == 1);
    SHOWI("entsperrt: Handler genau einmal", usr1_count);
    sigpending(&pend);
    SHOW("  nichts mehr anhängig", sigismember(&pend, SIGUSR1) == 0);
    T("kill an sich selbst, Signal 0", kill(getpid(), 0));
    T("kill unbekannte PID", kill(0x3ffffff0, SIGUSR1));
    T("kill ungültiges Signal", kill(getpid(), 99));
    T("sigaction SIGKILL", sigaction(SIGKILL, &sa, NULL));
    T("sigaction ungültiges Signal", sigaction(99, &sa, NULL));

    /* Unterbrochener Aufruf: ohne SA_RESTART gibt read EINTR zurück (Signal kommt vom Kind). */
    int p[2];
    NEED(pipe(p) == 0);
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_alarm;
    sigemptyset(&sa.sa_mask);
    NEED(sigaction(SIGALRM, &sa, NULL) == 0);
    pid_t killer = fork();
    NEED(killer >= 0);
    if (killer == 0) {
        struct timespec d = { 0, 50 * 1000000L };
        nanosleep(&d, NULL);
        _exit(kill(getppid(), SIGALRM) == 0 ? 0 : 3);
    }
    char b;
    T("read unterbrochen (kein SA_RESTART)", read(p[0], &b, 1));
    int ws;
    NEED(waitpid(killer, &ws, 0) == killer);

    /* Kind stirbt an SIGSEGV (Standardbehandlung). */
    pid_t c = fork();
    NEED(c >= 0);
    if (c == 0) {
        signal(SIGSEGV, SIG_DFL);
        *(volatile char *)0 = 1;
        _exit(0);
    }
    NEED(waitpid(c, &ws, 0) == c);
    SHOW("Kind: durch Signal beendet", WIFSIGNALED(ws));
    SHOWI("  Signal", WTERMSIG(ws));

    /* Kind, das mit SIGKILL beendet wird (nicht abfangbar). */
    c = fork();
    NEED(c >= 0);
    if (c == 0) {
        signal(SIGTERM, SIG_IGN);
        for (;;) pause();
    }
    NEED(kill(c, SIGTERM) == 0);
    NEED(kill(c, SIGKILL) == 0);
    NEED(waitpid(c, &ws, 0) == c);
    SHOW("SIGKILL trotz ignoriertem SIGTERM", WIFSIGNALED(ws) && WTERMSIG(ws) == SIGKILL);

    /* Ende: eigener Absturz, Standardbehandlung. */
    signal(SIGSEGV, SIG_DFL);
    printf("vor dem Absturz\n");
    *(volatile char *)0 = 1;
    printf("NACH DEM ABSTURZ (darf nicht erscheinen)\n");
    return 0;
}
