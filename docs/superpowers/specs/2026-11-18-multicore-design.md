# Multicore und Hyperthreading: Architekturentwurf

Stand 18. November 2026, Auftrag Peter. Entwurf, noch nicht freigegeben; die Entscheidungen in Abschnitt 10 sind offen.
Kein Kernelcode ist geändert. Belege beziehen sich auf ZehOS master `5c5538d`, Cosmos 3.0.97 (Paketcommit `efcf20c`)
und den Upstream-Branch `feat/smp-phase5b` (`4495213`, gelesen am 18. November 2026).

## 1. Ziel

ZehOS nutzt alle logischen CPUs des Rechners, kennt deren Aufbau (Verbund, Kern, Hyperthread) und verteilt Threads
danach. Der Round-Robin-Scheduler wird durch einen Scheduler ersetzt, der Klassen, mehrere CPUs und Hyperthreads kennt.

Leitregel (Peter, 18. November): **so isoliert wie möglich bauen.** Stellt Cosmos seine eigene SMP-Umsetzung fertig,
bauen wir unsere Übergangslösung aus und deren Stand ein, ohne ZehOS umzuschreiben.

Nicht-Ziele: Prozessisolation (12.4), Threads für Pakete (12.7), NUMA, CPU-Hotplug, Energiesparzustände, ARM64.

## 2. Befund: Cosmos 3.0.97

Cosmos 3.0.97 läuft auf genau einer CPU. Die Schnittstellen sind für mehrere CPUs geformt, es startet sie aber nichts.

| Bereich | Stand in 3.0.97 | Beleg |
| --- | --- | --- |
| Roadmap | „SMP: Not Started“ | `docs/roadmap.md:44` |
| CPU-Identität | fest 0 | `SchedulerManager.cs:441` (`GetCurrentCpuId() => 0`) |
| Start weiterer CPUs | kein Limine-MP-Request, kein Trampolin | `Cosmos.Kernel.Boot.Limine/Limine.cs:5-13` |
| Kontextwechsel | Zwischenablage als einzelne globale Variablen | `Native.X64/CPU/Interrupts.s:337-342` |
| Local APIC | nur IPI an sich selbst, xAPIC über MMIO | `Core.X64/Cpu/LocalApic.cs:210` |
| GC | „Welt anhalten“ heißt nur: Interrupts dieser CPU maskieren | `GarbageCollector.cs:353`, `GarbageCollector.Mark.cs:97` |
| Sperren | 89 Abschnitte schützen sich nur per Interruptmaske (System 43, Core 36, HAL 7, Core.X64 3) | Zählung `DisableInterruptsScope`/`MaskInterrupts` |
| GDT/TSS | Cosmos nutzt die GDT von Limine, kein TSS, keine IST-Stacks | keine Fundstelle im Quellbaum |
| Platzierung | `SelectCpu` und `Balance` haben keinen Aufrufer | `docs/articles/dev/scheduler.md:373` |
| MADT | CPU-Liste wird bereits gelesen (bis 256) | `Native.MultiArch/ACPI/acpi_wrapper.c:166` |

Vorhanden und brauchbar: `PerCpuState`, `SpinLock` mit IRQ-sicherer Variante, Heap-, Seiten- und Seriell-Sperre,
Ausnahmezustand je Thread (Phase 0 von Upstream, in 3.0.97 enthalten).

## 3. Befund: Upstream baut SMP gerade selbst

Epic `CosmosOS/Cosmos#3336` (angelegt 23. Oktober 2026) mit Entwurfsdokument und einem Stapel aufeinander aufbauender
Entwurfs-PRs des Cosmos-Maintainers, zuletzt geändert am 17. November 2026:

