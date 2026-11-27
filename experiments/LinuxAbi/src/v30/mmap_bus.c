/* V30-P3: SIGBUS einer privaten Dateiabbildung. Eine Seite ganz hinter dem Dateiende liefert SIGBUS mit BUS_ADRERR und der
 * Fehleradresse, die Seite, in der die Datei endet, liest danach Nullen. Nach ftruncate kleiner gilt dasselbe fuer eine noch
 * nicht beruehrte Seite; waechst die Datei wieder, liest sie Nullen. Ohne Handler endet ein Kind mit Signal 7. Nicht geprueft wird
 * eine vor dem Kuerzen gelesene Seite (Linux nimmt sie aus der Abbildung, ZehOS behaelt sie: Grenze in LINUX_ABI.md).
 * Erwartung: Linux 6.12. */
#define _GNU_SOURCE
#include <fcntl.h>
#include <setjmp.h>
#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/wait.h>
#include <unistd.h>

#define PG 4096

static sigjmp_buf back;
static volatile int sig, code;
static volatile unsigned long addr;

static void on_bus(int s, siginfo_t *si, void *uc)
{
    (void)uc;
    sig = s;
    code = si->si_code;
    addr = (unsigned long)si->si_addr;
    siglongjmp(back, 1);
}

/* 1: Zugriff gelang (Byte in *v), 0: SIGBUS mit Adresse p. */
static int touch(volatile unsigned char *p, unsigned char *v)
{
    sig = code = 0;
    addr = 0;
    if (sigsetjmp(back, 1)) return 0;
    *v = *p;
    return 1;
}

int main(void)
{
    struct sigaction sa;
    memset(&sa, 0, sizeof sa);
    sa.sa_sigaction = on_bus;
    sa.sa_flags = SA_SIGINFO;
    sigaction(SIGBUS, &sa, 0);

    int fd = open("b", O_RDWR | O_CREAT | O_TRUNC, 0644);
    unsigned char buf[PG + 10];
    for (int i = 0; i < PG + 10; i++) buf[i] = (unsigned char)(i * 3 + 1);
    printf("Datei=%d\n", fd >= 0 && write(fd, buf, sizeof buf) == (ssize_t)sizeof buf);
    unsigned char *m = mmap(0, 4 * PG, PROT_READ, MAP_PRIVATE, fd, 0);
    printf("mmap 4 Seiten bei 1 Seite + 10=%d\n", m != MAP_FAILED);
    if (m == MAP_FAILED) return 1;

    unsigned char v = 0;
    int ok = touch(m + PG + 9, &v);
    printf("Endseite Inhalt=%d %d\n", ok, v == buf[PG + 9]);
    ok = touch(m + PG + 100, &v);
    printf("Endseite hinter Ende Null=%d %d\n", ok, v == 0);
    ok = touch(m + 2 * PG + 5, &v);
    printf("ganz hinter Ende: Zugriff=%d Signal=%d Code=%d Adresse=%d\n", ok, sig, code, addr == (unsigned long)(m + 2 * PG + 5));

    printf("ftruncate klein=%d\n", ftruncate(fd, 100));
    ok = touch(m + 7, &v);
    printf("Seite 0 nach Kuerzen=%d %d\n", ok, v == buf[7]);
    ok = touch(m + 200, &v);
    printf("Seite 0 hinter neuem Ende Null=%d %d\n", ok, v == 0);
    ok = touch(m + 3 * PG, &v);
    printf("Seite 3 nach Kuerzen: Zugriff=%d Signal=%d Code=%d\n", ok, sig, code);
    printf("ftruncate gross=%d\n", ftruncate(fd, 4 * PG));
    ok = touch(m + 3 * PG + 1, &v);
    printf("Seite 3 nach Wachsen=%d %d\n", ok, v == 0);

    /* Kernelzugriff auf eine Seite hinter dem Ende: write aus der Abbildung ist -EFAULT, kein Signal. */
    printf("ftruncate wieder klein=%d\n", ftruncate(fd, 100));
    int p[2];
    pipe(p);
    sig = 0;
    ssize_t w = write(p[1], m + 2 * PG, 16);
    printf("write aus Seite hinter Ende=%d Signal=%d\n", (int)w, sig);

    pid_t kid = fork();
    if (kid == 0) {
        signal(SIGBUS, SIG_DFL);
        v = *(volatile unsigned char *)(m + 2 * PG);
        _exit(0);
    }
    int st = 0;
    waitpid(kid, &st, 0);
    printf("Kind ohne Handler: Signal=%d\n", WIFSIGNALED(st) ? WTERMSIG(st) : -1);
    close(fd);
    unlink("b");
    return 0;
}
