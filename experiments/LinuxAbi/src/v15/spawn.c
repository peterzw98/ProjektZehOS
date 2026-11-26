// V15 "Prozess startet Prozess": posix_spawn (musl: clone(CLONE_VM|CLONE_VFORK|SIGCHLD) + execve) eines kleinen
// statischen Programms, das "kind" schreibt und mit 42 endet; Fehlerfall (Datei fehlt -> ENOENT über die Pipe von musl)
// und rohes vfork mit _exit vor execve. Ohne wait4 auf ZehOS-Seite (senior2, task/lb-v15-wait) prüft das Programm nur,
// was auch ohne Warten feststeht; die kurze Pause ordnet die Ausgabe des Kinds vor die Schlusszeile.
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <spawn.h>
#include <stdio.h>
#include <string.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>

extern char **environ;

// ELF64, ein PT_LOAD (R X) ab 0x400000, Einsprung 0x400078:
// write(1, "kind\n", 5); exit_group(42)
static const unsigned char code[] = {
    0xb8, 0x01, 0x00, 0x00, 0x00,             // mov eax, 1
    0xbf, 0x01, 0x00, 0x00, 0x00,             // mov edi, 1
    0x48, 0x8d, 0x35, 0x13, 0x00, 0x00, 0x00, // lea rsi, [rip + 0x13]
    0xba, 0x05, 0x00, 0x00, 0x00,             // mov edx, 5
    0x0f, 0x05,                               // syscall
    0xb8, 0xe7, 0x00, 0x00, 0x00,             // mov eax, 231
    0xbf, 0x2a, 0x00, 0x00, 0x00,             // mov edi, 42
    0x0f, 0x05,                               // syscall
    'k', 'i', 'n', 'd', '\n',
};

static void put64(unsigned char *p, unsigned long v) { for (int i = 0; i < 8; i++) p[i] = (unsigned char)(v >> (8 * i)); }
static void put32(unsigned char *p, unsigned v) { for (int i = 0; i < 4; i++) p[i] = (unsigned char)(v >> (8 * i)); }
static void put16(unsigned char *p, unsigned v) { p[0] = (unsigned char)v; p[1] = (unsigned char)(v >> 8); }

static int write_child(const char *path)
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

static void pause_ms(long ms)
{
    struct timespec t = { ms / 1000, (ms % 1000) * 1000000L };
    nanosleep(&t, 0);
}

int main(void)
{
    if (write_child("kind") != 0) { printf("schreiben fehlgeschlagen errno %d\n", errno); return 1; }

    pid_t pid = 0;
    char *argv[] = { "kind", 0 };
    int rc = posix_spawn(&pid, "kind", 0, 0, argv, environ);
    pause_ms(300);
    printf("spawn rc %d pid>1 %d anderer %d\n", rc, pid > 1, pid != getpid());

    pid_t pid2 = 0;
    char *argv2[] = { "fehlt", 0 };
    rc = posix_spawn(&pid2, "fehlt", 0, 0, argv2, environ);
    printf("spawn fehlt rc %d\n", rc);

    // Rohes vfork: Das Kind sieht eine eigene PID, schreibt in geteilten Speicher und endet vor execve.
    static volatile int shared;
    pid_t self = getpid();
    // vfork direkt als Befehl in main: Über syscall() kehrte das Kind aus dem Rahmen der libc-Funktion zurück und überschriebe ihn
    // für die Eltern (Rücksprungadresse, gesicherte Register), das ist auch auf Linux undefiniert (v war dort 0).
    long v;
    __asm__ volatile("syscall" : "=a"(v) : "0"((long)SYS_vfork) : "rcx", "r11", "memory");
    if (v == 0) {
        shared = (getpid() != self) ? 2 : 1;
        syscall(SYS_exit_group, 7);
    }
    printf("vfork pid>1 %d geteilt %d\n", v > 1, shared);
    printf("ende\n");
    return 0;
}
