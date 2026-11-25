/* Schreiben auf eine schreibgeschützte Seite: Prozess endet mit SIGSEGV (kein Handler). */
#include "util.h"
#include <sys/mman.h>
#include <unistd.h>

int main(void)
{
    INIT();
    long ps = sysconf(_SC_PAGESIZE);
    volatile char *p = mmap(NULL, ps, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    NEED(p != MAP_FAILED);
    p[0] = 'a';
    SHOWI("mprotect RO", mprotect((void *)p, ps, PROT_READ));
    SHOWI("read RO", p[0]);
    printf("vor dem Schreiben\n");
    p[0] = 'b';
    printf("NACH DEM SCHREIBEN (darf nicht erscheinen)\n");
    return 0;
}