| Phase | PR | Inhalt |
| --- | --- | --- |
| 1 | #3367 | Per-CPU-Block hinter `IA32_GS_BASE`, echte CPU-Identität |
| 2 | #3368 | Start der weiteren CPUs über Limine-MP-Request und Trampolin, Timer und APIC je CPU |
| 3a | #3371 | Queue- und Registry-Sperren, Aufwecken über Reschedule-IPI (Vektor `0xF0`), Migration |
| 3b | #3374 | Affinität: `CpuSet`, `ThreadAffinity`, `KernelCpus`; neue Signaturen von `SelectCpu`/`Balance` |
| 4 | #3397 | GC hält alle CPUs per Stop-IPI (`0xF1`) an und scannt sie aus dem Interruptrahmen |
| 4b | #3399 | „Eine Maske ist keine Sperre“: Tastaturqueue, `TickSource`, Sitzungstabellen, Netzwerkstack |
| 5 | #3412 | Platzierung beim Aufwecken, Lastausgleich, `Environment.ProcessorCount` |
| 5b | #3416 | `Main` läuft auf eigenem Thread; Idle-Threads sind für die Policy unsichtbar |

Abstand: `v3.0.97` → `feat/smp-phase5b` sind 61 Commits in 164 Dateien (49 SMP-Commits, 12 unveröffentlichte
`gen3`-Commits), in `src/` +6.407/−1.695 Zeilen. Eine Test-Kernel-Suite `Cosmos.Kernel.Tests.Smp` liegt bei.

**Bewertung (Stichprobe von `Smp.cs`, `SpinLock.cs`, `CpuStop`, `KernelMain.cs`, `IScheduler.cs`, Entwurfsreview):**
Der Entwurf ist der übliche und richtige (eigene Run-Queue und Sperre je CPU, jede CPU plant sich selbst). Der Code
ist sorgfältig: geordnete Sperrreihenfolge, Regel „unter einem Spinlock wird nichts allokiert“ mit Zähler und Panic
statt Hänger, Zeitlimit beim CPU-Start. Er ist aber Entwurf: alle PRs stehen auf „draft“, nichts davon ist in `gen3`
oder einem Release. Selbst gebaut oder im Gast gestartet habe ich ihn nicht.

Lücken im Upstream-Stapel, die ZehOS betreffen:

- **Keine Hyperthread-Topologie.** Alle logischen CPUs gelten als gleich; Geschwister eines Kerns sind unbekannt.
- **Panic hält nur die eigene CPU an** (`Core/Panic.cs:28-56`); die anderen laufen weiter.
- **Kein x2APIC** (APIC-IDs bis 255; Rechner, die xAPIC sperren, starten laut Entwurfsreview nicht).
- **Kein Schalter für die CPU-Zahl** auf der Kernel-Kommandozeile.
- **CPU 0 ist „Housekeeping“**: Geräteinterrupts und Treiberthreads bleiben dort, `Main` und seine Threads laufen
  auf den übrigen. Bei zwei CPUs hat der Hauptfluss damit weiter genau eine.
- `SpinLock` dreht ohne `pause`; auf Hyperthreads bremst das den Geschwisterthread.

## 4. Wege

| | A: auf Upstream-Release warten | B: Cosmos aus Quelle bauen, Upstream-Stapel einspielen | C: SMP-Mechanismus selbst schreiben |
| --- | --- | --- | --- |
| Eigener Code im heikelsten Teil (CPU-Start, GC-Stopp, Sperrordnung) | keiner | keiner, nur Upstream-Commits | rund 6.000 Zeilen Entsprechung |
| SMP in ZehOS ab | unbekannt (Release-Takt bis 1. November: fünf Versionen in vier Wochen) | sofort nach Etappe 2 | Monate |
| Ausbau, wenn Upstream fertig ist | entfällt | Paketquelle zurück auf NuGet stellen | großer Umbau, zwei Mechanismen weichen ab |
| Umwege an der Wurzel beheben | nein | möglich, aber gegen die Leitregel abzuwägen (Abschnitt 8) | möglich |
| Risiko | Termin, Lücken aus Abschnitt 3 bleiben | Entwurfscode ändert sich; 12 ungeprüfte `gen3`-Commits kommen mit | Fehler in GC und Sperren, Dauerpflege |

**Empfehlung: B, mit A als Rückfall.** 3.0.97 selbst bringt kein SMP mit, es gibt dort also nichts „Schlechtes“ zu
ersetzen. Der Upstream-Stapel ist nicht schlecht, sondern unfertig; ein Eigenbau (C) würde dieselbe Arbeit mit mehr
Risiko wiederholen und widerspricht der Leitregel, weil er sich nicht mehr austauschen lässt. Weg B ist zugleich die
isolierteste Lösung: Unser „eigener Bau“ ist Upstreams eigener Code, nur früher.

