# Multicore, Hyperthreading und eigener Cosmos-Build: Umsetzungsplan (Übersicht)

> **Für ausführende Agenten:** Dieser Plan ist in Etappen und Arbeitspakete zerlegt. Lies diese Übersicht ganz und danach
> nur die Datei deiner Etappe. Jedes Arbeitspaket ist ein eigener Branch und ein eigener Pull Request. Schritte haben
> Kästchen (`- [ ]`) zum Abhaken. Empfohlene Arbeitsweise je Paket: superpowers:subagent-driven-development oder
> superpowers:executing-plans.

**Ziel:** ZehOS baut Cosmos aus Quelle, behebt dort die ext2- und 12.4-Umwege an der Wurzel, nutzt alle logischen CPUs
und ersetzt Round-Robin durch einen Klassenscheduler, der Hyperthreads kennt.

**Architektur:** Den SMP-Mechanismus liefert Cosmos (Upstreams eigener Entwurfsstapel, im Branch `cosmos` des
ZehOS-Repositorys gebaut). Policy, Topologie und Appregeln liegen in ZehOS oberhalb der Scheduler-Schnittstelle. Eigene
Cosmos-Patches sind einzelne, in `PATCHES.md` gelistete Commits.

**Technik:** C# / .NET 10, Cosmos Gen3 3.0.97 (Commit `efcf20c`), NativeAOT, Limine, MSTest und Moq für Hosttests,
QEMU (TCG, KVM, WHPX) und VMware für Gastläufe.

**Entwurf:** [2026-11-18-multicore-design.md](../specs/2026-11-18-multicore-design.md), dazu das
[Inventar der Umwege](../specs/2026-11-18-cosmos-umwege-inventar.md). Der Plan setzt den Entwurf um; wer ein Paket
bearbeitet, liest die dort genannten Abschnitte.

**Zeilenangaben:** Sie gelten für ZehOS `master` `5c5538d` (wo vermerkt `6348c7a`), Cosmos `efcf20c` und den
Upstream-Stand `4495213`. `master` bewegt sich schnell; vor jeder Änderung die Stelle über den genannten Namen oder
den zitierten Text suchen.

## Etappen und Dateien

| Etappe | Inhalt | Plan | Voraussetzung |
| --- | --- | --- | --- |
| 0 | Audit geteilter Zustände (nur Dokument) | [e0-audit](2026-11-18-multicore-e0-audit.md) | keine |
| 1 | Cosmos aus Quelle, unverändert | [e1-cosmos-quelle](2026-11-18-multicore-e1-cosmos-quelle.md) | Peters Freigabe für den Branch `cosmos` |
| 2 | Patchreihe ext2 | [e2-ext2](2026-11-18-multicore-e2-ext2.md) | Etappe 1 |
| 3 | Patchreihe 12.4 (Ausnahme-Hook, LINT1) | [e3-ausnahme-hook](2026-11-18-multicore-e3-ausnahme-hook.md) | Etappe 1 |
| 4 | Klassenscheduler und Topologie auf einer CPU | [e4-scheduler](2026-11-18-multicore-e4-scheduler.md) | keine |
| 5 | SMP-Stapel, alle Threads auf CPU 0 | [e5-smp](2026-11-18-multicore-e5-smp.md) | Etappen 1 und 4; nicht gleichzeitig mit 3 |
| 6 | Worker auf weiteren CPUs | [e6-e7-verteilung](2026-11-18-multicore-e6-e7-verteilung.md) | Etappen 0 und 5 |
| 7 | Oberfläche verlässt CPU 0, Hyperthreads, Panic, Energie | [e6-e7-verteilung](2026-11-18-multicore-e6-e7-verteilung.md) | Etappe 6 |
| 8 | Basiswechsel auf ein Cosmos-Release mit SMP | Abschnitt unten | ein solches Release |

Technisch können die Etappen 0, 1 und 4 sofort und nebeneinander beginnen. Zur Einordnung in die übrige Arbeit hat der
Koordinator am 18. November 2026 im IRC Peters Reihenfolge weitergegeben: Storage und ClassiCube zuerst, dann der
Cosmos-Eigenbau, dann 12.4; Etappe 1 darf laufen, ohne Test- und Review-Kapazität der anderen Arbeitsgruppen zu
binden; die Etappen 2 und 3 beginnen erst nach dem Merge von Storage 7a-S4 und 7b. Stand 18. November 2026, 14 Uhr
(Koordinator, `#second`): Die Etappen 0, 1 und 4 laufen. Die Etappe 2 beginnt nicht vor dem Tag des Releases 0.6,
weil sie ext2-Verhalten ändert, das gerade abgenommen wird. Die Etappe 3 braucht zusätzlich Peters Freigabe für
12.4. Gastläufe für den Cosmos-Eigenbau auf dem gemeinsamen KVM-Rechner beginnen erst nach den Storage-Gastläufen.
Innerhalb jeder Etappe nennt die Etappendatei, welche
Pakete nebeneinander laufen können; Pakete mit demselben Buchstaben-Präfix und verschiedener Nummer (etwa `E2-P1`,
`E2-P2`) sind dafür geschnitten.

