# Etappen 6 und 7: Arbeit auf alle CPUs verteilen

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-18-multicore.md) lesen. Empfohlene Arbeitsweise:
> superpowers:executing-plans. Schritte haben Kästchen zum Abhaken.

**Ziel Etappe 6:** Wasm-Worker und Hintergrundaufgaben laufen auf den CPUs 1 bis n, verteilt nach Kernen und
Hyperthreads. Die Hauptschleife bleibt auf CPU 0.

**Ziel Etappe 7:** Auch die Hauptschleife läuft auf den Compute-CPUs; CPU 0 behält Interrupts und Treiber. Panic und
Energieaktionen wirken auf alle CPUs. Hyperthreads lassen sich abschalten. Alle Zielplattformen sind gemessen oder
ausdrücklich offen.

**Architektur:** Die Freigabe geschieht allein über Affinitäten: Ein Thread, dessen erlaubte Menge nur CPU 0 enthält,
verhält sich wie in Etappe 5. Jede Stufe erweitert die Menge einer Threadgruppe und lässt sich durch
`zehos.cpus=1` zurücknehmen. Platzierung und Ausgleich kommen aus `ClassPlacement` (E4-B).

**Voraussetzung:** Etappe 5 ist auf `master`. Etappe 0 liegt vor; jeder Auditbefund „Schutz fehlt“ der betroffenen
Threadgruppe ist behoben, bevor die Gruppe freigegeben wird.

**Entwurf:** [Abschnitte 5.2 bis 5.5 und 6](../specs/2026-11-18-multicore-design.md).

## Pakete

| Paket | Inhalt | Rolle | Nach | Neben |
| --- | --- | --- | --- | --- |
| E6-A | Topologie lesen und anzeigen | Engineer senior | Etappe 5 | E6-D, E6-F |
| E6-B | Platzierung und Ausgleich im Adapter | Engineer senior | E6-A | E6-D, E6-F |
| E6-C | Auditbefunde Worker beheben, Wasm-Worker freigeben | je Befund ein Engineer | E6-B, Etappe 0 | E6-D, E6-F |
| E6-D | `SMP_PROBE` und Skript | Engineer | Etappe 5 | A, B, C, F |
| E6-E | Hintergrundaufgaben freigeben, GC-Pausen und Speicherprobe messen | Engineer und Clerk | E6-C, E6-D | E6-F |
| E6-F | Diagnose je CPU (`tasks scheduler`, Task-Manager) | Engineer | Etappe 5 | alle |
| E7-A | Hauptschleife auf die Compute-CPUs | Engineer senior | E6-E | E7-B, E7-D |
| E7-B | Cosmos-Patch: Panic hält alle CPUs an | Engineer senior | Etappe 5 | alle |
| E7-C | Energieaktionen mit mehreren CPUs | Engineer und Clerk | E7-A | E7-D |
| E7-D | Hyperthreads abschaltbar, Platzierung im Gast belegt | Engineer | E6-B | E7-A, E7-B |
| E7-E | Plattformmatrix | Clerks, Benutzer mit VMware und Hardware | E7-A bis E7-D | keine |
| E7-F | Abschlussdoku | Engineer | E7-E | keine |

## Review-Schwerpunkte

1. Die Maschine hat nur eine oder zwei CPUs: Die Menge der Worker-CPUs darf nie leer werden (E6-C, `CpuSelection`-Tests).
2. Ein Thread wird gestohlen, während er gerade aufwacht oder seine Affinität wechselt: Cosmos prüft die Antwort von `Balance` unter beiden Sperren; der Adapter darf trotzdem nie einen Thread nennen, den er nicht unter der Sperre der Quell-CPU gelesen hat (E6-B).
3. Eine Hostfunktion einer Wasm-App greift auf Daten der Hauptschleife zu, die bisher nur durch „eine CPU“ geschützt waren (E6-C, Audit).
4. Eine GC-Sammlung, ausgelöst auf einer CPU, während eine andere `CosmosIrqLock` hält (E6-D, Probenschritt `gc` mit Eingabelast).
5. `zehos.smt=off` auf einer Maschine ohne Hyperthreading oder mit unbekannter Topologie ändert nichts und meldet das (E7-D).

---

## Etappe 6

### Paket E6-A: Topologie lesen und anzeigen

**Branch:** `task/mc-e6-a-topologie-lesen`

