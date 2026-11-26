/* V7b: Kontingent je Wurzel (64 MiB Nettowachstum, DirectoryFileStore.DefaultQuotaBytes), Wachstum in Schritten, Freigabe durch Löschen
 * und Kürzen; ftruncate nach oben (V14) zählt wie eine Lücke. Was andere Programme vorher hinterlassen haben, verkleinert den Platz; die
 * Prüfungen rechnen deshalb mit dem tatsächlich geschriebenen Platz statt mit genau 64 MiB. Nur im Gast (check.h). */
#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#include "check.h"

static long size_of(int fd) { struct stat st; return fstat(fd, &st) == 0 ? (long)st.st_size : -1; }

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    static char chunk[MiB];
    memset(chunk, 'q', sizeof chunk);
    int fd = open("big", O_CREAT | O_RDWR | O_TRUNC, 0644);
    OK("open big", fd);
    ERR("ftruncate 65 MiB (über dem Kontingent)", ftruncate(fd, 65 * MiB), ENOSPC);
    EQ("size", size_of(fd), 0);
    EQ("lseek 65 MiB", lseek(fd, 65 * MiB, SEEK_SET), 65 * MiB);
    ERR("write at 65 MiB", write(fd, "x", 1), ENOSPC);
    EQ("size", size_of(fd), 0);
    lseek(fd, 0, SEEK_SET);
    long total = 0, r;
    int err = 0;
    for (;;) {
        r = write(fd, chunk, sizeof chunk);
        if (r < 0) { err = errno; break; }
        total += r;
    }
    printf("geschrieben MiB=%ld rest=%ld\n", total / MiB, total % MiB);
    TRUE("ende mit ENOSPC", err == ENOSPC);
    TRUE("geschrieben zwischen 40 und 64 MiB", total >= 40 * MiB && total <= 64 * MiB);
    EQ("size", size_of(fd), total);
    char c = 0;
    EQ("pread last", pread(fd, &c, 1, total - 1), 1);
    TRUE("last byte q", c == 'q');
    EQ("close", close(fd), 0);
    fd = open("small", O_CREAT | O_RDWR | O_TRUNC, 0644);
    ERR("write small (Kontingent voll)", write(fd, chunk, 1), ENOSPC);
    EQ("unlink big", unlink("big"), 0);
    EQ("write small (nach unlink)", write(fd, chunk, MiB), MiB);
    EQ("ftruncate 32 MiB", ftruncate(fd, 32 * MiB), 0);
    EQ("size", size_of(fd), 32 * MiB);
    EQ("position bleibt", lseek(fd, 0, SEEK_CUR), MiB);
    EQ("lseek 40 MiB", lseek(fd, 40 * MiB, SEEK_SET), 40 * MiB);
    EQ("write at 40 MiB (Lücke 8 MiB)", write(fd, "y", 1), 1);
    EQ("size", size_of(fd), 40 * MiB + 1);
    /* Belegt: 40 MiB + 1; die 30 MiB bis 70 MiB passen nicht mehr in den Rest (total - 40 MiB - 1, total höchstens 64 MiB). */
    lseek(fd, 70 * MiB, SEEK_SET);
    ERR("write at 70 MiB", write(fd, "y", 1), ENOSPC);
    EQ("size", size_of(fd), 40 * MiB + 1);
    c = 1;
    EQ("pread in ftruncate-Lücke", pread(fd, &c, 1, 20 * MiB), 1);
    TRUE("ftruncate-Lücke 0", c == 0);
    c = 1;
    EQ("pread in write-Lücke", pread(fd, &c, 1, 36 * MiB), 1);
    TRUE("write-Lücke 0", c == 0);
    EQ("ftruncate 2 MiB (gibt frei)", ftruncate(fd, 2 * MiB), 0);
    EQ("pwrite at 30 MiB (nach Freigabe)", pwrite(fd, "z", 1, 30 * MiB), 1);
    EQ("size", size_of(fd), 30 * MiB + 1);
    EQ("close", close(fd), 0);
    EQ("unlink small", unlink("small"), 0);
    printf("fehler=%d\n", fails);
    return fails ? 1 : 0;
}
