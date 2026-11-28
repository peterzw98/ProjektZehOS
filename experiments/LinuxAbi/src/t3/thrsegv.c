/* T3: SIGSEGV in einem Thread ohne Handler beendet den ganzen Prozess: signal 11, Kernel laeuft weiter. */
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
