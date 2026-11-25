/* Anonymer Speicher: mmap, munmap, mprotect, madvise, MAP_FIXED, Nullung, Fehlerwege. */
#include "util.h"
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>

int main(void)
{
    INIT();
    long ps = sysconf(_SC_PAGESIZE);
    SHOWI("pagesize", ps);
    size_t len = 16 * ps;
    unsigned char *p = mmap(NULL, len, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    NEED(p != MAP_FAILED);
    SHOW("aligned", ((unsigned long)p & (ps - 1)) == 0);
    int zero = 1;
    for (size_t i = 0; i < len; i++) if (p[i]) zero = 0;
    SHOW("zero filled", zero);
    for (size_t i = 0; i < len; i++) p[i] = (unsigned char)(i * 7);
    unsigned long sum = 0;
    for (size_t i = 0; i < len; i++) sum += p[i];
    SHOWI("sum", sum);
    SHOWI("madvise DONTNEED", madvise(p, len, MADV_DONTNEED));
    zero = 1;
    for (size_t i = 0; i < len; i++) if (p[i]) zero = 0;
    SHOW("zero after DONTNEED", zero);
    SHOWI("munmap middle", munmap(p + 4 * ps, 4 * ps));
    p[0] = 1; p[len - 1] = 2;
    SHOWI("mprotect RO", mprotect(p, ps, PROT_READ));
    SHOWI("read RO", p[0]);
    SHOWI("mprotect RW", mprotect(p, ps, PROT_READ | PROT_WRITE));
    p[0] = 3; SHOWI("write RW", p[0]);
    unsigned char *q = mmap(p + 4 * ps, ps, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_FIXED, -1, 0);
    SHOW("MAP_FIXED in Loch", q == p + 4 * ps);
    SHOWI("munmap rest a", munmap(p, 4 * ps));
    SHOWI("munmap rest b", munmap(p + 4 * ps, ps));
    SHOWI("munmap rest c", munmap(p + 8 * ps, 8 * ps));
    SHOWI("munmap unaligned", munmap(p + 1, ps)); SHOWI("errno", errno);
    void *z = mmap(NULL, 0, PROT_READ, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    SHOW("len 0 fails", z == MAP_FAILED); SHOWI("errno", errno);
    z = mmap(NULL, ps, PROT_READ, MAP_PRIVATE, -1, 0);
    SHOW("no ANON, fd -1 fails", z == MAP_FAILED); SHOWI("errno", errno);
    void *big = mmap(NULL, (size_t)1 << 48, PROT_READ, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    SHOW("256 TiB NORESERVE fails", big == MAP_FAILED);
    void *res = mmap(NULL, (size_t)1 << 30, PROT_NONE, MAP_PRIVATE | MAP_ANONYMOUS | MAP_NORESERVE, -1, 0);
    SHOW("1 GiB PROT_NONE reserve", res != MAP_FAILED);
    SHOWI("commit part", mprotect(res, 4 * ps, PROT_READ | PROT_WRITE));
    memset(res, 0x5a, 4 * ps);
    SHOWI("byte", ((unsigned char *)res)[4 * ps - 1]);
    return 0;
}
