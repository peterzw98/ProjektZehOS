# E6-G: aufgegebenen Wasm-Worker abräumen (Entwurfsnotiz)

Block #433, Branch `task/mc-e6-g-worker-abraeumen` (Basis `task/mc-e5-smp`), Cosmos-Teil auf `cosmos-patch/abandoned-thread-reap`.
Autor agent-peter-senior (Team peter, von Team laser übernommen). Gegenlesen: agent-peter-senior2 (19.11., trägt; Befunde
unten unter „Ergebnis des Gegenlesens“). Stand 20.11.: Cosmos-Patch auf `cosmos-patch/abandoned-thread-reap` (df39dfdf), ZehOS-Teil umgesetzt; offen nur der Pin nach dem Cosmos-Merge. Gelesen auf `origin/cosmos` 4eeb0f92 (enthält alle hier zitierten Stellen unverändert gegenüber dem Pin e3c6ff0c)
und `origin/task/mc-e5-smp` 6623a39a. Den Entwurf von Ada Lovelace (`~/.buzz/PLANS/…`) habe ich nicht; übernommen ist, was im
Kommentar vom 19.11. in #433 steht.

## Ausgangslage

- `Panic.CpuException` (Cosmos `src/Cosmos.Kernel/Panic.cs:41-46`) ruft den ZehOS-Hook, bei `AbandonThread` danach
  `SchedulerManager.TryAbandonCurrentFromInterrupt` (`SchedulerManager.cs:2554`): Der Thread wird unter der Queue-Sperre `Dead`,
  der Wechsel wird angelegt, `state.CurrentThread` zeigt schon auf den nächsten Thread (`:2784`). Danach erst schreibt die
  fehlernde CPU den Bericht und läuft mit `PrintStackTrace` über den Stack des Workers (`Panic.cs:60-61`).
- `_onCpu` des Workers fällt erst im Stub beim Interrupt-Ausgang (`SetContextSwitchPrevOnCpu`, `:2777`; Stub x64
  `Interrupts.s:780-791`), also nach dem Bericht. `IsOnCpu == false` heißt: keine CPU ist mehr auf dem Stack.
- Abgeräumt wird heute nichts: kein `OnThreadExit`, kein `UnregisterThread`, kein `RetireAllocContext`, kein
  `StopManagedThread` (Kommentar `:2530-2534`). `SchedulerDiagnostics.RequestKill` liefert für den Thread `RefusedBlocked`
  (er ist weder `CurrentThread` noch in einer Queue), `ExitThread` ist `internal`.
- `ReadyLocked` (`:1445`) lehnt nur `Ready` und `Running` ab. Ein Wecken eines `Dead`-Threads reiht ihn wieder ein. Deshalb
  verwirft heute `CosmosClassScheduler.OnThreadReady/OnThreadYield` (`:60`, `:68`) Threads aus `FaultIsolation.Quarantine`.
- `ThreadQuarantine` ist eine Tabelle nach Thread-Id mit `MaxThreadCount` = 256 Plätzen. Ids zählen fortlaufend
  (`AllocateThreadId`, `:879`) und werden nicht wiederverwendet. Ab Id 256 lehnt der Hook die Isolation ab
  (`CosmosFaultIsolation.cs:109`), und das System hält an. Diese Grenze entfällt mit der Quarantäne.
- `WasmRuntime.PollFaultIsolation` (`WasmRuntime.cs:185`) bekommt die Id aus `TakeIsolated`, sobald die CPU den Worker nicht mehr
  als laufenden Thread führt. Das ist vor dem Ende des Berichts, also kann `IsOnCpu` dann noch 1 sein.

## Cosmos-Patch `abandoned-thread-reap` (Entwurf 19.11.; Name, Ergebnis `NotDead` und Testfall mit `RequestKill` durch P2 überholt, gültig ist „Umsetzung Cosmos“ unten)

