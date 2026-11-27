/* V24 (Ladybird): Ausnahmen im Ring 3 als Signal an einen Handler: #UD (SIGILL, ILL_ILLOPN), #DE (SIGFPE, FPE_INTDIV), #GP (SIGSEGV,
 * SI_KERNEL, si_addr 0), Seitenfehler mit mprotect im Handler und Wiederholung des Befehls; Rückkehr über den ucontext (RIP weiter)
 * und rt_sigreturn. Ohne int3: Cosmos trägt das Gate für #BP mit DPL 0 ein, int3 im Ring 3 ist dort #GP (SIGSEGV statt SIGTRAP). Kinder: blockiertes, ignoriertes Signal und Fehler im Handler beenden mit dem Signal.
 * Dazu V21-P3: 1 GiB MAP_SHARED|MAP_ANONYMOUS|MAP_NORESERVE über fork. Die Ausgabe ist mit Linux 6.12 gleich (expected/v24). */
#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/wait.h>
#include <ucontext.h>
#include <unistd.h>

#define SHOW(name, cond) printf("%s=%d\n", (name), (cond) ? 1 : 0)
#define SHOWI(name, val) printf("%s=%lld\n", (name), (long long)(val))
#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)

static volatile sig_atomic_t got_sig, got_code, addr_is_rip, addr_zero, hits;
static volatile long skip;            /* so viele Bytes überspringt der Handler (0: Befehl wiederholen) */
static char *volatile ro_page;
static long ps;

static void on_fault(int sig, siginfo_t *si, void *ucv)
{
    ucontext_t *uc = ucv;
    greg_t *rip = &uc->uc_mcontext.gregs[REG_RIP];
    got_sig = sig;
    got_code = si->si_code;
    addr_is_rip = (greg_t)(uintptr_t)si->si_addr == *rip;
    addr_zero = si->si_addr == NULL;
    hits++;
    if (sig == SIGSEGV && ro_page && (char *)si->si_addr >= ro_page && (char *)si->si_addr < ro_page + ps) {
        mprotect(ro_page, ps, PROT_READ | PROT_WRITE);   /* der Befehl läuft danach noch einmal */
        return;
    }
    *rip += skip;
}

static void set(int sig, void (*h)(int, siginfo_t *, void *))
{
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = h;
    sa.sa_flags = SA_SIGINFO;
    sigemptyset(&sa.sa_mask);
    NEED(sigaction(sig, &sa, NULL) == 0);
}

static void reset(long n) { got_sig = got_code = addr_is_rip = addr_zero = hits = 0; skip = n; }

static void do_ud2(void) { __asm__ volatile("ud2"); }                                     /* 0f 0b */
static void do_div0(void) { __asm__ volatile("xor %%edx,%%edx; mov $1,%%eax; xor %%ecx,%%ecx; .byte 0xf7,0xf1" ::: "eax", "ecx", "edx"); } /* div ecx */
static void do_gp(void) { __asm__ volatile("movabs $0x8000000000000000,%%rax; .byte 0x48,0x8b,0x00" ::: "rax", "memory"); } /* mov (%rax),%rax */

static int child_status(void (*body)(void))
{
    pid_t c = fork();
    NEED(c >= 0);
    if (c == 0) {
        body();
        _exit(0);
    }
    int ws;
    NEED(waitpid(c, &ws, 0) == c);
    return WIFSIGNALED(ws) ? WTERMSIG(ws) : 100 + WEXITSTATUS(ws);
}

static void blocked_fpe(void)
{
    set(SIGFPE, on_fault);
    sigset_t s;
    sigemptyset(&s);
    sigaddset(&s, SIGFPE);
    sigprocmask(SIG_BLOCK, &s, NULL);
    skip = 2;
    do_div0();
}

static void ignored_ill(void)
{
    signal(SIGILL, SIG_IGN);
    do_ud2();
}

static void on_segv_faults_again(int sig, siginfo_t *si, void *uc)
{
    (void)sig; (void)si; (void)uc;
    *(volatile char *)0 = 1;   /* SIGSEGV ist im Handler blockiert: Linux beendet mit SIGSEGV */
}

static void fault_in_handler(void)
{
    set(SIGSEGV, on_segv_faults_again);
    *(volatile char *)8 = 1;
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    ps = sysconf(_SC_PAGESIZE);

    set(SIGILL, on_fault);
    reset(2);
    do_ud2();
    SHOWI("ud2: signo", got_sig);
    SHOWI("  si_code (ILL_ILLOPN=2)", got_code);
    SHOW("  si_addr = RIP", addr_is_rip);

    set(SIGFPE, on_fault);
    reset(2);
    do_div0();
    SHOWI("div 0: signo", got_sig);
    SHOWI("  si_code (FPE_INTDIV=1)", got_code);
    SHOW("  si_addr = RIP", addr_is_rip);

    set(SIGSEGV, on_fault);
    reset(3);
    do_gp();
    SHOWI("nichtkanonisch (#GP): signo", got_sig);
    SHOWI("  si_code (SI_KERNEL=128)", got_code);
    SHOW("  si_addr 0", addr_zero);

    /* Schreibschutz, im Handler aufgehoben: der Befehl läuft noch einmal und schreibt. */
    ro_page = mmap(NULL, ps, PROT_READ, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    NEED(ro_page != MAP_FAILED);
    reset(0);
    ro_page[5] = 42;
    SHOWI("mprotect im Handler: Treffer", hits);
    SHOWI("  si_code (SEGV_ACCERR=2)", got_code);
    SHOWI("  geschrieben", ro_page[5]);
    ro_page = NULL;

    /* Mehrfach hintereinander: Maske nach rt_sigreturn wieder frei. */
    reset(2);
    for (int i = 0; i < 100; i++) do_ud2();
    SHOWI("100 x ud2: Treffer", hits);

    SHOWI("Kind: SIGFPE blockiert -> Signal", child_status(blocked_fpe));
    SHOWI("Kind: SIGILL ignoriert -> Signal", child_status(ignored_ill));
    SHOWI("Kind: Fehler im SIGSEGV-Handler -> Signal", child_status(fault_in_handler));

    /* V21-P3 (3): großer geteilter anonymer Bereich ohne Reservierung, nur wenige Seiten benutzt, über fork geteilt. */
    size_t big = (size_t)1 << 30;
    volatile char *g = mmap(NULL, big, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    SHOW("1 GiB MAP_SHARED|MAP_NORESERVE", g != MAP_FAILED);
    if (g != MAP_FAILED) {
        g[0] = 1;
        pid_t c = fork();
        NEED(c >= 0);
        if (c == 0) {
            g[big - 1] = 7;
            g[0] = 2;
            _exit(0);
        }
        int ws;
        NEED(waitpid(c, &ws, 0) == c);
        SHOW("  Kind schreibt, Eltern sehen es", g[0] == 2 && g[big - 1] == 7);
        NEED(munmap((void *)g, big) == 0);
    }
    printf("fertig\n");
    return 0;
}
