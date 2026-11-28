/* N4-S3: Beenden fatale Signale einen Thread, der im Ring 3 rechnet und keinen Systemaufruf macht? ZehOS stellt Signale mit
 * Handler nur bei der Rueckkehr aus einem Aufruf oder einer Ausnahme zu; SIGKILL und Signale mit Standardaktion "beenden"
 * muessen den Prozess trotzdem beenden, auch wenn ein Geschwister auf einer anderen CPU rechnet (zehos.linuxsmp=1).
 * Jeder Fall laeuft in einem Kind; das Kind startet Rechenthreads (reine Arithmetik, kein Aufruf), wartet, bis sie rechnen,
 * und dann sendet entweder das Kind selbst (kill, tgkill, Absturz) oder der Elternprozess (kill) das Signal. Der Elternprozess
 * meldet den Status des Kindes; ein Haenger endet am Wecker des Kindes (signal 14). Endet das Kind auch daran nicht, beendet der
 * Elternprozess es an seinem eigenen Wecker mit SIGKILL, bevor er selbst endet: kein rechnender Waise (S3-Review P3). Zeiten stehen
 * nicht in der Ausgabe. */
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

/* Geteilt zwischen Eltern und Kind (MAP_SHARED), damit der Elternprozess weiss, wann die Rechenthreads laufen. */
struct shared {
    atomic_int spinning;
    atomic_int spinner_tid;
};
static struct shared *sh;
static volatile unsigned long sink;

static void *spin(void *arg)
{
    (void)arg;
    atomic_store(&sh->spinner_tid, (int)syscall(SYS_gettid));
    atomic_fetch_add(&sh->spinning, 1);
    unsigned long x = 1;
    for (;;) {
        /* Kein Systemaufruf, kein Speicherzugriff ausser dem Register: der Thread verlaesst den Ring 3 nur ueber einen Interrupt. */
        x = x * 6364136223846793005UL + 1442695040888963407UL;
        __asm__ volatile("" : "+r"(x));
    }
    sink = x;
    return NULL;
}

/* Kind des laufenden Falls; der Wecker des Elternprozesses beendet es vor dem eigenen Ende. */
static volatile pid_t current_child;

static void parent_alarm(int sig)
{
    (void)sig;
    if (current_child > 0) {
        kill(current_child, SIGKILL);
    }
    _exit(3);
}

static void pause_ms(long ms)
{
    struct timespec ts = { ms / 1000, (ms % 1000) * 1000000L };
    nanosleep(&ts, NULL);
}

static void wait_spinning(int n)
{
    while (atomic_load(&sh->spinning) < n) pause_ms(1);
    pause_ms(50); /* die Rechenthreads sind jetzt sicher in der Schleife */
}

/* Kind: startet n Rechenthreads und fuehrt dann die Aktion des Falls aus. */
static void child(int threads, int self_action, int sig)
{
    pthread_t t;
    for (int i = 0; i < threads; i++) CNEED(pthread_create(&t, NULL, spin, NULL) == 0);
    if (threads == 0) spin(NULL); /* Einzelthread: das Kind selbst rechnet, der Elternprozess sendet */
    wait_spinning(threads);
    switch (self_action) {
    case 1: kill(getpid(), sig); break;                                                       /* an den Prozess */
    case 2: syscall(SYS_tgkill, getpid(), atomic_load(&sh->spinner_tid), sig); break;          /* an den rechnenden Thread */
    case 3: *(volatile int *)0 = 1; break;                                                     /* Absturz neben dem Rechner */
    default: spin(NULL);                                                                       /* der Elternprozess sendet */
    }
    /* Kehrt der Aufruf zurueck, ohne dass der Prozess endet, rechnet dieser Thread weiter; der Wecker des Elternprozesses meldet das. */
    spin(NULL);
}

static void run_case(const char *name, int threads, int self_action, int sig)
{
    atomic_store(&sh->spinning, 0);
    atomic_store(&sh->spinner_tid, 0);
    pid_t pid = fork();
    NEED(pid >= 0);
    if (pid == 0) {
        signal(SIGALRM, SIG_DFL); /* der geerbte Handler des Elternprozesses gilt im Kind nicht */
        alarm(10); /* eigener Wecker: ein Haenger erscheint als "signal 14" dieses Falls */
        child(threads, self_action, sig);
        _exit(4);
    }
    current_child = pid;
    alarm(20); /* je Fall neu: laenger als der Wecker des Kindes */
    if (self_action == 0) {
        wait_spinning(threads == 0 ? 1 : threads);
        NEED(kill(pid, sig) == 0);
    }
    int st = 0;
    NEED(waitpid(pid, &st, 0) == pid);
    current_child = 0;
    if (WIFSIGNALED(st)) {
        printf("%s: signal %d\n", name, WTERMSIG(st));
    } else {
        printf("%s: exit %d\n", name, WIFEXITED(st) ? WEXITSTATUS(st) : -1);
    }
}

int main(void)
{
    INIT();
    NEED(signal(SIGALRM, parent_alarm) != SIG_ERR);
    sh = mmap(NULL, sizeof *sh, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
    NEED(sh != MAP_FAILED);
    run_case("self kill SIGKILL, 1 spinner", 1, 1, SIGKILL);
    run_case("self kill SIGTERM, 1 spinner", 1, 1, SIGTERM);
    run_case("self tgkill spinner SIGKILL", 1, 2, SIGKILL);
    run_case("self tgkill spinner SIGTERM", 1, 2, SIGTERM);
    run_case("self tgkill spinner SIGUSR1", 1, 2, SIGUSR1);
    run_case("fault beside 3 spinners", 3, 3, 0);
    run_case("parent kill SIGKILL, single spinner", 0, 0, SIGKILL);
    run_case("parent kill SIGTERM, single spinner", 0, 0, SIGTERM);
    run_case("parent kill SIGKILL, 3 spinners + main", 3, 0, SIGKILL);
    run_case("parent kill SIGINT, 3 spinners + main", 3, 0, SIGINT);
    printf("done\n");
    return 0;
}
