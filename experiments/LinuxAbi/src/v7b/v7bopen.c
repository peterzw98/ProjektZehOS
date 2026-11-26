/* V7b: entfernte, noch offene Dateien und Ordner-rename mit offenen Kindern (PR #741, Delta-Review P2-B/P2-C). Cosmos lässt den Namen einer
 * entfernten, offenen Datei bis zum letzten Schließen stehen; DirectoryFileStore blendet ihn aus (-ENOENT), hält ihn aber für Anlegen fest
 * (-EEXIST, Abweichung von Linux) und benennt Ordner mit offenen oder entfernten, offenen Kindern nicht um (-EBUSY, Abweichung von Linux).
 * Nur im Gast (check.h). */
#include <dirent.h>
#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#include "check.h"

/* 1, wenn der Name in der Liste des Ordners steht. */
static int listed(const char *dir, const char *name)
{
    DIR *d = opendir(dir);
    if (!d) return -1;
    struct dirent *e;
    int found = 0;
    while ((e = readdir(d)) != NULL)
        if (strcmp(e->d_name, name) == 0) found = 1;
    closedir(d);
    return found;
}

int main(void)
{
    setvbuf(stdout, NULL, _IONBF, 0);
    struct stat st;
    char buf[8] = {0};

    /* Teil 1: entfernte, offene Datei (P2-B). */
    EQ("mkdir d", mkdir("d", 0755), 0);
    int fd = open("d/k", O_CREAT | O_RDWR | O_TRUNC, 0644);
    OK("open d/k", fd);
    EQ("write d/k", write(fd, "abcd", 4), 4);
    EQ("unlink d/k", unlink("d/k"), 0);
    ERR("stat d/k (entfernt)", stat("d/k", &st), ENOENT);
    ERR("open d/k (entfernt)", open("d/k", O_RDONLY), ENOENT);
    ERR("unlink d/k (zweites Mal)", unlink("d/k"), ENOENT);
    ERR("rename d/k (entfernt)", rename("d/k", "d/k2"), ENOENT);
    EQ("listed d/k", listed("d", "k"), 0);
    ERR("open O_EXCL d/k (Cosmos führt Namen noch)", open("d/k", O_CREAT | O_EXCL | O_RDWR, 0644), EEXIST);
    ERR("mkdir d/k (Cosmos führt Namen noch)", mkdir("d/k", 0755), EEXIST);
    ERR("rmdir d (entferntes, offenes Kind)", rmdir("d"), ENOTEMPTY);
    EQ("fstat size", fstat(fd, &st) == 0 ? (long)st.st_size : -1, 4);
    EQ("pread d/k", pread(fd, buf, 4, 0), 4);
    TRUE("inhalt abcd", memcmp(buf, "abcd", 4) == 0);
    EQ("write nach unlink", write(fd, "ef", 2), 2);

    /* Teil 2: Ordner mit entferntem, offenem Kind (P2-C). */
    EQ("mkdir e", mkdir("e", 0755), 0);
    ERR("rename d -> e2 (entferntes, offenes Kind)", rename("d", "e2"), EBUSY);
    EQ("stat d unverändert", stat("d", &st), 0);
    ERR("stat e2", stat("e2", &st), ENOENT);
    EQ("close d/k", close(fd), 0);
    EQ("listed d/k nach close", listed("d", "k"), 0);
    fd = open("d/k", O_CREAT | O_EXCL | O_RDWR, 0644);
    OK("open O_EXCL d/k nach close", fd);
    EQ("fstat neu leer", fstat(fd, &st) == 0 ? (long)st.st_size : -1, 0);
    EQ("close neu", close(fd), 0);
    EQ("unlink d/k neu", unlink("d/k"), 0);
    EQ("rename d -> e2 nach close", rename("d", "e2"), 0);
    EQ("rmdir e2", rmdir("e2"), 0);

    /* Teil 3: offenes Kind, auch tiefer (Enkel), Geschwister mit gleichem Präfix. */
    EQ("mkdir e/x", mkdir("e/x", 0755), 0);
    EQ("mkdir e/x/y", mkdir("e/x/y", 0755), 0);
    EQ("mkdir ex", mkdir("ex", 0755), 0);
    fd = open("e/x/y/g", O_CREAT | O_RDWR | O_TRUNC, 0644);
    OK("open e/x/y/g", fd);
    ERR("rename e -> f (Enkel offen)", rename("e", "f"), EBUSY);
    ERR("rename e/x -> e/z (Kind offen)", rename("e/x", "e/z"), EBUSY);
    EQ("rename ex -> ey (gleiches Präfix, nicht betroffen)", rename("ex", "ey"), 0);
    EQ("rename e/x/y/g -> e/x/y/h (offene Datei selbst)", rename("e/x/y/g", "e/x/y/h"), 0);
    EQ("write nach rename", write(fd, "zz", 2), 2);
    EQ("close e/x/y/h", close(fd), 0);
    EQ("stat e/x/y/h size", stat("e/x/y/h", &st) == 0 ? (long)st.st_size : -1, 2);
    EQ("rename e -> f nach close", rename("e", "f"), 0);
    EQ("stat f/x/y/h", stat("f/x/y/h", &st), 0);

    /* Aufräumen, damit spätere Programme (v7bquota) den Platz haben. */
    EQ("unlink f/x/y/h", unlink("f/x/y/h"), 0);
    EQ("rmdir f/x/y", rmdir("f/x/y"), 0);
    EQ("rmdir f/x", rmdir("f/x"), 0);
    EQ("rmdir f", rmdir("f"), 0);
    EQ("rmdir ey", rmdir("ey"), 0);
    printf("fails=%d\n", fails);
    return fails ? 1 : 0;
}
