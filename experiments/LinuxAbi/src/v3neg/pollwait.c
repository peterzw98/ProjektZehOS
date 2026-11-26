/* Ladybird V3, Gegenprobe zum Review P2: poll ohne Deskriptoren mit fast unendlicher Frist schläft im Kernel. Das Zeitlimit
 * muss den Schlaf beenden und das Programm mit signal 9 abbrechen; "zurueck" darf nie erscheinen. Nicht in run-linux.py
 * vergleichbar (schläft dort rund 24 Tage). */
#include <poll.h>
#include <unistd.h>

int main(void)
{
    write(1, "wartet\n", 7);
    poll(0, 0, 0x7fffffff);
    write(1, "zurueck\n", 8);
    return 0;
}