**Dateien:**
- Anlegen: `ProjektZeh/Scheduling/CosmosCpuTopologyReader.cs`
- Ändern: `ProjektZeh/Scheduling/CpuTopology.cs` (Methode `Describe`), Test in `ProjektZeh.Tests/CpuTopologyTests.cs`
- Ändern: `ProjektZeh/Kernel.cs` (Aufruf nach `CosmosBootOptions.Apply()`, Übergabe an `CosmosClassScheduler`), `ProjektZeh/Runtime/CosmosSystemInfo.cs` (Zeile in der Systeminfo des Taskmanagers; einen Shell-Befehl `sysinfo` gibt es nicht und er entsteht nicht)

**Schnittstellen (liefert):**

```csharp
internal sealed class CpuTopology
{
    /// true nur aus FromApicIds (gemessen); Flat ist Rückfall oder Annahme.
    public bool Measured { get; }
    /// "4 CPUs, 2 Kerne, 1 Verbund, Hyperthreading ja"; ist Measured false: "4 CPUs, Topologie unbekannt"
    public string Describe();
}

internal static class CosmosCpuTopologyReader
{
    /// Liest die Topologie aller CPUs unter KernelCpus.Count. Bei jedem Fehler: CpuTopology.Flat(Anzahl) und
    /// eine Warnung über log.
    public static CpuTopology Read(Action<string> log);
}
```

**Wie `Read` vorgeht:** Die Zuordnung CPU-Index zu APIC-ID ist in Cosmos intern, deshalb liest sie jede CPU selbst.
Für jeden Index `cpu` von 0 bis `KernelCpus.Count - 1`: einen Thread starten (`ThreadStarter.TryStart`), der zuerst
`ThreadAffinity.Current = CpuSet.Single((uint)cpu)` setzt, dann prüft, dass `KernelCpus.Current == cpu`, und die
APIC-ID liest: `X86Base.CpuId(0x0B, 0).Edx`, wenn Blatt 0 mindestens `0x0B` meldet und `(CpuId(0x0B, 0).Ebx & 0xFFFF) != 0`,
sonst `LocalApic.GetId()`. Auf den Thread warten (`Join` mit 2 s Zeitlimit; Zeitüberschreitung ist ein Fehler). Die
Verschiebungen kommen einmal von `CpuTopologyDecoder.ReadShifts(X86Base.CpuId)`. Ergebnis:
`CpuTopology.FromApicIds(ids, smtShift, packageShift)`.

**Bevorzugte Variante, falls Cosmos sie anbietet (Entscheidung Koordinator 19.11.):** Limine liefert die LAPIC-ID jeder CPU (`Limine.Mp`, `LimineMpResponseX64.Cpus[i]->LapicId`), und Cosmos vergibt die Indizes aus dieser Liste (Upstream `44952133` `Smp.cs:225-246`). Mit einem öffentlichen Zugriff `KernelCpus.HardwareId(index)`, einem kleinen Cosmos-Patch als Vorschlag an den E5-A-Besitzer, entfallen Thread, Affinität und Zeitlimit je CPU. ZehOS bildet die Indexvergabe nicht selbst nach. Bis der Zugriff existiert, gilt der Weg oben.

- [ ] **Schritt 1: Test** `Describe_TwoCoresTwoThreads`: `FromApicIds([0,1,2,3], 1, 2).Describe()` ist `"4 CPUs, 2 Kerne, 1 Verbund, Hyperthreading ja"`; `FromApicIds([0], 0, CpuTopologyDecoder.SinglePackage).Describe()` ist `"1 CPU, 1 Kern, 1 Verbund, Hyperthreading nein"`; `Flat(4).Describe()` ist `"4 CPUs, Topologie unbekannt"` (ein Rückfall behauptet kein gemessenes „nein“). Dazu `FromApicIds_DuplicateIds_Rejected`: doppelte APIC-IDs werfen (sonst legt `DenseIndex` sie zu einem Kern zusammen und meldet falsch „Hyperthreading ja“); der Leser fällt dann mit Warnung auf `Flat` zurück. Schlägt fehl, dann umsetzen.
- [ ] **Schritt 2:** `CosmosCpuTopologyReader` schreiben, in `Kernel.cs` aufrufen, Ergebnis ins Systemprotokoll (Bootzeile `Topologie: <Describe()>`) und in die Systeminfo des Taskmanagers (`CosmosSystemInfo`). Die Abnahme gilt für beide Oberflächen.
- [ ] **Schritt 3:** Hosttests, nativer Build.
- [ ] **Schritt 4: Gast (Clerk):** `python3 scripts/test-desktop-guest.py --iso <ISO> --accel kvm` je mit `ZEHOS_QEMU_SMP` = leer, `4`, `4,cores=2,threads=2`, `8,cores=4,threads=2`. Erwartet im seriellen Log: `Topologie: 1 CPU, 1 Kern, …`, `4 CPUs, 4 Kerne, … nein`, `4 CPUs, 2 Kerne, … ja`, `8 CPUs, 4 Kerne, … ja`. Dasselbe mit `--accel tcg`.
- [ ] **Schritt 5:** Commit (`feat(scheduling): CPU-Topologie beim Boot lesen`), Review, PR.