## Regeln für alle Pakete

Diese Regeln gehören zu jedem Arbeitspaket, auch wenn es sie nicht wiederholt. Die Projektregeln in `AGENTS.md` gelten
zusätzlich und gehen im Zweifel vor.

**Ablauf je Paket**

1. Paket und Branch im IRC-Kanal der Arbeitsgruppe ankündigen (am 18. November 2026 `#second`; maßgeblich ist das
   Kanaltopic); der Koordinator vergibt Pakete, Planfragen beantwortet der Lead `agent-peter-multithread`. Wer ein Paket hat, ist für
   dessen Dateien der einzige Schreiber.
2. Branch `task/mc-<paket>-<kurzname>` (klein geschrieben, etwa `task/mc-e4-a-topologie`) vom aktuellen `master`.
3. Schritte der Etappendatei in Reihenfolge; Test zuerst, wo ein Test genannt ist.
4. Vor dem PR: Hosttests, bei Kernelcode nativer Build, dazu die im Paket genannten Gastläufe. Im Team führen Clerks
   Gastläufe aus; der Engineer beauftragt sie mit Branch, Commit und Befehl.
5. Unabhängiges Review durch einen anderen Agenten, dann PR mit den Reviewern aus `AGENTS.md`. Kein Merge ohne
   Freigabe eines Projektbenutzers.

**Prüfbefehle** (vom Repository-Stamm)

```powershell
dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror
cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release
cosmos run --iso ProjektZeh/output-x64/ProjektZeh.iso --arch x64 -- -action shutdown=poweroff,reboot=reset
```

**Cosmos-Patches** (ab Etappe 1)

- Ein Patch ist genau ein Commit im Branch `cosmos`, eingebracht per PR gegen `cosmos`, mit einer Zeile in
  `PATCHES.md`: Commit, Datei, Grund, entfernter ZehOS-Umweg, Prüfung.
- Geprüft wird ein Patch durch den ZehOS-PR, der ihn verwendet: Er setzt `deploy/cosmos/COMMIT` auf den Patch-Commit,
  entfernt den Umweg und wiederholt dessen bisherige Prüfung. Der ZehOS-PR wird erst nach dem `cosmos`-PR gemergt.
- Ein Patch behebt einen Fehler oder öffnet eine Schnittstelle. ZehOS-Policy (Scheduler, Topologie, Appregeln)
  gehört nie in den Cosmos-Baum.
- Keine Issues, PRs oder Kommentare bei CosmosOS/Cosmos oder anderen fremden Projekten.
- Der Umweg in ZehOS bleibt unverändert in Kraft, bis der ZehOS-PR mit bestandener Prüfung gemergt ist.

**Nebenläufigkeit** (ab Etappe 4 für neuen Code, ab Etappe 6 für allen Code)

- In den Scheduler-Hooks für Bereitwerden, Auswahl, Tick, Platzierung und Ausgleich: keine Allokation, kein Protokoll,
  keine Sperre außer der von Cosmos übergebenen. Erlaubt ist Allokation in `OnThreadCreate` und `InitializeCpu`.
- Eine Interruptmaske ist keine Sperre. Daten, die ein Interrupt und ein Thread teilen, schützt `CosmosIrqLock`
  (Etappe 4, Paket E4-D). In ihr: keine Allokation, kein Protokoll, keine zweite Sperre, nur wenige Zeilen.
- Reihenfolge der ZehOS-Sperren bleibt: `StorageLock` vor `AppStorageGate`; neue Sperren nennt das jeweilige Paket.

**Daten und Tests**

- Schreibende Gasttests laufen auf einer isolierten Kopie des Datenimages, nie auf `.tools/qemu/data-fat32.img`.
- Ein Hosttest bleibt in seinem eigenen temporären Ordner.
- Ergebnisse werden mit Umgebung berichtet (QEMU-Version, Beschleuniger, CPU-Aufbau). Ein QEMU-Ergebnis belegt
  nichts für VMware oder echte Hardware; fehlende Läufe stehen als „offen“ im PR.

**Dokumentation je Paket:** bei geänderter Bedienung `README.md`, bei neuen Cosmos-Erkenntnissen
`docs/COSMOS_SUPPORT.md`. `ROADMAP.md` wird erst am Ende einer Etappe angefasst (letztes Paket der Etappe).

## Feste Namen und Werte

Diese Namen verwenden mehrere Etappen. Wer sie ändern will, ändert diese Tabelle im selben PR.

