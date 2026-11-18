# Etappe 5: SMP-Stapel einspielen, alle Threads auf CPU 0

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-18-multicore.md) lesen. Empfohlene Arbeitsweise:
> superpowers:executing-plans. Schritte haben Kästchen zum Abhaken.

**Ziel:** ZehOS läuft auf dem Cosmos-Stand mit Upstreams SMP-Stapel. Alle weiteren CPUs sind online, aber noch ohne
Arbeit: Hauptthread und Standardaffinität stehen auf CPU 0. Die Hauptschleife ist ein normaler Thread der Klasse
Interaktiv, die Idle-Sonderregel ist entfernt.

**Architektur:** Der Stapel kommt als ein Commit in den Branch `cosmos` (kumulierter Unterschied von Upstream, ohne
eigene Änderung). In ZehOS ändert sich der Adapter `CosmosClassScheduler` auf die neue Schnittstelle; alles Übrige ist
Angleichen vorhandener Plugs und Messen. Verteilt wird erst in Etappe 6.

**Voraussetzung:** Etappen 1 und 4 abgeschlossen. Nicht gleichzeitig mit Etappe 3.

**Entwurf:** [Abschnitte 3, 5 und 9](../specs/2026-11-18-multicore-design.md).

**Upstream-Stand:** Branch `feat/smp-phase5b` von `https://github.com/CosmosOS/Cosmos`, Commit
`44952133e7820565552f30b057baa0660bd0a80d` (gelesen am 18. November 2026). Upstream kann diesen Branch überschreiben.
Ist der Commit nicht mehr abrufbar, nimmt E5-A die aktuelle Spitze, hält ihren Commit in `PATCHES.md` fest und meldet
das im Kanal; die Schnittstellen in diesem Plan sind dann gegen diesen Stand neu zu prüfen.

## Integrationsbranch

Zwischen dem Einspielen des Stapels und der Anpassung von Adapter und Plugs lässt sich ZehOS nicht bauen. Deshalb
arbeiten E5-B, E5-C, E5-D und E5-G auf PRs gegen den Integrationsbranch `task/mc-e5-smp` (von `master`), nicht gegen
`master`. Erst wenn dort Hosttests, nativer Build und die Läufe aus E5-J grün sind, geht **ein** PR von
`task/mc-e5-smp` nach `master`. Den Integrationsbranch führt der Bearbeiter von E5-B.

## Pakete

| Paket | Inhalt | Rolle | Nach | Neben |
| --- | --- | --- | --- | --- |
| E5-A | Stapel in den Branch `cosmos` einspielen | Engineer senior | Etappe 1 | E5-G |
| E5-B | Adapter auf die neue Schnittstelle, Hauptschleife Interaktiv, Idle-Regel entfernen | Engineer senior | E5-A | E5-D, E5-G |
| E5-C | Kommandozeile `zehos.cpus`, Hauptthread und Standardaffinität auf CPU 0 | Engineer | E5-B | E5-D |
| E5-D | Plugs und Zugriffe auf Cosmos-Interna angleichen | Engineer senior | E5-A | E5-B, E5-C |
| E5-G | CPU-Aufbau als Parameter in allen Startskripten | Engineer junior | keine | alle |
| E5-J | Abnahmeläufe mit 1 und 4 CPUs | Clerk | B, C, D, G | E5-H, E5-I |
| E5-H | Speicherqualifikation auf dem neuen Cosmos-Stand | Clerk mit Storage-Lead | B, C, D | E5-J, E5-I |
| E5-I | Vergleich Stride gegen Klassenscheduler | Clerk und Engineer | B, C, D | E5-J, E5-H |
| E5-F | Stackprobe vor Threadstarts: entfernen, wenn belegt überflüssig | Engineer | E5-J | E5-K |
| E5-K | Cosmos-Patch: Stacks beendeter Threads freigeben | Engineer senior | E5-J | E5-F |