---

### Paket E6-B: Platzierung und Ausgleich

**Branch:** `task/mc-e6-b-platzierung`. **Datei:** `ProjektZeh/Scheduling/CosmosClassScheduler.cs`.

**Schnittstellen (verwendet):** `ClassPlacement.SelectCpu`, `ClassPlacement.StealOrder`, `ClassRunQueues<T>.PeekStealable`
(E4-B); `CpuTopology` aus E6-A, dem Scheduler im Konstruktor übergeben; `SchedulerManager.LockCpu` (Cosmos).

**`SelectCpu(thread, cpus, allowed)`:** drei `stackalloc`-Spannen der Länge `cpus.Length` füllen: `allowed[i] = allowedSet.Contains(i)`;
`running[i]` ist `null`, wenn `cpus[i].CurrentThread` der Idle-Thread dieser CPU oder `null` ist, sonst die Klasse aus
dessen `ClassThreadState`; `queued[i]` ist `Count` der Queues dieser CPU (ohne Sperre gelesen, ein Schnappschuss
genügt). Klasse und `LastCpu` des Threads aus seinem Zustand. Rückgabe von `ClassPlacement.SelectCpu`. Keine Allokation.

**`PickNext`:** zusätzlich `LastCpu` des gewählten Threads auf `cpuState.CpuId` setzen.

**`Balance(cpuState, allCpuStates)`:** `StealOrder` in eine `stackalloc`-Spanne; die CPUs in dieser Reihenfolge
durchgehen, je CPU `using (SchedulerManager.LockCpu(victim))` und `PeekStealable(t => nicht gepinnt und
t.Affinity.Contains(cpuState.CpuId))`; den ersten Fund zurückgeben, sonst `null`. Immer nur eine fremde Sperre zur
gleichen Zeit, nie die eigene.

- [ ] **Schritt 1:** Umsetzen. Die Regeln selbst sind in E4-B hostgetestet; hier entsteht nur die Übersetzung.
- [ ] **Schritt 2:** Hosttests, nativer Build.
- [ ] **Schritt 3: Gast (Clerk):** die Läufe aus E5-J mit `ZEHOS_QEMU_SMP=4`. Erwartet: unverändert grün. Noch sind alle Threads auf CPU 0 erlaubt; dieser Lauf belegt nur, dass die neuen Wege nichts stören.
- [ ] **Schritt 4:** Commit (`feat(scheduling): Platzierung und Lastausgleich nach Topologie`), Review mit Schwerpunkt 2, PR.

---

### Paket E6-C: Wasm-Worker freigeben

**Zuerst die Auditbefunde.** Aus `docs/SMP_AUDIT.md` jede Zeile mit Kontext `WW` oder `WN` und „Reicht? nein/unklar“.
Jede Zeile ist ein eigenes kleines Paket `E6-C.<n>` (Branch `task/mc-e6-c<n>-<kurzname>`): die dort genannte Änderung,
ein Hosttest, wo die Logik plattformunabhängig ist, PR. Diese Pakete laufen nebeneinander, solange sie verschiedene
Dateien ändern; der Koordinator vergibt sie anhand der Tabelle. Zahl und Inhalt stehen erst nach Etappe 0 fest.

**Dann die Freigabe.** Branch `task/mc-e6-c-worker-freigeben`. **Voraussetzung:** E5-A Schritt 2a (`ausnahme-hook` setzt den aufgegebenen Thread auf `Dead`) ist gemergt. Ohne ihn hält eine isolierte Ausnahme auf einer Worker-CPU mit leerer Queue an (E5-Plan, Review-Schwerpunkt 7). **Offener Punkt:** `FaultIsolationPolicy.Decide` unterscheidet „Ausnahme im Gastcode“ nicht von „Ausnahme in einem IRQ-Handler, der den Worker im Gastcode unterbrochen hat“. Abhilfe: die RIP gegen den Codebereich von wasm3/AOT prüfen oder eine Interrupt-Schachtelungstiefe in `Decide` einbeziehen. Das blockiert E5-A nicht, weil dort `TryAcquire` (E5-A Schritt 2b) den Hänger in einen Halt umwandelt (Zweitlesung Klaus Kusche, 19.11.).

