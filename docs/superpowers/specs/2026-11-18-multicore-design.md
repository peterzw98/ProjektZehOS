# Multicore und Hyperthreading: Architekturentwurf

Stand 18. November 2026, Auftrag Peter. Entwurf; Peters Entscheidungen vom 18. November sind eingearbeitet (Abschnitt 10).
Kein Kernelcode ist geändert. Belege beziehen sich auf ZehOS master `5c5538d`, Cosmos 3.0.97 (Paketcommit `efcf20c`)
und den Upstream-Branch `feat/smp-phase5b` (`4495213`, gelesen am 18. November 2026).

## 1. Ziel

ZehOS nutzt alle logischen CPUs des Rechners, kennt deren Aufbau (Verbund, Kern, Hyperthread) und verteilt Threads
danach. Der Round-Robin-Scheduler wird durch einen Scheduler ersetzt, der Klassen, mehrere CPUs und Hyperthreads kennt.

Leitregel (Peter, 18. November): **so isoliert wie möglich bauen.** Stellt Cosmos seine eigene SMP-Umsetzung fertig,
bauen wir den SMP-Teil unseres Builds aus und deren Stand ein, ohne ZehOS umzuschreiben.

Zum selben Vorhaben gehört jetzt: Cosmos wird aus Quelle gebaut, und Umwege um Cosmos-Fehler werden dort an der
Wurzel behoben, zuerst ext2 und die Stellen aus 12.4 (Abschnitt 8).

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

Vorhanden: `PerCpuState`, Heap-, Seiten- und Seriell-Sperre, Ausnahmezustand je Thread (Phase 0 von Upstream, in
3.0.97 enthalten). Cosmos' `SpinLock` ist `internal` (`Core/Scheduler/SpinLock.cs:17`), ZehOS kann ihn nicht nutzen.

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
  auf den übrigen. Bei zwei CPUs hat der Hauptfluss damit weiter genau eine. Übernommen (Entscheidung 4); erwartet
  werden mindestens vier CPUs.
- **Kein TSS, keine IST-Stacks, kein Hook für CPU-Ausnahmen, keine Operation „Thread von außen beenden“.** Das sind
  die Stellen, an denen 12.4 heute Umwege braucht; der Stapel ändert daran nichts.
- `SpinLock` dreht ohne `pause`; auf Hyperthreads bremst das den Geschwisterthread.
- **Stacks beendeter Threads bleiben belegt.** `ReleaseStack` gibt nur den Stack eines Threads frei, der nie gelaufen
  ist (`SchedulerThread.cs:307-321`, drei Aufrufer beim Ablehnen eines Threads).
- **`Main` wird mit der Affinität „Compute“ erzeugt**, also auf allen CPUs außer 0. `KernelCpus.DefaultAffinity` zu
  setzen verschiebt den Hauptthread nicht; das tut nur `ThreadAffinity.Current` (`KernelCpus.cs:62-72`).
  `Environment.ProcessorCount` liest die Standardaffinität beim ersten Zugriff und behält den Wert.

## 4. Wege

| | A: auf Upstream-Release warten | B: Cosmos aus Quelle bauen, Upstream-Stapel einspielen | C: SMP-Mechanismus selbst schreiben |
| --- | --- | --- | --- |
| Eigener Code im heikelsten Teil (CPU-Start, GC-Stopp, Sperrordnung) | keiner | keiner, nur Upstream-Commits | rund 6.000 Zeilen Entsprechung |
| SMP in ZehOS ab | unbekannt (Release-Takt bis 1. November: fünf Versionen in vier Wochen) | nach Etappe 5 | Monate |
| Ausbau, wenn Upstream fertig ist | entfällt | Grundstand wechseln, die übernommenen SMP-Commits entfallen (Isolationsregel 6) | großer Umbau, zwei Mechanismen weichen ab |
| Umwege an der Wurzel beheben | nein | ja (Abschnitt 8) | möglich |
| Risiko | Termin, Lücken aus Abschnitt 3 bleiben | Entwurfscode ändert sich; 12 ungeprüfte `gen3`-Commits kommen mit | Fehler in GC und Sperren, Dauerpflege |

**Entschieden: Weg B.** 3.0.97 selbst bringt kein SMP mit, es gibt dort also nichts „Schlechtes“ zu
ersetzen. Der Upstream-Stapel ist nicht schlecht, sondern unfertig; ein Eigenbau (C) würde dieselbe Arbeit mit mehr
Risiko wiederholen und widerspricht der Leitregel, weil er sich nicht mehr austauschen lässt. Weg B ist zugleich die
isolierteste Lösung: Unser „eigener Bau“ ist Upstreams eigener Code, nur früher.


