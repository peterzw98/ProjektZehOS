/* Ladybird V11: vier Threads oeffnen, dup-en, beschreiben und schliessen gleichzeitig Dateien im Arbeitsverzeichnis, waehrend main
   chdir/getcwd ruft. Ohne Sperre fuer Deskriptortabelle, Arbeitsverzeichnis und Dateisystem vergibt der Kernel Nummern doppelt,
   verliert Anhaenge oder scheitert in der Pfadaufloesung. */
#include <fcntl.h>
#include <pthread.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#define THREADS 4
#define ROUNDS 200

static int failures[THREADS];

static void *work(void *arg)
{
    int k = (int)(long)arg;
    char path[32];
    snprintf(path, sizeof path, "/tmp/v11/f%d", k);
    for (int i = 0; i < ROUNDS; i++) {
        int fd = open(path, O_WRONLY | O_CREAT | O_APPEND, 0644);
        int copy = fd >= 0 ? dup(fd) : -1;
        char c = (char)('a' + k);
        ssize_t w = copy >= 0 ? write(copy, &c, 1) : -1;
        int c1 = fd >= 0 ? close(fd) : -1;
        int c2 = copy >= 0 ? close(copy) : -1;
        if (fd < 3 || copy < 3 || w != 1 || c1 != 0 || c2 != 0) {
            failures[k]++;
        }
    }
    return NULL;
}

int main(void)
{
    mkdir("/tmp", 0755);
    mkdir("/tmp/v11", 0755);
    for (int k = 0; k < THREADS; k++) {
        char path[32];
        snprintf(path, sizeof path, "/tmp/v11/f%d", k);
        int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC, 0644);
        if (fd < 0 || close(fd) != 0) {
            printf("anlegen %d gescheitert\n", k);
            return 1;
        }
    }

    pthread_t t[THREADS];
    for (int k = 0; k < THREADS; k++) {
        pthread_create(&t[k], NULL, work, (void *)(long)k);
    }

    /* main wechselt waehrenddessen das Arbeitsverzeichnis; getcwd muss immer einen der beiden Werte liefern. */
    int bad_cwd = 0;
    char buf[64];
    for (int i = 0; i < ROUNDS; i++) {
        if (chdir(i % 2 ? "/tmp" : "/tmp/v11") != 0 || getcwd(buf, sizeof buf) == NULL ||
            (strcmp(buf, "/tmp") != 0 && strcmp(buf, "/tmp/v11") != 0)) {
            bad_cwd++;
        }
    }

    for (int k = 0; k < THREADS; k++) {
        pthread_join(t[k], NULL);
    }

    printf("cwd fehler %d\n", bad_cwd);
    int next = open("/tmp/v11/f0", O_RDONLY);
    printf("naechste nummer %d\n", next);
    close(next);
    for (int k = 0; k < THREADS; k++) {
        char path[32];
        struct stat st;
        snprintf(path, sizeof path, "/tmp/v11/f%d", k);
        int ok = stat(path, &st) == 0;
        printf("f%d: fehler %d groesse %ld\n", k, failures[k], ok ? (long)st.st_size : -1L);
        unlink(path);
    }

    rmdir("/tmp/v11");
    return 0;
}