**Dateien:**
- Anlegen: `ProjektZeh/Scheduling/CpuSelection.cs` (ohne Cosmos-Typen), Test `ProjektZeh.Tests/CpuSelectionTests.cs`
- Ändern: `ProjektZeh/Scheduling/ThreadStarter.cs`, `ProjektZeh/Runtime/CosmosBootOptions.cs`, `ProjektZeh/Kernel.cs`
- Ändern: `ProjektZeh/Runtime/Wasm/WasmRuntime.cs` (Start des Workers, bei `5c5538d` Zeile 280), `ProjektZeh/Runtime/Wasm/CosmosWasmNetwork.cs:88`

**Schnittstellen (liefert):**

```csharp
namespace ProjektZeh.Scheduling;

internal enum ThreadPlacement { Inherit = 0, Workers = 1 }

internal static class CpuSelection
{
    /// CPUs für Worker: alle außer CPU 0, begrenzt auf Indizes unter cpuLimit, bei smtOff ohne CPUs, die ein
    /// Geschwister mit kleinerem Index haben. Bleibt nichts übrig: nur CPU 0.
    public static bool[] Workers(CpuTopology topology, int? cpuLimit, bool smtOff);
}

internal static class ThreadStarter
{
    internal static Action<ThreadPlacement> ApplyPlacement { get; set; } = _ => { };
    public static ResultObject<Thread> TryStart(ThreadStart body, int stackBytes = 0, bool background = false,
        ThreadClass threadClass = ThreadClass.Normal, ThreadPlacement placement = ThreadPlacement.Inherit);
}

internal static class CosmosBootOptions
{
    public static CpuSet WorkerCpus { get; }   // aus CpuSelection.Workers, nach dem Lesen der Topologie gesetzt
}
```

`Kernel.cs` setzt `ThreadStarter.ApplyPlacement = p => { if (p == ThreadPlacement.Workers) ThreadAffinity.Current = CosmosBootOptions.WorkerCpus; };`.
Der Wasm-Worker und der Wasm-Netzwerk-Worker starten mit `placement: ThreadPlacement.Workers`.

- [ ] **Schritt 1: Tests** (`CpuSelectionTests`):

| Test | Gegeben | Erwartet (`true` an den Indizes) |
| --- | --- | --- |
| `Workers_FourCpus_AllButZero` | `Flat(4)`, kein Limit | 1, 2, 3 |
| `Workers_SingleCpu_FallsBackToZero` | `Flat(1)` | 0 |
| `Workers_LimitOne_OnlyZero` | `Flat(4)`, Limit 1 | 0 |
| `Workers_LimitThree` | `Flat(8)`, Limit 3 | 1, 2 |
| `Workers_SmtOff_DropsSecondThreads` | `FromApicIds([0..7], 1, 3)`, `smtOff` | 2, 4, 6 |
| `Workers_SmtOff_TwoCoresTwoThreads_KeepsOneWorkerCpu` | `FromApicIds([0,1,2,3], 1, 2)`, `smtOff` | 2 |
| `Workers_SmtOff_WithoutSiblings_ChangesNothing` | `Flat(4)`, `smtOff` | 1, 2, 3 |
| `Workers_TwoCpus` | `Flat(2)` | 1 |

  Dazu in `ThreadStarterTests`: `TryStart_WithWorkersPlacement_AppliesPlacementOnTheNewThreadBeforeBody`.
- [ ] **Schritt 2:** Tests schlagen fehl, umsetzen, Tests bestehen.
- [ ] **Schritt 3:** Nativer Build. **Gast (Clerk)**, je mit `ZEHOS_QEMU_SMP` = leer, `2`, `4`, `4,cores=2,threads=2`, KVM: `python3 scripts/test-wasm-doom-desktop.py --iso <ISO> --scenario full --accel kvm`; ClassiCube- und NetSurf-Läufe wie in E5-J; `python3 scripts/test-isolation-probe.py` positiv, negativ, NMI; `SMP_PROBE` aus E6-D. Im Desktop `tasks scheduler`: Der Wasm-Worker läuft auf einer CPU ungleich 0.
- [ ] **Schritt 4: Messung:** `python3 scripts/measure-wasm-frametimes.py --iso <ISO> --accel kvm --runs 5 -m 1G` für Doom und ClassiCube mit `ZEHOS_QEMU_SMP=4` gegen den Stand vor diesem Paket. Erwartet: Bildrate nicht niedriger; Wert berichten.
- [ ] **Schritt 5:** Commit (`feat(wasm): Wasm-Worker laufen auf den Compute-CPUs`), Review mit Schwerpunkt 3, PR.