## 5. Zielarchitektur

```
ZehOS (bleibt beim Tausch unverändert)
  Scheduling/ClassSchedulerCore<T>     Policy, ohne Cosmos-Typen, hostgetestet
  Scheduling/CpuTopology               Kern-/Geschwisterzuordnung, ohne Cosmos-Typen, hostgetestet
  Scheduling/CosmosClassScheduler      Adapter auf IScheduler (einzige Datei mit Naht-Signaturen)
  Scheduling/CosmosCpuTopologyReader   liest CPUID und APIC-IDs
  Kernel.cs                            wählt Scheduler, setzt Affinitäten
------------------------------ Naht: öffentliche Cosmos-API in der Form des Upstream-Stapels -----------
Cosmos (Branch cosmos: Release-Stand + Upstream-SMP-Stapel + ZehOS-Patchreihe)
  Per-CPU-Block, CPU-Start, IPIs, Sperren, GC-Stopp, Affinität, Platzierungsaufrufe
```

### 5.1 Isolationsregeln

1. **Der Cosmos-Baum enthält drei Arten von Commits:** den Stand eines Releases (heute 3.0.97), unverändert
   übernommene Upstream-Commits (der SMP-Stapel) und eigene Patches. Jeder eigene Patch ist ein eigener Commit und
   steht in `PATCHES.md` mit Commit, Grund, entferntem ZehOS-Umweg und Prüfung. Die Reihenfolge im Verlauf ist die
   der Entstehung; `PATCHES.md` ist die Liste, nach der die Patches auf einen neuen Grundstand übertragen werden.
2. **Im Cosmos-Baum liegt keine ZehOS-Policy.** Patches beheben Fehler oder öffnen eine Schnittstelle; Scheduler,
   Topologie und Appregeln bleiben in ZehOS.
3. **ZehOS spricht für SMP nur die API, die Upstream einführt** (`IScheduler` in der Form von Phase 3b/5b,
   `ThreadAffinity`, `KernelCpus`, `Environment.ProcessorCount`). Kein neuer `UnsafeAccessor` auf SMP-Interna.
4. **Alle Naht-Signaturen des Schedulers stehen in einer Datei** (`CosmosClassScheduler.cs`). Ändert Upstream die
   experimentelle Naht noch einmal, ändert sich nur sie.
5. **Die Paketversion ist der Schalter.** `ProjektZeh.csproj` und die CI lesen die Cosmos-Version aus einer
   Eigenschaft (`3.0.97-zehos.N` aus dem lokalen Feed).
6. **Tausch, wenn ein Cosmos-Release SMP enthält:** Der Branch beginnt neu mit dem Stand dieses Releases, die
   übernommenen Upstream-Commits entfallen, die Patches aus `PATCHES.md` werden darauf neu aufgespielt. Der eigene Build bleibt, solange `PATCHES.md` nicht leer ist: Upstream
   erfährt von unseren Korrekturen nichts (keine Issues oder PRs bei Cosmos), ein reines NuGet-Release enthielte sie nicht.

### 5.2 CPU-Start und Per-CPU-Zustand

Kommt vollständig aus Cosmos (Phasen 1 und 2): Limine startet jede logische CPU, auch Hyperthreads, und parkt sie;
Cosmos gibt sie nacheinander frei, jede richtet APIC und Timer ein und endet in ihrem Idle-Thread (`hlt`-Schleife).
ZehOS ergänzt nur:

- Bootzeile und `sysinfo`: Zahl der CPUs, Kerne und Hyperthreads.
- CPU-Begrenzung ohne Cosmos-Patch: Mit `zehos.cpus=1` auf der Kernel-Kommandozeile (`Bootloader/limine.conf`,
  gelesen über `Limine.Cmdline`) setzt ZehOS als Erstes im Hauptthread `ThreadAffinity.Current` und
  `KernelCpus.DefaultAffinity` auf CPU 0, vor dem ersten Threadstart und vor dem ersten Zugriff auf
  `Environment.ProcessorCount`. Die anderen CPUs sind online, bleiben aber im Idle. Der Recovery-Build
  (`ZehOSRecovery`) verhält sich immer so.
- `zehos.smt=off` nimmt die zweiten Hyperthreads jedes Kerns aus der Standardaffinität.

