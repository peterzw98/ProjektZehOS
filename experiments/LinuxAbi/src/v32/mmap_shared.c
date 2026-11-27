/* V32: MAP_SHARED auf einer regulaeren Datei wie sqlite WAL (-shm): Datei anlegen, je Seite ein Byte ans Ende schreiben, 8 Seiten
 * geteilt abbilden. Eltern und Kind (eigener Deskriptor, eigene Abbildung) sehen dieselben Seiten; pread/pwrite sehen die Abbildung;
 * msync-Pruefungen; nach munmap und close steht alles in der Datei; ftruncate laesst wachsen; lesender Deskriptor: PROT_WRITE und
 * mprotect -EACCES, sieht aber den Schreiber. Erwartung: Linux 6.12. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/wait.h>
#include <unistd.h>

#define PG 4096

static int same(const void *a, const char *b) { return memcmp(a, b, strlen(b)) == 0; }

int main(void)
{
    int fd = open("s-shm", O_RDWR | O_CREAT | O_TRUNC, 0644);
    int grow = 1;
    for (int i = 1; i <= 8; i++) grow &= pwrite(fd, "", 1, (off_t)i * PG - 1) == 1;
    printf("Datei=%d %d\n", fd >= 0, grow);
    char *m = mmap(0, 8 * PG, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    printf("mmap=%d\n", m != MAP_FAILED);
    if (m == MAP_FAILED) return 1;
    strcpy(m + PG + 8, "eltern");

    fflush(stdout);
    pid_t pid = fork();
    if (pid == 0) {
        int cfd = open("s-shm", O_RDWR);
        char *c = mmap(0, 8 * PG, PROT_READ | PROT_WRITE, MAP_SHARED, cfd, 0);
        printf("Kind sieht Eltern=%d\n", c != MAP_FAILED && same(c + PG + 8, "eltern"));
        strcpy(c + 7 * PG, "kind");
        strcpy(m + 3 * PG, "geerbt"); /* geerbte Abbildung bleibt geteilt */
        fflush(stdout);
        _exit(0);
    }
    int st = 0;
    waitpid(pid, &st, 0);
    printf("Kind exit=%d\n", WIFEXITED(st) ? WEXITSTATUS(st) : -1);
    printf("Eltern sieht Kind=%d geerbt=%d\n", same(m + 7 * PG, "kind"), same(m + 3 * PG, "geerbt"));

    char buf[16] = {0};
    printf("pread sieht Abbildung=%d\n", pread(fd, buf, 6, PG + 8) == 6 && same(buf, "eltern"));
    int n = (int)pwrite(fd, "XY", 2, PG + 8);
    printf("pwrite=%d Abbildung sieht pwrite=%d\n", n, same(m + PG + 8, "XYtern"));
    lseek(fd, 7 * PG, SEEK_SET);
    printf("read=%d\n", read(fd, buf, 4) == 4 && same(buf, "kind"));
    lseek(fd, 5 * PG, SEEK_SET);
    n = (int)write(fd, "wr", 2);
    printf("write=%d Abbildung sieht write=%d\n", n, same(m + 5 * PG, "wr"));

    printf("msync=%d\n", msync(m, 8 * PG, MS_SYNC));
    errno = 0;
    n = msync(m + 1, PG, MS_SYNC);
    printf("msync unausgerichtet=%d %d\n", n, errno == EINVAL);
    errno = 0;
    n = msync(m, PG, MS_SYNC | MS_ASYNC);
    printf("msync SYNC|ASYNC=%d %d\n", n, errno == EINVAL);
    char *gap = mmap(0, 2 * PG, PROT_READ, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
    munmap(gap + PG, PG);
    errno = 0;
    n = msync(gap, 2 * PG, MS_ASYNC);
    printf("msync Luecke=%d %d\n", n, errno == ENOMEM);
    printf("msync anonym=%d\n", msync(gap, PG, MS_ASYNC));

    n = munmap(m, 8 * PG);
    printf("munmap=%d close=%d\n", n, close(fd));
    int ro = open("s-shm", O_RDONLY);
    memset(buf, 0, sizeof buf);
    printf("Datei hat Eltern=%d", pread(ro, buf, 6, PG + 8) == 6 && same(buf, "XYtern"));
    printf(" Kind=%d", pread(ro, buf, 4, 7 * PG) == 4 && same(buf, "kind"));
    printf(" geerbt=%d\n", pread(ro, buf, 6, 3 * PG) == 6 && same(buf, "geerbt"));

    errno = 0;
    char *bad = mmap(0, PG, PROT_READ | PROT_WRITE, MAP_SHARED, ro, 0);
    printf("lesend PROT_WRITE=%d %d\n", bad == MAP_FAILED, errno == EACCES);
    char *r = mmap(0, 8 * PG, PROT_READ, MAP_SHARED, ro, 0);
    printf("lesend mmap=%d Inhalt=%d\n", r != MAP_FAILED, r != MAP_FAILED && same(r + 7 * PG, "kind"));
    errno = 0;
    n = mprotect(r, PG, PROT_READ | PROT_WRITE);
    printf("mprotect schreibbar=%d %d\n", n, errno == EACCES);

    int rw = open("s-shm", O_RDWR);
    char *w = mmap(0, 9 * PG, PROT_READ | PROT_WRITE, MAP_SHARED, rw, 0);
    strcpy(w + 2 * PG, "neu");
    printf("Leser sieht Schreiber=%d\n", same(r + 2 * PG, "neu"));
    printf("ftruncate=%d\n", ftruncate(rw, 9 * PG));
    strcpy(w + 8 * PG + 1, "dahinter");
    int u1 = munmap(w, 9 * PG), u2 = munmap(r, 8 * PG), c1 = close(rw), c2 = close(ro);
    printf("munmap=%d %d close=%d %d\n", u1, u2, c1, c2);

    ro = open("s-shm", O_RDONLY);
    memset(buf, 0, sizeof buf);
    long size = (long)lseek(ro, 0, SEEK_END);
    printf("Groesse=%ld neu=%d", size, pread(ro, buf, 3, 2 * PG) == 3 && same(buf, "neu"));
    printf(" dahinter=%d\n", pread(ro, buf, 8, 8 * PG + 1) == 8 && same(buf, "dahinter"));
    close(ro);
    return 0;
}
