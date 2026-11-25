/* x87 (long double), SSE und Fließkommazustand über Systemaufrufe hinweg. */
#include "util.h"
#include <math.h>
#include <unistd.h>

int main(void)
{
    INIT();
    SHOWI("sizeof long double", sizeof(long double));
    SHOWI("LDBL_MANT_DIG", __LDBL_MANT_DIG__);
    volatile long double x = 1.0L, three = 3.0L;
    long double third = x / three;
    printf("1/3 = %.21Lg\n", third);
    printf("sqrt2 = %.21Lg\n", sqrtl(2.0L));
    long double s = 0;
    for (int i = 1; i <= 100; i++) s += 1.0L / i;
    printf("H100 = %.21Lg\n", s);
    volatile long double big = 1e4000L;
    printf("1e4000 = %Lg\n", big);
    volatile long double tiny = 1e-4000L;
    printf("1e-4000 = %Lg\n", tiny);
    SHOW("ldexp", ldexpl(1.0L, 16000) > 1e4800L);
    volatile double d = 0.1;
    printf("0.1+0.2 = %.17g\n", d + 0.2);
    volatile float f = 16777216.0f;
    printf("float 2^24+1 = %.1f\n", f + 1.0f);
    /* FPU-Zustand muss einen Systemaufruf überstehen. */
    volatile long double keep = 2.5L;
    volatile double keepd = 1.25;
    usleep(1000);
    printf("keep = %Lg %g\n", keep * 2, keepd * 2);
    volatile double zero = 0.0;
    printf("1/0 = %g, 0/0 is nan = %d\n", 1.0 / zero, isnan(zero / zero));
    printf("fmod = %g, pow = %g\n", fmod(10.0, 3.0), pow(2.0, 0.5));
    return 0;
}
