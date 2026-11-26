/* Ladybird V3, Gegenprobe: ein Programm ohne Systemaufruf in einer Endlosschleife. Der Kernel beendet es nach dem
 * Zeitlimit (signal 9), nicht umgekehrt. Nicht in run-linux.py vergleichbar (läuft dort bis zur Zeitüberschreitung). */
#include <unistd.h>

int main(void)
{
    write(1, "dreht\n", 6);
    for (volatile unsigned long i = 0;; i++) {
    }
}