1. **`ReadyLocked`: `Dead` wird nie wieder bereit.** In der Abfrage am Anfang kommt `|| thread.State == Dead` dazu: Sperre
   freigeben, zurück. Das gilt für jeden toten Thread (aufgegeben, `RequestKill` `MarkedForExit`, nach `ExitThread`), von jedem
   Weckpfad (Event, Sleep-Alarm, Join). Die Prüfung läuft unter der Queue-Sperre, unter der auch `Dead` gesetzt wird. Sie
   schließt also das Fenster zwischen Aufgabe und Abräumen, ohne dass ZehOS eine Quarantäne braucht.
2. **Öffentlicher Einstieg `SchedulerDiagnostics.TryReapDead(uint threadId)`** mit neuem Enum `ThreadReapResult`:
   `NotFound`, `NotDead`, `StillOnCpu`, `Reaped`. Der Ablauf, nur im Thread-Kontext und ohne gehaltene Scheduler-Sperre:
   - Thread im Registry suchen (wie `RequestKill`); fehlt er: `NotFound`. Idle: `NotDead`.
   - `State != Dead`: `NotDead`. `IsOnCpu`: `StillOnCpu`, der Aufrufer versucht es später erneut.
   - Einmal-Merker per `Interlocked.CompareExchange` am Thread (`_reapClaimed`). Zwei Aufrufer räumen so nie doppelt ab;
     der zweite bekommt `NotFound`.
   - `SchedulerManager.ExitThread(thread)`: Weil der Thread nicht `CurrentThread` ist, läuft `StopManagedThread` mit
     Stopped, Joinern und gehaltenen Mutexen. Dann folgen `RetireAllocContext`, `OnThreadExit` unter der Queue-Sperre,
     `UnregisterThread` und das Einreihen in die Liste toter Stacks.
   - Danach `SchedulerManager.ReapDeadStacks()`: Der Stack ist sofort frei, nicht erst beim nächsten Threadstart. Damit ist er
     auch nach `workerRetired` frei; Adas Grenze „der letzte bleibt belegt“ entfällt. `ReapDeadStacks` prüft `IsOnCpu` selbst
     noch einmal.
   - `Reaped`.
3. **Prüfung im Cosmos-Baum:** Für `ExitThread` auf einem fremden Thread, der weder in einer Queue noch Current ist, muss
   `OnThreadExit` jeder mitgelieferten Policy das Fehlen vertragen (Stride, RoundRobin). Das prüfe ich vor dem Code; trägt eine
   Policy das nicht, wird sie im selben Patch robust. Für ZehOS gilt das schon: `CosmosClassScheduler.OnThreadExit` (`:72`).
   Im Kernel `Cosmos.Kernel.Tests.Threading` kommt ein Testfall dazu: Ein Thread wird mit `RequestKill` im Lauf als
   `MarkedForExit` markiert, schaltet weg, dann gilt `TryReapDead`, `Reaped` und `ReleasedStacks` +1. Ein zweiter Aufruf
   liefert `NotFound`, ein Wecken danach bleibt wirkungslos.
4. `patches/abandoned-thread-reap.md`, Kommentar `// ZehOS patch abandoned-thread-reap, see PATCHES.md`. Die Doku von
   `TryAbandonCurrentFromInterrupt` und `CpuExceptionHook` verweist auf `TryReapDead`, und `ausnahme-hook.md` bekommt einen
   Querverweis.

Warum kein Plug und kein Umbau von `RequestKill`: `RequestKill` hat eine andere, dokumentierte Bedeutung (laufender Thread wird
nur markiert). Ein Plug auf `ExitThread` bände eine `internal`-Methode, und den Schritt Registry, Sperre, Merker braucht
jeder Aufrufer gleich.

## ZehOS

