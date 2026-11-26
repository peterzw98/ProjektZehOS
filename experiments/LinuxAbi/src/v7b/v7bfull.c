/* V7b: Platte voll, bevor das Kontingent erreicht ist (Gastlauf mit Fülldatei im Seed, experiments/LinuxAbi/run-guest-full.py):
 * write endet mit -ENOSPC (nicht -EIO), die Länge stimmt mit dem Geschriebenen überein, nach dem Löschen ist wieder Platz.
 * Ohne Fülldatei endet das Schreiben am Kontingent, ebenfalls mit -ENOSPC; "unter 64 MiB" zeigt, welcher Fall eintrat. Nur im Gast. */
#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#include "check.h"

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    static char chunk[MiB];
    memset(chunk, 'f', sizeof chunk);
    int fd = open("fill", O_CREAT | O_RDWR | O_TRUNC, 0644);
    OK("open fill", fd);
    long total = 0, r;
    int err = 0;
    for (;;) {
        r = write(fd, chunk, sizeof chunk);
        if (r < 0) { err = errno; break; }
        total += r;
    }
    struct stat st;
    fstat(fd, &st);
    printf("geschrieben MiB=%ld rest=%ld unter 64 MiB=%d\n", total / MiB, total % MiB, total < 64 * MiB);
    report("ende mit ENOSPC", -1, err, err == ENOSPC);
    TRUE("size == geschrieben", (long)st.st_size == total);
    ERR("write again", write(fd, chunk, 1), ENOSPC);
    EQ("lseek +8 MiB", lseek(fd, total + 8 * MiB, SEEK_SET), total + 8 * MiB);
    ERR("write hinter Lücke", write(fd, chunk, 1), ENOSPC);
    fstat(fd, &st);
    TRUE("size unverändert", (long)st.st_size == total);
    EQ("close", close(fd), 0);
    EQ("unlink fill", unlink("fill"), 0);
    fd = open("after", O_CREAT | O_RDWR | O_TRUNC, 0644);
    EQ("write after unlink", write(fd, chunk, MiB), MiB);
    EQ("close", close(fd), 0);
    EQ("unlink after", unlink("after"), 0);
    printf("fehler=%d\n", fails);
    return fails ? 1 : 0;
}