## Review-Schwerpunkte

1. Ein Scheduler-Hook allokiert oder protokolliert unter der Queue-Sperre: seltener Hänger statt Fehler (E5-B, Review Zeile für Zeile; Cosmos löst im Debug eine Panic aus).
2. `Environment.ProcessorCount` wird vor dem Setzen der Standardaffinität gelesen und bleibt falsch (E5-C, Bootreihenfolge).
3. Ein Plug bindet nach dem Stapel nicht mehr und wird stiller No-Op (E5-D, `check-plug-binding.py`).
4. Code liest `GetCpuState(0)` und meint „diese CPU“ (E5-D, Suche).
5. Eine CPU kommt beim Start nicht hoch: Cosmos hält mit Panic an; ZehOS muss das in der Abnahme mit vier und acht CPUs sehen, nicht erst auf Hardware (E5-J).

---

### Paket E5-A: Stapel einspielen

**Branch:** `cosmos-upstream/smp-phase5b`, von `cosmos`, PR gegen `cosmos`.

- [ ] **Schritt 1:** Upstream als temporären Remote holen: `git remote add cosmos-upstream https://github.com/CosmosOS/Cosmos` und `git fetch cosmos-upstream 44952133e7820565552f30b057baa0660bd0a80d efcf20cc62884b042a0adadffe9fc4540491fd89`. Unter Windows vorher `git config core.longpaths true`.
- [ ] **Schritt 2:** Kumulierten Unterschied anwenden: `git diff --binary efcf20cc 44952133 -- . ':(exclude).github/workflows' | git apply --3way --index`. Konflikte entstehen nur dort, wo eigene Patches dieselben Zeilen ändern (sicher: `src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs`, falls E3-C schon gemergt ist; möglich: `src/Cosmos.Kernel/Panic.cs`, `SchedulerManager.cs` nach E3-A). Jeden Konflikt so lösen, dass Upstreams Fassung gilt und der eigene Patch darauf neu angebracht wird.
- [ ] **Schritt 3:** Prüfen, dass nichts außer den eigenen Patches abweicht: `git diff --stat 44952133 -- . ':(exclude).github' ':(exclude)PATCHES.md'` listet genau die Dateien, die `PATCHES.md` nennt.
- [ ] **Schritt 4:** `PATCHES.md`: neuer Abschnitt „Übernommener Upstream-Stand“ mit Branch, Commit, Datum und dem Befehl aus Schritt 2. Die bestehenden Patchzeilen bekommen den Vermerk, ob sie neu angebracht wurden.
- [ ] **Schritt 5:** Ein Commit (`upstream: SMP stack feat/smp-phase5b@4495213`). Pakete bauen: `scripts/build-cosmos.sh --commit <Commit>`. Erwartet: alle Pakete im Feed.
- [ ] **Schritt 6:** PR gegen `cosmos`. Gemergt wird er zusammen mit dem PR des Integrationsbranches nach `master`.

---

### Paket E5-B: Adapter auf die neue Schnittstelle

**Branch:** `task/mc-e5-b-adapter`, PR gegen `task/mc-e5-smp`.

**Dateien:**
- Ändern: `deploy/cosmos/COMMIT`, `deploy/cosmos/VERSION`
- Ändern: `ProjektZeh/Scheduling/CosmosClassScheduler.cs`
- Ändern: `ProjektZeh/Scheduling/ThreadClass.cs` (`AlwaysYields` entfernen), `ClassRunQueues.cs` (Regeln zu `AlwaysYields` entfernen)
- Ändern: `ProjektZeh.Tests/ClassRunQueuesTests.cs` (Tests `AlwaysYields_…` und `MainLoopAndBusyWorker_SameShareAsRoundRobin` entfernen, neuer Test unten)
- Ändern: `ProjektZeh/Kernel.cs` (`ThreadStarter.ApplyClass`, Klasse des Hauptthreads)