### 5.3 Topologie und Hyperthreading

`CpuTopology` ordnet jedem CPU-Index Verbund, Kern und Geschwister zu. Quelle sind die APIC-ID jeder CPU und die
Feldbreiten aus CPUID (`X86Base.CpuId`, wie in `CosmosVmwareClockPlug`). Die Zuordnung CPU-Index zu APIC-ID ist in
Cosmos intern (`Smp.TryGetHardwareId`); ZehOS liest sie deshalb selbst, indem je CPU ein dorthin gepinnter Thread
seine APIC-ID liest (CPUID-Blatt `0x0B`, Register EDX, sonst `LocalApic.GetId()`). Feldbreiten:
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
wie gleiche Kerne. Zwei Wasm-Apps dürfen sich einen Kern teilen (Entscheidung 5); sie teilen sich dann Caches
(Seitenkanäle). Eine Regel „nie zwei Apps auf einem Kern“ bleibt eine mögliche Verschärfung für 12.4.

### 5.4 Sperren und GC

Gelten für jeden ZehOS-Code, der in Scheduler-Hooks oder Interrupts läuft:

- Unter einem Spinlock und in den Scheduler-Hooks für Bereitwerden, Auswahl, Tick und Platzierung wird nichts
  allokiert und nichts protokolliert. Der Scheduler-Kern arbeitet deshalb auf vorab dimensionierten Feldern, wie heute
  `RoundRobinQueue`. Allokieren dürfen nur `OnThreadCreate` und die übrigen Lebenszyklus-Hooks (`IScheduler.cs:18-21`).
- Eine Interruptmaske ist keine Sperre mehr. Wo Unterbrechung und Thread Daten teilen, braucht es eine IRQ-sichere
  Sperre. ZehOS bringt dafür eine eigene mit (`CosmosIrqLock`: Interrupts maskieren, dann drehen). Sie nimmt nicht am
  Stopp-Protokoll des GC teil; deshalb gilt in ihr ohne Ausnahme: keine Allokation, kein Protokoll, keine zweite
  Sperre, nur wenige Zeilen.
- Der GC bleibt „Welt anhalten“. Mit mehr CPUs stehen bei jeder Sammlung alle; die Pausen werden gemessen
  (Abnahme in Etappe 6), bevor die Oberfläche auf eine andere CPU als die Treiber wandert.

### 5.5 Was sich in ZehOS ändern muss

| Stelle | Heute | Mit SMP |
| --- | --- | --- |
| `Input/CosmosInputService.cs:59`, `CosmosKeyboardPlug.cs:34`, `CosmosUsbTabletDriver.cs:53` | Maske schützt Puffer zwischen Interrupt und Hauptschleife | `CosmosIrqLock` (5.4); Plug gegen Upstreams neue `KeyboardManager`-Sperre (4b) abgleichen |
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

Der neue Scheduler heißt hier **Klassenscheduler**. Er ersetzt Round-Robin schon in Etappe 4, vor dem SMP-Stapel.

**Grenze bis Etappe 5.** Auf 3.0.97 ist die Hauptschleife der Idle-Thread und blockiert nie; ihr `Thread.Sleep`
wartet per Halt, ohne die CPU abzugeben. Als Klasse „Interaktiv“ würde sie die Worker auf 2 von 10 Ticks drücken, auch
wenn die Oberfläche nichts zu tun hat. In Etappe 4 läuft sie deshalb in der Klasse Normal mit der heutigen
Idle-Sonderregel und verhält sich wie unter Round-Robin; neu ist dort nur die Klasse Hintergrund. Der Vorrang der
Oberfläche kommt in Etappe 5, sobald `Main` ein normaler Thread ist (Phase 5b).

**Aufbau.** `ClassSchedulerCore<T>` (ohne Cosmos-Typen) hält je CPU drei Queues fester Größe.
`CosmosClassScheduler` reicht die Hooks durch. Auf 3.0.97 implementiert der Adapter die alte Naht, danach die neue;
der Kern bleibt gleich.

**Klassen.** Gesetzt beim Start über `ThreadStarter.TryStart` (neuer Parameter; einziger Startweg im Produktivcode,
die Proben starten Threads direkt und bleiben Normal) und `SchedulerManager.SetPriority`:

| Klasse | Threads | Zeitscheibe |
| --- | --- | --- |
| Interaktiv | Hauptschleife (Desktop, Textshell, Eingabe), ab Etappe 5 | 1 Tick (10 ms) |
| Normal | Wasm-Worker, Kennwort-Worker, Shell-Jobs | 2 Ticks (wie heute) |
| Hintergrund | `BackgroundTaskService`, Netzautomatik, Downloads | 4 Ticks |

**Auswahl je CPU.** Die höchste nicht leere Klasse läuft, innerhalb der Klasse reihum. Wird ein Thread einer höheren
Klasse bereit, verdrängt er sofort. Gegen Verhungern gilt eine feste Regel statt Alterung: Wartet eine niedrigere
Klasse, kommt sie nach 8 aufeinanderfolgenden Scheiben der höheren einmal dran. Ab Etappe 5 behält eine dauernd
rechnende Hauptschleife so neben einem Wasm-Worker 8 von 10 Ticks; eine wartende gibt die CPU ganz ab. Heute bekommt
sie unter Last ein Drittel, ob sie rechnet oder wartet.

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

**Vergleich mit Cosmos' eigener Policy (Stride).** Stride ist der Standard-Scheduler von Cosmos; ZehOS ersetzt ihn
heute durch `SetScheduler`. Die kleinste Lösung wäre, den eigenen Scheduler zu löschen und Klassen über
`SchedulerManager.SetPriority` auf Tickets abzubilden.

| | Stride (Cosmos) | Klassenscheduler (ZehOS) |
| --- | --- | --- |
| Eigener Code | fast keiner | rund 400 Zeilen plus Hosttests |
| Kosten je Entscheidung | sortiertes Einfügen, linear in der Queue-Länge (`InsertByPass`, 3.0.97: `StrideScheduler.cs:532`) | drei feste Queues, konstant |
| Oberfläche unter Last | Vorsprung von einer halben Zeitscheibe für „interaktive“ Threads, per Heuristik | verdrängt sofort, per Regel |
| Fairness bei vielen gleichen Threads | anteilig nach Tickets, sehr gut | reihum in der Klasse |
| Platzierung | bleibt auf der letzten CPU, wechselt nur zu einer CPU mit unter 80 % von deren Ticketlast (SMP-Stand `StrideScheduler.cs:363-412`); Hyperthreads unbekannt, ein freies Geschwister eines belegten Kerns zählt wie ein freier Kern | erst ganze Kerne (5.3) |
| Verhalten nachvollziehbar und hostgetestet | Cosmos-intern, `internal` | ja |

Bei den zehn bis zwanzig Threads von ZehOS sind die Kosten je Entscheidung in beiden Fällen unerheblich. Was die
Leistung bestimmt, ist die Platzierung: Zwei rechnende Threads auf den Geschwistern eines Kerns leisten zusammen
deutlich weniger als auf zwei Kernen. Auf Rechnern mit Hyperthreading ist der Klassenscheduler deshalb vom Entwurf her
die bessere Wahl, und er gibt der Oberfläche eine feste Zusage statt einer Heuristik. **Empfehlung: Klassenscheduler.**
Gemessen ist das nicht. Zwei Messungen sichern die Wahl ab: Etappe 4 vergleicht auf 3.0.97 Round-Robin mit dem
Klassenscheduler (darf nicht schlechter sein). Etappe 5 vergleicht auf dem SMP-Stand Stride mit dem Klassenscheduler,
dort erstmals mit der Hauptschleife als Thread; Stride kostet dafür nur einen Build ohne `SetScheduler`. Liegt der
Klassenscheduler dort nicht mindestens gleichauf, wird die Empfehlung neu bewertet.

## 7. Eigener Cosmos-Build

Lizenz: BSD 3-Clause (`LICENSE`, „Copyright (c) 2007-2026, CosmosOS“). Selbst bauen und weitergeben ist erlaubt,
solange Copyright und Lizenztext in Quelle und ISO erhalten bleiben.

**So baut Cosmos sich selbst** (`.devcontainer/postCreateCommand.sh`, `Packages.slnx`): 23 Projekte werden in
Abhängigkeitsreihenfolge mit `dotnet pack` nach `artifacts/package/release` gepackt, das als lokaler NuGet-Feed dient.
`Cosmos.Sdk` zieht Patcher, ILC-Anbindung, Assembler- und C-Build in derselben Version nach; die CLI `cosmos` und
`Cosmos.Patcher` sind globale Tools aus demselben Feed. ILC selbst kommt weiter von Microsoft. Ein `ProjectReference`
auf die Cosmos-Projekte funktioniert nicht, weil SDK, Patcher und ILC Pakete mit `build/`-Targets erwarten.

