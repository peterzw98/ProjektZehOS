/* V7b: symlink auf dem FAT-Datenlaufwerk des Gastes. FAT kennt keine Symlinks; wie vfat unter Linux liefert symlink -EPERM und legt nichts an.
 * Das ist der Gastbeleg dafuer, dass src/s1/symlinks (nur Linux) aus dem Gast-Buendel herausfaellt (Tor N2-Z(1)). Nur im Gast (check.h). */
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>
#include "check.h"

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    struct stat st;
    char buf[16] = {0};

    int fd = open("b.txt", O_CREAT | O_EXCL | O_RDWR, 0644);
    OK("open b.txt", fd);
    EQ("write b.txt", write(fd, "hello", 5), 5);
    close(fd);

    ERR("symlink l.txt", symlink("b.txt", "l.txt"), EPERM);
    ERR("lstat l.txt (nicht angelegt)", lstat("l.txt", &st), ENOENT);
    ERR("readlink l.txt (fehlt)", readlink("l.txt", buf, sizeof buf), ENOENT);
    ERR("readlink b.txt (keine Verknuepfung)", readlink("b.txt", buf, sizeof buf), EINVAL);
    EQ("lstat b.txt", lstat("b.txt", &st), 0);
    TRUE("b.txt ist keine Verknuepfung", !S_ISLNK(st.st_mode));

    EQ("unlink b.txt", unlink("b.txt"), 0);
    return fails ? 1 : 0;
}
