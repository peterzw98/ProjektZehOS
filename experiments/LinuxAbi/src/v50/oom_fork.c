/* V50: acht Kinder fuellen gleichzeitig den Speicher; zusammen ueberschreiten sie ihn, jedes einzeln noch nicht. Der Elternprozess lebt weiter. */
#include "util.h"
#include <sys/mman.h>
#include <sys/wait.h>
#include <unistd.h>

int main(void)
{
    INIT();
    int started = 0;
    for (int k = 0; k < 8; k++)
    {
        pid_t pid = fork();
        if (pid < 0) break;
        if (pid == 0)
        {
            size_t len = 128UL << 20;
            unsigned char *p = mmap(NULL, len, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
            if (p == MAP_FAILED) _exit(3);
            for (size_t i = 0; i < len; i += 4096) p[i] = 1;
            sleep(1);
            _exit(0);
        }
        started++;
    }
    int ok = 0, killed = 0, other = 0, st;
    while (wait(&st) > 0)
    {
        if (WIFEXITED(st) && WEXITSTATUS(st) == 0) ok++;
        else if (WIFSIGNALED(st) && WTERMSIG(st) == 9) killed++;
        else other++;
    }
    SHOW("alle gezaehlt", ok + killed + other == started);
    SHOW("keine anderen Ausgaenge", other == 0);
    return 0;
}
