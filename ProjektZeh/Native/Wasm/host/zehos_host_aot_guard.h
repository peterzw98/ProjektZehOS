// ZehOS: Marke des laufenden AOT-Laufs (Roadmap 13.5, #423; Abbau nach CPU-Ausnahme #582).
// Eine isolierte CPU-Ausnahme gibt den Wasm-Worker auf (CosmosFaultIsolation.cs): zehos_wasm_run_aot kehrt nie zurueck,
// die Marke bleibt gesetzt. Lag die Ausnahme im AOT-Code, baut die Hauptschleife den Lauf ueber zehos_wasm_aot_reclaim ab und
// loescht sie. Sonst bleibt WAMRs Laufzeitzustand (Registrierungen, Instanz, Referenzzaehler) halb benutzt stehen; der
// naechste AOT-Lauf baute darauf auf, deshalb laufen Apps dann bis zum Neustart unter wasm3.
// Es laeuft hoechstens ein AOT-Lauf zur Zeit (ein Worker). Der Ersatzworker kann auf einer anderen CPU laufen, daher atomar.

#ifndef ZEHOS_HOST_AOT_GUARD_H
#define ZEHOS_HOST_AOT_GUARD_H

typedef struct { int running; } zehos_aot_guard;

// 0, wenn ein Lauf begonnen und nicht abgebaut wurde; die Marke bleibt dann gesetzt.
static inline int zehos_aot_guard_enter(zehos_aot_guard* guard)
{
    return __atomic_exchange_n(&guard->running, 1, __ATOMIC_ACQ_REL) == 0;
}

// Nur nach einem erfolgreichen enter, sobald der WAMR-Zustand des Laufs abgebaut ist.
static inline void zehos_aot_guard_leave(zehos_aot_guard* guard)
{
    __atomic_store_n(&guard->running, 0, __ATOMIC_RELEASE);
}

// 1, solange ein Lauf begonnen und nicht abgebaut ist.
static inline int zehos_aot_guard_running(zehos_aot_guard* guard)
{
    return __atomic_load_n(&guard->running, __ATOMIC_ACQUIRE) != 0;
}

#endif
