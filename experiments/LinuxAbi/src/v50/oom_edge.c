/* V50: Speicher bis dicht an die Kernel-Reserve fuellen (sysinfo), dann Kernelobjekte anlegen: fork, Threads, Pipes, Socketpaare, Dateien.
 * Jeder Aufruf darf mit ENOMEM/EAGAIN/EMFILE scheitern, nie den Kernel anhalten; danach bis zum Ende weiterfuellen (SIGKILL). */
#include "util.h"
#include <fcntl.h>
#include <pthread.h>
#include <sys/mman.h>
#include <sys/socket.h>
#include <sys/sysinfo.h>
#include <sys/wait.h>
#include <unistd.h>

static void *nop(void *a) { return a; }

static unsigned long freemib(void)
{
    struct sysinfo si;
    sysinfo(&si);
    return (unsigned long)si.freeram * si.mem_unit >> 20;
}

int main(void)
{
    INIT();
    const size_t chunk = 1UL << 20;
    unsigned long total = 0;
    while (freemib() > 68)
    {
        unsigned char *p = mmap(NULL, chunk, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED) break;
        for (size_t i = 0; i < chunk; i += 4096) p[i] = 1;
        total += chunk;
    }
    printf("gefuellt: %d\n", total > (64UL << 20));
    int forkok = 0, forkfail = 0, throk = 0, thrfail = 0, pipeok = 0, pipefail = 0, sockok = 0, sockfail = 0, openok = 0;
    for (int i = 0; i < 40; i++)
    {
        pid_t pid = fork();
        if (pid == 0) _exit(0);
        if (pid > 0) { int st; waitpid(pid, &st, 0); forkok++; } else forkfail++;
    }
    for (int i = 0; i < 200; i++)
    {
        pthread_t t;
        if (pthread_create(&t, NULL, nop, NULL) == 0) { pthread_join(t, NULL); throk++; } else thrfail++;
    }
    for (int i = 0; i < 200; i++)
    {
        int fds[2];
        if (pipe(fds) == 0) { pipeok++; close(fds[0]); close(fds[1]); } else pipefail++;
        if (socketpair(AF_UNIX, SOCK_STREAM, 0, fds) == 0) { sockok++; close(fds[0]); close(fds[1]); } else sockfail++;
        int f = open("/proc/meminfo", O_RDONLY);
        if (f >= 0) { char b[64]; if (read(f, b, sizeof b) > 0) openok++; close(f); }
    }
    printf("fork+thr+pipe+sock+open ausgefuehrt: %d\n", forkok + forkfail == 40 && throk + thrfail == 200 && pipeok + pipefail == 200 && sockok + sockfail == 200);
    fflush(stdout);
    fprintf(stderr, "fork %d/%d thr %d/%d pipe %d/%d sock %d/%d open %d\n", forkok, forkfail, throk, thrfail, pipeok, pipefail, sockok, sockfail, openok);
    for (;;)
    {
        unsigned char *p = mmap(NULL, chunk, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED) return 0;
        for (size_t i = 0; i < chunk; i += 4096) p[i] = 1;
    }
}