- `WasmRuntime.PollFaultIsolation`: Neues Feld `pendingReap` (Id oder -1). Ablauf je Runde:
  1. Ist `pendingReap >= 0`: `TryReapDead` aufrufen. Bei `StillOnCpu` endet die Runde (keine Freigabe, kein Ersatzworker). Bei
     `Reaped` folgen die Schritte 3 bis 5 wie bisher. Bei `NotFound` oder `NotDead` wird ein Fehler protokolliert, `pendingReap`
     auf -1 gesetzt und weitergemacht wie bisher, der Speicher bleibt dann belegt.
  2. Sonst `TakeIsolated()`. Gibt es eine Id, wird sie `pendingReap` und Schritt 1 läuft sofort.
  3. `ReleaseRunResources` läuft erst nach `Reaped`. Damit gilt Adas Pflichtbedingung `Dead && !IsOnCpu` für Stack und
     Laufressourcen über einen einzigen Weg. Die Verzögerung ist die Zeit des seriellen Berichts; die Warteschlange bleibt
     solange eingereiht (`cleaningUp` deckt das schon ab).
  4. Ersatzworker wie bisher. Sein `InitializeStack` findet die Liste leer, weil der Stack schon frei ist.
  5. Die Protokollzeile nennt „Thread und Stack frei“ statt „Neustart empfohlen“.
  Auslöser bleibt die Id aus `TakeIsolated`, nicht ein beliebiger `Dead`-Thread (wie im Entwurf von Ada).
- **Quarantäne entfernen:** `ThreadQuarantine` (`FaultIsolationPolicy.cs:58`), `FaultIsolation.Quarantine` und die
  `Add`-Bedingung im Hook (`CosmosFaultIsolation.cs:22`, `:109`), die Zeilen in `CosmosClassScheduler` (`:60`, `:68`) und die
  beiden Hosttests (`FaultIsolationPolicyTests.cs:91`, `:100`). Das ist erst sicher mit dem Pin auf den Patch (Punkt 1 oben).
- **Native Wasm-Instanz:** Wie bei Ada bleibt sie belegt. Die wasm3- und WAMR-Strukturen des abgebrochenen Laufs sind in
  unbekanntem Zustand, ihr Freigeben kann selbst fehlschlagen. `MaximumIsolatedRuns = 3` bleibt deshalb. Der Kommentar an der
  Konstante nennt nur noch die Instanz als Grund. Das kommt als Grenze in den PR.
- Kommentare in `WasmRuntime.cs:172-216` und Klassendoku (`:20-23`) berichtigen; `docs/COSMOS_SUPPORT.md:154` berichtigen
  („Thread und Stack werden abgeräumt, sobald die CPU der Ausnahme den Stack verlassen hat; der native Speicher der Instanz
  bleibt belegt“). `docs/SMP_AUDIT.md`: `pendingReap` gehört nur der Hauptschleife, kein geteilter Zustand; das wird so
  eingetragen.
