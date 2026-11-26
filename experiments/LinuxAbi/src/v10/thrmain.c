/* Ladybird V10: pthread_exit in main; der Prozess lebt, bis der letzte Thread endet (dann Status 0). */
#include <pthread.h>
#include <stdio.h>
#include <time.h>

static void *late(void *arg)
{
    (void)arg;
    struct timespec ts = { 0, 30 * 1000 * 1000 };
    nanosleep(&ts, NULL);
    printf("worker: nach main\n");
    fflush(stdout);
    return NULL;
}

int main(void)
{
    pthread_t t;
    pthread_create(&t, NULL, late, NULL);
    printf("main: pthread_exit\n");
    fflush(stdout);
    pthread_exit(NULL);
}
