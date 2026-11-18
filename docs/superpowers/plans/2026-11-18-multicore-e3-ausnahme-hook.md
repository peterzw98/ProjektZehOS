# Etappe 3: Patchreihe 12.4 (Ausnahme-Hook, IRQContext, LINT1)

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-18-multicore.md) lesen, besonders „Cosmos-Patches“.
> Empfohlene Arbeitsweise: superpowers:executing-plans. Schritte haben Kästchen zum Abhaken.

**Ziel:** Cosmos bietet einen öffentlichen Hook für CPU-Ausnahmen. ZehOS nutzt ihn für die Fehlerisolation aus
12.4-I1 und braucht dafür keinen Plug auf `Panic`, keine Attrappe von `IRQContext`, keine festen Feldversätze und
keinen `UnsafeAccessor` mehr.

**Architektur:** Zwei kleine Cosmos-Patches (Hook, LINT1) und je ein ZehOS-PR, der den Umweg entfernt. Die
Entscheidung „anhalten oder Thread aufgeben“ bleibt in ZehOS (`FaultIsolationPolicy`), Cosmos führt sie nur aus.

**Voraussetzung:** Etappe 1 (Cosmos wird aus dem Branch `cosmos` gebaut; erfüllt seit #290) und Peters Freigabe für
12.4 (liegt seit dem 18. November 2026 vor). **Nicht gleichzeitig** mit Etappe 5. Die Einordnung in den Rest von
12.4 und die Hinweise zu Version und Commit stehen im [Plan 12.4](2026-11-18-isolation-12-4.md), Bahn H; dessen Paket
I2-C ändert in `ProjektZeh/Runtime/CosmosFaultIsolation.cs` nur eine neue Methode und läuft nach E3-B.

**Entwurf:** [Abschnitt 8.2](../specs/2026-11-18-multicore-design.md); Hintergrund in
[Isolation 12.4](../specs/2026-11-18-isolation-12-4.md), Abschnitt 5, und `docs/COSMOS_SUPPORT.md`, „CPU-Ausnahmen im
Wasm-Worker“.

Nicht Teil dieser Etappe: Stacks aufgegebener Threads freigeben (Paket E5-K) und TSS mit IST-Stacks (offene Entscheidung).

## Pakete

| Paket | Inhalt | Rolle | Nach | Neben |
| --- | --- | --- | --- | --- |
| E3-A | Cosmos-Patch: `CpuExceptionHook`, `IRQContext` öffentlich | Engineer senior | Etappe 1 | E3-C |
| E3-B | ZehOS: Fehlerisolation auf den Hook umstellen | Engineer senior | E3-A | E3-C |
| E3-C | Cosmos-Patch LINT1 als NMI und ZehOS-Probe bereinigen | Engineer | Etappe 1 | E3-A, E3-B |

## Review-Schwerpunkte

1. Eine zweite Ausnahme, während die erste noch ausgegeben wird (etwa beim Ablaufen einer kaputten Rahmenkette), darf nicht isoliert werden: Das System hält an (E3-A, Schachtelungszähler; E3-B, Gastprobe negativ).
2. Der Hook entscheidet „Thread aufgeben“, aber der Scheduler findet keinen anderen Thread: Das System hält an, statt in den fehlerhaften Code zurückzukehren (E3-A, Rückgabewert von `TryAbandonCurrentFromInterrupt`).
3. Kein Hook gesetzt (Recovery vor dem Setzen, andere Cosmos-Kernel): Verhalten wie heute (E3-A).
4. Vektoren außerhalb der Whitelist (NMI, #MC, #DF) werden auch mit Hook nie isoliert (E3-B, vorhandene `FaultIsolationPolicyTests`).
5. Der Hook läuft mit gesperrten Interrupts auf dem Stack des fehlerhaften Threads: keine Allokation, keine Sperre (E3-B, Review).

---

### Paket E3-A: Cosmos-Patch „öffentlicher Ausnahme-Hook“

**Branch im ZehOS-Repository:** `cosmos-patch/ausnahme-hook`, abgezweigt von `cosmos`, PR gegen `cosmos`.

**Dateien (Pfade im Branch `cosmos`):**
- Anlegen: `src/Cosmos.Kernel.Core/CPU/CpuExceptionHook.cs`
- Ändern: `src/Cosmos.Kernel.Core/CPU/IRQContext.cs:16` und `:105` (`internal struct` → `public struct`, beide Architekturen)
- Ändern: `src/Cosmos.Kernel.Core/Scheduler/SchedulerManager.cs` (neue interne Methode neben `ScheduleFromInterrupt`, `:1213`)
- Ändern: `src/Cosmos.Kernel/Panic.cs:19-34` (`CpuException`)
- Ändern: `PATCHES.md`

**Schnittstellen (liefert):**

```csharp
// src/Cosmos.Kernel.Core/CPU/CpuExceptionHook.cs
namespace Cosmos.Kernel.Core.CPU;

/// What the kernel does after a CPU exception once the context has been printed.
public enum CpuExceptionAction
{
    /// Halt the system (the behavior without a hook).
    Halt = 0,
    /// Give up the faulting thread: never schedule it again and switch to another thread.
    AbandonThread = 1,
}

/// Runs with interrupts masked on the faulting thread's stack: must not allocate, lock or log through anything
/// that locks.
public delegate CpuExceptionAction CpuExceptionHandler(ref IRQContext context);

public static class CpuExceptionHook
{
    /// Set once at boot. Null means every CPU exception halts.
    public static CpuExceptionHandler? Handler { get; set; }
}
```

```csharp
// SchedulerManager.cs, internal
/// Switches away from the current thread from an exception handler. False when no switch was staged
/// (no other thread, scheduler not running): the caller must halt.
internal static bool TryAbandonCurrentFromInterrupt(nuint currentRsp);
```

`TryAbandonCurrentFromInterrupt`: gibt `false` zurück, wenn `!IsRunning` oder kein `CurrentThread`; sonst
`ScheduleFromInterrupt(GetCurrentCpuId(), currentRsp)` und danach `ContextSwitchNative.GetContextSwitchSp() != 0`
(derselbe Wert, den ZehOS heute über `_native_get_context_switch_sp` liest; den passenden Import in
`ContextSwitchNative` verwenden oder dort ergänzen). Die Methode stellt den aufgegebenen Thread nicht selbst unter
Quarantäne; das tut weiter die ZehOS-Policy in `OnThreadReady`/`OnThreadYield`.

**Änderung an `Panic.CpuException`** (Reihenfolge der Ausgabe bleibt; neu sind Zähler und Hook):

```csharp
private static int s_cpuExceptionDepth;

internal static void CpuException(string exceptionName, ref IRQContext ctx)
{
    InternalCpu.DisableInterrupts();
    int depth = ++s_cpuExceptionDepth;

    // ... unveränderte Ausgabe: Kopf, PrintCpuContext(ref ctx), PrintStackTrace(ref ctx), Trennlinie ...

    // A fault while a first one is still being reported is never handed to the hook.
    if (depth == 1 && CpuExceptionHook.Handler is { } handler
        && handler(ref ctx) == CpuExceptionAction.AbandonThread
        && SchedulerManager.TryAbandonCurrentFromInterrupt((nuint)Unsafe.AsPointer(ref ctx)))
    {
        Serial.WriteString("Thread abandoned.\n");
        s_cpuExceptionDepth = 0;
        return;
    }

    Serial.WriteString("System halted.\n");
    HaltCpu();
}
```

Der Zähler ist bewusst einer für alle CPUs: Treffen nach Etappe 5 zwei CPUs gleichzeitig eine Ausnahme, hält die
zweite an. Das ist die sichere Richtung.

- [ ] **Schritt 1:** Branch `cosmos-patch/ausnahme-hook` von `origin/cosmos`. Unter Windows vorher `git config core.longpaths true`.
- [ ] **Schritt 2:** `CpuExceptionHook.cs` anlegen, `IRQContext` öffentlich machen. Prüfen, ob der Cosmos-Baum eine API-Liste führt (`docs/articles/dev/public-api.md` nennt die Regeln; nach `PublicAPI` und nach Analyzer-Meldungen zum Ring suchen) und sie im selben Commit nachziehen.
- [ ] **Schritt 3:** `TryAbandonCurrentFromInterrupt` und die Änderung an `Panic.CpuException` umsetzen.
- [ ] **Schritt 4:** Pakete bauen: im ZehOS-Arbeitsbaum `scripts/build-cosmos.sh --commit <Commit des Patch-Branches>` (Schnittstelle aus E1-B). Erwartet: Pakete ohne Warnungen als Fehler im Feed.
- [ ] **Schritt 5:** `PATCHES.md`: Zeile `ausnahme-hook | CPU/CpuExceptionHook.cs, CPU/IRQContext.cs, Scheduler/SchedulerManager.cs, Cosmos.Kernel/Panic.cs | öffentlicher Hook statt Plug auf Panic | CosmosPanicHaltPlug, CosmosIrqContextStub, UnsafeAccessor ScheduleFromInterrupt/Halt | test-isolation-probe.py positiv, negativ, NMI`.
- [ ] **Schritt 6:** Ein Commit (`patch: public CPU exception hook`), PR gegen `cosmos`. Der PR nennt E3-B als Prüfung und wird erst gemergt, wenn E3-B mit diesem Commit grün ist.

---

### Paket E3-B: ZehOS auf den Hook umstellen

**Branch:** `task/mc-e3-b-fehlerisolation-hook`. **Nach:** Commit aus E3-A liegt vor (gemergt oder als PR-Kopf).

**Dateien:**
- Ändern: `deploy/cosmos/COMMIT` (Commit aus E3-A), `deploy/cosmos/VERSION` (nächste `-zehos.N`)
- Ändern: `ProjektZeh/Runtime/CosmosFaultIsolation.cs`
- Löschen: `ProjektZeh/Runtime/CosmosIrqContextStub.cs`
- Ändern: `ProjektZeh/Kernel.cs` (Hook setzen, direkt vor `InstallScheduler()` bei `:386`)
- Doku: `docs/COSMOS_SUPPORT.md` (Abschnitt „CPU-Ausnahmen im Wasm-Worker“), `docs/superpowers/specs/2026-11-18-cosmos-umwege-inventar.md` (zwei Zeilen als erledigt kennzeichnen)

**Schnittstellen (verwendet):** `CpuExceptionHook`, `CpuExceptionAction`, `IRQContext` aus E3-A.

**Schnittstellen (liefert):** `internal static CpuExceptionAction FaultIsolation.OnCpuException(ref IRQContext context)`.
Unverändert bleiben `RegisterWorker`, `EnterNativeRun`, `LeaveNativeRun`, `TakeIsolated`, `IsolatedVector`,
`IsolatedRip`, `Quarantine` und `FaultIsolationPolicy.Decide`.

**Was `OnCpuException` tut** (der Inhalt von `OnCpuContext` und `OnHalt` in einem Schritt):

1. `vector = context.interrupt <= 255 ? (long)context.interrupt : FaultIsolationPolicy.UnknownVector`, `rip = context.rip`.
2. Die ZehOS-Zeile seriell ausgeben wie heute: `ZehOS: Thread <Id>, Wasm-Worker <Id>, Gastcode <0|1>` (`CosmosFaultIsolation.cs:118-125`); die Probeskripte lesen sie.
3. Wie heute in `OnHalt` (`:147-166`) entscheiden: `FaultIsolationPolicy.Decide(...)` mit denselben Argumenten; bei `Isolate` und erfolgreichem `Quarantine.Add` die Felder `inNativeRun`, `workerThreadId`, `isolatedVector`, `isolatedRip`, `isolatedThreadId` setzen und `CpuExceptionAction.AbandonThread` zurückgeben.
4. Sonst `CpuExceptionAction.Halt`.

**Was entfällt:** die Klasse `CosmosPanicHaltPlug`; die Konstanten der Feldversätze (`:54-56`); `OnCpuContext`,
`Field`, `Pair`, `Single` (Cosmos gibt die Register wieder selbst aus); `contextVector`, `contextClaimed`,
`contextRip`; `ContextSwitchTarget` (`DllImport`); beide `UnsafeAccessor` (`ScheduleFromInterrupt`, `Halt`); die
Endlosschleife am Ende von `OnHalt`.

- [ ] **Schritt 1:** `deploy/cosmos/COMMIT` und `VERSION` setzen, `scripts/build-cosmos.sh` ausführen.
- [ ] **Schritt 2:** `CosmosFaultIsolation.cs` wie oben umbauen, `CosmosIrqContextStub.cs` löschen, in `Kernel.cs` `Cosmos.Kernel.Core.CPU.CpuExceptionHook.Handler = FaultIsolation.OnCpuException;` setzen.
- [ ] **Schritt 3:** Prüfen: `grep -n "UnsafeAccessor\|DllImport\|\[Plug" ProjektZeh/Runtime/CosmosFaultIsolation.cs` zeigt nur noch den Import `zehos_host_depth_address`.
- [ ] **Schritt 4:** Hosttests (`FaultIsolationPolicyTests` unverändert grün), nativer Build mit Log (`cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release --verbose 2>&1 | tee artifacts/kernel-build.log`), dann `python3 scripts/check-plug-binding.py artifacts/kernel-build.log ProjektZeh`. Erwartet: kein Plug auf `Cosmos.Kernel.Panic` mehr gelistet, alle übrigen gebunden.
- [ ] **Schritt 5: Gastläufe (Clerk), KVM und TCG:**

| Lauf | ISO | Befehl | Erwartet |
| --- | --- | --- | --- |
| positiv | `-p:ZehOSIsolationProbe=true` | `python3 scripts/test-isolation-probe.py --iso <ISO> --accel kvm --runs 10` | 10/10 PASS; seriell `Thread abandoned.` statt `System halted.` |
| positiv TCG | wie oben | `… --accel tcg --runs 3` | 3/3 PASS |
| negativ | `-p:ZehOSIsolationProbeNegative=true` | `… --negative` | Halt mit `System halted.` |
| NMI | `-p:ZehOSIsolationProbeNmi=true` | `… --nmi` | Halt |
| Desktop | normal | `python3 scripts/test-desktop-guest.py --iso <ISO> --accel tcg` | Erfolg |
| Doom | normal | `python3 scripts/test-wasm-doom-desktop.py --iso <ISO> --scenario quick` | Erfolg |

- [ ] **Schritt 6:** Doku anpassen (der Satz „Cosmos schreibt vor dem Plug weiterhin `System halted.`“ entfällt), Commit (`refactor(isolation): Fehlerisolation ueber den Cosmos-Ausnahme-Hook`), Review, PR. Erst nach dem Merge von E3-A in `cosmos` mergen; vorher `deploy/cosmos/COMMIT` auf den gemergten Commit setzen.

---

### Paket E3-C: LINT1 als NMI

**Branch Cosmos:** `cosmos-patch/lint1-nmi` (PR gegen `cosmos`). **Branch ZehOS:** `task/mc-e3-c-lint1`.

**Dateien:**
- Cosmos ändern: `src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs:172-174`, `PATCHES.md`
- ZehOS ändern: `deploy/cosmos/COMMIT`, `deploy/cosmos/VERSION`, `ProjektZeh/Runtime/CosmosIsolationProbe.cs:40-44` (Entmaskierung entfernen), `docs/COSMOS_SUPPORT.md` (Absatz zu LINT1 in „CPU-Ausnahmen im Wasm-Worker“)

**Änderung in Cosmos:** In `LocalApic.Initialize` bleibt `LINT0` maskiert. `LINT1` wird mit Zustellart NMI
eingerichtet: neue Konstante `private const uint LVT_DELIVERY_NMI = 0b100u << 8;` und `Write(LAPIC_LINT1, LVT_DELIVERY_NMI);`.
Kommentar im Code (englisch, wie der Baum): LINT1 ist auf PCs die NMI-Leitung; maskiert verliert der Kernel NMIs still.

- [ ] **Schritt 1:** Cosmos-Patch schreiben, `PATCHES.md`-Zeile `lint1-nmi | Core.X64/Cpu/LocalApic.cs | NMI erreicht den Kernel | Entmaskierung in CosmosIsolationProbe | test-isolation-probe.py --nmi`, ein Commit, PR gegen `cosmos`.
- [ ] **Schritt 2:** In ZehOS `COMMIT`/`VERSION` setzen, Pakete bauen, die Entmaskierung aus der Probe entfernen.
- [ ] **Schritt 3: Gastläufe (Clerk):** `python3 scripts/test-isolation-probe.py --iso <NMI-ISO> --accel kvm --nmi` → Halt nach `inject-nmi`; `python3 scripts/test-desktop-guest.py --iso <normales ISO> --accel tcg` und `--accel kvm` → Erfolg (kein unerwarteter NMI im Normalbetrieb); `powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/test-power-commands.ps1` → Erfolg.
- [ ] **Schritt 4:** VMware: Boot bis Desktop mit `scripts/start-vmware.ps1`. Kann niemand prüfen: „offen“ im PR.
- [ ] **Schritt 5:** Doku, Commit (`fix(isolation): NMI-Probe ohne eigene LINT1-Entmaskierung`), Review, PR. Reihenfolge der Merges wie in E3-B.

Wird E3-C nach Etappe 5 bearbeitet, steht die Stelle im SMP-Stand zweimal (`LocalApic.cs:183-185` für die Boot-CPU,
`:225-226` für weitere CPUs); beide ändern.
