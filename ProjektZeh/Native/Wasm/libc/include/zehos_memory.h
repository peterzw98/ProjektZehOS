// Interne Laufarena: kein Wasm-Import und noch keine automatische Aktivierung im Worker.
#pragma once
#include <stddef.h>

// Der Rueckruf muss die aktuelle Scheduler-Thread-ID ohne Allokation oder Ausnahme liefern.
// limit == 0 misst nur; ein aktiver Lauf oder ein falscher Aufrufer wird mit 0 abgelehnt.
int zehos_memory_begin(size_t limit, int owner, int (*current_thread)(void));
// Nur nach dem Ende oder sicheren Aufgeben des Workers: auch verbliebene native Bloecke freigeben.
// Wiederholtes end ist erlaubt; peak/exceeded bleiben bis zum naechsten begin erhalten.
void zehos_memory_end(void);
size_t zehos_memory_used(void);
size_t zehos_memory_peak(void);
int zehos_memory_exceeded(void);
