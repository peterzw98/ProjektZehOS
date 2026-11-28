/* V50: vier Threads beruehren gleichzeitig eigene Bereiche, bis der Speicher voll ist (Wettlauf um die letzten Frames). */
#include "util.h"
#include <pthread.h>
#include <sys/mman.h>

static void *fill(void *arg)
{
    (void)arg;
    for (;;)
    {
        size_t chunk = 2UL << 20;
        unsigned char *p = mmap(NULL, chunk, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED) return NULL;
        for (size_t i = 0; i < chunk; i += 4096) p[i] = 1;
    }
}

int main(void)
{
    INIT();
    pthread_t t[4];
    for (int i = 0; i < 4; i++) NEED(pthread_create(&t[i], NULL, fill, NULL) == 0);
    for (int i = 0; i < 4; i++) pthread_join(t[i], NULL);
    printf("alle Threads fertig\n");
    return 0;
}
