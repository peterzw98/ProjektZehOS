/* toybox wc: Zeilen, Wörter, Bytes einer Datei und der leeren Standardeingabe. */
#include "tb_common.h"
int main(int argc, char **argv)
{
    (void)argc;
    char p[4096];
    tb_path(argv[0], "wc", p, sizeof p);
    tb_file("in.txt", "eins zwei\ndrei\n\nvier fuenf sechs\n");
    char *args[] = {p, "in.txt", "-", NULL};
    execv(p, args);
    perror("execv");
    return 2;
}
