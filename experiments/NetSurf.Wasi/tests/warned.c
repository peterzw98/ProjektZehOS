// Prüft die Tabelle gewarnter Formularziele nativ mit kleiner Größe: Kein gemerktes Ziel darf verdrängt werden, und
// eine volle Tabelle behandelt jedes Ziel als gewarnt (fail-closed). build.py übersetzt mit -DWARNED_MAX=2.
#include <assert.h>
#include <stdio.h>

#include "../frontend/warned_targets.h"

int main(void)
{
	assert(WARNED_MAX == 2);
	assert(!warned_contains("http://a.example/ziel?feld=1"));

	warned_add("http://a.example/ziel?feld=1");
	assert(warned_contains("http://a.example/ziel?feld=1"));
	assert(!warned_contains("http://b.example/ziel?feld=2"));

	// Doppeltes Eintragen belegt keinen weiteren Platz.
	warned_add("http://a.example/ziel?feld=1");
	assert(warned.count == 1);

	// Jetzt voll: das erste Ziel bleibt gemerkt, auch nach weiteren Warnungen.
	warned_add("http://b.example/ziel?feld=2");
	assert(warned.count == WARNED_MAX);
	warned_add("http://c.example/ziel?feld=3");
	assert(warned.count == WARNED_MAX);
	assert(warned_contains("http://a.example/ziel?feld=1"));
	assert(warned_contains("http://b.example/ziel?feld=2"));

	// Fail-closed: auch nie gesehene Ziele gelten als gewarnt.
	assert(warned_contains("http://d.example/neu"));

	puts("warned_targets: ok");
	return 0;
}
