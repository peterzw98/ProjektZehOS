/* Verzeichnisse: mkdir, rmdir, getdents64 (über readdir), getcwd, chdir, rename, Fehlerwege. */
#include "util.h"
#include <dirent.h>
#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

static int cmp(const void *a, const void *b) { return strcmp(*(char *const *)a, *(char *const *)b); }

static void list(const char *path)
{
    DIR *d = opendir(path);
    NEED(d != NULL);
    char *names[32]; int n = 0;
    struct dirent *e;
    while ((e = readdir(d)) != NULL && n < 32) names[n++] = strdup(e->d_name);
    closedir(d);
    qsort(names, n, sizeof *names, cmp);
    printf("list %s:", path);
    for (int i = 0; i < n; i++) printf(" %s", names[i]);
    printf("\n");
}

int main(void)
{
    INIT();
    char cwd[512];
    NEED(getcwd(cwd, sizeof cwd) != NULL);
    SHOW("cwd absolute", cwd[0] == '/');
    list(".");
    SHOWI("mkdir d", mkdir("d", 0755));
    SHOWI("mkdir d again", mkdir("d", 0755)); SHOWI("errno", errno);
    SHOWI("mkdir d/e/f", mkdir("d/e/f", 0755)); SHOWI("errno", errno);
    SHOWI("mkdir d/e", mkdir("d/e", 0755));
    for (const char *n = "bca"; *n; n++) {
        char p[8] = { 'd', '/', *n, '.', 't', 0 };
        int fd = open(p, O_CREAT | O_WRONLY, 0644);
        NEED(fd >= 0);
        close(fd);
    }
    list("d");
    SHOWI("rmdir non-empty", rmdir("d")); SHOWI("errno", errno);
    SHOWI("rename d/a.t d/e/z.t", rename("d/a.t", "d/e/z.t"));
    list("d"); list("d/e");
    SHOWI("chdir d/e", chdir("d/e"));
    char in[512];
    NEED(getcwd(in, sizeof in) != NULL);
    SHOW("cwd suffix /d/e", strlen(in) > 4 && strcmp(in + strlen(in) - 4, "/d/e") == 0);
    SHOWI("chdir ../..", chdir("../.."));
    SHOWI("chdir file", chdir("d/b.t")); SHOWI("errno", errno);
    SHOWI("opendir file", opendir("d/b.t") == NULL); SHOWI("errno", errno);
    struct stat st;
    SHOWI("stat d", stat("d", &st)); SHOW("is dir", S_ISDIR(st.st_mode));
    SHOWI("unlink dir", unlink("d")); SHOWI("errno", errno);
    SHOWI("unlink files", unlink("d/b.t") | unlink("d/c.t") | unlink("d/e/z.t"));
    SHOWI("rmdir d/e", rmdir("d/e"));
    SHOWI("rmdir d", rmdir("d"));
    SHOWI("rmdir again", rmdir("d")); SHOWI("errno", errno);
    list(".");
    return 0;
}
