/* Symbolische Links: symlink, readlink, lstat, stat ueber den Link, O_NOFOLLOW (ELOOP). Braucht ein Dateisystem mit Symlinks:
   das FAT-Datenlaufwerk des Gastes liefert -EPERM wie vfat unter Linux, deshalb laeuft dieses Programm nicht im Buendel von test-linux-all. */
#include "util.h"
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

int main(void)
{
    INIT();
    int fd = open("b.txt", O_CREAT | O_EXCL | O_RDWR, 0644);
    NEED(fd >= 0);
    NEED(write(fd, "hello", 5) == 5);
    close(fd);
    struct stat st;
    SHOWI("symlink", symlink("b.txt", "l.txt"));
    char link[32] = { 0 };
    SHOWI("readlink", readlink("l.txt", link, sizeof link - 1));
    printf("link=%s\n", link);
    SHOWI("lstat", lstat("l.txt", &st)); SHOW("is link", S_ISLNK(st.st_mode));
    SHOWI("stat via link", stat("l.txt", &st)); SHOW("is regular", S_ISREG(st.st_mode));
    SHOWI("O_NOFOLLOW link", open("l.txt", O_RDONLY | O_NOFOLLOW)); SHOWI("errno", errno);
    SHOWI("unlink link", unlink("l.txt"));
    SHOWI("unlink", unlink("b.txt"));
    return 0;
}
