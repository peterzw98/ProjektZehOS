/* V30: private Dateiabbildung (MAP_PRIVATE), deren Seiten erst beim Zugriff aus der Datei kommen. Prueft, was ld-musl und
 * Ladybird davon brauchen und was das spaete Lesen veraendern koennte: Inhalt je Seite und Nullen hinter dem Dateiende,
 * Schreiben ohne Rueckwirkung auf die Datei, Abbildung mit Versatz, fork mit noch nicht gelesenen Seiten, close und unlink vor
 * dem ersten Zugriff, munmap in der Mitte, MADV_DONTNEED (Seite kommt wieder aus der Datei) und eine Dateiaenderung vor dem
 * ersten Zugriff (Linux: die unberuehrte private Seite sieht sie, eine schon kopierte nicht). Erwartung: Linux 6.12. */
#define _GNU_SOURCE
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/wait.h>
#include <unistd.h>

#define PG 4096
#define LEN (3 * PG + 100)

static unsigned char want(long i) { return (unsigned char)(i * 7 + i / PG); }

static int check(const unsigned char *m, long from, long to)
{
    for (long i = from; i < to; i++) {
        if (m[i - from] != want(i)) return 0;
    }
    return 1;
}

static int make(const char *name)
{
    static unsigned char buf[LEN];
    for (long i = 0; i < LEN; i++) buf[i] = want(i);
    int fd = open(name, O_RDWR | O_CREAT | O_TRUNC, 0644);
    if (fd < 0 || write(fd, buf, LEN) != LEN) return -1;
    return fd;
}

int main(void)
{
    int fd = make("f");
    printf("Datei angelegt=%d\n", fd >= 0);
    unsigned char *m = mmap(0, 4 * PG, PROT_READ | PROT_WRITE, MAP_PRIVATE, fd, 0);
    printf("mmap privat=%d\n", m != MAP_FAILED);
    if (m == MAP_FAILED) return 1;

    /* Seite 2 noch nicht beruehren: die Datei aendert sich vorher. */
    unsigned char x = 0xEE;
    printf("pwrite Seite 2=%d\n", (int)pwrite(fd, &x, 1, 2 * PG + 5));
    printf("Seite 0 Inhalt=%d\n", check(m, 0, PG));
    printf("Seite 3 Rest=%d\n", check(m + 3 * PG, 3 * PG, LEN));
    int zero = 1;
    for (long i = LEN; i < 4 * PG; i++) zero &= m[i - 0] == 0;
    printf("hinter Dateiende Nullen=%d\n", zero);
    printf("Seite 2 sieht pwrite=%d\n", m[2 * PG + 5] == 0xEE);

    m[PG + 1] = 0x55;
    unsigned char y = 0;
    pread(fd, &y, 1, PG + 1);
    printf("Schreiben privat=%d Datei unveraendert=%d\n", m[PG + 1] == 0x55, y == want(PG + 1));
    x = 0x11;
    pwrite(fd, &x, 1, PG + 2);
    printf("kopierte Seite sieht pwrite nicht=%d\n", m[PG + 2] == want(PG + 2));

    /* Versatz: Seiten 1 und 2 der Datei, vor jedem Zugriff fork, close und unlink. */
    unsigned char *o = mmap(0, 2 * PG, PROT_READ | PROT_WRITE, MAP_PRIVATE, fd, PG);
    printf("mmap Versatz=%d\n", o != MAP_FAILED);
    pid_t kid = fork();
    if (kid == 0) {
        int ok = o[3] == want(PG + 3) && o[PG + 7] == want(2 * PG + 7);
        o[0] = 0x77;
        m[3 * PG] = 0x66;
        _exit(ok ? 0 : 3);
    }
    int st = -1;
    waitpid(kid, &st, 0);
    printf("Kind liest ungelesene Seiten=%d\n", WIFEXITED(st) && WEXITSTATUS(st) == 0);
    printf("Kind schreibt nicht zurueck=%d %d\n", o[0] == want(PG), m[3 * PG] == want(3 * PG));
    close(fd);
    unlink("f");
    printf("nach close und unlink=%d\n", o[PG + 9] == want(2 * PG + 9));

    printf("munmap Mitte=%d\n", munmap(m + PG, PG));
    printf("Nachbarn bleiben=%d %d\n", check(m, 0, 16), m[2 * PG] == want(2 * PG));
    m[3] = 0x99;
    printf("MADV_DONTNEED=%d\n", madvise(m, PG, MADV_DONTNEED));
    printf("Seite wieder aus Datei=%d\n", m[3] == want(3));
    printf("munmap Rest=%d %d %d\n", munmap(m, PG), munmap(m + 2 * PG, 2 * PG), munmap(o, 2 * PG));
    return 0;
}
