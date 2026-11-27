/* V45-P3 (Ladybird, Review P3 2): rt_sigreturn nach einem Ausnahme-Rahmen, in dem der Handler nur EIN Register passend zum
 * sysretq-Rückweg setzt (RCX = RIP oder R11 = RFLAGS), beide passend, oder TF bzw. RF in die Flags schreibt. Der Rückweg darf sysretq
 * nur nehmen, wenn RCX = RIP und R11 = RFLAGS und weder TF noch RF gesetzt sind (Linux do_syscall_64); sonst gingen RCX oder R11 verloren,
 * kehrte TF zu früh ein (sysretq fängt vor dem ersten Befehl, iretq danach) oder fiele RF weg. Linux übernimmt TF und RF aus dem Rahmen
 * (FIX_EFLAGS): Mit TF fängt der Befehl nach der Rückkehr ein SIGTRAP (TRAP_TRACE) genau einen Befehl weiter. Ausgabe gleich Linux 6.12
 * (expected/v45). */
#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ucontext.h>
#include <unistd.h>

#define SHOW(name, cond) printf("%s=%d\n", (name), (cond) ? 1 : 0)
#define SHOWI(name, val) printf("%s=%lld\n", (name), (long long)(val))
#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)

#define RCX_IN 0x1111222233334444UL
#define R11_IN 0x5555666677778888UL
#define RDX_IN 0x0123456789abcdefUL
#define FLAG_TF 0x100UL
#define FLAG_RF 0x10000UL

enum { NUR_RCX = 1, NUR_R11, BEIDE, TF, TF_BEIDE, RF, RF_BEIDE };

static volatile long mode, hits, traps, trap_code;
static volatile uint64_t rec_rip, rec_rcx, rec_r11, trap_rip, trap_addr;

static void on_ill(int sig, siginfo_t *si, void *ucv)
{
    ucontext_t *uc = ucv;
    greg_t *g = uc->uc_mcontext.gregs;
    (void)sig; (void)si;
    hits++;
    g[REG_RIP] += 2;   /* ud2 überspringen */
    if (mode == TF || mode == TF_BEIDE) {
        g[REG_EFL] |= FLAG_TF;
    }
    if (mode == RF || mode == RF_BEIDE) {
        g[REG_EFL] |= FLAG_RF;
    }
    if (mode == NUR_RCX || mode == BEIDE || mode == TF_BEIDE || mode == RF_BEIDE) {
        g[REG_RCX] = g[REG_RIP];
    }
    if (mode == NUR_R11 || mode == BEIDE || mode == TF_BEIDE || mode == RF_BEIDE) {
        g[REG_R11] = g[REG_EFL];
    }
    rec_rip = (uint64_t)g[REG_RIP];
    rec_rcx = (uint64_t)g[REG_RCX];
    rec_r11 = (uint64_t)g[REG_R11];
}

static void on_trap(int sig, siginfo_t *si, void *ucv)
{
    ucontext_t *uc = ucv;
    (void)sig;
    traps++;
    trap_code = si->si_code;
    trap_addr = (uint64_t)si->si_addr;
    trap_rip = (uint64_t)uc->uc_mcontext.gregs[REG_RIP];
    uc->uc_mcontext.gregs[REG_EFL] &= ~(greg_t)FLAG_TF;   /* nur ein Schritt */
}

static void set(int sig, void (*fn)(int, siginfo_t *, void *))
{
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = fn;
    sa.sa_flags = SA_SIGINFO;
    sigemptyset(&sa.sa_mask);
    NEED(sigaction(sig, &sa, NULL) == 0);
}

/* ud2 mit gesetzten RCX, R11, RDX; danach (erster Befehl: setc, 3 Byte) die Werte, CF und die Adresse hinter ud2. */
static void ud2_regs(uint64_t *rcx, uint64_t *r11, uint64_t *rdx, uint64_t *cf, uint64_t *after)
{
    uint64_t c, r, d, f, a;
    __asm__ volatile(
        "movabs %[ci], %%rcx\n\t"
        "movabs %[ri], %%r11\n\t"
        "movabs %[di], %%rdx\n\t"
        "clc\n\t"
        "ud2\n\t"
        "1:\n\t"
        "setc %%al\n\t"
        "movzbl %%al, %%eax\n\t"
        "mov %%rcx, %[c]\n\t"
        "mov %%r11, %[r]\n\t"
        "mov %%rdx, %[d]\n\t"
        "mov %%rax, %[f]\n\t"
        "lea 1b(%%rip), %%rax\n\t"
        "mov %%rax, %[a]\n\t"
        : [c] "=m"(c), [r] "=m"(r), [d] "=m"(d), [f] "=m"(f), [a] "=m"(a)
        : [ci] "i"(RCX_IN), [ri] "i"(R11_IN), [di] "i"(RDX_IN)
        : "rax", "rcx", "rdx", "r11", "cc", "memory");
    *rcx = c; *r11 = r; *rdx = d; *cf = f; *after = a;
}

static void run(long m, const char *name)
{
    uint64_t c, r, d, f, a;
    mode = m; hits = 0; traps = 0; trap_code = 0; trap_rip = 0; trap_addr = 0;
    ud2_regs(&c, &r, &d, &f, &a);
    printf("%s: Treffer=%ld\n", name, (long)hits);
    SHOW("  rip hinter ud2", rec_rip == a);
    SHOW("  rcx wie im Rahmen", c == rec_rcx);
    SHOW("  rcx = rip", c == a);
    SHOW("  rcx unveraendert", c == RCX_IN);
    SHOW("  r11 wie im Rahmen", r == rec_r11);
    SHOW("  r11 unveraendert", r == R11_IN);
    SHOW("  rdx erhalten", d == RDX_IN);
    SHOWI("  cf", f);
    SHOWI("  SIGTRAP", traps);
    if (traps) {
        SHOWI("    si_code", trap_code);
        SHOWI("    rip - Ziel", (long long)(trap_rip - a));
        SHOWI("    si_addr - Ziel", (long long)(trap_addr - a));
    }
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    set(SIGILL, on_ill);
    set(SIGTRAP, on_trap);

    run(NUR_RCX, "nur rcx = rip");
    run(NUR_R11, "nur r11 = rflags");
    run(BEIDE, "rcx = rip und r11 = rflags");
    run(TF, "TF im Rahmen");
    run(TF_BEIDE, "TF, rcx = rip, r11 = rflags");
    run(RF, "RF im Rahmen");
    run(RF_BEIDE, "RF, rcx = rip, r11 = rflags");

    int same = 0;
    for (int i = 0; i < 50; i++) {
        uint64_t c, r, d, f, a;
        mode = TF_BEIDE; traps = 0; trap_rip = 0;
        ud2_regs(&c, &r, &d, &f, &a);
        same += traps == 1 && trap_rip - a == 3 && c == a && d == RDX_IN;
    }
    SHOWI("50 x TF, rcx = rip, r11 = rflags: ein Schritt", same);
    printf("fertig\n");
    return 0;
}