Die Etappen 0 und 1 (Abschnitt 9) nützen in Weg A und B gleichermaßen und brauchen keine Cosmos-Änderung.

## 5. Zielarchitektur

```
ZehOS (bleibt beim Tausch unverändert)
  Scheduling/ClassSchedulerCore<T>     Policy, ohne Cosmos-Typen, hostgetestet
  Scheduling/CpuTopology               Kern-/Geschwisterzuordnung, ohne Cosmos-Typen, hostgetestet
  Scheduling/CosmosClassScheduler      Adapter auf IScheduler (einzige Datei mit Naht-Signaturen)
  Scheduling/CosmosCpuTopologyReader   liest CPUID und APIC-IDs
  Kernel.cs                            wählt Scheduler, setzt Affinitäten
------------------------------ Naht: öffentliche Cosmos-API in der Form des Upstream-Stapels -----------
Cosmos (austauschbar: selbst gebaute Pakete heute, NuGet-Release später)
  Per-CPU-Block, CPU-Start, IPIs, Sperren, GC-Stopp, Affinität, Platzierungsaufrufe
```

### 5.1 Isolationsregeln

1. **Im Cosmos-Baum liegt keine ZehOS-Logik.** Er enthält Upstream-Commits und höchstens eine nummerierte Liste
   kleiner Patches (`PATCHES.md`, je Patch ein Commit, mit Grund und Ausbaubedingung).
2. **ZehOS spricht nur die API, die Upstream einführt** (`IScheduler` in der Form von Phase 3b/5b, `ThreadAffinity`,
   `KernelCpus`, `Environment.ProcessorCount`). Kein neuer `UnsafeAccessor` auf SMP-Interna.
3. **Alle Naht-Signaturen stehen in einer Datei** (`CosmosClassScheduler.cs`). Ändert Upstream die experimentelle
   Naht noch einmal, ändert sich nur sie.
4. **Die Paketversion ist der Schalter.** `ProjektZeh.csproj` bezieht `Cosmos.*` über eine Versionseigenschaft:
   `3.0.97-zehos.N` aus dem lokalen Feed oder später `3.0.x` von NuGet. Der Ausbau ist diese eine Zeile plus das
   Entfernen von Feed und Quellbaum.
5. **Was Upstream nicht hat, baut ZehOS oberhalb der Naht** (Topologie, Policy, CPU-Zahl-Schalter über Affinität).
   Nur was sich dort nicht lösen lässt, wird ein Patch nach Regel 1.

### 5.2 CPU-Start und Per-CPU-Zustand

Kommt vollständig aus Cosmos (Phasen 1 und 2): Limine startet jede logische CPU, auch Hyperthreads, und parkt sie;
Cosmos gibt sie nacheinander frei, jede richtet APIC und Timer ein und endet in ihrem Idle-Thread (`hlt`-Schleife).
ZehOS ergänzt nur:

- Bootzeile und `sysinfo`: Zahl der CPUs, Kerne und Hyperthreads.
- CPU-Begrenzung ohne Cosmos-Patch: Mit `zehos.cpus=1` auf der Kernel-Kommandozeile (`Bootloader/limine.conf`) setzt
  ZehOS die Standardaffinität auf CPU 0. Die anderen CPUs sind online, bleiben aber im Idle. Der Recovery-Build
  (`ZehOSRecovery`) verhält sich immer so.
- `zehos.smt=off` nimmt die zweiten Hyperthreads jedes Kerns aus der Standardaffinität.

### 5.3 Topologie und Hyperthreading

`CpuTopology` ordnet jedem CPU-Index Verbund, Kern und Geschwister zu. Quelle sind die APIC-IDs (Limine-MP-Liste) und
die Feldbreiten aus CPUID, einmal auf der Boot-CPU gelesen (`X86Base.CpuId`, wie in `CosmosVmwareClockPlug`):
Blatt `0x1F`, sonst `0x0B`, sonst `0x04`/`0x01`, bei AMD `0x8000001E`. Ohne verwertbare Angaben gilt jede CPU als
eigener Kern. Die Zerlegung ist eine reine Funktion und wird mit aufgezeichneten CPUID-Werten im Hosttest geprüft.