**Neue Schnittstelle** (`src/Cosmos.Kernel.Core/Scheduler/IScheduler.cs` im Upstream-Stand; die Kommentare dort sind der Vertrag und vor der Arbeit ganz zu lesen):

| Hook | Änderung gegenüber E4-C |
| --- | --- |
| `PickNext` | Läuft unter der Queue-Sperre der CPU. `null` heißt: der laufende Thread behält die CPU, wenn er weiterlaufen kann, sonst läuft der Idle-Thread. Deshalb: `var next = queues.Pick(running); return ReferenceEquals(next, running) ? null : next;` |
| `OnTick` | Wird für den Idle-Thread nicht mehr aufgerufen. |
| `OnThreadCreate` | `new ClassThreadState()` ohne `AlwaysYields`; die Policy sieht nie einen Idle-Thread. |
| `SelectCpu(SchedulerThread thread, PerCpuState[] cpus, CpuSet allowed)` | In dieser Etappe: `thread.CpuId`, falls `allowed.Contains(thread.CpuId)`, sonst `allowed.First`. Platzierung nach Topologie kommt in E6-B. |
| `Balance(PerCpuState cpuState, PerCpuState[] allCpuStates)` | Liefert `SchedulerThread?`; in dieser Etappe `null`. |
| `OnThreadMigrate` | Ist der Thread `Ready`: `Blocked` auf der Quelle, `Ready` auf dem Ziel. Ist er `Running`: nur `Ready` auf dem Ziel. |
| `SetPriority(PerCpuState, SchedulerThread, long)` | unverändert; Aufrufer ist jetzt `SchedulerManager.SetPriority(SchedulerThread thread, long priority)` ohne CPU-Nummer. |
| `GetRunQueueCount`, `GetRunQueueThread`, `Snapshot` | statt `MaskInterrupts` die Sperre der CPU: `using (SchedulerManager.LockCpu(state)) { … }` |

Allokation ist nur in `InitializeCpu`, `ShutdownCpu` und `OnThreadCreate` erlaubt; die Auswahlspur bleibt ein
vorab angelegtes Feld je CPU (heute eines für alle: auf `PerCpuState.SchedulerData` oder ein Feld je CPU-Index umziehen). Der Zähler
`preemptions` zieht mit um (Befund aus `docs/SMP_AUDIT.md`); `Snapshot` summiert ihn über die CPUs.

**Klasse des Hauptthreads und `ApplyClass`:**

```csharp
ThreadStarter.ApplyClass = threadClass =>
{
    using var mask = SchedulerManager.MaskInterrupts();
    if (SchedulerManager.GetCpuState(KernelCpus.Current)?.CurrentThread is { } current)
        SchedulerManager.SetPriority(current, (long)threadClass);
};
ThreadStarter.ApplyClass(ThreadClass.Interactive);   // im Hauptthread, direkt nach InstallScheduler()
```

- [ ] **Schritt 1: Test ändern.** Neuer Test `InteractiveThatBlocks_GivesCpuToNormalAndPreemptsOnWake`: `i` und `n` warten; `Pick(null)` liefert `i`; `i` blockiert (`Pick(null)` liefert `n`); `Ready(i)`; `Pick(n)` liefert `i`; nach `Yielded(n)` und `Blocked`-Ende von `i` liefert `Pick(null)` wieder `n` mit unverändertem Rest. Die drei `AlwaysYields`-Tests entfernen.
- [ ] **Schritt 2:** `AlwaysYields` aus `ClassThreadState` und `ClassRunQueues<T>` entfernen. Hosttests grün.
- [ ] **Schritt 3:** `CosmosClassScheduler.cs` nach der Tabelle anpassen, `Kernel.cs` wie oben. `deploy/cosmos/COMMIT` auf den Commit aus E5-A.
- [ ] **Schritt 4:** Nativer Build. Er schlägt an den Stellen fehl, die E5-D angleicht; diese Fehler an E5-D melden, nicht hier beheben. Für sich abnehmbar ist E5-B, wenn `ProjektZeh/Scheduling/*.cs` und `Kernel.cs` fehlerfrei übersetzen.
- [ ] **Schritt 5:** Commit (`feat(scheduling): Adapter fuer die SMP-Schnittstelle, Hauptschleife interaktiv`), Review mit Schwerpunkt 1, PR gegen `task/mc-e5-smp`.

