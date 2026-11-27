/* V45 (Ladybird): rt_sigreturn nach einem Ausnahme-Rahmen stellt jedes Register her, auch RCX und R11. Der Handler kehrt
 * zurück (kein siglongjmp); die Ausnahme traf mitten im Code, wo RCX und R11 lebendige Werte tragen. Fälle: #UD mit
 * Überspringen, Seitenfehler mit mprotect und Wiederholung, Handler ändert RCX, R11 und CF im ucontext. Gegenprobe: ein
 * gewöhnlicher syscall gibt RCX = Rücksprungadresse und R11 = RFLAGS zurück (wie Linux). Ausgabe gleich Linux 6.12 (expected/v45). */
#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <ucontext.h>
#include <unistd.h>

#define SHOW(name, cond) printf("%s=%d\n", (name), (cond) ? 1 : 0)
#define SHOWI(name, val) printf("%s=%lld\n", (name), (long long)(val))
#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)

#define RCX_IN 0x1111222233334444UL
#define R11_IN 0x5555666677778888UL
#define RDX_IN 0x0123456789abcdefUL
#define RCX_SET 0x0badc0de0badc0deUL
#define R11_SET 0x7e57ab1e7e57ab1eUL

static volatile long skip, hits, change;
static char *volatile ro_page;
static long ps;

static void on_fault(int sig, siginfo_t *si, void *ucv)
{
    ucontext_t *uc = ucv;
    (void)sig;
    hits++;
    if (change) {
        uc->uc_mcontext.gregs[REG_RCX] = (greg_t)RCX_SET;
        uc->uc_mcontext.gregs[REG_R11] = (greg_t)R11_SET;
        uc->uc_mcontext.gregs[REG_EFL] |= 1;   /* CF */
    }
    if (ro_page && (char *)si->si_addr >= ro_page && (char *)si->si_addr < ro_page + ps) {
        mprotect(ro_page, ps, PROT_READ | PROT_WRITE);   /* der Befehl läuft danach noch einmal */
        return;
    }
    uc->uc_mcontext.gregs[REG_RIP] += skip;
}

static void set(int sig)
{
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = on_fault;
    sa.sa_flags = SA_SIGINFO;
    sigemptyset(&sa.sa_mask);
    NEED(sigaction(sig, &sa, NULL) == 0);
}

/* ud2 mit gesetzten RCX, R11, RDX; danach die Werte und CF (clc vorher). */
static void ud2_regs(uint64_t *rcx, uint64_t *r11, uint64_t *rdx, uint64_t *cf)
{
    uint64_t c, r, d, f;
    __asm__ volatile(
        "movabs %[ci], %%rcx\n\t"
        "movabs %[ri], %%r11\n\t"
        "movabs %[di], %%rdx\n\t"
        "clc\n\t"
        "ud2\n\t"
        "setc %%al\n\t"
        "movzbl %%al, %%eax\n\t"
        "mov %%rcx, %[c]\n\t"
        "mov %%r11, %[r]\n\t"
        "mov %%rdx, %[d]\n\t"
        "mov %%rax, %[f]\n\t"
        : [c] "=m"(c), [r] "=m"(r), [d] "=m"(d), [f] "=m"(f)
        : [ci] "i"(RCX_IN), [ri] "i"(R11_IN), [di] "i"(RDX_IN)
        : "rax", "rcx", "rdx", "r11", "cc", "memory");
    *rcx = c; *r11 = r; *rdx = d; *cf = f;
}

/* Schreiben auf eine schreibgeschützte Seite mit gesetzten RCX und R11; der Handler hebt den Schutz auf. */
static void write_regs(char *p, uint64_t *rcx, uint64_t *r11)
{
    uint64_t c, r;
    __asm__ volatile(
        "movabs %[ci], %%rcx\n\t"
        "movabs %[ri], %%r11\n\t"
        "movb $42, (%[p])\n\t"
        "mov %%rcx, %[c]\n\t"
        "mov %%r11, %[r]\n\t"
        : [c] "=m"(c), [r] "=m"(r)
        : [ci] "i"(RCX_IN), [ri] "i"(R11_IN), [p] "r"(p)
        : "rcx", "r11", "cc", "memory");
    *rcx = c; *r11 = r;
}

/* Gegenprobe: syscall (getpid) setzt RCX auf die Adresse danach und R11 auf RFLAGS. */
static void plain_syscall(uint64_t *rcx, uint64_t *after, uint64_t *r11, uint64_t *flags)
{
    uint64_t c, a, r, f;
    __asm__ volatile(
        "mov %[nr], %%eax\n\t"
        "syscall\n\t"
        "1:\n\t"
        "mov %%rcx, %[c]\n\t"
        "mov %%r11, %[r]\n\t"
        "lea 1b(%%rip), %%rax\n\t"
        "mov %%rax, %[a]\n\t"
        "pushfq\n\t"
        "pop %%rax\n\t"
        "mov %%rax, %[f]\n\t"
        : [c] "=m"(c), [a] "=m"(a), [r] "=m"(r), [f] "=m"(f)
        : [nr] "i"(SYS_getpid)
        : "rax", "rcx", "r11", "cc", "memory");
    *rcx = c; *after = a; *r11 = r; *flags = f;
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    ps = sysconf(_SC_PAGESIZE);
    uint64_t c, r, d, f, a, fl;

    set(SIGILL);
    hits = 0; skip = 2; change = 0;
    ud2_regs(&c, &r, &d, &f);
    SHOWI("ud2: Treffer", hits);
    SHOW("  rcx erhalten", c == RCX_IN);
    SHOW("  r11 erhalten", r == R11_IN);
    SHOW("  rdx erhalten", d == RDX_IN);
    SHOWI("  cf", f);

    hits = 0; change = 1;
    ud2_regs(&c, &r, &d, &f);
    SHOWI("ud2, Handler setzt rcx/r11/cf: Treffer", hits);
    SHOW("  rcx aus ucontext", c == RCX_SET);
    SHOW("  r11 aus ucontext", r == R11_SET);
    SHOW("  rdx erhalten", d == RDX_IN);
    SHOWI("  cf", f);
    change = 0;

    int same = 0;
    for (int i = 0; i < 100; i++) {
        ud2_regs(&c, &r, &d, &f);
        same += c == RCX_IN && r == R11_IN && d == RDX_IN && f == 0;
    }
    SHOWI("100 x ud2: rcx/r11 erhalten", same);

    set(SIGSEGV);
    ro_page = mmap(NULL, ps, PROT_READ, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    NEED(ro_page != MAP_FAILED);
    hits = 0;
    write_regs(ro_page, &c, &r);
    SHOWI("SIGSEGV, mprotect im Handler: Treffer", hits);
    SHOWI("  geschrieben", ro_page[0]);
    SHOW("  rcx erhalten", c == RCX_IN);
    SHOW("  r11 erhalten", r == R11_IN);
    ro_page = NULL;

    plain_syscall(&c, &a, &r, &fl);
    SHOW("syscall: rcx = Adresse danach", c == a);
    SHOW("  r11 = rflags (ohne RF)", (r & ~0x10000UL) == (fl & ~0x10000UL));
    printf("fertig\n");
    return 0;
}
