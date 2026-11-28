/* V50: memfd wachsen lassen und beschreiben (write statt Seitenfehler): Dateispeicher gegen die Grenze. */
#define _GNU_SOURCE
#include "util.h"
#include <sys/mman.h>
#include <unistd.h>

int main(void)
{
    INIT();
    int fd = memfd_create("v50", 0);
    NEED(fd >= 0);
    static char buf[1 << 20];
    unsigned long total = 0;
    for (;;)
    {
        ssize_t w = write(fd, buf, sizeof buf);
        if (w != (ssize_t)sizeof buf) { SHOWI("fehler", errno); SHOW("mehr als 32 MiB", total > (32UL << 20)); return 0; }
        total += sizeof buf;
        if ((total & ((32UL << 20) - 1)) == 0) printf("MiB=%lu\n", total >> 20);
    }
}