---

### Paket E5-C: CPU-Begrenzung beim Boot

**Branch:** `task/mc-e5-c-cpu-grenze`, PR gegen `task/mc-e5-smp`. **Nach:** E5-B.

**Dateien:**
- Anlegen: `ProjektZeh/Runtime/BootOptions.cs` (ohne Cosmos-Typen), Test `ProjektZeh.Tests/BootOptionsTests.cs`; Eintrag in `ProjektZeh.Tests/ProjektZeh.Tests.csproj` nach dem Muster von Zeile 36
- Anlegen: `ProjektZeh/Runtime/CosmosBootOptions.cs` (liest `Limine.Cmdline`, setzt Affinitäten)
- Ändern: `ProjektZeh/Kernel.cs` (erster Aufruf im Boot, vor jedem Threadstart)
- Doku: `docs/handbuch/ENTWICKLUNG.md` (VM-Start: Kommandozeilenschalter; früher in der README)

**Schnittstellen (liefert):**

```csharp
namespace ProjektZeh.Runtime;

internal readonly record struct BootOptions(int? Cpus, bool SmtOff)
{
    /// Liest "zehos.cpus=<n>" (n >= 1) und "zehos.smt=off" aus der Kernel-Kommandozeile; Unbekanntes wird ignoriert,
    /// ein ungültiger Wert gilt als nicht angegeben.
    public static BootOptions Parse(string? commandLine);
}

internal static class CosmosBootOptions
{
    public static BootOptions Current { get; }      // nach Apply
    /// Zuerst im Hauptthread aufrufen. Etappe 5: setzt immer ThreadAffinity.Current und KernelCpus.DefaultAffinity
    /// auf CpuSet.Single(0), unabhängig von den Optionen.
    public static void Apply();
}
```

- [ ] **Schritt 1: Tests** (`BootOptionsTests`): `Parse(null)` → `(null, false)`; `"zehos.cpus=1"` → `Cpus == 1`; `"quiet zehos.cpus=4 zehos.smt=off"` → `(4, true)`; `"zehos.cpus=0"`, `"zehos.cpus=abc"`, `"zehos.cpus="` → `Cpus == null`; `"zehos.smt=on"` → `SmtOff == false`; `"xzehos.cpus=2"` → `Cpus == null` (nur ganze Wörter).
- [ ] **Schritt 2:** Tests schlagen fehl, dann `BootOptions.Parse` umsetzen, Tests bestehen.
- [ ] **Schritt 3:** `CosmosBootOptions.Apply`: Kommandozeile aus `Cosmos.Kernel.Boot.Limine.Limine.Cmdline` (nullterminierter `byte*`, kann `null` sein) in einen String wandeln, parsen, dann `ThreadAffinity.Current = CpuSet.Single(0)` und `KernelCpus.DefaultAffinity = CpuSet.Single(0)`. Beides in `try`/`catch (InvalidOperationException)`: Ohne laufenden Scheduler (Recovery-Fälle) bleibt es bei einer Protokollzeile.
- [ ] **Schritt 4:** In `Kernel.cs` als erste Anweisung des Boots aufrufen, vor `InstallScheduler()` und vor allem, was `Environment.ProcessorCount` lesen könnte. Bootzeile ins Systemprotokoll: `CPUs online=<KernelCpus.Count>, genutzt=1`.
- [ ] **Schritt 5:** Hosttests, Commit (`feat(boot): CPU-Begrenzung ueber die Kernel-Kommandozeile`), Review, PR gegen `task/mc-e5-smp`.

