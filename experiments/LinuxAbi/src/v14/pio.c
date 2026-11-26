/* V14: Schreiben an fester Stelle und Dateilänge. pwrite64, preadv/pwritev (Position bleibt), ftruncate (kürzen, verlängern mit Nullbytes,
 * Fehlerwege), fsync/fdatasync (Datei, Verzeichnis, Gerät, Terminal oder Pipe). Ausgabe nur Rückgabewerte, errno und Inhalte. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/uio.h>
#include <unistd.h>

#define T(label, expr) do { errno = 0; long r_ = (long)(expr); printf("%s: ret=%ld errno=%d\n", label, r_, r_ < 0 ? errno : 0); } while (0)

static long pos(int fd) { return (long)lseek(fd, 0, SEEK_CUR); }

static long size(int fd)
{
    struct stat st;
    return fstat(fd, &st) == 0 ? (long)st.st_size : -1;
}

/* Inhalt der Datei, Nullbytes als '.'. */
static void show(const char *label, const char *path)
{
    char buf[128];
    int fd = open(path, O_RDONLY);
    long n = fd >= 0 ? (long)read(fd, buf, sizeof buf) : -1;
    if (fd >= 0) close(fd);
    printf("%s: n=%ld [", label, n);
    for (long i = 0; i < n; i++) putchar(buf[i] ? buf[i] : '.');
    printf("]\n");
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    int fd = open("p.txt", O_CREAT | O_RDWR | O_TRUNC, 0644);
    T("write", write(fd, "abcdef", 6));
    T("pwrite 1", pwrite(fd, "XY", 2, 1));
    printf("pos=%ld\n", pos(fd));
    T("pwrite hinter Ende", pwrite(fd, "Z", 1, 9));
    printf("pos=%ld size=%ld\n", pos(fd), size(fd));
    show("inhalt", "p.txt");
    T("pwrite len 0", pwrite(fd, "Z", 0, 3));
    T("pwrite negativ", pwrite(fd, "Z", 1, -1));
    T("pwrite negativ, fd falsch", pwrite(99, "Z", 1, -1));
    T("pwrite fd falsch", pwrite(99, "Z", 1, 0));
    T("pwrite stdout", pwrite(1, "Z", 1, 0));
    T("pwrite bad buf", pwrite(fd, (void *)16, 1, 0));
    printf("pos=%ld\n", pos(fd));

    char a[3] = {0}, b[4] = {0};
    struct iovec rv[2] = {{a, 2}, {b, 3}};
    T("preadv 2", preadv(fd, rv, 2, 2));
    printf("a=%s b=%s pos=%ld\n", a, b, pos(fd));
    T("preadv hinter Ende", preadv(fd, rv, 2, 100));
    struct iovec wv[2] = {{"12", 2}, {"345", 3}};
    T("pwritev 4", pwritev(fd, wv, 2, 4));
    printf("pos=%ld size=%ld\n", pos(fd), size(fd));
    show("inhalt", "p.txt");
    T("pwritev 0 Eintraege", pwritev(fd, wv, 0, 0));
    T("pwritev negativ", pwritev(fd, wv, 2, -2));

    T("ftruncate 3", ftruncate(fd, 3));
    printf("pos=%ld size=%ld\n", pos(fd), size(fd));
    T("ftruncate 7", ftruncate(fd, 7));
    printf("pos=%ld size=%ld\n", pos(fd), size(fd));
    show("inhalt", "p.txt");
    T("write nach ftruncate", write(fd, "W", 1));
    printf("pos=%ld size=%ld\n", pos(fd), size(fd));
    show("inhalt", "p.txt");
    T("ftruncate negativ", ftruncate(fd, -1));
    T("ftruncate negativ, fd falsch", ftruncate(99, -1));
    T("ftruncate fd falsch", ftruncate(99, 0));
    T("ftruncate stdout", ftruncate(1, 0));
    T("ftruncate 0", ftruncate(fd, 0));
    printf("size=%ld\n", size(fd));
    T("fsync", fsync(fd));
    T("fdatasync", fdatasync(fd));
    T("close", close(fd));

    int ro = open("p.txt", O_RDONLY);
    T("ftruncate nur lesend", ftruncate(ro, 0));
    T("pwrite nur lesend", pwrite(ro, "Z", 1, 0));
    T("fsync nur lesend", fsync(ro));
    close(ro);

    int ap = open("p.txt", O_WRONLY | O_APPEND);
    T("write append", write(ap, "abc", 3));
    T("pwrite append 0", pwrite(ap, "Q", 1, 0));
    printf("pos=%ld\n", pos(ap));
    T("ftruncate append 2", ftruncate(ap, 2));
    close(ap);
    show("inhalt", "p.txt");

    T("mkdir", mkdir("d", 0755));
    int d = open("d", O_RDONLY | O_DIRECTORY);
    T("pwrite verzeichnis", pwrite(d, "Z", 1, 0));
    T("preadv verzeichnis", preadv(d, rv, 1, 0));
    T("ftruncate verzeichnis", ftruncate(d, 0));
    T("fsync verzeichnis", fsync(d));
    close(d);
    T("rmdir", rmdir("d"));

    int n = open("/dev/null", O_RDWR);
    T("pwrite /dev/null", pwrite(n, "Z", 1, 5));
    T("ftruncate /dev/null", ftruncate(n, 0));
    T("fsync /dev/null", fsync(n));
    close(n);
    T("fsync stdout", fsync(1));
    T("fdatasync fd falsch", fdatasync(99));
    T("unlink", unlink("p.txt"));
    return 0;
}
