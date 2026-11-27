/* Review V18 P3: rt_sigreturn mit einem RSP, den der Handler im Signalrahmen (uc_mcontext) verbogen hat. Ohne KERNEL_RSP
 * nichtkanonisch (0x8000000000000000), mit KERNEL_RSP in der Kernelhälfte (0xffff800000001000). Linux lädt den Wert und der
 * erste Stapelzugriff nach dem Rücksprung endet mit SIGSEGV; ZehOS lehnt die Rückkehr in den Ring 3 schon in UserMode.Prepare ab
 * (RSP >= 0x0000800000000000) und beendet den Prozess ebenso mit Signal 11. Erwartet: beide Zeilen, dann "signal 11".
 * Ausgabe nur per write, damit nichts in einem Puffer hängen bleibt. */
#define _GNU_SOURCE
#include <signal.h>
#include <string.h>
#include <ucontext.h>
#include <unistd.h>

#ifdef KERNEL_RSP
#define BAD_RSP 0xffff800000001000UL
#else
#define BAD_RSP 0x8000000000000000UL
#endif

static void say(const char *s) { write(1, s, strlen(s)); }

static void on_usr1(int sig, siginfo_t *si, void *ctx)
{
    (void)sig;
    (void)si;
    ucontext_t *uc = ctx;
    uc->uc_mcontext.gregs[REG_RSP] = (greg_t)BAD_RSP;
    say("handler setzt rsp\n");
}

int main(void)
{
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = on_usr1;
    sa.sa_flags = SA_SIGINFO;
    sigaction(SIGUSR1, &sa, NULL);
    say("vor kill\n");
    kill(getpid(), SIGUSR1);
    say("nach rt_sigreturn: darf nicht kommen\n");
    return 0;
}