Was die Policy daraus macht:

1. **Erst ganze Kerne.** Ein Thread kommt auf eine CPU, deren Kern ganz frei ist, bevor er sich einen Kern teilt.
2. **Die Oberfläche bekommt ihren Kern.** Solange andere Kerne frei sind, meiden Normal- und Hintergrundthreads das
   Geschwister der CPU, auf der ein interaktiver Thread läuft.
3. **Stehlen von nah nach fern:** Geschwister, dann derselbe Verbund, dann der Rest.
4. **Leere Hyperthreads halten an** (`hlt` im Idle-Thread), damit der Kern dem Geschwister ganz gehört.

Grenzen: VMware zeigt Gästen in der Regel Kerne, keine Hyperthreads; dort wirkt nur die Mehrkernverteilung. QEMU
bildet Hyperthreads mit `-smp 4,cores=2,threads=2` nach, das prüft die Logik, nicht die Leistung. Leistungsaussagen zu
Hyperthreading brauchen echte Hardware und bleiben bis dahin offen. Hybrid-CPUs (P- und E-Kerne) behandelt der Entwurf
wie gleiche Kerne. Zwei Wasm-Apps auf Geschwistern teilen sich Caches (Seitenkanäle); eine Regel „nie zwei Apps auf
einem Kern“ gehört zu 12.4 und ist hier nur als Entscheidung 5 vermerkt.

### 5.4 Sperren und GC

Gelten für jeden ZehOS-Code, der in Scheduler-Hooks oder Interrupts läuft:

- Unter einem Spinlock und in jedem Scheduler-Hook wird nichts allokiert und nichts protokolliert. Der Scheduler-Kern
  arbeitet deshalb auf vorab dimensionierten Feldern, wie heute `RoundRobinQueue`.
- Eine Interruptmaske ist keine Sperre mehr. Wo Unterbrechung und Thread Daten teilen, braucht es eine IRQ-sichere Sperre.
- Der GC bleibt „Welt anhalten“. Mit mehr CPUs stehen bei jeder Sammlung alle; die Pausen werden gemessen
  (Abnahme in Etappe 4), bevor die Oberfläche auf eine andere CPU als die Treiber wandert.

### 5.5 Was sich in ZehOS ändern muss

| Stelle | Heute | Mit SMP |
| --- | --- | --- |
| `Input/CosmosInputService.cs:59`, `CosmosKeyboardPlug.cs:34`, `CosmosUsbTabletDriver.cs:53` | Maske schützt Puffer zwischen Interrupt und Hauptschleife | IRQ-sichere Sperre; Plug gegen Upstreams neue `KeyboardManager`-Sperre (4b) abgleichen |
| `Scheduling/CosmosRoundRobinScheduler.cs`, `RoundRobinCore`, `RoundRobinQueue` | Policy mit Idle-Sonderfall | entfällt, ersetzt durch Abschnitt 6 |
| `Runtime/InteractiveSession.cs:146` (Hauptschleife) | läuft als Idle-Thread, `Thread.Sleep(1)` wartet per Halt | läuft nach 5b als normaler Thread und parkt wirklich |
| `Runtime/CosmosFaultIsolation.cs:168-186` | `UnsafeAccessor` auf `ScheduleFromInterrupt`, Quarantäne-Menge | Signatur und Rahmen-Offsets (120/152) gegen neue `Interrupts.s` prüfen; Quarantäne ohne Sperre lesbar halten |
| `CosmosPanicHaltPlug` (`Runtime/CosmosFaultIsolation.cs:19`) | ersetzt `Panic.HaltCpu` | muss zusätzlich die anderen CPUs anhalten (Lücke aus Abschnitt 3) |
| `Runtime/Wasm/*`, `Native/Wasm/host/zehos_host_core.c`, `libc.c` | je App ein Worker, globale C-Zähler | Audit je Hostfunktion: geteilte Daten und ihre Sperre; globale C-Zustände je Instanz |
| `Runtime/CosmosVmwareClockPlug.cs` | TSC- und APIC-Frequenz für eine CPU | Kalibrierwert gilt für alle CPUs; TSC-Abweichung zwischen CPUs prüfen |
| Energieaktionen (`Kernel.cs`) | aushängen, dann ACPI | zuerst alle anderen CPUs stilllegen |
| `Apps/TaskManager/SystemMetrics.cs`, `tasks scheduler` | eine CPU-Last | Last je CPU, CPU und Klasse je Thread |
| `scripts/start-qemu.ps1`, `start-vmware.ps1:39`, Gasttests | 1 vCPU | Parameter für CPU-Zahl und Aufbau; Testmatrix aus Abschnitt 9 |

