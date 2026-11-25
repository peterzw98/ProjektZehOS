/* brk: Programmende wachsen und schrumpfen lassen (rohe Aufrufe, ohne malloc). */
#include "util.h"
#include <string.h>
#include <sys/syscall.h>
#include <unistd.h>

static unsigned long sys_brk(unsigned long a) { return (unsigned long)syscall(SYS_brk, a); }

int main(void)
{
    INIT();
    unsigned long start = sys_brk(0);
    SHOW("brk(0) != 0", start != 0);
    unsigned long want = start + 64 * 4096;
    unsigned long got = sys_brk(want);
    SHOW("grow 256 KiB", got == want);
    memset((void *)start, 0xa5, 64 * 4096);
    SHOWI("last byte", ((unsigned char *)start)[64 * 4096 - 1]);
    SHOW("brk(0) == new", sys_brk(0) == want);
    got = sys_brk(start + 4096);
    SHOW("shrink to 1 page", got == start + 4096);
    got = sys_brk(want);
    SHOW("grow again", got == want);
    SHOW("zero after regrow", ((unsigned char *)start)[64 * 4096 - 1] == 0);
    got = sys_brk(start - 4096 * 1024UL);
    SHOW("below start refused", got == want);
    got = sys_brk((unsigned long)1 << 47);
    SHOW("beyond user space refused", got == want);
    return 0;
}
