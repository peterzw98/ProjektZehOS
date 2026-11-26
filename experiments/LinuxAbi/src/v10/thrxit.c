/* Ladybird V10: exit (exit_group) aus einem Thread beendet den Prozess, waehrend main in pthread_join wartet. */
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

static void *quit(void *arg)
{
    (void)arg;
    printf("thread: exit 3\n");
    fflush(stdout);
    exit(3);
}

static void *block(void *arg)
{
    (void)arg;
    for (;;) pause();
    return NULL;
}

int main(void)
{
    pthread_t a, b;
    pthread_create(&b, NULL, block, NULL);
    pthread_create(&a, NULL, quit, NULL);
    pthread_join(b, NULL);
    printf("nie erreicht\n");
    return 0;
}
