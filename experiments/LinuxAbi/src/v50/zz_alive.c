/* V50: laeuft nach den OOM-Programmen: Der Kernel lebt, Frames sind wieder frei. */
#include "util.h"
#include <stdlib.h>
#include <string.h>

int main(void)
{
    INIT();
    size_t n = 16UL << 20;
    char *p = malloc(n);
    NEED(p);
    memset(p, 7, n);
    printf("alive\n");
    return 0;
}