---

### Paket E6-D: `SMP_PROBE`

**Branch:** `task/mc-e6-d-smp-probe`

**Dateien:**
- Anlegen: `ProjektZeh/Runtime/CosmosSmpProbe.cs` (ganz unter `#if ZEHOS_SMP_PROBE`), Aufruf in `Kernel.cs` nach dem Muster von `CosmosThreadProbe`
- Ändern: `ProjektZeh/ProjektZeh.csproj` (`DefineConstants` für `ZehOSSmpProbe`, nach dem Muster von `ZehOSThreadProbe`)
- Anlegen: `scripts/test-smp-probe.py` (nutzt `start_qemu` aus `test-wasm-doom-desktop.py` wie `test-isolation-probe.py`), Test in `scripts/tests/`
- Doku: `README.md` (Liste der Proben)

**Serielle Ausgabe:** je Schritt `SMP_PROBE <schritt> PASS` oder `SMP_PROBE <schritt> FAIL <Grund>`, Messwerte als
`SMP_PROBE INFO <schritt> <name>=<wert>`, am Ende `SMP_PROBE PASS` oder `SMP_PROBE FAIL`. Auf einer CPU melden die
Schritte, die mehrere brauchen, `SMP_PROBE <schritt> SKIP cpus=1`, und das Ende ist trotzdem `SMP_PROBE PASS`.

| Schritt | Inhalt | Bestanden, wenn |
| --- | --- | --- |
| `cpus` | `KernelCpus.Count` und Topologie ausgeben | Skript: Zahl gleich der ersten Zahl aus `ZEHOS_QEMU_SMP` |
| `counters` | je Worker-CPU ein dorthin gepinnter Thread zählt 300 ms einen eigenen Zähler und per `Interlocked` einen gemeinsamen | jeder eigene Zähler > 0; gemeinsamer Zähler gleich der Summe |
| `parallel` | dieselbe feste Rechenlast einmal auf einem, einmal auf allen Worker-CPUs | `INFO parallel speedup_permille=…`; unter KVM mit mindestens 2 Worker-CPUs Wert ≥ 1500 (TCG: nur Information) |
| `migrate` | ein Thread setzt 200-mal reihum `ThreadAffinity.Current` auf jede Worker-CPU | nach jedem Setzen `KernelCpus.Current` gleich dem Ziel |
| `locks` | je Worker-CPU ein Thread: 10.000 Übergaben über `lock`/`Monitor.Wait`/`Pulse` und über `Messaging/MessageQueue` | keine Nachricht verloren oder doppelt, Reihenfolge je Sender erhalten |
| `gc` | je Worker-CPU ein Thread allokiert 2 s lang Felder von 64 Byte bis 64 KiB mit Prüfsumme; der Hauptthread ruft 20-mal `GC.Collect()` | alle Prüfsummen stimmen; `INFO gc max_pause_ms=…` (größte Lücke eines 1-ms-Taktthreads) |
| `threads` | 100 Threads nacheinander starten und beenden | alle beendet; `INFO threads free_delta_kib=…` (nach E5-K: unter 1024) |
| `isolation` | nur Hinweiszeile, dass `test-isolation-probe.py` getrennt läuft | – |

- [ ] **Schritt 1:** Skripttest: `test-smp-probe.py` wertet eine Beispielausgabe mit `PASS`, eine mit `FAIL locks` und eine mit falscher CPU-Zahl richtig aus (Ende 0, 1, 1).
- [ ] **Schritt 2:** Probe und Skript schreiben. Aufruf: `python3 scripts/test-smp-probe.py --iso <ISO> --accel kvm|tcg`; CPU-Aufbau über `ZEHOS_QEMU_SMP`.
- [ ] **Schritt 3:** ISO mit `dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSSmpProbe=true -warnaserror`. **Gast (Clerk):** mit `ZEHOS_QEMU_SMP` = leer, `2`, `4`, `4,cores=2,threads=2`, `8,cores=4,threads=2`, je KVM und TCG, je drei Läufe. Erwartet: `SMP_PROBE PASS` in allen.
- [ ] **Schritt 4:** Commit (`test: SMP-Gastprobe`), Review, PR.

---

### Paket E6-E: Hintergrundaufgaben freigeben und messen

**Branch:** `task/mc-e6-e-hintergrund`. **Nach:** E6-C, E6-D; Auditbefunde mit Kontext `BG` und `DW` behoben (wie in E6-C).

