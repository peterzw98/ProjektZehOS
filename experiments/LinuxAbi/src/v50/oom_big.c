/* V50: ein einziger riesiger Bereich (8 GiB, ueber dem Gastspeicher), sequentiell beruehrt: Seitenfehler bis zur Grenze, dann SIGKILL. */
#include "util.h"
#include <sys/mman.h>

int main(void)
{
    INIT();
    size_t len = 8UL << 30;
    unsigned char *p = mmap(NULL, len, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    if (p == MAP_FAILED) { SHOWI("enomem", errno == ENOMEM); return 0; }
    for (size_t i = 0; i < len; i += 4096)
    {
        p[i] = 1;
        if ((i & ((64UL << 20) - 1)) == 0) printf("MiB=%lu\n", i >> 20);
    }
    return 0;
}
