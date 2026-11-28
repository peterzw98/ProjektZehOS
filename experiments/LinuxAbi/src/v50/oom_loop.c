/* V50: zwoelfmal hintereinander ein Kind den Speicher fuellen lassen (privat, geteilt, mit vier Threads im Wechsel). Nach jedem SIGKILL muss der
 * Speicher wieder frei sein: Der Eltern-Prozess beruehrt danach 128 MiB und das naechste Kind kommt genauso weit wie das erste. */
#include "util.h"
#include <pthread.h>
#include <sys/mman.h>
#include <sys/sysinfo.h>
#include <signal.h>
#include <sys/wait.h>
#include <unistd.h>

static int shared_mode;

static void *fill(void *arg)
{
    (void)arg;
    const size_t chunk = 2UL << 20;
    for (;;)
    {
        unsigned char *p = mmap(NULL, chunk, PROT_READ | PROT_WRITE, (shared_mode ? MAP_SHARED : MAP_PRIVATE) | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED) return NULL;
        for (size_t i = 0; i < chunk; i += 4096) p[i] = 1;
    }
}

int main(void)
{
    INIT();
    int killed = 0, bad = 0, parentok = 0;
    for (int round = 0; round < 12; round++)
    {
        pid_t pid = fork();
        if (pid < 0) { bad++; continue; }
        if (pid == 0)
        {
            shared_mode = round % 3 == 1;
            if (round % 3 == 2)
            {
                pthread_t t[3];
                for (int i = 0; i < 3; i++) pthread_create(&t[i], NULL, fill, NULL);
            }
            fill(NULL);
            for (;;) pause(); /* mit Threads: ENOMEM statt SIGKILL im Haupt-Thread; die anderen Threads laufen noch */
        }
        int st;
        if (round % 3 == 2) { /* Kind mit schlafendem Haupt-Thread selbst beenden, wenn es nicht schon gestorben ist */
            sleep(2);
            if (waitpid(pid, &st, WNOHANG) == 0) { kill(pid, SIGKILL); waitpid(pid, &st, 0); }
        }
        else waitpid(pid, &st, 0);
        if (WIFSIGNALED(st) && WTERMSIG(st) == 9) killed++; else bad++;
        { struct sysinfo si; sysinfo(&si); printf("runde %d frei nach Kind: %lu MiB\n", round, (unsigned long)si.freeram * si.mem_unit >> 20); }
        size_t len = 128UL << 20;
        unsigned char *p = mmap(NULL, len, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (p != MAP_FAILED)
        {
            for (size_t i = 0; i < len; i += 4096) p[i] = 1;
            munmap(p, len);
            parentok++;
        }
    }
    SHOWI("kinder beendet", killed);
    SHOWI("eltern konnten 128 MiB", parentok);
    SHOWI("sonst", bad);
    return 0;
}
