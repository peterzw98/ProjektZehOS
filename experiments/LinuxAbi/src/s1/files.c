/* Dateien: openat, read, write, readv, writev, pread, lseek, fstat, unlink, rename, Fehlerwege. */
#include "util.h"
#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/uio.h>
#include <unistd.h>

int main(void)
{
    INIT();
    int fd = open("a.txt", O_CREAT | O_EXCL | O_RDWR, 0644);
    NEED(fd >= 0);
    SHOWI("write", write(fd, "hello ", 6));
    struct iovec w[2] = { { "wor", 3 }, { "ld\n", 3 } };
    SHOWI("writev", writev(fd, w, 2));
    struct stat st;
    NEED(fstat(fd, &st) == 0);
    SHOWI("size", st.st_size);
    SHOW("regular", S_ISREG(st.st_mode));
    SHOWI("mode", st.st_mode & 07777); /* umask 022 setzt der Vergleicher */
    SHOWI("nlink", st.st_nlink);
    SHOWI("lseek SET 6", lseek(fd, 6, SEEK_SET));
    char buf[16] = { 0 };
    SHOWI("read", read(fd, buf, 5));
    printf("data=%s\n", buf);
    char b1[4] = { 0 }, b2[4] = { 0 };
    struct iovec r[2] = { { b1, 3 }, { b2, 3 } };
    lseek(fd, 0, SEEK_SET);
    SHOWI("readv", readv(fd, r, 2));
    printf("readv data=%s|%s\n", b1, b2);
    SHOWI("lseek END", lseek(fd, 0, SEEK_END));
    SHOWI("read at EOF", read(fd, buf, 5));
    SHOWI("lseek beyond", lseek(fd, 100, SEEK_SET));
    SHOWI("write beyond", write(fd, "x", 1));
    NEED(fstat(fd, &st) == 0);
    SHOWI("size after hole", st.st_size);
    SHOWI("close", close(fd));
    SHOWI("close again", close(fd)); SHOWI("errno", errno);
    SHOWI("open excl again", open("a.txt", O_CREAT | O_EXCL | O_RDWR, 0644)); SHOWI("errno", errno);
    SHOWI("open missing", open("fehlt.txt", O_RDONLY)); SHOWI("errno", errno);
    SHOWI("access ok", access("a.txt", R_OK | W_OK));
    SHOWI("access missing", access("fehlt.txt", F_OK)); SHOWI("errno", errno);
    SHOWI("rename", rename("a.txt", "b.txt"));
    SHOWI("stat old", stat("a.txt", &st)); SHOWI("errno", errno);
    SHOWI("stat new", stat("b.txt", &st)); SHOWI("size", st.st_size);
    SHOWI("symlink", symlink("b.txt", "l.txt"));
    char link[32] = { 0 };
    SHOWI("readlink", readlink("l.txt", link, sizeof link - 1));
    printf("link=%s\n", link);
    SHOWI("lstat", lstat("l.txt", &st)); SHOW("is link", S_ISLNK(st.st_mode));
    SHOWI("stat via link", stat("l.txt", &st)); SHOW("is regular", S_ISREG(st.st_mode));
    SHOWI("O_NOFOLLOW link", open("l.txt", O_RDONLY | O_NOFOLLOW)); SHOWI("errno", errno);
    SHOWI("unlink link", unlink("l.txt"));
    SHOWI("unlink", unlink("b.txt"));
    SHOWI("unlink again", unlink("b.txt")); SHOWI("errno", errno);
    SHOWI("open dir for write", open(".", O_WRONLY)); SHOWI("errno", errno);
    int dn = open("/dev/null", O_RDWR);
    NEED(dn >= 0);
    SHOWI("/dev/null write", write(dn, "abc", 3));
    SHOWI("/dev/null read", read(dn, buf, 3));
    close(dn);
    return 0;
}
