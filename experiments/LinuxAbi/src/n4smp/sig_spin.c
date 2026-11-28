/* N4 IRQ-Signal (V18-Grenze): Erreicht ein Signal mit Handler einen Thread, der im Ring 3 rechnet und keinen Systemaufruf macht?
 * Bis N4 stellte ZehOS solche Signale nur bei der Rueckkehr aus einem Aufruf oder einer Ausnahme zu; der Rechner sah sie nie.
 * Seit dem Cosmos-Patch user-return-work laeuft die Zustellung auch auf dem Rueckweg eines Interrupts (die Bruecke stoesst die CPU
 * des Threads an). Jeder Fall laeuft in einem Kind mit eigenem Wecker: ein Haenger erscheint als "signal 14" des Falls.
 * Der Rechner haelt waehrenddessen Werte in GPRs und XMM-Registern (asm-Bindung, keine Speicherkopie) und prueft sie nach dem
 * Ende der Schleife; der Handler rechnet selbst mit SSE und x87, damit ein verlorener oder vertauschter Zustand auffaellt.
 * Faelle: tgkill an den Rechner aus einem zweiten Thread (einmal und zwanzigmal nacheinander), kill an den Prozess, waehrend der
 * Hauptthread das Signal blockiert, und kill aus dem Elternprozess an ein Kind, dessen einziger Thread rechnet. Keine Zeiten in der
 * Ausgabe. */
#define _GNU_SOURCE
#include "../s3/util.h"
#include <pthread.h>
#include <signal.h>
#include <stdatomic.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <time.h>

struct shared {
    atomic_int spinning;
};
static struct shared *sh;

static atomic_int handled;
static atomic_int spinner_tid;
static volatile double handler_sink;

static void on_signal(int sig)
{
    /* Der Handler benutzt FPU und SSE selbst: der Rahmen muss den Zustand des Rechners sichern und herstellen. */
    double a = (double)sig;
    long double b = 3.0L;
    for (int i = 0; i < 8; i++) {
        a = a * 1.25 + 0.5;
        b = b * 1.5L;
    }
    handler_sink = a + (double)b;
    atomic_fetch_add(&handled, 1);
}

/* Rechnet ohne Aufruf, bis want Signale behandelt sind. Liefert 0, wenn alle gebundenen Register danach stimmen. */
static int spin_until(int want)
{
    atomic_store(&spinner_tid, (int)syscall(SYS_gettid));
    atomic_fetch_add(&sh->spinning, 1);
    unsigned long r1 = 0x1111111111111111UL, r2 = 0x2222222222222222UL, r3 = 0x3333333333333333UL;
    double x1 = 1.5, x2 = -2.25, x3 = 1e10;
    unsigned long x = 1;
    while (atomic_load_explicit(&handled, memory_order_relaxed) < want) {
        /* Nur Register: der Thread verlaesst den Ring 3 nur ueber einen Interrupt. Die Bindungen halten r1-r3 und x1-x3 in GPRs und
         * XMM-Registern ueber die ganze Schleife, also auch ueber den Handler. */
        x = x * 6364136223846793005UL + 1442695040888963407UL;
        __asm__ volatile("" : "+r"(x), "+r"(r1), "+r"(r2), "+r"(r3), "+x"(x1), "+x"(x2), "+x"(x3));
    }
    int bad = 0;
    bad |= r1 != 0x1111111111111111UL ? 1 : 0;
    bad |= r2 != 0x2222222222222222UL ? 2 : 0;
    bad |= r3 != 0x3333333333333333UL ? 4 : 0;
    bad |= x1 != 1.5 ? 8 : 0;
    bad |= x2 != -2.25 ? 16 : 0;
    bad |= x3 != 1e10 ? 32 : 0;
    return bad;
}

static void pause_ms(long ms)
{
    struct timespec ts = { ms / 1000, (ms % 1000) * 1000000L };
    nanosleep(&ts, NULL);
}

