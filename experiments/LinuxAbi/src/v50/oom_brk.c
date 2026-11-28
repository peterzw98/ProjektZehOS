/* V50: brk in 1-MiB-Schritten wachsen lassen und beruehren, bis der Speicher voll ist (ENOMEM = Rueckgabe unveraendert oder SIGKILL). */
#include "util.h"
#include <unistd.h>

int main(void)
{
    INIT();
    char *start = sbrk(0);
    NEED(start != (char *)-1);
    unsigned long total = 0;
    for (;;)
    {
        char *old = sbrk(1L << 20);
        if (old == (char *)-1)
        {
            SHOWI("enomem", errno == ENOMEM);
            SHOW("mehr als 64 MiB", total > (64UL << 20));
            return 0;
        }
        for (long i = 0; i < (1L << 20); i += 4096) old[i] = 1;
        total += 1UL << 20;
        if ((total & ((32UL << 20) - 1)) == 0) printf("MiB=%lu\n", total >> 20);
    }
}
