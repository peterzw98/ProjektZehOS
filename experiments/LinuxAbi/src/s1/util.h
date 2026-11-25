/* Gemeinsame Hilfen für S1. Jedes Programm schreibt nur deterministischen Text
 * nach stdout (kein PID, keine Adressen, keine Uhrzeiten, keine Pfade des
 * Arbeitsverzeichnisses). Ungepuffert, damit die Ausgabe auch bei einem Signal
 * vollständig ist. */
#ifndef LINUXABI_UTIL_H
#define LINUXABI_UTIL_H
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>

#define INIT() setvbuf(stdout, NULL, _IONBF, 0)
#define SHOW(name, cond) printf("%s=%d\n", (name), (cond) ? 1 : 0)
#define SHOWI(name, val) printf("%s=%lld\n", (name), (long long)(val))
/* Bricht mit Exitcode 2 ab, wenn eine Voraussetzung des Programms selbst fehlt;
 * das ist dann auch im Vergleich ein Unterschied. */
#define NEED(cond) do { if (!(cond)) { printf("NEED fehlgeschlagen: %s Zeile %d errno=%d\n", #cond, __LINE__, errno); exit(2); } } while (0)
#endif