**Einbindung in ZehOS: ein eigener Branch `cosmos` im ZehOS-Repository** (Entscheidung 3), nach dem
Muster des Branches `packages`.

| | Branch `cosmos` in ZehOS | eigenes Repository |
| --- | --- | --- |
| Review, Branchregeln, Reviewer, Agentenzugriff | vorhanden | alles neu einzurichten |
| Größe | nur der Dateibaum: 1.730 Dateien, 13 MB (ohne Upstream-Historie) | volle Historie, rund 290 MB |
| Versehentlicher PR bei Upstream | nicht möglich | bei einem GitHub-Fork der Standardweg von `gh pr create` |
| Fester Commit für den Build | `git fetch origin <sha>` im selben Repository | zweites Repository mit eigenem Zugriff |
| Upstream nachziehen | Commits aus einem temporären Remote aufspielen | normales `git merge` |

Für einen Baum, der nur diese Patchreihe trägt und danach nicht weiterlebt, überwiegen die Vorteile des Branches.

- Der Branch beginnt mit einem einzelnen Commit, der den Dateibaum von `efcf20c` enthält, ohne `.github/workflows`
  (sonst liefen Cosmos' eigene Workflows in unserem Repository). Unter Windows braucht die Arbeitskopie
  `core.longpaths=true` (ein Testfixture hat einen überlangen Dateinamen).
- Änderungen am Branch `cosmos` laufen per Pull Request gegen diesen Branch (Branchregel wie bei `packages`).
- ZehOS master nennt den zu bauenden Commit in einer Datei (`deploy/cosmos/COMMIT`). `scripts/build-cosmos.sh` holt
  genau diesen Commit, packt die Pakete mit der Version `3.0.97-zehos.N` in einen lokalen Feed und installiert CLI
  und Patcher daraus. Die eigene Nummer verhindert, dass der NuGet-Cache das gleichnamige Paket von nuget.org liefert.
- `NuGet.Config` im ZehOS-Stamm leitet `Cosmos.*` auf den lokalen Feed (`packageSourceMapping`), alles andere bleibt
  bei nuget.org. `ProjektZeh.csproj` (SDK-Zeile, zwei `PackageReference`) und die CI (`ci.yml:217-218`, heute fest
  `--version 3.0.97`) lesen die Version aus einer Eigenschaft.
- Die CI baut die Pakete einmal je Commit und legt sie im Cache ab. Veröffentlicht werden sie nicht.
- **Ein Cosmos-Patch wird durch den ZehOS-PR geprüft, der ihn verwendet:** Dieser PR setzt `deploy/cosmos/COMMIT`
  auf den Patch-Commit und entfernt den Umweg; die normale ZehOS-CI (Hosttests, nativer Build, Gastläufe) läuft
  dann gegen den gepatchten Stand.

**Aufwand und Pflege:** einmalig Skript, Feed und CI-Umstellung (Etappe 1). Laufend: Jedes Nachziehen des
Upstream-Stands ist eine eigene Änderung mit nativem Build und Gastsuite. Mit `PathMap` im eigenen Build verschwinden
die festen Upstream-Pfade aus den DLLs; `scripts/check-release-paths.py` braucht dann keine Ausnahmeliste mehr.

Nicht geprüft: ob der Paketbau unter Windows ohne Anpassung durchläuft (`postCreateCommand.ps1` liegt bei), und die
Bauzeit.

## 8. Umwege an der Wurzel beheben

Vollständiges Inventar mit ZehOS-Datei und Ursache in der Cosmos-Quelle:
[2026-11-18-cosmos-umwege-inventar.md](2026-11-18-cosmos-umwege-inventar.md). Entschieden (6): beheben, zuerst ext2,
dann 12.4. Upstream hat an `FileSystem/Ext2` seit 3.0.97 nichts geändert; die Fehler sind dort also noch vorhanden.

Jeder Patch folgt demselben Ablauf: Commit im Branch `cosmos` mit Eintrag in `PATCHES.md`, dann ein ZehOS-PR, der
den Commit einbindet, den Umweg entfernt und die bisherige Prüfung des Umwegs gegen den gepatchten Stand wiederholt.
Der Plug bleibt bis zu diesem PR unverändert in Kraft.

### 8.1 Patchreihe ext2 (Etappe 2)

| Patch in Cosmos | Entfernt in ZehOS | Prüfung |
| --- | --- | --- |
| `RemoveEntry` verschmilzt nur innerhalb eines Blocks (`Ext2Directory.cs:302, 340`) | `CosmosExt2RemoveEntryPlug` | E4.0-Lauf mit `e2fsck -fn` |
| `Rmdir` gibt alle Blöcke frei (`Ext2InodeOperations.cs:472`) | `CosmosExt2RmdirPlug` | wie oben |
| `Truncate` gibt geleerte Zeigerblöcke frei, auch dreifach indirekt (`Ext2Superblock.cs:1014, 1043`) | `CosmosExt2TruncatePlug` | wie oben, dazu große Datei |
| `FreeInode` schreibt Inode und dtime (`Ext2Superblock.cs:617`) | `CosmosExt2FreeInodePlug`, `Ext2InodeRelease` | wie oben |
| Formatter schreibt Backup-Superblöcke und korrekte Geometrie (`Ext2Formatter.cs:185`) | `CosmosExt2FormatterPlug`, `CosmosExt2Formatter` samt Kopie | Formatieren, `e2fsck`, Mount unter Linux |
| Mount prüft Feature-Bits (`Ext2Superblock.cs:154`) | `Ext2FeatureGate` großteils | Ablehnung fremder Features |
| Verzeichnisgrenze an die Blockzahl koppeln (`Ext2Directory.cs:19`) | Grenze 8189 in `FileService`, `CosmosDirectoryEntryLimit` | Verzeichnis über 8189 Einträge |
| Inode-Cache begrenzen (`Ext2Superblock.cs:20, 759`) | dokumentierte Grenze bei rund 17.500 Inodes | Speicherlauf |

Mit dem letzten ext2-Plug entfallen auch die Attrappentypen in `CosmosExt2PlugTypes.cs`. Die Reihe gehört fachlich
zur Storage-Arbeitsgruppe und wird mit ihr abgestimmt; sie berührt deren laufende Qualifikation.

### 8.2 Patchreihe 12.4 (Etappe 3)

Betroffen ist nur, was I1 schon gemergt hat. I2 ist geplant, aber nicht umgesetzt und derzeit zurückgestellt.

| Patch in Cosmos | Entfernt in ZehOS | Prüfung |
| --- | --- | --- |
| Öffentlicher Hook für CPU-Ausnahmen: bekommt Vektor und Kontext, entscheidet „anhalten“ oder „Thread aufgeben“ (`Cosmos.Kernel/Panic.cs:19-36`) | `CosmosPanicHaltPlug`, `UnsafeAccessor` auf `ScheduleFromInterrupt` und `InternalCpu.Halt`, Import `_native_get_context_switch_sp` | `test-isolation-probe.py`, positiv und negativ |
| `IRQContext` öffentlich (`Core/CPU/IRQContext.cs:16`) | `CosmosIrqContextStub`, feste Versätze 120/152 | wie oben |
| LINT1 als NMI einrichten (`LocalApic.cs:172`) | Entmaskierung in `CosmosIsolationProbe` | NMI-Gegenprobe |
| Stacks beendeter und aufgegebener Threads freigeben. Ein Thread kann seinen eigenen Stack nicht freigeben; nötig ist ein Aufräumer, der tote Threads nach dem Wechsel weg von ihnen abräumt. Eigener, mittlerer Patch nach Etappe 5 | Quarantäne-Zeile im Scheduler; Leck von 8 MiB je Fehler; Zwang zu langlebigen Workern | Isolationsprobe wiederholt und 100 kurze Threads, freier Speicher bleibt konstant |

**12.4 muss dafür nicht vorher fertig sein.** Die Reihenfolge ist umgekehrt günstiger: I2 (erzwungenes Beenden)
braucht genau die Operation aus der letzten Zeile und baut besser auf dem öffentlichen Hook auf als auf den heutigen
Plugs. Vorhanden ist bereits `SchedulerDiagnostics.RequestKill` (öffentlich, schon in 3.0.97): Es beendet einen
wartenden Thread sofort und markiert einen laufenden nur als tot, räumt ihn aber nie ab. Bindend ist nur: I2 wird nicht gleichzeitig mit Etappe 3 oder Etappe 5 umgesetzt, weil alle drei
`CosmosFaultIsolation.cs` und `WasmRuntime.cs` ändern. Empfohlen: I2 nach Etappe 5 wieder aufnehmen.

Nicht in dieser Reihe, aber jetzt möglich und eigens zu entscheiden: eigenes TSS mit IST-Stacks. Das beseitigte die
Red-Zone-Gefahr für AOT-Code (heute Umweg `wamrc-noredzone` und `check-aot-redzone.py`) und machte Stacküberläufe
erkennbar, verlangt aber einen Umbau des Kontextwechsels und ist groß.

### 8.3 Weitere Umwege (Vorrat, je nach Bedarf)

- **Entfallen mit dem SMP-Stapel selbst, ohne eigenen Patch** (zu belegen in Etappe 5): Idle-Sonderregel des
  Schedulers (Phase 5b), Stackprobe `ThreadStarter`/`CosmosStackProbe` (Nullprüfung in `SchedulerThread.cs:261-271`).
- **Vermutlich schon heute überholt** (Doku beschreibt 3.0.94, Gasttest steht aus): `UnsafeAccessor` auf
  `PageAllocator` (`MemoryDiagnostics` ist öffentlich), fehlendes TLS-`close_notify` (`SocketPlug.cs:253`),
  „serielle Ausgabe nicht zeilenatomar“ (`Serial.cs:109-134`).
- **Mit Patch behebbar:** VFS (Nur-Lese-Wrapper, `Unregister`, Mount-Abfrage, `lstat`, `utimensat`), GPT mit CRC,
  Eingabe (Break-Codes, Rohdeltas der Maus, deutsches Layout, Tablet), VMware-Zeitkalibrierung, Teilrechteck-Ausgabe,
  zufälliger TCP-Startport, Zufallsbefehle, `malloc` über 4 GiB, Build (`InvalidateStaleCObjects`,
  `MapNativeSourcePaths`, `TreatAsLocalProperty`, Plug-Bindung als Fehler).
- **Bewusst nicht:** `StorageLock`. Die globale Sperre funktioniert und ist unter SMP nötig; eine feinere Sperre in
  Cosmos wäre groß und brächte erst bei parallelem Plattenzugriff etwas.

## 9. Etappen

Jede Etappe ist ein eigener Auftrag mit eigenem Plan.

| Etappe | Inhalt | Abnahme |
| --- | --- | --- |
| 0 Audit | Liste aller geteilten Zustände zwischen Interrupt, Hauptschleife und Workern (C# und C), je Eintrag die Sperre oder der Befund „fehlt“ | Dokument; keine Codeänderung |
| 1 Cosmos aus Quelle | Branch `cosmos` mit dem Stand `efcf20c`, Bauskript, lokaler Feed, CI | ISO aus eigenen Paketen besteht dieselben Host- und Gastprüfungen wie mit NuGet 3.0.97 |
| 2 Patchreihe ext2 | Abschnitt 8.1, ein Patch je PR-Paar | je Patch die genannte Prüfung; am Ende kein ext2-Plug mehr |
| 3 Patchreihe 12.4 | Abschnitt 8.2 ohne die letzte Zeile | Isolationsprobe positiv und negativ; kein Plug auf `Panic` mehr |
| 4 Scheduler und Topologie | Klassenscheduler ersetzt Round-Robin auf einer CPU (Hauptschleife noch Klasse Normal mit Idle-Regel); `CpuTopology` mit Hosttests; Eingabe-Sperren | Hosttests; Gast: `THREAD_PROBE`, `MESSAGES_PROBE` PASS; Bildraten von ClassiCube und Doom nicht unter Round-Robin |
| 5 SMP-Stapel, alle Threads auf CPU 0 | Upstream-Stapel auf den Branch `cosmos` aufspielen und Konflikte mit den eigenen Patches lösen (sicher: LINT1 in `LocalApic.cs`); Adapter auf neue Naht; Hauptschleife als Thread in Klasse Interaktiv, Idle-Regel entfällt; verbliebene Plugs angleichen; Hauptthread und Standardaffinität auf CPU 0; Patch „Stacks freigeben“ aus 8.2 | `-smp 1` und `-smp 4`: gesamte Gastsuite grün; weitere CPUs online und im Idle; Speicherqualifikation für den neuen Cosmos-Stand wiederholt; Vergleich Stride gegen Klassenscheduler (Abschnitt 6) |
| 6 Worker auf weiteren CPUs | Wasm-Worker, dann Hintergrundaufgaben über Affinität freigeben; Platzierung und Ausgleich aktiv | neue `SMP_PROBE` (parallele Zähler, GC unter Last, Sperrstress, Migration) PASS; Speicherprobe aus #247 fehlerfrei; GC-Pausen gemessen |
| 7 Hauptschleife frei, Hyperthreads | Oberfläche verlässt CPU 0; Regeln aus 5.3; Panic und Energieaktionen über alle CPUs | Matrix aus Abschnitt 9a; `test-power-commands.ps1` mit 4 CPUs; Recovery mit einer CPU |
| 8 Basiswechsel | sobald ein Cosmos-Release SMP enthält: neuer Grundstand, SMP-Schicht entfällt, Patchreihe neu aufspielen | gesamte Suite grün; `PATCHES.md` aktuell |

**Abhängigkeiten:** 0 und 4 brauchen kein geändertes Cosmos und können sofort beginnen. 2 und 3 setzen 1 voraus und
sind voneinander unabhängig. 5 setzt 1 und 4 voraus. 3 und 5 hängen fachlich nicht voneinander ab (der Stapel lässt
`IRQContext.cs` und `Panic.cs` unverändert), ändern in ZehOS aber dieselben Dateien und laufen deshalb nacheinander;
empfohlen 3 vor 5. 6 und 7 folgen in dieser Reihenfolge.

### 9a. Prüfmatrix

| Umgebung | CPU-Aufbau | Stand |
| --- | --- | --- |
| QEMU TCG | 1; 2; `4,cores=2,threads=2`; 8 | zu prüfen, erste Umgebung |
| QEMU KVM (Clerk) | wie TCG | zu prüfen |
| QEMU WHPX | 2; 4 | offen; WHPX bricht schon heute mit `-cpu max` ab |
| VMware | `numvcpus` 2 und 4 | offen; keine Hyperthreads im Gast |
| echte Hardware | mit Hyperthreading | offen |

## 10. Entscheidungen

Peter, 18. November 2026:

- **1 Weg B:** Cosmos aus Quelle bauen, Upstreams SMP-Stapel aufspielen.
- **4 CPU 0 bleibt Housekeeping-CPU** wie bei Upstream; erwartet werden mindestens vier CPUs.
- **5 Wasm-Apps dürfen sich einen Kern teilen.**
- **6 Umwege werden an der Wurzel behoben,** zuerst ext2, dazu die Stellen aus 12.4; deshalb beginnt der eigene
  Build vor der SMP-Arbeit (Etappe 1).

- **3 Die Cosmos-Quelle liegt im Branch `cosmos` des ZehOS-Repositorys** (Abschnitt 7).

Empfehlung, der Peter nicht widersprochen hat und die der Plan zugrunde legt:

- **2 Scheduler:** Klassenscheduler, abgesichert durch die Messungen in Etappe 4 und 5 (Abschnitt 6).

Folge von Entscheidung 6, bitte bewusst mittragen: Der eigene Cosmos-Build ist damit kein Übergang mehr. Er bleibt,
solange eigene Patches bestehen; austauschbar bleibt der SMP-Teil (Isolationsregel 6).

Offen:

- **7 TSS mit IST-Stacks** als eigenes, großes Vorhaben (Abschnitt 8.2), ja oder nein.

## 11. Risiken

- Upstream ändert den Entwurfsstapel oder die experimentelle Naht noch. Gegenmittel: fester Commit im Branch
  `cosmos`, Nachziehen als eigene Änderung mit nativem Build und Gastlauf, Naht in einer Datei.
- Mit dem Stapel kommen 12 unveröffentlichte `gen3`-Commits. Die Speicherqualifikation (Etappe E4) gilt für 3.0.97
  und wird für diesen Stand in Etappe 5 wiederholt.
- Eigene Patches in ext2 können Daten zerstören. Gegenmittel: je Patch die bestehende Gastprüfung mit `e2fsck` auf
  einer Kopie des Datenimages, der Plug bleibt bis zum bestandenen Lauf in Kraft.
- Jeder Cosmos-Patch muss bei jedem Basiswechsel neu aufgespielt und geprüft werden; die Reihe klein zu halten ist
  Teil der Pflege.
- Rennen, die auf einer CPU nie auffielen (ZehOS und Cosmos). Gegenmittel: Etappe 0, stufenweise Freigabe über
  Affinität (Etappen 5 bis 7), Rückfall „Ein Kern“.
- Längere GC-Pausen mit mehr CPUs.
- Kein Nachweis auf echter Hardware und unter VMware/WHPX, bis dort gemessen ist.