- [ ] **Schritt 1:** `ProjektZeh/Scheduling/BackgroundTaskService.cs` und `DedicatedWorker.cs`: Start mit `placement: ThreadPlacement.Workers`. Hosttests (`SchedulingTests`, `WorkerPoolTests`) grün.
- [ ] **Schritt 2: Gast (Clerk), `ZEHOS_QEMU_SMP=4`, KVM:** Speicherprobe aus #247 (paralleles Lesen, Aufruf wie in `docs/COSMOS_SUPPORT.md`, „Dateisysteme nicht threadsicher“): mindestens 180.000 Lesungen ohne Fehler, `e2fsck -fn` Ende 0. `python3 scripts/test-network.py` und Paketinstallation (`scripts/test-wasm-doom-desktop.py --scenario full`). Anmeldung und Kennwortwechsel im Desktop.
- [ ] **Schritt 3: GC-Pausen:** `SMP_PROBE` mit `ZEHOS_QEMU_SMP` = leer, `4`, `8,cores=4,threads=2`; `INFO gc max_pause_ms` je fünf Läufe notieren. Dazu im Desktop mit laufendem Doom: `frametimes on`, 60 s, größte Bildzeit notieren. Abnahme: größte GC-Pause mit acht CPUs höchstens doppelt so lang wie mit einer und unter 100 ms; sonst Befund an Peter, bevor E7-A beginnt.
- [ ] **Schritt 4:** Werte in `docs/COSMOS_SUPPORT.md`, Commit (`feat(scheduling): Hintergrundaufgaben auf den Compute-CPUs`), Review, PR.

---

### Paket E6-F: Diagnose je CPU

**Branch:** `task/mc-e6-f-diagnose`

**Dateien:** `ProjektZeh/Scheduling/CosmosClassScheduler.cs` (`Snapshot`), `ProjektZeh/Kernel.cs` (`DescribeScheduler`),
`ProjektZeh/Apps/TaskManager/SystemMetrics.cs`, Tests `ProjektZeh.Tests/TaskShellCommandsTests.cs` und die Tests des Task-Managers.

- `Snapshot(uint cpu)` liefert je CPU: laufender Thread (Id, Klasse), wartende Threads je Klasse, belegte Ticks. Belegte Ticks zählt der Adapter in `OnTick` in einem Feld je CPU (wird für den Idle-Thread nicht aufgerufen); alle Ticks liefert `PerCpuState.TickCount`.
- `tasks scheduler` gibt eine Zeile je CPU aus: `CPU <n>: läuft <Id> (<Klasse>), wartend Interaktiv=<a> Normal=<b> Hintergrund=<c>, Last <p> %`.
- `SystemMetrics` bekommt die Last je CPU (belegte durch alle Ticks seit der letzten Abfrage); der Task-Manager zeigt sie als Liste. Die Gesamtlast bleibt wie bisher.

- [ ] **Schritt 1:** Tests für die Formatierung (plattformunabhängiger Teil: aus Zahlen wird die Zeile; Last 0 bei 0 Ticks) schreiben, umsetzen.
- [ ] **Schritt 2:** Hosttests, nativer Build, Gast: `tasks scheduler` mit `ZEHOS_QEMU_SMP=4` zeigt vier Zeilen; die Referenzbilder des Task-Managers (`GoldenTests`) neu erzeugen und prüfen.
- [ ] **Schritt 3:** Commit, Review, PR.

---

## Etappe 7

### Paket E7-A: Hauptschleife auf die Compute-CPUs

**Branch:** `task/mc-e7-a-hauptschleife`. **Nach:** E6-E; alle Auditbefunde mit Kontext `HS` gegen `IRQ` behoben.

**Datei:** `ProjektZeh/Runtime/CosmosBootOptions.cs`.

`Apply` setzt den Hauptthread nicht mehr auf CPU 0, sondern nach dem Lesen der Topologie auf
`CosmosBootOptions.WorkerCpus`, ebenso `KernelCpus.DefaultAffinity`. Reihenfolge im Boot: zuerst wie bisher beides
auf CPU 0 (früh, vor dem ersten Zugriff auf `Environment.ProcessorCount`), dann Topologie lesen, dann erweitern.
Bei `zehos.cpus=1`, im Recovery-Build und auf einer Maschine mit einer CPU bleibt es bei CPU 0.

