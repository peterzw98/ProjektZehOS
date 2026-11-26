/* Ladybird V10: ein Absturz in einem Thread beendet den ganzen Prozess (SIGSEGV). */
#include <pthread.h>
#include <stdio.h>

static void *crash(void *arg)
{
    (void)arg;
    *(volatile int *)16 = 1;
    return NULL;
}

int main(void)
{
    pthread_t t;
    printf("main: vor dem Absturz\n");
    fflush(stdout);
    pthread_create(&t, NULL, crash, NULL);
    pthread_join(t, NULL);
    printf("nie erreicht\n");
    return 0;
}