`StorageLock` (#247) und `ThreadStarter` (#241) bleiben, wie sie sind.

## 6. Scheduler-Ersatz

Der neue Scheduler heißt hier **Klassenscheduler**. Er ersetzt Round-Robin auch auf einer CPU und kommt deshalb
schon in Etappe 1, vor jedem Cosmos-Wechsel.

**Aufbau.** `ClassSchedulerCore<T>` (ohne Cosmos-Typen) hält je CPU drei Queues fester Größe.
`CosmosClassScheduler` reicht die Hooks durch. Auf 3.0.97 implementiert der Adapter die alte Naht, danach die neue;
der Kern bleibt gleich.

**Klassen.** Gesetzt beim Start über `ThreadStarter.TryStart` (einziger Startweg im Kernel, neuer Parameter) und
`SchedulerManager.SetPriority`:

| Klasse | Threads | Zeitscheibe |
| --- | --- | --- |
| Interaktiv | Hauptschleife (Desktop, Textshell, Eingabe) | 1 Tick (10 ms) |
| Normal | Wasm-Worker, Kennwort-Worker, Shell-Jobs | 2 Ticks (wie heute) |
| Hintergrund | `BackgroundTaskService`, Netzautomatik, Downloads | 4 Ticks |

**Auswahl je CPU.** Die höchste nicht leere Klasse läuft, innerhalb der Klasse reihum. Wird ein Thread einer höheren
Klasse bereit, verdrängt er sofort. Gegen Verhungern gilt eine feste Regel statt Alterung: Wartet eine niedrigere
Klasse, kommt sie nach 8 aufeinanderfolgenden Scheiben der höheren einmal dran. Eine dauernd rechnende
Hauptschleife behält so neben einem Wasm-Worker 8 von 10 Ticks; heute bekommt sie unter Last ein Drittel.

**Platzierung** (`SelectCpu`): unter den erlaubten CPUs in dieser Reihenfolge: die letzte CPU des Threads, wenn sie
frei ist; eine CPU auf ganz freiem Kern; eine freie CPU, deren Geschwister keine höhere Klasse ausführt; sonst die
kürzeste Queue. Die Regeln 1 und 2 aus Abschnitt 5.3 sind Teil dieser Reihenfolge.

**Lastausgleich** (`Balance`): Eine CPU ohne Arbeit stiehlt den vordersten wartenden Thread der höchsten Klasse, von
nah nach fern, nie einen gepinnten oder gerade laufenden, nie gegen die Affinität.

**Erhalten bleibt:** die Quarantäne-Zeile aus 12.4-I1 in `OnThreadReady`, die Auswahlspur für `tasks scheduler` (dann je CPU)
und der Name in der Bootzeile (`Scheduler=ZehOS Klassen`).

**Hosttests:** Vorrang und sofortige Verdrängung; Reihum in der Klasse; 8-zu-1-Regel; Stehlen und Affinität;
Platzierung auf den Topologien 1×1, 2×1, 2×2 und 4×2; Quarantäne; volle Queue. Die bestehenden Round-Robin-Tests
gelten sinngemäß für die Klasse Normal weiter.

**Kleinere Alternative:** Cosmos' eigene Stride-Policy (im Upstream-Stapel bereits SMP-fähig) wieder verwenden und
nur Klassen auf Tickets abbilden. `StrideScheduler` ist aber `internal`, und Hyperthread-Platzierung fehlt ihr; das
bräuchte einen Cosmos-Patch und verletzt damit Isolationsregel 1. Deshalb nicht empfohlen.

## 7. Eigener Cosmos-Build

Lizenz: BSD 3-Clause (`LICENSE`, „Copyright (c) 2007-2026, CosmosOS“). Selbst bauen und weitergeben ist erlaubt,
solange Copyright und Lizenztext in Quelle und ISO erhalten bleiben.

**So baut Cosmos sich selbst** (`.devcontainer/postCreateCommand.sh`, `Packages.slnx`): 23 Projekte werden in
Abhängigkeitsreihenfolge mit `dotnet pack` nach `artifacts/package/release` gepackt, das als lokaler NuGet-Feed dient.
`Cosmos.Sdk` zieht Patcher, ILC-Anbindung, Assembler- und C-Build in derselben Version nach; die CLI `cosmos` und
`Cosmos.Patcher` sind globale Tools aus demselben Feed. ILC selbst kommt weiter von Microsoft. Ein `ProjectReference`
auf die Cosmos-Projekte funktioniert nicht, weil SDK, Patcher und ILC Pakete mit `build/`-Targets erwarten.

**Einbindung in ZehOS:**

- Ein Spiegel-Repository hält den festen Upstream-Commit vor. Nötig, weil die Entwurfsbranches bei Upstream
  überschrieben oder gelöscht werden können. Der Spiegel enthält keine eigenen Commits außer den Patches aus `PATCHES.md`.
- Ein Skript (`scripts/build-cosmos.sh`) baut daraus die Pakete mit eigener Version `3.0.97-zehos.N` in einen lokalen
  Feed. Die eigene Nummer verhindert, dass der NuGet-Cache das gleichnamige Paket von nuget.org liefert.
- `NuGet.Config` im ZehOS-Stamm leitet `Cosmos.*` auf den lokalen Feed (`packageSourceMapping`), alles andere bleibt
  bei nuget.org. `ProjektZeh.csproj` (SDK-Zeile, zwei `PackageReference`) und die CI (`ci.yml:217-218`, heute fest
  `--version 3.0.97`) lesen die Version aus einer Eigenschaft.
- Die CI baut die Pakete einmal je Commit-Stand und legt sie im Cache ab. Eine Veröffentlichung der Pakete ist nicht
  vorgesehen.

**Aufwand und Pflege:** einmalig Skript, Feed und CI-Umstellung (Etappe 2). Laufend: Jedes Nachziehen des
Upstream-Stands ist eine eigene Änderung mit nativem Build und Gastsuite. Mit `PathMap` im eigenen Build verschwinden
die festen Upstream-Pfade aus den DLLs; `scripts/check-release-paths.py` braucht dann keine Ausnahmeliste mehr.

Nicht geprüft: ob der Paketbau unter Windows ohne Anpassung durchläuft (`postCreateCommand.ps1` liegt bei), und die
Bauzeit.

## 8. Umwege, die ein eigener Build beheben könnte

Vollständiges Inventar mit ZehOS-Datei und Ursache in der Cosmos-Quelle:
[2026-11-18-cosmos-umwege-inventar.md](2026-11-18-cosmos-umwege-inventar.md). Hier nach Wirkung sortiert.

**a) Entfallen mit dem Upstream-Stapel selbst, ohne eigenen Patch**

