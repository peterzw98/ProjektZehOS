// ZehOS: startet die Laufarena der C-Laufzeit (libc.c, zehos_memory.h) fuer den Lauf des aufrufenden Threads
// (Roadmap 12.4-I2-C). Eigene Datei statt zehos_host_core.c: Die nativen Hosttests binden zehos_host_core.c gegen die
// System-libc, die zehos_memory_begin nicht kennt.

#include <stddef.h>
#include <zehos_memory.h>

// CosmosFaultIsolation.CurrentThreadId: Scheduler-Id des laufenden Threads ohne Allokation, -1 ohne Scheduler.
extern int zehos_current_thread_id(void);

// Besitzer ist der aufrufende Thread (der Wasm-Worker vor zehos_wasm_run bzw. zehos_wasm_run_aot). Liefert 0 ohne
// Scheduler oder wenn noch eine Arena laeuft; der Lauf geht dann ohne Arena weiter.
int zehos_arena_begin(size_t limit)
{
    return zehos_memory_begin(limit, zehos_current_thread_id(), zehos_current_thread_id);
}
