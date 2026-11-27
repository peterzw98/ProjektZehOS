/* V35 (Ladybird): Prozessgruppen. Der Hub startet seine Dienste mit posix_spawn und POSIX_SPAWN_SETPGROUP (musl: setpgid im
 * vfork-Kind vor execve). Geprüft: neue Gruppe (pgroup 0), Beitritt zu einer bestehenden Gruppe, unbekannte Gruppe (EPERM über
 * posix_spawn), setpgid des Elternprozesses nach execve (EACCES), getpgid/getsid fremder Prozesse, setsid im fork-Kind und
 * danach setpgid aus einer anderen Sitzung (EPERM), wait4 und kill mit negativer Gruppe. Gibt nur Werte aus, die nicht davon
 * abhängen, ob das Programm selbst Sitzungsführer ist (unter Linux startet es der Vergleicher, im Gast führt es seine Sitzung).
 * Die Ausgabe ist mit Linux 6.12 gleich (expected/v35/pgid.txt). */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <spawn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/wait.h>
#include <unistd.h>

#define T(label, expr) do { errno = 0; long r_ = (long)(expr); printf("%s: ret=%ld errno=%d\n", label, r_, errno); } while (0)
#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)

extern char **environ;

// ELF64, ein PT_LOAD (R X) ab 0x400000, Einsprung 0x400078: for (;;) pause(); (endet nur durch ein Signal)
static const unsigned char code[] = {
    0xb8, 0x22, 0x00, 0x00, 0x00, // mov eax, 34 (pause)
    0x0f, 0x05,                   // syscall
    0xeb, 0xf7,                   // jmp zum Anfang
};

static void put64(unsigned char *p, unsigned long v) { for (int i = 0; i < 8; i++) p[i] = (unsigned char)(v >> (8 * i)); }
static void put32(unsigned char *p, unsigned v) { for (int i = 0; i < 4; i++) p[i] = (unsigned char)(v >> (8 * i)); }
static void put16(unsigned char *p, unsigned v) { p[0] = (unsigned char)v; p[1] = (unsigned char)(v >> 8); }

static int write_sleeper(const char *path)
{
    unsigned char f[0x78 + sizeof code];
    memset(f, 0, sizeof f);
    memcpy(f, "\x7f" "ELF\x02\x01\x01", 7);
    put16(f + 16, 2);          // ET_EXEC
    put16(f + 18, 62);         // x86-64
    put32(f + 20, 1);
    put64(f + 24, 0x400078);   // Einsprung
    put64(f + 32, 64);         // Programmköpfe
    put16(f + 52, 64);
    put16(f + 54, 56);
    put16(f + 56, 1);
    unsigned char *ph = f + 64;
    put32(ph, 1);              // PT_LOAD
    put32(ph + 4, 5);          // R X
    put64(ph + 16, 0x400000);
    put64(ph + 24, 0x400000);
    put64(ph + 32, sizeof f);
    put64(ph + 40, sizeof f);
    put64(ph + 48, 0x1000);
    memcpy(f + 0x78, code, sizeof code);
    int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0755);
    if (fd < 0) return -1;
    int ok = write(fd, f, sizeof f) == (ssize_t)sizeof f;
    return close(fd) == 0 && ok ? 0 : -1;
}

/* posix_spawn des Schläfers mit POSIX_SPAWN_SETPGROUP in Gruppe pgroup (0 = eigene neue Gruppe); liefert den Fehlercode. */
static int spawn_in_group(pid_t *pid, pid_t pgroup)
{
    posix_spawnattr_t at;
    NEED(posix_spawnattr_init(&at) == 0);
    NEED(posix_spawnattr_setflags(&at, POSIX_SPAWN_SETPGROUP) == 0);
    NEED(posix_spawnattr_setpgroup(&at, pgroup) == 0);
    char *argv[] = { "schlaf", 0 };
    int rc = posix_spawn(pid, "schlaf", 0, &at, argv, environ);
    posix_spawnattr_destroy(&at);
    return rc;
}

