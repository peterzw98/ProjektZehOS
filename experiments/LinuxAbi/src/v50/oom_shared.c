/* V50: MAP_SHARED|MAP_ANONYMOUS in 4-MiB-Stuecken beruehren (memfd-Frames, anderer Zaehlpfad als privater Speicher). */
#include "util.h"
#include <sys/mman.h>

int main(void)
{
    INIT();
    const size_t chunk = 4UL << 20;
    unsigned long total = 0;
    for (;;)
    {
        unsigned char *p = mmap(NULL, chunk, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED) { SHOWI("enomem", errno == ENOMEM); return 0; }
        for (size_t i = 0; i < chunk; i += 4096) p[i] = 1;
        total += chunk;
        if ((total & ((32UL << 20) - 1)) == 0) printf("MiB=%lu\n", total >> 20);
    }
}