---

### Paket E5-D: Plugs und Zugriffe auf Interna angleichen

**Branch:** `task/mc-e5-d-plugs`, PR gegen `task/mc-e5-smp`.

Für jede Zeile: die Zielmethode im neuen Cosmos-Stand lesen (`git show <Commit aus E5-A>:<Pfad>`), Signatur und
Verhalten mit dem ZehOS-Umweg vergleichen, anpassen, in der PR-Beschreibung je Zeile „unverändert“, „angepasst: …“
oder „entfallen“ eintragen.

| ZehOS-Stelle | Ziel in Cosmos | Zu prüfen |
| --- | --- | --- |
| `Input/CosmosKeyboardPlug.cs` | `Cosmos.Kernel.System/Input/KeyboardManager.cs` (Phase 4b: eigene Sperre) | Signatur von `HandleScanCode`; ob der Plug jetzt unter einer Cosmos-Sperre läuft und `CosmosIrqLock` darin erlaubt bleibt (keine zweite Sperre nötig?) |
| `Input/CosmosMousePlug.cs`, die sechs `UnsafeAccessor` | `Input/MouseManager.cs` | Feldnamen der privaten Felder |
| `Input/CosmosInputService.cs:78-81` | `Input/KeyboardLayout.cs` | `ConvertScanCode` |
| `Runtime/CosmosVmwareClockPlug.cs` (neun `UnsafeAccessor`) | `Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs`, `X64CpuOps.cs` | `CalibrateTimer` läuft jetzt je CPU oder einmal? Der Plug muss für jede CPU denselben Wert liefern |
| `Runtime/CosmosFaultIsolation.cs` | `SchedulerManager`, Hook aus E3 | jedes `GetCpuState(0)` durch die laufende CPU ersetzen (`KernelCpus.Current`); Quarantäne bleibt |
| `Runtime/CosmosSystemInfo.cs:30-37` | `PageAllocator` | auf `MemoryDiagnostics` umstellen (öffentlich), `UnsafeAccessor` entfernen |
| `Scheduling/CosmosStackProbe.cs` | Exporte `__cosmos_heap_alloc`, `__cosmos_heap_free` | vorhanden und gleiche Signatur |
| `Graphics/DisplayService.cs:120-122` | `Graphics/Canvas.cs` | Feld `_display` |
| `Update/CosmosHttpUpdateSource.cs:94-109` | `Network/Protocols/Tcp/TcpConnection.cs` (Phase 4b geändert) | Feld `s_nextPort` |
| `Security/CosmosSaltSource.cs`, `CosmosVirtioEntropyDriver.cs`, `Input/CosmosUsbTabletDriver.cs` | Driver Kit | Übersetzen und Bindung |
| verbliebene ext2-Plugs (falls Etappe 2 nicht fertig) | `FileSystem/Ext2/**` (im Stapel unverändert) | Bindung |
| `Runtime/CosmosThreadProbe.cs`, `Messaging/MessageGuestProbe.cs`, weitere Proben | `SchedulerManager` | Übersetzen |

- [ ] **Schritt 1:** `grep -rn "GetCpuState(0)\|UnsafeAccessor\|DllImport(\"\\*\"\|\[Plug" ProjektZeh --include=*.cs` als Arbeitsliste; mit der Tabelle abgleichen, Fehlendes ergänzen.
- [ ] **Schritt 2:** Zeile für Zeile angleichen.
- [ ] **Schritt 3:** Nativer Build mit Log (`cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release --verbose 2>&1 | tee artifacts/kernel-build.log`), dann `python3 scripts/check-plug-binding.py artifacts/kernel-build.log ProjektZeh`. Erwartet: jeder Plug gebunden.
- [ ] **Schritt 4:** Hosttests. Commit (`fix: Plugs und Cosmos-Zugriffe an den SMP-Stand angeglichen`), Review, PR gegen `task/mc-e5-smp`.

