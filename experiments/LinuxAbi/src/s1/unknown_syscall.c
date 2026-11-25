/* Unbekannte Systemaufrufnummern liefern ENOSYS (38) und beenden den Prozess nicht. */
#include "util.h"
#include <sys/syscall.h>
#include <unistd.h>

int main(void)
{
    INIT();
    long nrs[] = { 1000, 9999, 100000, 0x3fffffff };
    for (unsigned i = 0; i < sizeof nrs / sizeof *nrs; i++) {
        errno = 0;
        long r = syscall(nrs[i], 0, 0, 0, 0, 0, 0);
        printf("syscall(%#lx): ret=%ld errno=%d\n", nrs[i], r, errno);
    }
    errno = 0;
    long r = syscall(-1L);
    printf("syscall(-1): ret=%ld errno=%d\n", r, errno);
    SHOWI("getpid still works", syscall(SYS_getpid) > 0);
    return 0;
}
