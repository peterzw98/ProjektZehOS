/* Uhren und Schlaf: nur Eigenschaften, keine Messwerte. */
#include "util.h"
#include <time.h>
#include <unistd.h>

static long long ns(struct timespec t) { return t.tv_sec * 1000000000LL + t.tv_nsec; }

int main(void)
{
    INIT();
    struct timespec a, b, r;
    NEED(clock_gettime(CLOCK_MONOTONIC, &a) == 0);
    NEED(clock_gettime(CLOCK_MONOTONIC, &b) == 0);
    SHOW("monotonic nondecreasing", ns(b) >= ns(a));
    SHOW("nsec in range", a.tv_nsec >= 0 && a.tv_nsec < 1000000000);
    NEED(clock_gettime(CLOCK_REALTIME, &r) == 0);
    SHOW("realtime after 2020", r.tv_sec > 1577836800LL);
    NEED(clock_gettime(CLOCK_MONOTONIC, &a) == 0);
    struct timespec s = { 0, 20 * 1000000L };
    SHOWI("nanosleep 20 ms", nanosleep(&s, NULL));
    NEED(clock_gettime(CLOCK_MONOTONIC, &b) == 0);
    long long d = ns(b) - ns(a);
    SHOW("slept >= 20 ms", d >= 20000000LL);
    SHOW("slept < 30 s", d < 30000000000LL);
    struct timespec bad = { 0, 1000000000L };
    SHOWI("nanosleep bad nsec", nanosleep(&bad, NULL)); SHOWI("errno", errno);
    SHOWI("bad clock id", clock_gettime((clockid_t)9999, &a)); SHOWI("errno", errno);
    SHOWI("usleep 0", usleep(0));
    return 0;
}