| Umweg in ZehOS | Ursache in 3.0.97 | Im Upstream-Stapel |
| --- | --- | --- |
| Idle-Sonderregel in `RoundRobinCore.cs:42` | Hauptschleife ist der Idle-Thread und wartet per Halt (`ConditionVariable.cs:170`) | Phase 5b: `Main` ist ein normaler Thread |
| Stackprobe `ThreadStarter`/`CosmosStackProbe` | `InitializeStack` prüft `MemoryOp.Alloc` nicht (`SchedulerThread.cs:167`) | Nullprüfung mit GC-Versuch (`SchedulerThread.cs:261-271`) |
| Zwang zu langlebigen Workern (`BackgroundTaskService`, `DedicatedWorker`) | Threadstacks werden nie freigegeben | `ReleaseStack` (`SchedulerThread.cs:311`) |

Ob die drei Umwege danach wirklich entfallen können, zeigt erst der Gastlauf in Etappe 3.

**b) Vermutlich schon heute überholt** (Doku beschreibt 3.0.94; Gasttest steht aus)

- `UnsafeAccessor` auf `PageAllocator` in `CosmosSystemInfo.cs:30`: `MemoryDiagnostics` ist öffentlich und wird in
  `CosmosSystemFiles.cs:36` schon genutzt.
