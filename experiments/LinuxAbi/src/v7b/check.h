/* Gemeinsame Prüfhilfen für V7b. Die Programme laufen nur im ZehOS-Gast (FAT-Datenplatte mit Kontingent), nicht gegen eine
 * Linux-Erwartung: Sie prüfen sich selbst, jede Zeile endet bei einer Abweichung auf " FEHLER", und der Exitcode ist 1, sobald eine
 * Prüfung fehlschlägt. Ein Gastlauf braucht deshalb nur --status "<name>=exit 0". */
#ifndef V7B_CHECK_H
#define V7B_CHECK_H
#include <errno.h>
#include <stdio.h>

#define MiB (1L << 20)
static int fails;

static void report(const char *label, long r, int err, int ok)
{
    printf("%s=%ld errno=%d%s\n", label, r, err, ok ? "" : " FEHLER");
    if (!ok) fails++;
}

/* Erfolg mit genau diesem Wert. */
#define EQ(label, expr, want) do { errno = 0; long r_ = (long)(expr); int e_ = r_ < 0 ? errno : 0; report(label, r_, e_, r_ == (long)(want)); } while (0)
/* Erfolg mit einem Wert ab 0 (Deskriptor). */
#define OK(label, expr) do { errno = 0; long r_ = (long)(expr); int e_ = r_ < 0 ? errno : 0; report(label, r_, e_, r_ >= 0); } while (0)
/* Fehler -1 mit genau diesem errno. */
#define ERR(label, expr, want) do { errno = 0; long r_ = (long)(expr); int e_ = r_ < 0 ? errno : 0; report(label, r_, e_, r_ == -1 && e_ == (want)); } while (0)
/* Wahrheitswert ohne Aufruf. */
#define TRUE(label, cond) report(label, (cond) ? 1 : 0, 0, (cond))
#endif
