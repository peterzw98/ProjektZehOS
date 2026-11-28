/* V50: mmap in 4-MiB-Stuecken und jede Seite beruehren, bis der Speicher voll ist. Linux: mmap -> ENOMEM oder OOM-Killer (SIGKILL);
 * ZehOS darf nur diesen Prozess beenden, nie den Kernel anhalten. Die Zeile "ok" erscheint nur bei ENOMEM. */
#include "util.h"
#include <sys/mman.h>
#include <unistd.h>

int main(void)
{
    INIT();
    const size_t chunk = 4UL << 20;
    unsigned long total = 0;
    for (;;)
    {
        unsigned char *p = mmap(NULL, chunk, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED)
        {
            SHOWI("enomem", errno == ENOMEM);
            SHOW("mehr als 64 MiB", total > (64UL << 20));
            return 0;
        }
        for (size_t i = 0; i < chunk; i += 4096) p[i] = 1;
        total += chunk;
        if ((total & ((32UL << 20) - 1)) == 0) printf("MiB=%lu\n", total >> 20);
    }
}