---

### Paket E5-G: CPU-Aufbau in den Startskripten

**Branch:** `task/mc-e5-g-smp-parameter`, PR gegen `master` (unabhängig vom Cosmos-Stand).

Eine Umgebungsvariable für alle Skripte, damit kein Aufruf seine Parameter ändern muss: `ZEHOS_QEMU_SMP`. Ist sie
gesetzt, hängt das Skript `-smp <Wert>` an die QEMU-Argumente; ist sie leer, bleibt alles wie heute (eine CPU).

**Dateien:**
- `scripts/test-wasm-doom-desktop.py:250` (`start_qemu`; daraus starten `test-desktop-guest.py`, `test-isolation-probe.py`, `measure-wasm-frametimes.py`, `test-classicube-*.py`, `test-netsurf-desktop.py`, `test-storage-*.py`)
- `scripts/test-network.py`, `scripts/test-simd-probe.py`, `scripts/test-wasm-loader.py:31` (`build_arguments`)
- `scripts/test-thread-probe.ps1`, `scripts/test-security-probe.ps1`, `scripts/test-power-commands.ps1`
- `scripts/start-qemu.ps1` (zusätzlich Parameter `-Smp`), `scripts/start-vmware.ps1:39` (Parameter `-Cpus`, Standard 1, setzt `numvcpus`)
- Test: `scripts/tests/test_qemu_tools.py`
- Doku: `README.md` (VM-Start)

- [ ] **Schritt 1: Test** in `scripts/tests/test_qemu_tools.py`: mit `ZEHOS_QEMU_SMP=4,cores=2,threads=2` enthalten die Argumente von `start_qemu` und `build_arguments` die Folge `-smp`, `4,cores=2,threads=2`; ohne die Variable kein `-smp`. Schlägt fehl.
- [ ] **Schritt 2:** In jedem Skript an der Stelle, an der die Argumentliste entsteht, die Variable auswerten. Test besteht: `python3 -m unittest discover scripts/tests`.
- [ ] **Schritt 3:** Unter dem heutigen `master` (eine CPU in ZehOS) `ZEHOS_QEMU_SMP=4 python3 scripts/test-desktop-guest.py --iso <ISO> --accel tcg` ausführen: Erfolg erwartet, weil 3.0.97 weitere CPUs nie startet.
- [ ] **Schritt 4:** Commit (`test: CPU-Aufbau der Gastlaeufe ueber ZEHOS_QEMU_SMP`), Review, PR.

---

### Paket E5-J: Abnahmeläufe

**Rolle:** Clerk. **Stand:** Spitze von `task/mc-e5-smp`.

Jeder Lauf einmal mit `ZEHOS_QEMU_SMP` leer und einmal mit `4`; die mit ★ zusätzlich mit `8,cores=4,threads=2`.

| Lauf | Befehl | Erwartet |
| --- | --- | --- |
| Hosttests | `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror` | grün |
| Boot und Desktop ★ | `python3 scripts/test-desktop-guest.py --iso <ISO> --accel tcg`, ebenso `--accel kvm` | Erfolg; seriell `[SMP]`-Zeilen für jede weitere CPU; Bootzeile `CPUs online=<n>, genutzt=1` |
| Recovery | `python3 scripts/test-desktop-guest.py --iso <Recovery-ISO> --recovery --accel tcg` | Erfolg |
| Threads und Nachrichten ★ | `./scripts/test-thread-probe.ps1 -IsoPath <Probe-ISO>` | `THREAD_PROBE PASS`, `MESSAGES_PROBE PASS` |
| Isolation | `python3 scripts/test-isolation-probe.py --iso <ISO> --accel kvm --runs 5`, dazu `--negative`, `--nmi` | wie in E3-B |
| Doom | `python3 scripts/test-wasm-doom-desktop.py --iso <ISO> --scenario full --accel kvm` | Erfolg |
| ClassiCube, NetSurf | `scripts/test-classicube-desktop.py`, `scripts/test-netsurf-desktop.py` (Aufruf wie in der CI beziehungsweise `README.md`) | Erfolg |
| Netz | `python3 scripts/test-network.py` (Aufruf wie in `docs/NETWORK_TESTS.md`) | Erfolg |
| Energie | `powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/test-power-commands.ps1` | Erfolg |
| VMware | `scripts/start-vmware.ps1 -Cpus 1`, dann `-Cpus 2` | Boot bis Desktop; sonst „offen“ |

