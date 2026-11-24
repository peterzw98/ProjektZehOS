// ZehOS: Sperre der AOT-Laufzeit nach einer CPU-Ausnahme im AOT-Code (Roadmap 13.5, #423).
// Eine isolierte CPU-Ausnahme gibt den Wasm-Worker auf (CosmosFaultIsolation.cs): zehos_wasm_run_aot kehrt nie zurueck,
// WAMRs Laufzeitzustand (Registrierungen, Instanz, Stackgrenze) bleibt halb benutzt stehen. Der naechste AOT-Lauf
// baute darauf auf; deshalb laufen Apps danach bis zum Neustart unter wasm3.
// Es laeuft hoechstens ein AOT-Lauf zur Zeit (ein Worker). Der Ersatzworker kann auf einer anderen CPU laufen, daher atomar.

#ifndef ZEHOS_HOST_AOT_GUARD_H
#define ZEHOS_HOST_AOT_GUARD_H

typedef struct { int running; } zehos_aot_guard;

// 0, wenn der vorige Lauf sein Ende nie erreicht hat; die Marke bleibt dann fuer immer gesetzt.
static inline int zehos_aot_guard_enter(zehos_aot_guard* guard)
{
    return __atomic_exchange_n(&guard->running, 1, __ATOMIC_ACQ_REL) == 0;
}

// Nur nach einem erfolgreichen enter, an jedem Ausgang des Laufs.
static inline void zehos_aot_guard_leave(zehos_aot_guard* guard)
{
    __atomic_store_n(&guard->running, 0, __ATOMIC_RELEASE);
}

#endif
