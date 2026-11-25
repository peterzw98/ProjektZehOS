/* Gemeinsame Hilfen für S3. Wie bei S1: nur deterministischer Text nach stdout (keine
 * PIDs, Adressen, Ports, Uhrzeiten), ungepuffert. Kindprozesse und Threads schreiben
 * nichts; sie melden sich über Exitcode, Speicher oder Dateideskriptoren. Jedes Programm
 * setzt einen Wecker (SIGALRM), damit ein Hänger als Signal 14 auffällt statt zu warten. */
#ifndef LINUXABI_UTIL_H
#define LINUXABI_UTIL_H
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>

#define INIT() do { setvbuf(stdout, NULL, _IONBF, 0); alarm(30); } while (0)
#define SHOW(name, cond) printf("%s=%d\n", (name), (cond) ? 1 : 0)
#define SHOWI(name, val) printf("%s=%lld\n", (name), (long long)(val))
/* Aufruf mit Rückgabewert und errno (0 bei Erfolg); nur für Aufrufe mit deterministischem Ergebnis. */
#define T(label, expr) do { errno = 0; long r_ = (long)(expr); printf("%s: ret=%ld errno=%d\n", label, r_, errno); } while (0)
/* Bricht mit Exitcode 2 ab, wenn eine Voraussetzung des Programms selbst fehlt. */
#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)
/* Wie NEED, aber für Kinder: still mit Exitcode 3 (kein Text, da stdout geteilt ist). */
#define CNEED(cond) do { if (!(cond)) _exit(3); } while (0)
#endif