- Fehlendes TLS-`close_notify`: `SocketPlug.cs:253` meldet Schreibbereitschaft inzwischen auch bei `CLOSE_WAIT`.
- „Serielle Ausgabe nicht zeilenatomar“: `Serial.WriteString` sperrt inzwischen je Aufruf (`Serial.cs:109-134`).

**c) Brauchen einen Patch in der Cosmos-Quelle**

| Gruppe | Umwege in ZehOS | Größe der Korrektur |
| --- | --- | --- |
| ext2 | fünf Plugs (`RemoveEntry`, `Rmdir`, `Truncate`, `FreeInode`, Formatter), Feature-Prüfung, Verzeichnisgrenze 8189, Attrappentypen | je klein bis mittel |
| VFS | Nur-Lese-Wrapper (`Release` ruft immer `Fsync`), kein `Unregister`, private Mount-Abfrage, `lstat` = `stat`, wirkungsloses `utimensat` | klein bis mittel |
| Dateisystem-Sperre | `StorageLock` um jeden Plattenzugriff (rund 60 Methoden) | groß |
| GPT | eigener Leser und Schreiber, weil Cosmos CRC 0 schreibt und nicht prüft | mittel |
| Eingabe | Tastatur-Plug (Break-Codes gehen verloren), Maus-Plug mit sechs `UnsafeAccessor`, deutsches Layout, USB-Tablet | klein bis mittel |
| Zeit | VMware-Kalibrierung über CPUID statt PIT, samt kopierter Cosmos-Quelle | klein bis mittel |
| CPU-Ausnahmen | `CosmosPanicHaltPlug`, `IRQContext`-Attrappe mit festen Versätzen 120/152 | mittel (öffentlicher Hook) |
| Kleinteile | Teilrechteck-Ausgabe (`Canvas._display`), zufälliger TCP-Startport, RDSEED/RDRAND-Importe, `malloc` über 4 GiB | klein |
| Build | `InvalidateStaleCObjects`, `MapNativeSourcePaths`, `TreatAsLocalProperty`, `check-plug-binding.py` | klein |

**Abwägung gegen die Leitregel.** Jeder Patch aus Gruppe c bleibt nur wirksam, solange wir selbst bauen. Beim
Rücktausch auf ein NuGet-Release müsste der zugehörige Plug zurückkommen, denn Upstream erfährt von den Fehlern nichts
(keine Issues oder PRs bei Cosmos). Solange der eigene Build als Übergang gedacht ist, bleiben die Plugs deshalb
stehen, und `PATCHES.md` bleibt leer bis auf das, was SMP zwingend braucht. Gruppe c lohnt erst, wenn der eigene
Build dauerhaft bleiben soll (Entscheidung 6). Das wäre dann ein eigenes Vorhaben, ein Patch je PR.

## 9. Etappen

Jede Etappe ist ein eigener Auftrag mit eigenem Plan; die Reihenfolge ist bindend, weil jede die vorige voraussetzt.