| Name | Bedeutung | Eingeführt |
| --- | --- | --- |
| Branch `cosmos` | Cosmos-Quellbaum im ZehOS-Repository | E1-A |
| `PATCHES.md` (Stamm des Branches `cosmos`) | Liste der eigenen Patches | E1-A |
| `deploy/cosmos/COMMIT` | eine Zeile: Commit des Branches `cosmos`, der gebaut wird | E1-B |
| `deploy/cosmos/VERSION` | eine Zeile: Paketversion, Start `3.0.97-zehos.1` | E1-B |
| `scripts/build-cosmos.sh` | baut die Pakete aus `COMMIT` in den lokalen Feed | E1-B |
| `deploy/cosmos/feed/` | lokaler NuGet-Feed; der Ordner ist versioniert, sein Inhalt nicht | E1-B |
| MSBuild-Eigenschaft `ZehOSCosmosVersion` | Cosmos-Version für SDK und Pakete | E1-B |
| `ProjektZeh.Scheduling.ThreadClass` | `Interactive = 0`, `Normal = 1`, `Background = 2` | E4-B |
| `ClassRunQueues<T>.TickNs` | `10_000_000` | E4-B |
| Zeitscheiben | Interaktiv 1 Tick, Normal 2 Ticks, Hintergrund 4 Ticks | E4-B |
| `ClassRunQueues<T>.StarvationLimit` | `8` | E4-B |
| Schedulername | `ZehOS Klassen` | E4-C |
| Kernel-Kommandozeile | `zehos.cpus=<n>`, `zehos.smt=off` | E5-C, E7-D |
| Probe-Schalter | `-p:ZehOSSmpProbe=true`, Konstante `ZEHOS_SMP_PROBE`, Marke `SMP_PROBE PASS` | E6-D |
| QEMU-CPU-Aufbauten der Matrix | `1`; `2`; `4`; `4,cores=2,threads=2`; `8,cores=4,threads=2` | E5-G |

## Entscheidungen, die Peter getroffen hat

Weg B (Cosmos aus Quelle mit Upstreams SMP-Stapel); Quelle im Branch `cosmos` des ZehOS-Repositorys; CPU 0 bleibt
Housekeeping-CPU, erwartet werden mindestens vier CPUs; Wasm-Apps dürfen sich einen Kern teilen; Umwege werden an der
Wurzel behoben, zuerst ext2 und 12.4. Der Klassenscheduler ist die zugrunde gelegte Empfehlung, abgesichert durch die
Messungen in E4-E und E5-I.

## Was noch offen ist

| Frage | Wer entscheidet | Blockiert |
| --- | --- | --- |
| Freigabe, den Branch `cosmos` anzulegen und eine Branchregel dafür zu setzen | Peter | E1-A |
| Lizenz der eigenen Cosmos-Patches. Der Plan nimmt an: wie ZehOS GPL-3.0, weil der Branch das Repository nie verlässt (E1-A, E2). Alternative: BSD 3-Clause wie Cosmos | Peter und Lars als Rechteinhaber | nichts; ändert nur den Text in `ZEHOS.md` und den Patch-Kommentar |
| Kommt die E4.0-Gastprobe (`origin/task/storage-e4-0-root-probe`) nach `master`? | Storage-Lead | Gastläufe in Etappe 2 (Paket E2-P0) |
| Neue Produktgrenze für Einträge je ext2-Verzeichnis; der Plan schlägt 65.536 vor | Storage-Lead | E2-P7, Schritt 3 |
| Läuft der Cosmos-Paketbau unter Windows? | Messung in E1-B, Schritt 6 | lokale Kernelbuilds unter Windows |
| Dauer des Paketbaus | Messung in E1-B, Schritt 5 | Zeitgrenzen in E1-C |
| TSS mit IST-Stacks als eigenes Vorhaben | Peter | nichts in diesem Plan |
| Wiederaufnahme von 12.4-I2 (empfohlen nach Etappe 5) | Peter | nichts in diesem Plan |
| Einzelbefunde des Audits | ergeben sich aus Etappe 0 | Zuschnitt von E6-C |
| Verhalten unter WHPX, VMware und echter Hardware | Messung in E7-E | Aussagen zu diesen Plattformen |

## Etappe 8: Basiswechsel

Auslöser: Ein veröffentlichtes Cosmos-Release enthält den SMP-Stapel. Ein Paket, ein Bearbeiter.

- [ ] Im Cosmos-Repository den Release-Commit bestimmen (`nuspec` des Pakets `Cosmos.Kernel.Core`, Feld `repository commit`).
- [ ] Im Branch `cosmos` einen neuen Grundstand-Commit mit dem Dateibaum dieses Commits anlegen (ohne `.github/workflows`), wie in E1-A.
- [ ] Jeden Eintrag aus `PATCHES.md` in Reihenfolge darauf übertragen (`git cherry-pick <commit>`); Einträge, die Upstream inzwischen selbst behoben hat, streichen und das in `PATCHES.md` vermerken.
- [ ] `deploy/cosmos/VERSION` auf `<neue Version>-zehos.1`, `deploy/cosmos/COMMIT` auf den neuen Stand.
- [ ] `CosmosClassScheduler.cs` gegen die veröffentlichte `IScheduler`-Schnittstelle übersetzen; Abweichungen dort beheben.
- [ ] Gesamte Host- und Gastsuite samt Matrix aus E7-E; Speicherqualifikation wie in E5-H.
- [ ] Ist `PATCHES.md` leer: `deploy/cosmos/`, `scripts/build-cosmos.sh`, `NuGet.Config`-Zuordnung und den Branch `cosmos` entfernen und die Pakete wieder von nuget.org beziehen.
