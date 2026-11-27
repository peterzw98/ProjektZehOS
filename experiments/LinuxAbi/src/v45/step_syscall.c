/* V45-P3 (Ladybird, Review P3 1): Einzelschritt (TF über popf, Handler lässt TF stehen) über syscall: getpid, eine unbekannte Nummer
 * (-ENOSYS) und rt_sigprocmask. Gezeigt wird die Folge der SIGTRAP-Adressen relativ zum Anfang der Strecke, si_code, das Ergebnis
 * des Aufrufs und RCX/R11 danach (RCX = Adresse hinter syscall, R11 = RFLAGS beim Aufruf, also mit TF). Linux nimmt bei TF den
 * Rückweg über iret (do_syscall_64); der Schritt über syscall darf keinen Befehl auslassen und keinen doppelt melden. Ausgabe gleich
 * Linux 6.12 (expected/v45). */
#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ucontext.h>
#include <unistd.h>

#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)
#define FLAG_TF 0x100UL
#define MAXSTEP 32

static volatile uint64_t base, end;
static volatile uint64_t step[MAXSTEP], code[MAXSTEP];
static volatile int nstep;

static void on_trap(int sig, siginfo_t *si, void *ucv)
{
    ucontext_t *uc = ucv;
    uint64_t rip = (uint64_t)uc->uc_mcontext.gregs[REG_RIP];
    (void)sig;
    if (nstep < MAXSTEP) {
        step[nstep] = rip - base;
        code[nstep] = (uint64_t)si->si_code;
    }
    nstep++;
    if (rip == end || nstep >= MAXSTEP) {
        uc->uc_mcontext.gregs[REG_EFL] &= ~(greg_t)FLAG_TF;
    }
}

static uint64_t sig_of(void)
{
    uint64_t h = (uint64_t)nstep;
    for (int i = 0; i < nstep && i < MAXSTEP; i++) {
        h = h * 1000003u + step[i] * 31u + code[i];
    }
    return h;
}

/* Gibt die Schritte aus (name != NULL) und liefert eine Prüfsumme der Folge. */
static uint64_t run(long nr, long a1, long a2, long a3, const char *name)
{
    uint64_t b, e, ret, rcx, r11, after;
    register long r10 __asm__("r10") = 8;
    nstep = 0;
    __asm__ volatile(
        "lea 1f(%%rip), %%rax\n\t"
        "mov %%rax, %[b]\n\t"
        "lea 3f(%%rip), %%rax\n\t"
        "mov %%rax, %[e]\n\t"
        "lea 2f(%%rip), %%rax\n\t"
        "mov %%rax, %[af]\n\t"
        "mov %[b], %%rax\n\t"
        "mov %%rax, base(%%rip)\n\t"
        "mov %[e], %%rax\n\t"
        "mov %%rax, end(%%rip)\n\t"
        "mov %[nr], %%rax\n\t"
        "pushfq\n\t"
        "orq $0x100, (%%rsp)\n\t"
        "popfq\n\t"
        "1:\n\t"
        "nop\n\t"
        "syscall\n\t"
        "2:\n\t"
        "mov %%rax, %[ret]\n\t"
        "mov %%rcx, %[rcx]\n\t"
        "mov %%r11, %[r11]\n\t"
        "nop\n\t"
        "3:\n\t"
        "nop\n\t"
        : [b] "=&m"(b), [e] "=&m"(e), [af] "=&m"(after), [ret] "=m"(ret), [rcx] "=m"(rcx), [r11] "=m"(r11)
        : [nr] "m"(nr), "D"(a1), "S"(a2), "d"(a3), "r"(r10)
        : "rax", "rcx", "r11", "cc", "memory");
    (void)b; (void)e;
    if (name == NULL) {
        return sig_of() + (rcx == after) + ((r11 & FLAG_TF) != 0) * 2 + ((long)ret == 0 ? 0 : 4);
    }
    printf("%s: Schritte=%d\n", name, nstep);
    for (int i = 0; i < nstep && i < MAXSTEP; i++) {
        printf("  +%llu si_code=%llu\n", (unsigned long long)step[i], (unsigned long long)code[i]);
    }
    printf("  ergebnis=%s\n", (long)ret == -ENOSYS ? "-ENOSYS" : (long)ret < 0 ? "fehler" : "ok");
    printf("  rcx = hinter syscall=%d\n", rcx == after);
    printf("  r11 mit TF=%d\n", (r11 & FLAG_TF) != 0);
    return 0;
}

int main(void)
{
    struct sigaction sa;
    sigset_t none, old;
    setvbuf(stdout, NULL, _IONBF, 0);
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = on_trap;
    sa.sa_flags = SA_SIGINFO;
    sigemptyset(&sa.sa_mask);
    NEED(sigaction(SIGTRAP, &sa, NULL) == 0);
    sigemptyset(&none);

    run(39, 0, 0, 0, "getpid");
    run(4321, 0, 0, 0, "unbekannt");
    run(14, SIG_BLOCK, (long)&none, (long)&old, "rt_sigprocmask");
    /* gleiche Folge wie der erste getpid-Schritt, 50 Mal (Ergebnis getpid > 0, daher Bit 4 gesetzt) */
    uint64_t first = run(39, 0, 0, 0, NULL);
    int same = 0;
    for (int i = 0; i < 50; i++) {
        same += run(39, 0, 0, 0, NULL) == first;
    }
    printf("50 x getpid gleiche Folge=%d\n", same);
    printf("fertig\n");
    return 0;
}
