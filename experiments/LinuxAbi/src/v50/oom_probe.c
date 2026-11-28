/* V50 (Messung): Freier Speicher nach dem Tod eines Kindes, das privat/geteilt gefuellt hat, ueber 4 Sekunden. */
#include "util.h"
#include <signal.h>
#include <sys/mman.h>
#include <sys/sysinfo.h>
#include <sys/wait.h>
#include <unistd.h>

static unsigned long freemib(void) { struct sysinfo si; sysinfo(&si); return (unsigned long)si.freeram * si.mem_unit >> 20; }

int main(void)
{
    INIT();
    printf("start frei %lu\n", freemib());
    for (int round = 0; round < 4; round++)
    {
        int shared = round & 1;
        pid_t pid = fork();
        if (pid == 0)
        {
            const size_t chunk = 2UL << 20;
            for (;;)
            {
                unsigned char *p = mmap(NULL, chunk, PROT_READ | PROT_WRITE, (shared ? MAP_SHARED : MAP_PRIVATE) | MAP_ANONYMOUS, -1, 0);
                if (p == MAP_FAILED) _exit(0);
                for (size_t i = 0; i < chunk; i += 4096) p[i] = 1;
            }
        }
        int st;
        waitpid(pid, &st, 0);
        printf("runde %d %s: ", round, shared ? "geteilt" : "privat");
        for (int k = 0; k < 3; k++) { printf("%lu ", freemib()); usleep(500000); }
        printf("\n");
    }
    return 0;
}
