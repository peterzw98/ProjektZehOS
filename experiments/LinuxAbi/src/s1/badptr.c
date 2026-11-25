/* Ungültige Zeiger an Systemaufrufe: EFAULT, nie ein Absturz des Prozesses. */
#include "util.h"
#include <fcntl.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

#define T(label, expr) do { errno = 0; long r_ = (long)(expr); printf("%s: ret=%ld errno=%d\n", label, r_, errno); } while (0)
#define BAD ((void *)1)
#define KERN ((void *)0xffff800000000000UL)
#define NONCANON ((void *)0x0001000000000000UL)

int main(void)
{
    INIT();
    char ok[8];
    int zfd = open("/dev/zero", O_RDONLY);
    NEED(zfd >= 0);
    T("write(1)", syscall(SYS_write, 1, BAD, 5));
    T("write(kernel)", syscall(SYS_write, 1, KERN, 5));
    T("write(noncanonical)", syscall(SYS_write, 1, NONCANON, 5));
    T("write(NULL)", syscall(SYS_write, 1, NULL, 5));
    T("write len 0", syscall(SYS_write, 1, BAD, 0));
    T("read(/dev/zero, bad)", syscall(SYS_read, zfd, BAD, 5));
    T("openat(bad path)", syscall(SYS_openat, AT_FDCWD, BAD, O_RDONLY));
    T("stat(bad path)", syscall(SYS_stat, BAD, ok));
    T("stat(bad buf)", syscall(SYS_stat, "/", BAD));
    T("getcwd(bad buf)", syscall(SYS_getcwd, BAD, 100));
    T("clock_gettime(bad)", syscall(SYS_clock_gettime, 1, BAD));
    T("getrandom(bad)", syscall(SYS_getrandom, BAD, 8, 0));
    T("uname(bad)", syscall(SYS_uname, BAD));
    T("readlink(bad)", syscall(SYS_readlink, "/proc/self/exe", BAD, 10));
    struct { void *p; unsigned long l; } iov[1] = { { BAD, 3 } };
    T("writev(bad iov base)", syscall(SYS_writev, 1, iov, 1));
    T("writev(bad iov array)", syscall(SYS_writev, 1, BAD, 1));
    T("mprotect(unmapped)", syscall(SYS_mprotect, (void *)0x10000, 4096, 0));
    T("sanity write", syscall(SYS_write, 1, "ok\n", 3));
    return 0;
}
