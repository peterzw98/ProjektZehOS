/* Ladybird V3, Gegenprobe: Schreiben auf eine nur lesbare Seite endet mit Status signal 11; die Ausgabe davor ist vollständig
 * und der Kernel läuft weiter. */
#include <stdio.h>
#include <unistd.h>

static const char text[] = "nur lesbar";

int main(void)
{
    static const char before[] = "vor dem Fehler\n";
    write(1, before, sizeof before - 1);
    *(volatile char *)text = 'X';
    write(1, "NICHT ERREICHT\n", 15);
    return 0;
}