Zusätzlich von Hand im Desktop: Die Oberfläche bleibt flüssig, während Doom läuft (Fenster verschieben); `tasks scheduler`
zeigt den Hauptthread nicht mehr als Idle-Thread.

- [ ] Alle Zeilen ausführen, Ergebnisse mit Umgebung in den PR des Integrationsbranches schreiben. Jeder Fehlschlag geht mit seriellem Log an den Bearbeiter des betroffenen Pakets.

---

### Paket E5-H: Speicherqualifikation wiederholen

**Rolle:** Clerk mit dem Lead der Storage-Arbeitsgruppe. Mit dem Stapel kommen zwölf unveröffentlichte Cosmos-Commits;
die Qualifikation aus `docs/STORAGE_3_0_97_FINDINGS.md` gilt für 3.0.97.

- [ ] Die dort und in `docs/DESKTOP_STORAGE_TESTS.md` geführten Gastläufe (E4.0-Läufe mit `e2fsck -fn`, `scripts/test-storage-ext2-guest.py`, `scripts/test-storage-filesystems.py`, `scripts/test-storage-data-disk.py`, Lesestresstest aus #247) auf dem Stand von `task/mc-e5-smp` wiederholen, je mit einer und vier CPUs, immer auf einer Kopie des Datenimages.
- [ ] Ergebnis als neuer datierter Abschnitt in `docs/STORAGE_3_0_97_FINDINGS.md` (Titel mit dem neuen Cosmos-Stand). Abweichungen sind Blocker für den PR nach `master`.

---

### Paket E5-I: Stride gegen Klassenscheduler

**Rolle:** Clerk misst, Engineer wertet aus.

- [ ] **Schritt 1:** Zwei ISOs vom Stand `task/mc-e5-smp`: normal, und eines mit der zusätzlichen Build-Eigenschaft `-p:ZehOSStrideScheduler=true`, die in `Kernel.cs` den Aufruf `InstallScheduler()` auslässt (Konstante `ZEHOS_STRIDE_SCHEDULER`, nur für diese Messung; der Schalter wird mit dem Paket eingeführt und bleibt als Diagnose).
- [ ] **Schritt 2:** Je ISO fünf Läufe im Wechsel, KVM, 1 GiB, `ZEHOS_QEMU_SMP=4`: `python3 scripts/measure-wasm-frametimes.py --iso <ISO> --accel kvm --runs 5 -m 1G` für Doom und ClassiCube wie in E4-E. Dazu je ISO derselbe Lauf mit einer rechnenden Hintergrundaufgabe (`tasks start busy`).
- [ ] **Schritt 3:** Bedienlatenz: mit dem Diagnosebau aus #254 (Wartezeit „bereit bis läuft“ der Hauptschleife, siehe `ROADMAP.md` zu 13.2) unter Doom-Last messen, je ISO.
- [ ] **Schritt 4:** Auswertung in `docs/COSMOS_SUPPORT.md`. Abnahme: Der Klassenscheduler ist bei den Bildraten nicht mehr als 3 % schlechter und bei der Wartezeit der Hauptschleife nicht schlechter als Stride. Sonst: Ergebnis an Peter, Entscheidung 2 des Entwurfs wird neu gestellt, Etappe 6 wartet.

---

### Paket E5-F: Stackprobe entfernen

**Nach:** E5-J. Der Stapel prüft die Stack-Allokation selbst und versucht nach einer Sammlung erneut
(`SchedulerThread.cs:261-271`).

- [ ] **Schritt 1:** Den Gastlauf wiederholen, der die Probe begründet hat (E4.0, 1 GiB, Schritt j aus `docs/STORAGE_3_0_97_FINDINGS.md`: Threadstart nach Dateilast), einmal mit Probe, einmal mit `ThreadStarter.StackProbe = _ => true`. Erwartet ohne Probe: kein Page Fault; ein scheiternder Start liefert einen Fehler an den Aufrufer.
- [ ] **Schritt 2:** Trifft das zu: `ProjektZeh/Scheduling/CosmosStackProbe.cs` löschen, `ThreadStarter.StackProbe` und die Zuweisung in `Kernel.cs` entfernen, `ThreadStarterTests` anpassen, Inventarzeile als erledigt kennzeichnen. Trifft es nicht zu: Probe bleibt, Befund in `docs/COSMOS_SUPPORT.md`.

---

### Paket E5-K: Cosmos-Patch „Stacks beendeter Threads freigeben“

**Nach:** E5-J. **Rolle:** Engineer senior. Der Stapel gibt nur Stacks nie gelaufener Threads frei
(`SchedulerThread.ReleaseStack`, drei Aufrufer). Je beendetem Thread bleiben 256 KiB, je aufgegebenem Wasm-Worker 8 MiB belegt.

**Randbedingungen für den Patch** (der Entwurf im Einzelnen ist Teil des Pakets und steht als erster Abschnitt im PR):

- Ein Stack wird nie freigegeben, solange eine CPU auf ihm läuft: frühestens nach dem Kontextwechsel weg vom toten Thread, und von einem anderen Stack aus.
- Freigegeben wird außerhalb der Queue-Sperre und nicht im Tick; geeignet ist die Stelle, an der Cosmos tote Threads aus der Registry nimmt (`UnregisterThread`), oder der Idle-Thread der CPU.
- Der GC darf den Stack danach nicht mehr durchsuchen: erst aus der Registry nehmen, dann freigeben.
- Gilt für regulär beendete Threads und für aufgegebene (Ausnahme-Hook aus E3, `SchedulerDiagnostics.RequestKill`).

**ZehOS danach:** Quarantäne-Zeilen in `CosmosClassScheduler` (`OnThreadReady`, `OnThreadYield`) und
`ThreadQuarantine` entfernen, wenn der aufgegebene Thread durch Cosmos als tot gilt und nie wieder bereit wird;
`docs/COSMOS_SUPPORT.md` („Je Ausnahme bleiben Thread, 8-MiB-Stack … belegt“) berichtigen.

- [ ] **Schritt 1:** Entwurfsnotiz (eine Seite) im PR gegen `cosmos`: Stelle des Aufräumens, Sperren, Reihenfolge gegenüber GC. Review durch einen zweiten Engineer senior, bevor Code entsteht.
- [ ] **Schritt 2:** Patch, Datei `patches/<Kurzname>.md`, Pakete bauen.
- [ ] **Schritt 3: Gastprobe** (neuer Teil der `SMP_PROBE` aus E6-D oder, wenn E6-D noch fehlt, in `CosmosThreadProbe`): 100 Threads nacheinander starten und beenden, freie Seiten davor und danach vergleichen (`MemoryDiagnostics`); erwartet: Differenz unter 1 MiB. Isolationsprobe mit `--runs 20`: freier Speicher sinkt nicht um 8 MiB je Lauf.
- [ ] **Schritt 4:** ZehOS-PR wie in „Cosmos-Patches“ beschrieben.