static void wait_spinning(int n)
{
    while (atomic_load(&sh->spinning) < n) pause_ms(1);
    pause_ms(50); /* der Rechner ist jetzt sicher in der Schleife */
}

struct job {
    int want;
    int result;
};

static void *spinner(void *arg)
{
    struct job *j = arg;
    j->result = spin_until(j->want);
    return NULL;
}

/* Kind: ein Rechenthread, der Hauptthread schickt count Signale (mode 1: tgkill, mode 2: kill an den Prozess, selbst blockiert). */
static void child_threads(int mode, int count)
{
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_signal;
    CNEED(sigaction(SIGUSR1, &sa, NULL) == 0);
    if (mode == 2) {
        sigset_t set;
        sigemptyset(&set);
        sigaddset(&set, SIGUSR1);
        CNEED(pthread_sigmask(SIG_BLOCK, &set, NULL) == 0); /* der Rechner erbt die Maske, deshalb erst nach pthread_create */
    }
    struct job j = { count, -1 };
    pthread_t t;
    if (mode == 2) {
        sigset_t none;
        sigemptyset(&none);
        sigset_t old;
        CNEED(pthread_sigmask(SIG_SETMASK, &none, &old) == 0);
        CNEED(pthread_create(&t, NULL, spinner, &j) == 0);
        CNEED(pthread_sigmask(SIG_SETMASK, &old, NULL) == 0);
    } else {
        CNEED(pthread_create(&t, NULL, spinner, &j) == 0);
    }
    wait_spinning(1);
    for (int i = 0; i < count; i++) {
        int before = atomic_load(&handled);
        if (mode == 1) {
            CNEED(syscall(SYS_tgkill, getpid(), atomic_load(&spinner_tid), SIGUSR1) == 0);
        } else {
            CNEED(kill(getpid(), SIGUSR1) == 0);
        }
        /* Erst das naechste Signal, wenn dieses behandelt ist: sonst fielen zwei anhaengige zu einem zusammen. */
        while (atomic_load(&handled) == before) pause_ms(1);
    }
    CNEED(pthread_join(t, NULL) == 0);
    _exit(j.result == 0 && atomic_load(&handled) == count ? 0 : 10 + j.result);
}

/* Kind mit einem einzigen Thread, der rechnet; der Elternprozess schickt SIGUSR1. */
static void child_single(void)
{
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = on_signal;
    CNEED(sigaction(SIGUSR1, &sa, NULL) == 0);
    int bad = spin_until(1);
    _exit(bad == 0 ? 0 : 10 + bad);
}

static void run_case(const char *name, int mode, int count)
{
    atomic_store(&sh->spinning, 0);
    pid_t pid = fork();
    NEED(pid >= 0);
    if (pid == 0) {
        alarm(10); /* eigener Wecker des Kindes: ein Haenger erscheint als "signal 14" dieses Falls */
        if (mode == 0) child_single();
        child_threads(mode, count);
    }
    if (mode == 0) {
        wait_spinning(1);
        NEED(kill(pid, SIGUSR1) == 0);
    }
    int st = 0;
    NEED(waitpid(pid, &st, 0) == pid);
    if (WIFSIGNALED(st)) {
        printf("%s: signal %d\n", name, WTERMSIG(st));
    } else {
        printf("%s: exit %d\n", name, WIFEXITED(st) ? WEXITSTATUS(st) : -1);
    }
}

int main(void)
{
    INIT();
    alarm(60); /* vier Faelle mit je 10 s Wecker */
    sh = mmap(NULL, sizeof *sh, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    NEED(sh != MAP_FAILED);
    run_case("tgkill spinner SIGUSR1 handler", 1, 1);
    run_case("tgkill spinner SIGUSR1 handler x20", 1, 20);
    run_case("kill process, main blocks, spinner takes it", 2, 1);
    run_case("parent kill, single spinner with handler", 0, 0);
    printf("done\n");
    return 0;
}
