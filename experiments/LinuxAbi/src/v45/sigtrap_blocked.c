/* V45-P3 (Ladybird, Review P3 3): Einzelschritt (TF) bei blockiertem, ignoriertem oder nicht behandeltem SIGTRAP. Linux stellt das
 * SIGTRAP eines #DB mit force_sig_fault zu: Ist es blockiert oder ignoriert, setzt es SIG_DFL (und hebt die Blockade auf), der Prozess
 * endet mit SIGTRAP; der Befehl wird nie endlos wiederholt. TF kommt einmal über popf, einmal über den Rahmen eines SIGILL-Handlers
 * (rt_sigreturn, uc_sigmask blockiert SIGTRAP). Jeder Fall läuft in einem Kind; der Elternprozess wartet höchstens 10 s und zeigt nur
 * den Status. Ausgabe gleich Linux 6.12 (expected/v45). */
#define _GNU_SOURCE
#include <errno.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/resource.h>
#include <sys/wait.h>
#include <time.h>
#include <ucontext.h>
#include <unistd.h>

#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)
#define FLAG_TF 0x100UL

enum { BLOCKIERT = 1, IGNORIERT, STANDARD, RAHMEN_BLOCKIERT, BEHANDELT };

static volatile long traps;

static void on_trap(int sig, siginfo_t *si, void *ucv)
{
    ucontext_t *uc = ucv;
    (void)sig; (void)si;
    traps++;
    uc->uc_mcontext.gregs[REG_EFL] &= ~(greg_t)FLAG_TF;
}

static void on_ill(int sig, siginfo_t *si, void *ucv)
{
    ucontext_t *uc = ucv;
    (void)sig; (void)si;
    uc->uc_mcontext.gregs[REG_RIP] += 2;   /* ud2 überspringen */
    uc->uc_mcontext.gregs[REG_EFL] |= FLAG_TF;
    sigaddset(&uc->uc_sigmask, SIGTRAP);   /* nach rt_sigreturn blockiert */
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

static void tf_popf(void)
{
    __asm__ volatile(
        "pushfq\n\t"
        "orq $0x100, (%%rsp)\n\t"
        "popfq\n\t"
        "nop\n\t"
        "nop\n\t"
        "pushfq\n\t"
        "andq $~0x100, (%%rsp)\n\t"
        "popfq\n\t"
        ::: "cc", "memory");
}

static void child(long m)
{
    struct rlimit no = { 0, 0 };
    setrlimit(RLIMIT_CORE, &no);
    if (m == BLOCKIERT || m == RAHMEN_BLOCKIERT || m == BEHANDELT) {
        set(SIGTRAP, on_trap);
    }
    if (m == IGNORIERT) {
        signal(SIGTRAP, SIG_IGN);
    }
    if (m == BLOCKIERT) {
        sigset_t s;
        sigemptyset(&s);
        sigaddset(&s, SIGTRAP);
        sigprocmask(SIG_BLOCK, &s, NULL);
    }
    if (m == RAHMEN_BLOCKIERT) {
        set(SIGILL, on_ill);
        __asm__ volatile("ud2\n\tnop\n\tnop\n\t" ::: "memory");
    } else {
        tf_popf();
    }
    _exit(10 + (int)traps);
}

static void run(long m, const char *name)
{
    pid_t pid = fork();
    NEED(pid >= 0);
    if (pid == 0) {
        child(m);
    }
    int st = 0;
    pid_t got = 0;
    for (int i = 0; i < 1000 && (got = waitpid(pid, &st, WNOHANG)) == 0; i++) {
        struct timespec ts = { 0, 10 * 1000 * 1000 };
        nanosleep(&ts, NULL);
    }
    if (got == 0) {
        kill(pid, SIGKILL);
        waitpid(pid, &st, 0);
        printf("%s: haengt (nach 10 s getoetet)\n", name);
        return;
    }
    NEED(got == pid);
    if (WIFSIGNALED(st)) {
        printf("%s: signal %d\n", name, WTERMSIG(st));
    } else {
        printf("%s: exit %d\n", name, WEXITSTATUS(st));
    }
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    run(BEHANDELT, "Handler, frei (exit 11 = ein SIGTRAP)");
    run(BLOCKIERT, "Handler, blockiert (sigprocmask)");
    run(IGNORIERT, "SIG_IGN");
    run(STANDARD, "SIG_DFL");
    run(RAHMEN_BLOCKIERT, "TF und Blockade ueber rt_sigreturn");
    run(BEHANDELT, "Handler, frei, nochmal");
    printf("fertig\n");
    return 0;
}
