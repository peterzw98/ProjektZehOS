/* V45-P3 (Ladybird, Review P3 1): Einzelschritt (TF über popf, Handler lässt TF stehen) über syscall: getpid, eine unbekannte Nummer
 * (-ENOSYS) und rt_sigprocmask. Gezeigt wird die Folge der SIGTRAP-Adressen als Marke in der Strecke, si_code, das Ergebnis
 * des Aufrufs und RCX/R11 danach (RCX = Adresse hinter syscall, R11 = RFLAGS beim Aufruf, also mit TF). Linux nimmt bei TF den
 * Rückweg über iret (do_syscall_64): Die Falle kommt erst nach dem ersten Befehl hinter syscall (Marke hinter_syscall fehlt), sonst
 * ist jeder Befehl genau einmal gemeldet. Ausgabe gleich Linux 6.12 (expected/v45). */
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

/* Marken an jeder Befehlsgrenze der Strecke, im asm gesetzt. Die Ausgabe nennt die Marke statt eines Byte-Abstands, damit sie
 * nicht von der Kodierung abhängt, die der Compiler für die Speicheroperanden wählt (Review P3). */
#define NMARK 7
static const char *const mark_name[NMARK] = { "nop", "syscall", "hinter_syscall", "mov_rcx", "mov_r11", "nop2", "ende" };
static volatile uint64_t mark[NMARK];
static volatile int64_t step[MAXSTEP];
static volatile uint64_t code[MAXSTEP];
static volatile int nstep;

/* Index der Marke zu rip, sonst -1; im Handler, daher nur eine Schleife ohne Aufrufe. */
static int64_t mark_of(uint64_t rip)
{
    for (int i = 0; i < NMARK; i++) {
        if (mark[i] == rip) {
            return i;
        }
    }
    return -1;
}

static void on_trap(int sig, siginfo_t *si, void *ucv)
{
    ucontext_t *uc = ucv;
    uint64_t rip = (uint64_t)uc->uc_mcontext.gregs[REG_RIP];
    (void)sig;
    if (nstep < MAXSTEP) {
        step[nstep] = mark_of(rip);
        code[nstep] = (uint64_t)si->si_code;
    }
    nstep++;
    if (rip == mark[NMARK - 1] || nstep >= MAXSTEP) {
        uc->uc_mcontext.gregs[REG_EFL] &= ~(greg_t)FLAG_TF;
    }
}

static uint64_t sig_of(void)
{
    uint64_t h = (uint64_t)nstep;
    for (int i = 0; i < nstep && i < MAXSTEP; i++) {
        h = h * 1000003u + (uint64_t)(step[i] + 1) * 31u + code[i];
    }
    return h;
}

/* Gibt die Schritte aus (name != NULL) und liefert eine Prüfsumme der Folge. */
static uint64_t run(long nr, long a1, long a2, long a3, const char *name)
{
    uint64_t ret, rcx, r11;
    register long r10 __asm__("r10") = 8;
    nstep = 0;
    /* pushfq schreibt unter RSP: Die Funktion darf dort Variablen in der roten Zone halten (128 Byte, x86-64-ABI). Deshalb
     * RSP vorher um 128 senken und erst nach popfq zurücksetzen; dazwischen keine Speicheroperanden (sie können
     * RSP-relativ sein). Das lea nach popfq ist der erste Einzelschritt, die Falle danach liegt auf Marke "nop". */
    __asm__ volatile(
        "lea 10f(%%rip), %%rax\n\t"
        "mov %%rax, mark+0(%%rip)\n\t"
        "lea 11f(%%rip), %%rax\n\t"
        "mov %%rax, mark+8(%%rip)\n\t"
        "lea 12f(%%rip), %%rax\n\t"
        "mov %%rax, mark+16(%%rip)\n\t"
        "lea 13f(%%rip), %%rax\n\t"
        "mov %%rax, mark+24(%%rip)\n\t"
        "lea 14f(%%rip), %%rax\n\t"
        "mov %%rax, mark+32(%%rip)\n\t"
        "lea 15f(%%rip), %%rax\n\t"
        "mov %%rax, mark+40(%%rip)\n\t"
        "lea 16f(%%rip), %%rax\n\t"
        "mov %%rax, mark+48(%%rip)\n\t"
        "mov %[nr], %%rax\n\t"
        "lea -128(%%rsp), %%rsp\n\t"
        "pushfq\n\t"
        "orq $0x100, (%%rsp)\n\t"
        "popfq\n\t"
        "lea 128(%%rsp), %%rsp\n\t"
        "10:\n\t"
        "nop\n\t"
        "11:\n\t"
        "syscall\n\t"
        "12:\n\t"
        "mov %%rax, %[ret]\n\t"
        "13:\n\t"
        "mov %%rcx, %[rcx]\n\t"
        "14:\n\t"
        "mov %%r11, %[r11]\n\t"
        "15:\n\t"
        "nop\n\t"
        "16:\n\t"
        "nop\n\t"
        : [ret] "=m"(ret), [rcx] "=m"(rcx), [r11] "=m"(r11)
        : [nr] "m"(nr), "D"(a1), "S"(a2), "d"(a3), "r"(r10)
        : "rax", "rcx", "r11", "cc", "memory");
    if (name == NULL) {
        return sig_of() + (rcx == mark[2]) + ((r11 & FLAG_TF) != 0) * 2 + ((long)ret == 0 ? 0 : 4);
    }
    printf("%s: Schritte=%d\n", name, nstep);
    for (int i = 0; i < nstep && i < MAXSTEP; i++) {
        printf("  bei %s si_code=%llu\n", step[i] >= 0 ? mark_name[step[i]] : "?", (unsigned long long)code[i]);
    }
    printf("  ergebnis=%s\n", (long)ret == -ENOSYS ? "-ENOSYS" : (long)ret < 0 ? "fehler" : "ok");
    printf("  rcx = hinter syscall=%d\n", rcx == mark[2]);
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