- [ ] **Schritt 1:** Umsetzen; Bootzeile `CPUs online=<n>, Oberfläche und Worker auf <Liste>`.
- [ ] **Schritt 2: Gast (Clerk):** alle Läufe aus E5-J mit `ZEHOS_QEMU_SMP` = leer, `2`, `4`, `4,cores=2,threads=2`, `8,cores=4,threads=2`; dazu `SMP_PROBE`. Zusätzlich je Aufbau einmal mit `zehos.cpus=1` auf der Kernel-Kommandozeile (ISO mit geänderter `Bootloader/limine.conf`, Zeile `cmdline: zehos.cpus=1` im Eintrag): Verhalten wie mit einer CPU.
- [ ] **Schritt 3: Messung:** Bildraten und Wartezeit der Hauptschleife wie in E5-I mit `ZEHOS_QEMU_SMP=4` gegen den Stand vor diesem Paket. Erwartet: Wartezeit der Hauptschleife unter Doom-Last sinkt; Bildraten nicht niedriger.
- [ ] **Schritt 4: Von Hand:** Tastatur, Maus und Tablet im Desktop unter Last (Doom läuft, `tasks start busy` zweimal): keine verlorenen oder doppelten Eingaben, Fenster lassen sich flüssig ziehen.
- [ ] **Schritt 5:** Commit (`feat(boot): Oberflaeche laeuft auf den Compute-CPUs`), Review, PR.

---

### Paket E7-B: Cosmos-Patch „Panic hält alle CPUs an“

**Cosmos-Branch:** `cosmos-patch/panic-stop-cpus`. **ZehOS-Branch:** `task/mc-e7-b-panic`.

**Lage:** Im Upstream-Stand sperrt `Panic` nur die Interrupts der eigenen CPU und hält sie an
(`src/Cosmos.Kernel.Core/Panic.cs:28-56`, `src/Cosmos.Kernel/Panic.cs`). Die anderen CPUs laufen weiter, schreiben auf
Platte und Bildschirm.

**Randbedingungen für den Patch** (Entwurfsnotiz als erster Abschnitt des PRs, Review durch einen zweiten Engineer senior):

- Die anhaltende CPU wartet nie unbegrenzt auf die anderen (höchstens 100 ms), auch wenn eine andere CPU mit gesperrten Interrupts in einer Sperre steht.
- Es funktioniert, wenn die anhaltende CPU selbst Sperren hält (Heap, Seriell, Queue).
- Jede andere CPU endet mit gesperrten Interrupts in einer `hlt`-Schleife und fasst danach nichts mehr an.
- Der Weg „Thread aufgeben“ aus dem Ausnahme-Hook (E3-A) hält keine CPU an.
- Naheliegend: ein eigener IPI-Vektor oder ein NMI an alle anderen (`LocalApic`, Ziel „alle außer mir“), dessen Handler nur anhält; `CpuStop` ist dafür ungeeignet, weil es auf Bestätigungen wartet.

- [ ] **Schritt 1:** Entwurfsnotiz, Review.
- [ ] **Schritt 2:** Patch, Datei `patches/panic-stop-cpus.md`, Commit, PR gegen `cosmos`.
- [ ] **Schritt 3: Gastprobe:** In `CosmosSmpProbe` ein Schalter `-p:ZehOSSmpProbePanic=true`: Ein Thread auf einer Worker-CPU schreibt alle 50 ms `SMP_PROBE tick <n>` seriell; nach 1 s löst der Hauptthread eine Panic aus (`#PF` im Probe-Thread wie in `CosmosIsolationProbe`, negativer Fall). `scripts/test-smp-probe.py --panic` erwartet: nach `System halted.` erscheint 2 s lang keine weitere `tick`-Zeile. Mit `ZEHOS_QEMU_SMP=4`, KVM und TCG, je fünf Läufe.
- [ ] **Schritt 4:** ZehOS-PR mit `COMMIT`/`VERSION`, Doku in `docs/COSMOS_SUPPORT.md`.

---

### Paket E7-C: Energieaktionen

**Branch:** `task/mc-e7-c-energie`. **Nach:** E7-A.

- [ ] **Schritt 1: Gast (Clerk):** `ZEHOS_QEMU_SMP=4 powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/test-power-commands.ps1`, einmal im Leerlauf, einmal mit laufendem Doom und zwei rechnenden Hintergrundaufgaben, je `shutdown` und `reboot`, je fünfmal, mit Datenlaufwerk (Kopie). Erwartet: jedes Mal sauberes Aushängen (`e2fsck -fn` beziehungsweise `fsck.fat -n` Ende 0) und Ausschalten oder Neustart.
- [ ] **Schritt 2:** Schlägt ein Lauf fehl: Ursache im Ablauf `Kernel.cs` (Vorbereitung der Energieaktionen) beheben. Die Reihenfolge muss sein: Wasm-Läufe beenden, Hintergrundaufgaben stoppen (`StopAll`), warten bis beide Gruppen beendet sind, dann unter `StorageLock` aushängen, dann die Energieaktion. Für jede Korrektur ein Hosttest, wo die Logik plattformunabhängig ist.
- [ ] **Schritt 3:** Ergebnis in `docs/COSMOS_SUPPORT.md`; Commit, Review, PR (reine Doku, wenn nichts zu beheben war).

