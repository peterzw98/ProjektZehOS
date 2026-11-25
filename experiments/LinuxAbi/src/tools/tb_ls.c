/* toybox ls: Verzeichnis lesen (getdents), sortiert, versteckte Dateien, Unterverzeichnis, fehlender Pfad. */
#include "tb_common.h"
#include <sys/stat.h>
int main(int argc, char **argv)
{
    (void)argc;
    char p[4096];
    tb_path(argv[0], "ls", p, sizeof p);
    mkdir("sub", 0755);
    tb_file("b.txt", "b");
    tb_file("a.txt", "aa");
    tb_file(".versteckt", "");
    tb_file("sub/inner", "i");
    char *args[] = {p, "-1", "-A", "-p", ".", "sub", "fehlt", NULL};
    execv(p, args);
    perror("execv");
    return 2;
}
