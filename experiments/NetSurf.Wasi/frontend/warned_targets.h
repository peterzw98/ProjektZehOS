/*
 * Ziel-URLs bereits gewarnter Formulare (HTTPS-Seite an http://-Ziel), ohne NetSurf-Abhängigkeit, damit
 * tests/warned.c das Verhalten nativ prüfen kann. build.py kopiert die Datei neben content/fetchers/zehos_http.c.
 *
 * NetSurf ruft solche Ziele ohne Formularmarke erneut ab, etwa "Zurück" auf einer Fehlerseite
 * (browser_window__reload_current_parameters mit den alten Parametern samt HTTPS-Referer) oder "Erneut versuchen".
 * Jeder solche Abruf mit HTTPS-Referer bekommt wieder die Warnung; bestätigt wird nur von der data:-Warnseite aus.
 * Ohne Frist, weil NetSurf die URL ebenso lange in current_parameters hält. Einträge werden nie verdrängt, weil ein
 * Frame sein Ziel noch in current_parameters halten könnte. Ist die Tabelle voll, gilt jedes Ziel als gewarnt
 * (fail-closed). Gespeichert wird nur ein Hash: Eintragen braucht so keinen Speicher und kann auch bei Speichermangel
 * nicht scheitern, eine Kollision bewirkt nur eine Warnung mehr. WARNED_MAX ist für Tests per -D überschreibbar;
 * normale Builds nutzen 256 (2 KiB).
 */
#ifndef ZEHOS_WARNED_TARGETS_H
#define ZEHOS_WARNED_TARGETS_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifndef WARNED_MAX
#define WARNED_MAX 256
#endif

static struct {
	uint64_t hash[WARNED_MAX];
	size_t count;
} warned;

/* FNV-1a, 64 Bit; kein Schutz gegen gezielte Kollisionen nötig, weil eine Kollision nur zusätzlich warnt. */
static uint64_t url_hash(const char *url_s)
{
	uint64_t hash = 14695981039346656037u;
	for (; *url_s != '\0'; url_s++) hash = (hash ^ (unsigned char)*url_s) * 1099511628211u;
	return hash;
}

static bool warned_contains(const char *url_s)
{
	if (warned.count == WARNED_MAX) return true; /* voll: fail-closed */
	uint64_t hash = url_hash(url_s);
	for (size_t i = 0; i < warned.count; i++) {
		if (warned.hash[i] == hash) return true;
	}
	return false;
}

static void warned_add(const char *url_s)
{
	if (warned_contains(url_s)) return; /* bekannt oder Tabelle voll */
	warned.hash[warned.count++] = url_hash(url_s);
}

#endif