---

### Paket E7-D: Hyperthreads

**Branch:** `task/mc-e7-d-hyperthreads`. **Nach:** E6-B.

- [ ] **Schritt 1:** `CosmosBootOptions`: `SmtOff` aus der Kommandozeile an `CpuSelection.Workers` durchreichen (die Funktion und ihre Tests bestehen seit E6-C). Bootzeile `Hyperthreading: genutzt` / `abgeschaltet (zehos.smt=off)` / `nicht vorhanden`.
- [ ] **Schritt 2: Platzierung im Gast belegen.** Neuer Schritt `placement` in `CosmosSmpProbe`: ein rechnender Thread der Klasse Interaktiv und ein rechnender der Klasse Normal, beide mit `ThreadPlacement.Workers`, 500 ms; beide melden ihre CPU (`KernelCpus.Current`) alle 50 ms. Bestanden, wenn bei vorhandenen freien Kernen in mindestens 9 von 10 Meldungen die beiden CPUs keine Geschwister sind (`CpuTopology.SiblingsOf`). Lauf mit `ZEHOS_QEMU_SMP=8,cores=4,threads=2`, KVM, fünfmal.
- [ ] **Schritt 3:** Lauf mit `zehos.smt=off` und `ZEHOS_QEMU_SMP=8,cores=4,threads=2`: `tasks scheduler` zeigt Arbeit nur auf den CPUs 2, 4, 6; mit `ZEHOS_QEMU_SMP=4` (ohne Hyperthreads) ändert der Schalter nichts und die Bootzeile sagt „nicht vorhanden“.
- [ ] **Schritt 4:** `README.md` (Kommandozeilenschalter), Commit, Review, PR.

---

### Paket E7-E: Plattformmatrix

**Rolle:** Clerks für QEMU; für VMware, WHPX und echte Hardware die Benutzer, die sie haben. Stand: `master` nach E7-A bis E7-D.

Je Zelle: Boot bis Desktop, `SMP_PROBE`, Doom-Lauf `quick`, Isolation positiv, `shutdown`. Ergebnis `bestanden`,
`fehlgeschlagen: <Befund>` oder `offen: <warum nicht geprüft>`.

| Umgebung | CPU-Aufbau |
| --- | --- |
| QEMU TCG | `1`; `2`; `4`; `4,cores=2,threads=2`; `8,cores=4,threads=2` |
| QEMU KVM | wie TCG |
| QEMU WHPX | `2`; `4` |
| VMware | `numvcpus` 1, 2 und 4 (`scripts/start-vmware.ps1 -Cpus <n>`) |
| echte Hardware mit Hyperthreading | wie vorhanden |

- [ ] Matrix ausführen und als Tabelle in `docs/COSMOS_SUPPORT.md` (neuer Abschnitt „Mehrere CPUs“) eintragen. Jede Zelle „fehlgeschlagen“ wird ein eigenes Folgepaket; „offen“ bleibt sichtbar stehen.

---

### Paket E7-F: Abschlussdoku

- [ ] `README.md`: Bedienung (Schalter, `tasks scheduler`, Task-Manager), VM-Start mit mehreren CPUs, bekannte Grenzen (VMware zeigt keine Hyperthreads; Leistungsaussagen zu Hyperthreading nur von echter Hardware).
- [ ] `docs/COSMOS_SUPPORT.md`: Referenzumgebung und „Grenzen“ auf den neuen Stand; die Aussage „mehrere CPUs wurden nicht geprüft“ ersetzen.
- [ ] `ROADMAP.md`: neuer Abschnitt „Mehrere CPUs und eigener Cosmos-Build“ mit den Etappen, je mit Datum und Beleg; 12.7 (Threads für Pakete) bekommt den Hinweis, dass die Grundlage vorliegt.
- [ ] `AGENTS.md`, Abschnitt „Bauen und prüfen“: Kernabläufe zusätzlich mit `ZEHOS_QEMU_SMP=4` prüfen; `SMP_PROBE` nennen. Wortlaut mit Peter abstimmen.
- [ ] `docs/superpowers/specs/2026-11-18-cosmos-umwege-inventar.md`: Schlussstand je Zeile.
- [ ] `python scripts/check-doc-links.py`, Commit, PR.
