/* getrandom: Eigenschaften, nie die Werte. */
#include "util.h"
#include <string.h>
#include <sys/random.h>
#include <sys/syscall.h>
#include <unistd.h>

int main(void)
{
    INIT();
    unsigned char a[32] = { 0 }, b[32] = { 0 };
    SHOWI("getrandom 32", getrandom(a, 32, 0));
    SHOWI("getrandom 32 again", getrandom(b, 32, 0));
    SHOW("differ", memcmp(a, b, 32) != 0);
    unsigned char zero[32] = { 0 };
    SHOW("not all zero", memcmp(a, zero, 32) != 0);
    SHOWI("getrandom 0", getrandom(a, 0, 0));
    SHOWI("NONBLOCK", getrandom(a, 8, GRND_NONBLOCK));
    SHOWI("GRND_RANDOM", getrandom(a, 8, GRND_RANDOM | GRND_NONBLOCK) >= 0);
    SHOWI("bad flags", getrandom(a, 8, 0x80)); SHOWI("errno", errno);
    SHOWI("bad buffer", syscall(SYS_getrandom, (void *)1, 8, 0)); SHOWI("errno", errno);
    unsigned char big[1024];
    SHOWI("getrandom 1024", getrandom(big, sizeof big, 0));
    return 0;
}
