/* toybox sh (S3: fork, execve, pipe, wait): Pipeline aus drei Programmen, Umleitung, Unterschale,
 * Exitcodes, Variablen, Schleife. Die Programme findet toysh über PATH (tb/ mit den Verknüpfungen). */
#include "tb_common.h"
int main(int argc, char **argv)
{
    (void)argc;
    char p[4096], dir[4096];
    tb_path(argv[0], "sh", p, sizeof p);
    tb_path(argv[0], "", dir, sizeof dir);
    setenv("PATH", dir, 1);
    const char *script =
        "echo eins zwei drei | wc -w\n"
        "echo abc > out.txt; cat out.txt out.txt | wc -c\n"
        "(exit 3); echo status=$?\n"
        "x=ok; for i in 1 2 3; do echo $x$i; done\n"
        "cat fehlt.txt 2>/dev/null || echo fehlgeschlagen\n"
        "ls -1 | wc -l\n"
        "exit 7\n";
    char *args[] = {p, "-c", (char *)script, NULL};
    execv(p, args);
    perror("execv");
    return 2;
}