static void report_wait(const char *label, pid_t want, int options)
{
    int st = 0;
    errno = 0;
    pid_t r = waitpid(want, &st, options);
    if (r > 0)
        printf("%s: kind %s signal=%d exit=%d\n", label, r == -want ? "fuehrer" : "anderes",
            WIFSIGNALED(st) ? WTERMSIG(st) : 0, WIFEXITED(st) ? WEXITSTATUS(st) : -1);
    else
        printf("%s: ret=%d errno=%d\n", label, (int)r, errno);
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    alarm(30);
    NEED(write_sleeper("schlaf") == 0);
    pid_t me = getpid(), mygroup = getpgrp(), mysession = getsid(0);

    // Hub-Muster: neues Kind in eigener Gruppe.
    pid_t a = 0;
    printf("spawn neue Gruppe rc=%d\n", spawn_in_group(&a, 0));
    NEED(a > 1);
    printf("getpgid(a)==a %d\n", getpgid(a) == a);
    printf("getsid(a)==eigene Sitzung %d\n", getsid(a) == mysession);
    printf("eigene Gruppe unveraendert %d\n", getpgrp() == mygroup && getpgid(0) == mygroup && getpgid(me) == mygroup);
    T("setpgid(a) nach execve", setpgid(a, mygroup));
    T("setpgid(a, a) nach execve", setpgid(a, a));

    // Beitritt zur Gruppe von a; dann eine Gruppe, die es nicht gibt.
    pid_t b = 0;
    printf("spawn in Gruppe a rc=%d\n", spawn_in_group(&b, a));
    printf("getpgid(b)==a %d\n", getpgid(b) == a);
    pid_t c = 0;
    int rc = spawn_in_group(&c, 999999);
    printf("spawn unbekannte Gruppe rc=%d\n", rc);
    if (rc == 0) { kill(c, SIGKILL); waitpid(c, 0, 0); }

    T("setpgid unbekannte PID", setpgid(999998, 0));
    T("setpgid negative Gruppe", setpgid(0, -1));
    T("getpgid unbekannte PID", getpgid(999998));
    T("getsid unbekannte PID", getsid(999998));

    // setsid im fork-Kind (kein Gruppenführer); danach liegt es in einer anderen Sitzung.
    int sync[2];
    NEED(pipe(sync) == 0);
    pid_t d = fork();
    NEED(d >= 0);
    if (d == 0)
    {
        close(sync[0]);
        pid_t s = setsid();
        char ok = s == getpid() && getsid(0) == getpid() && getpgrp() == getpid() ? 'j' : 'n';
        errno = 0;
        int again = setsid();
        char eperm = again == -1 && errno == EPERM ? 'j' : 'n';
        errno = 0;
        int sp = setpgid(0, 0);
        char leader = sp == -1 && errno == EPERM ? 'j' : 'n';
        char out[3] = { ok, eperm, leader };
        if (write(sync[1], out, 3) != 3) _exit(3);
        for (;;) pause();
    }
    close(sync[1]);
    char got[3] = { 0, 0, 0 };
    NEED(read(sync[0], got, 3) == 3);
    printf("kind setsid neu=%c erneut EPERM=%c setpgid Fuehrer EPERM=%c\n", got[0], got[1], got[2]);
    printf("getsid(d)==d %d\n", getsid(d) == d);
    T("setpgid(d) andere Sitzung", setpgid(d, d));
    T("setpgid in Gruppe einer anderen Sitzung", setpgid(0, d));
    kill(d, SIGKILL);
    report_wait("wait d", d, 0);

    // wait4 und kill nach Gruppe.
    report_wait("waitpid(-a, WNOHANG) laufend", -a, WNOHANG);
    T("kill(-a, 0)", kill(-a, 0));
    T("kill(-a, SIGTERM)", kill(-a, SIGTERM));
    // Welches Kind zuerst endet, hängt vom Zeitplan ab; gezählt wird nur, dass beide mit SIGTERM aus Gruppe a kommen.
    int reaped_a = 0, reaped_b = 0, by_term = 0;
    for (int i = 0; i < 2; i++)
    {
        int st = 0;
        pid_t r = waitpid(-a, &st, 0);
        reaped_a += r == a;
        reaped_b += r == b;
        by_term += WIFSIGNALED(st) && WTERMSIG(st) == SIGTERM;
    }
    printf("waitpid(-a) zweimal: a=%d b=%d SIGTERM=%d\n", reaped_a, reaped_b, by_term);
    report_wait("waitpid(-a) leer", -a, 0);
    T("kill(-a) nach dem Ernten", kill(-a, 0));
    T("kill(0, 0) eigene Gruppe", kill(0, 0));
    report_wait("waitpid(0, WNOHANG) ohne Kinder", 0, WNOHANG);
    printf("ende\n");
    return 0;
}