| Etappe | Inhalt | Abnahme |
| --- | --- | --- |
| 0 Audit | Liste aller geteilten Zustände zwischen Interrupt, Hauptschleife und Workern (C# und C), je Eintrag die Sperre oder der Befund „fehlt“ | Dokument; keine Codeänderung |
| 1 Scheduler und Topologie auf 3.0.97 | Klassenscheduler ersetzt Round-Robin auf einer CPU; `CpuTopology` mit Hosttests; Eingabe-Sperren | Hosttests; Gast: `THREAD_PROBE`, `MESSAGES_PROBE` PASS; ClassiCube- und Doom-Bildraten nicht unter master; Wartezeit der Hauptschleife unter Last gemessen |
| 2 Cosmos aus Quelle, ohne SMP | Quellbaum, Paketbau, lokaler Feed, CI; Stand genau `efcf20c` | ISO aus eigenen Paketen besteht dieselben Host- und Gastprüfungen wie mit NuGet 3.0.97 |
| 3 SMP-Stapel, alle Threads auf CPU 0 | Upstream-Stapel einspielen; Adapter auf neue Naht; Hauptschleife als Thread; Plugs angleichen; Standardaffinität = CPU 0 | `-smp 1` und `-smp 4`: gesamte Gastsuite grün; weitere CPUs online und im Idle |
| 4 Worker auf weiteren CPUs | Wasm-Worker, dann Hintergrundaufgaben über Affinität freigeben; Platzierung und Ausgleich aktiv | neue `SMP_PROBE` (parallele Zähler, GC unter Last, Sperrstress, Migration) PASS; Speicherprobe aus #247 fehlerfrei; GC-Pausen gemessen |
| 5 Hauptschleife frei, Hyperthreads | Oberfläche verlässt CPU 0; Regeln aus 5.3; Panic und Energieaktionen über alle CPUs | Matrix aus Abschnitt 9a; `test-power-commands.ps1` mit 4 CPUs; Recovery mit einer CPU |
| 6 Ausbau | sobald ein Cosmos-Release SMP enthält: Version umstellen, Feed und Quellbaum entfernen, Patches gegenprüfen | gesamte Suite grün mit NuGet-Paketen; `PATCHES.md` leer oder Rest begründet |

In Weg A entfallen die Etappen 2 und 6; Etappe 3 beginnt mit dem Release.

### 9a. Prüfmatrix

| Umgebung | CPU-Aufbau | Stand |
| --- | --- | --- |
| QEMU TCG | 1; 2; `4,cores=2,threads=2`; 8 | zu prüfen, erste Umgebung |
| QEMU KVM (Clerk) | wie TCG | zu prüfen |
| QEMU WHPX | 2; 4 | offen; WHPX bricht schon heute mit `-cpu max` ab |
| VMware | `numvcpus` 2 und 4 | offen; keine Hyperthreads im Gast |
| echte Hardware | mit Hyperthreading | offen |

## 10. Offene Entscheidungen (Peter)

1. **Weg A oder B.** Empfohlen B. Das ändert die bisherige Linie „Cosmos-DLL nicht patchen“: Die Pakete kämen aus
   eigenem Bau, inhaltlich aber aus Upstream-Commits.
2. **Scheduler:** Klassenscheduler (Abschnitt 6) oder die kleinere Stride-Variante.
3. **Ort der Cosmos-Quelle:** eigenes Repository als Submodul oder Unterordner in ZehOS (Abschnitt 7).
4. **CPU 0 als Housekeeping-CPU** wie Upstream. Bei zwei CPUs hat die Oberfläche dann weiter nur eine.
5. **Hyperthreads und Wasm-Apps:** Kern teilen erlaubt (Entwurf) oder schon jetzt verbieten.
6. **Umwege aus Abschnitt 8:** im eigenen Build beheben oder der Isolation wegen stehen lassen.

## 11. Risiken

- Upstream ändert den Entwurfsstapel oder die experimentelle Naht noch. Gegenmittel: fester Commit, Nachziehen als
  eigene Änderung mit nativem Build und Gastlauf, Naht in einer Datei.
- Mit dem Stapel kommen 12 unveröffentlichte `gen3`-Commits. Die Speicherqualifikation (Etappe E4) gilt für 3.0.97
  und müsste für diesen Stand wiederholt werden.
- Rennen, die auf einer CPU nie auffielen (ZehOS und Cosmos). Gegenmittel: Etappe 0, stufenweise Freigabe über
  Affinität (Etappen 3 bis 5), Rückfall „Ein Kern“.
- Längere GC-Pausen mit mehr CPUs.
- Kein Nachweis auf echter Hardware und unter VMware/WHPX, bis dort gemessen ist.
