/* V50: viele kleine Abbildungen (je eine Seite, beruehrt) bis zur Grenze: Kartenverwaltung und Frames wachsen zusammen. */
#include "util.h"
#include <sys/mman.h>

int main(void)
{
    INIT();
    unsigned long n = 0;
    for (;;)
    {
        unsigned char *p = mmap(NULL, 4096, PROT_READ | (n & 1 ? PROT_WRITE : PROT_WRITE | PROT_EXEC), MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED) { SHOWI("enomem", errno == ENOMEM); SHOW("viele", n > 1000); return 0; }
        *p = 1;
        n++;
        if (n % 20000 == 0) printf("n=%lu\n", n);
    }
}