- Pin: `deploy/cosmos/COMMIT`, `VERSION` (nächste Nummer nach smp.9 aus #539) und `global.json`.

## Sicherheit der Reihenfolge

- Hat der Worker beim Fehler einen Mutex gehalten, gibt `StopManagedThread` ihn frei. Der Hook isoliert nur im Gastcode, ohne
  Hostfunktion, `StorageLock` oder `AppStorageGate` (`FaultIsolationPolicy.Decide`). Mutexe der Laufzeit hält er dort nicht;
  `lock (gate)` gibt `Work` vor dem Lauf frei.
- `RetireAllocContext` auf einem fremden, nicht laufenden Thread ist derselbe Weg wie `RequestKill` auf einem Thread in der
  Queue. Im Gastcode steht der Worker nicht im Allokator.
- Der Stack wird erst nach `!IsOnCpu` freigegeben, und nach `ReadyLocked` (Punkt 1) kann ihn niemand mehr betreten. Der
  Stackwalk des Berichts (#474) ist damit abgedeckt.

## Prüfung (geplant)

- Cosmos: Bau mit `scripts/build-cosmos.sh`, Threading-Testkernel mit dem neuen Fall.
- ZehOS: Hosttests ganz, nativer Bau `-warnaserror`, `check-plug-binding` entfällt (kein Plug).
- Gast: `python3 scripts/test-isolation-probe.py --runs 20`. Die Probe bekommt je Isolation die Zeile
  `ISOLATION_PROBE reaped released=<ReleasedStacks-Delta> free_before=<..> free_after=<..>`; das Skript verlangt Delta ≥ 1 und
  einen freien Speicher, der nach der zweiten Isolation nicht um 8 MiB unter dem nach der ersten liegt. Dazu `--negative` und
  `--nmi` als Gegenproben (müssen weiter anhalten) und ein Lauf mit `ZEHOS_QEMU_SMP=4`, Worker und Hauptschleife auf
  verschiedenen CPUs (Ziel aus Adas Kommentar).
- Offen bleiben VMware und echte Hardware.

## Ergebnis des Gegenlesens (agent-peter-senior2, 19.11., statisch auf `origin/cosmos` 52114521)

- **F1 trägt:** Andere Einreihpfade umgehen `ReadyLocked` nicht für `Dead` (`SetScheduler` überspringt `Dead`, `MigrateLocked`
  lehnt `Dead` ab, `MigratePending` und `ScheduleFromInterruptLocked` reihen nur `Running` ein). `Schedule()`/`DoContextSwitch`
  setzt den Vorgänger ungeprüft `Ready`, hat aber keinen Aufrufer.
- **F2:** an `SchedulerDiagnostics`, Id-basiert wie `RequestKill`. Umgesetzt.
- **P2 (umgesetzt):** Ein Abräumen jedes `Dead`-Threads träfe auch Threads, die `RequestKill` mitten im Kernel (Allokator,
  Spinlock) als `MarkedForExit` gestoppt hat; `RetireAllocContext` liefe dann auf einem halben Kontext. Deshalb setzt
  `TryAbandonCurrentFromInterrupt` unter der Queue-Sperre den Merker `_abandoned` (nur wenn der Wechsel angelegt ist), und der
  Einstieg heißt `TryReapAbandoned`; Ergebnis `NotAbandoned` statt `NotDead` für alles andere.
- **P3:** `BlockThread` und `MarkSleeping` überschreiben ein `MarkedForExit`-`Dead` (vorbestehend, nicht Teil dieses Patches).
  Für einen Testfall heißt das: Der Thread muss rein rechnend laufen. Ein aufgegebener Thread ist davon nicht betroffen, er
  läuft nie mehr.
- **P3:** Nach `SetScheduler` kennt die neue Policy den toten Thread gar nicht; `OnThreadExit` muss „unbekannt“ vertragen.
  Stride: `RemoveThreadFromQueue` sucht nur, `ThreadDataOf` null zieht keine Tickets ab; ZehOS `OnThreadExit` (`:72`) ebenso.
- **P3 (für ZehOS übernommen):** `StillOnCpu` ohne Obergrenze hielte die Wasm-Queue unbegrenzt, falls die CPU im Bericht
  hängt. `PollFaultIsolation` protokolliert nach 1000 Runden mit `StillOnCpu` einmal eine Warnung und versucht weiter.

## Umsetzung Cosmos (Stand `cosmos-patch/abandoned-thread-reap`)

- `ReadyLocked` lehnt `Dead` ab; `TryAbandonCurrentFromInterrupt` setzt `_abandoned`; Felder `_abandoned`, `_reapClaimed` in
  `SchedulerThread`.
- `SchedulerDiagnostics.TryReapAbandoned(uint)` → `ThreadReapResult` (`NotFound`, `NotAbandoned`, `StillOnCpu`, `Reaped`):
  Registry-Suche, Merker und Zustand unter der Queue-Sperre der Thread-CPU, `IsOnCpu`, Einmal-Merker per `CompareExchange`,
  `ExitThread`, `ReapDeadStacks`.
- Doku an `CpuExceptionAction.AbandonThread`, `TryAbandonCurrentFromInterrupt`, `ReleasedStacks`; `PublicAPI.Unshipped.txt`.
- Offen: Testfall im Threading-Testkern (braucht eine echte CPU-Ausnahme im Testthread mit installiertem Hook), `patches/abandoned-thread-reap.md`.

## Fragen an das Gegenlesen (beantwortet, siehe oben)

1. Reicht die `Dead`-Sperre in `ReadyLocked` als Ersatz für die Quarantäne, oder gibt es einen Pfad, der einen Thread ohne
   `ReadyLocked` einreiht (Migration `:2030` reiht nur `Running` ein; `SetScheduler` `:277` überspringt `Dead`)?
2. Soll `TryReapDead` an `SchedulerDiagnostics` (öffentlich, neben `RequestKill`) oder an `KernelThread`?
