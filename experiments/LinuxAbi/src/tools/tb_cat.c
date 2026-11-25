/* toybox cat: Datei mit Zeilennummern, mehrere Dateien, Standardeingabe (leer, "-"), fehlende Datei. */
#include "tb_common.h"
int main(int argc, char **argv)
{
    (void)argc;
    char p[4096];
    tb_path(argv[0], "cat", p, sizeof p);
    tb_file("in.txt", "eins\nzwei\n\ndrei ohne Zeilenende");
    tb_file("zwei.txt", "x\ny\n");
    /* Fehler und Ausgabe landen beide in der Ausgabe; der Exitcode ist 1 wegen der fehlenden Datei. */
    char *args[] = {p, "-", "in.txt", "zwei.txt", "fehlt.txt", NULL};
    execv(p, args);
    perror("execv");
    return 2;
}
