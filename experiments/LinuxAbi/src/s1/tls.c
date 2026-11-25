/* Thread Local Storage im Hauptthread: .tdata, .tbss, FS-Basis, arch_prctl. */
#include "util.h"
#define ARCH_GET_FS 0x1003 /* musl-gcc liefert asm/prctl.h nicht */
#include <string.h>
#include <sys/syscall.h>
#include <unistd.h>

static __thread int initialised = 41;
static __thread int zeroed;
static __thread char bufr[3000] = { 'x' };
static __thread long long counter;
__thread int exported = 7;

int main(void)
{
    INIT();
    SHOWI("initialised", initialised);
    SHOWI("zeroed", zeroed);
    SHOWI("bufr[0]", bufr[0]);
    SHOWI("bufr[2999]", bufr[2999]);
    initialised++; zeroed = 5; bufr[2999] = 'y';
    for (int i = 0; i < 1000; i++) counter += i;
    SHOWI("initialised+1", initialised);
    SHOWI("zeroed=5", zeroed);
    SHOWI("bufr[2999]", bufr[2999]);
    SHOWI("counter", counter);
    SHOWI("exported", exported);
    unsigned long fs = 0;
    SHOWI("arch_prctl GET_FS", syscall(SYS_arch_prctl, ARCH_GET_FS, &fs));
    SHOW("FS != 0", fs != 0);
    unsigned long self;
    __asm__ volatile("movq %%fs:0, %0" : "=r"(self));
    SHOW("fs:0 == FS", self == fs);
    SHOW("&initialised near FS", (unsigned long)&initialised < fs && fs - (unsigned long)&initialised < 1 << 20);
    usleep(1000);
    SHOWI("after syscall", initialised);
    return 0;
}
