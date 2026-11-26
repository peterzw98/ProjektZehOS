/* V7b: FAT findet Einträge auch über den 8.3-Kurznamen. Über Linux darf nur der eigene Name treffen (-ENOENT), und Anlegen unter dem
 * Kurznamen eines vorhandenen Eintrags ist -EEXIST statt den vorhandenen zu treffen. Ohne diese Prüfung (Stand vor PR #741) öffnet
 * "ABCDEF~1.TXT" die Datei "abcdefghij.txt", und O_TRUNC oder unlink darüber zerstört sie. Nur im Gast (check.h). */
#include <dirent.h>
#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#include "check.h"

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    struct stat st;
    char buf[64];
    int fd = open("abcdefghij.txt", O_CREAT | O_EXCL | O_WRONLY, 0644);
    OK("create long", fd);
    EQ("write", write(fd, "x", 1), 1);
    close(fd);
    ERR("open ABCDEF~1.TXT", open("ABCDEF~1.TXT", O_RDONLY), ENOENT);
    ERR("open abcdef~1.txt", open("abcdef~1.txt", O_RDONLY), ENOENT);
    ERR("stat ABCDEF~1.TXT", stat("ABCDEF~1.TXT", &st), ENOENT);
    ERR("lstat ABCDEF~1.TXT", lstat("ABCDEF~1.TXT", &st), ENOENT);
    ERR("access ABCDEF~1.TXT", access("ABCDEF~1.TXT", F_OK), ENOENT);
    ERR("readlink ABCDEF~1.TXT", readlink("ABCDEF~1.TXT", buf, sizeof buf), ENOENT);
    ERR("unlink ABCDEF~1.TXT", unlink("ABCDEF~1.TXT"), ENOENT);
    ERR("create ABCDEF~1.TXT excl", open("ABCDEF~1.TXT", O_CREAT | O_EXCL | O_WRONLY, 0644), EEXIST);
    ERR("create ABCDEF~1.TXT trunc", open("ABCDEF~1.TXT", O_CREAT | O_TRUNC | O_WRONLY, 0644), EEXIST);
    ERR("mkdir ABCDEF~1.TXT", mkdir("ABCDEF~1.TXT", 0755), EEXIST);
    ERR("symlink ABCDEF~1.TXT", symlink("ziel", "ABCDEF~1.TXT"), EEXIST);
    fd = open("other.txt", O_CREAT | O_WRONLY, 0644);
    close(fd);
    ERR("rename other ABCDEF~1.TXT", rename("other.txt", "ABCDEF~1.TXT"), EEXIST);
    /* vfat unter Linux würde hier umbenennen; ZehOS lehnt auch den eigenen Zweitnamen ab (PR #741, bekannte Abweichung). */
    ERR("rename long ABCDEF~1.TXT", rename("abcdefghij.txt", "ABCDEF~1.TXT"), EEXIST);
    EQ("stat long", stat("abcdefghij.txt", &st), 0);
    EQ("long size", st.st_size, 1);
    EQ("stat other", stat("other.txt", &st), 0);
    EQ("mkdir verzeichnislang", mkdir("verzeichnislang", 0755), 0);
    ERR("chdir VERZEI~1", chdir("VERZEI~1"), ENOENT);
    ERR("rmdir VERZEI~1", rmdir("VERZEI~1"), ENOENT);
    DIR *bad = opendir("VERZEI~1");
    TRUE("opendir VERZEI~1 scheitert", bad == NULL);
    if (bad) closedir(bad);
    ERR("open VERZEI~1/x create", open("VERZEI~1/x", O_CREAT | O_WRONLY, 0644), ENOENT);
    fd = open("verzeichnislang/x", O_CREAT | O_WRONLY, 0644);
    OK("open verzeichnislang/x create", fd);
    close(fd);
    /* Die Liste zeigt nur die eigenen Namen, keinen Kurznamen. */
    DIR *d = opendir(".");
    int seen = 0, shortname = 0;
    struct dirent *e;
    while (d && (e = readdir(d)) != NULL) {
        printf("eintrag %s\n", e->d_name);
        if (strchr(e->d_name, '~')) shortname++;
        if (!strcmp(e->d_name, "abcdefghij.txt") || !strcmp(e->d_name, "other.txt") || !strcmp(e->d_name, "verzeichnislang")) seen++;
    }
    if (d) closedir(d);
    TRUE("liste hat die drei Namen", seen == 3);
    TRUE("liste ohne Kurznamen", shortname == 0);
    EQ("unlink x", unlink("verzeichnislang/x"), 0);
    EQ("rmdir verzeichnislang", rmdir("verzeichnislang"), 0);
    EQ("unlink long", unlink("abcdefghij.txt"), 0);
    EQ("unlink other", unlink("other.txt"), 0);
    printf("fehler=%d\n", fails);
    return fails ? 1 : 0;
}
