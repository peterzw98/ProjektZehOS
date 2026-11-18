# Multicore, Hyperthreading und eigener Cosmos-Build: Umsetzungsplan (Übersicht)

> **Für ausführende Agenten:** Dieser Plan ist in Etappen und Arbeitspakete zerlegt. Lies diese Übersicht ganz und danach
> nur die Datei deiner Etappe. Jedes Arbeitspaket ist ein eigener Branch und ein eigener Pull Request. Schritte haben
> Kästchen (`- [ ]`) zum Abhaken. Empfohlene Arbeitsweise je Paket: superpowers:subagent-driven-development oder
> superpowers:executing-plans.

**Ziel:** ZehOS baut Cosmos aus Quelle, behebt dort die ext2- und 12.4-Umwege an der Wurzel, nutzt alle logischen CPUs
und ersetzt Round-Robin durch einen Klassenscheduler, der Hyperthreads kennt.

**Architektur:** Den SMP-Mechanismus liefert Cosmos (Upstreams eigener Entwurfsstapel, im Branch `cosmos` des
ZehOS-Repositorys gebaut). Policy, Topologie und Appregeln liegen in ZehOS oberhalb der Scheduler-Schnittstelle. Eigene
Cosmos-Patches sind einzelne Commits, je mit einer Datei `patches/<Kurzname>.md`.

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
| 1 | Cosmos aus Quelle, unverändert | [e1-cosmos-quelle](2026-11-18-multicore-e1-cosmos-quelle.md) | keine (Branch `cosmos` besteht) |
| 2 | Patchreihe ext2 | [e2-ext2](2026-11-18-multicore-e2-ext2.md) | Etappe 1 |
| 3 | Patchreihe 12.4 (Ausnahme-Hook, LINT1) | [e3-ausnahme-hook](2026-11-18-multicore-e3-ausnahme-hook.md); eingeordnet als Bahn H im [Plan 12.4](2026-11-18-isolation-12-4.md) | Etappe 1 |
| 4 | Klassenscheduler und Topologie auf einer CPU | [e4-scheduler](2026-11-18-multicore-e4-scheduler.md) | keine |
| 5 | SMP-Stapel, alle Threads auf CPU 0 | [e5-smp](2026-11-18-multicore-e5-smp.md) | Etappen 1 und 4; nicht gleichzeitig mit 3 |
| 6 | Worker auf weiteren CPUs | [e6-e7-verteilung](2026-11-18-multicore-e6-e7-verteilung.md) | Etappen 0 und 5 |
| 7 | Oberfläche verlässt CPU 0, Hyperthreads, Panic, Energie | [e6-e7-verteilung](2026-11-18-multicore-e6-e7-verteilung.md) | Etappe 6 |
| 8 | Basiswechsel auf ein Cosmos-Release mit SMP | Abschnitt unten | ein solches Release |
| 12.4 | Isolation: Abbruch, Speicherbudget, Fuzz (die Cosmos-Patches sind Etappe 3) | [isolation-12-4](2026-11-18-isolation-12-4.md) | je Paket die Etappe aus dem Gesamtzeitplan |

Technisch können die Etappen 0, 1 und 4 sofort und nebeneinander beginnen. Zur Einordnung in die übrige Arbeit hat der
Koordinator am 18. November 2026 im IRC Peters Reihenfolge weitergegeben: Storage und ClassiCube zuerst, dann der
Cosmos-Eigenbau, dann 12.4; Etappe 1 darf laufen, ohne Test- und Review-Kapazität der anderen Arbeitsgruppen zu
binden; die Etappen 2 und 3 beginnen erst nach dem Merge von Storage 7a-S4 und 7b. Stand 18. November 2026, 14 Uhr
(Koordinator, `#second`): Die Etappen 0, 1 und 4 laufen. Die Etappe 2 beginnt nicht vor dem Tag des Releases 0.6,
weil sie ext2-Verhalten ändert, das gerade abgenommen wird. Die Etappe 3 und die übrigen 12.4-Pakete sind
seit dem 18. November 2026 eingeplant; wann sie starten, steht im Gesamtzeitplan unten. Gastläufe für den Cosmos-Eigenbau auf dem gemeinsamen KVM-Rechner beginnen erst nach den Storage-Gastläufen.
Innerhalb jeder Etappe nennt die Etappendatei, welche
Pakete nebeneinander laufen können; Pakete mit demselben Buchstaben-Präfix und verschiedener Nummer (etwa `E2-P1`,
`E2-P2`) sind dafür geschnitten.

## Gesamtzeitplan mit 12.4

Peter am 18. November 2026 im Terminal (weitergegeben vom Koordinator in `#second`): 12.4 ist ein Teil dieses Plans
und wird mit eingeplant; ein 12.4-Paket startet, wenn eine Multicore-Etappe es als Nächstes braucht. Vorher läuft,
was ohne 12.4 geht. Die 12.4-Pakete stehen im [Plan 12.4](2026-11-18-isolation-12-4.md); die Multicore-Etappe 3 ist
dort die Bahn H.

**Wann welches 12.4-Paket nötig wird:**

| 12.4-Paket | nötig vor | Grund |
| --- | --- | --- |
| E3-A, E3-C, E3-B, I1-N (Bahn H) | E5-A | Bahn H und Etappe 5 ändern den Branch `cosmos` und `Runtime/CosmosFaultIsolation.cs`. Läuft H vorher, trägt E5-A die Patches aus `PATCHES.md` nur noch nach, statt gleichzeitig dieselben Dateien zu bauen |
| I2-A, I2-B (Abbruch) | E6-C | E6-C ändert `Runtime/Wasm/WasmRuntime.cs` und gibt den Wasm-Worker auf weitere CPUs frei; der Abbruchmerker gilt auf jeder CPU und gehört vorher hinein, damit die Audit-Pakete `E6-C.<n>` den endgültigen Stand prüfen |
| I2-C, I2-D, I2-E (Speicher) | E6-C | wie oben; dazu sind Laufarena und `zehos_host_depth` Befunde des Audits (Abschnitt C-Code) und werden in `E6-C.<n>` behandelt |
| I2-F (Fuzz) | keine Etappe | nur neue Testdateien; läuft, wenn jemand frei ist, spätestens vor I2-Z |
| I2-Z (Abschluss) | E7-E | die Plattformmatrix soll die fertige Isolation mitprüfen |

**Dateien, die mehrere Etappen ändern** (ein Schreiber zur Zeit; der spätere rebased):

| Datei oder Ort | Pakete |
| --- | --- |
| Branch `cosmos`, `deploy/cosmos/COMMIT` und `VERSION`, `global.json` (`msbuild-sdks`) | E2-P1 bis E2-P8, E3-A, E3-C, E5-A, E5-K, E7-B. Jeder Patch-PR erhöht `VERSION` um eins; wer zuletzt mergt, nimmt die nächste Nummer |
| `Runtime/CosmosFaultIsolation.cs` | E3-B, I2-C, E5-B |
| `Runtime/Wasm/WasmRuntime.cs` | I2-A, I2-C, I2-E, E6-C |
| `Scheduling/` (Adapter) | E4-C, E5-B, E6-B |
| `ProjektZeh/Input/` | E4-D |

**Wellen** (Stand 18. November 2026, 16:20; „frei“ nach den Meldungen im Kanal):

| Welle | Startet, wenn | Pakete, nebeneinander | Besetzungsvorschlag |
| --- | --- | --- | --- |
| 1 | jetzt | E4-C (gestapelt auf #312); E4-D; E1-F (läuft); E1-E (läuft); E0-B; E5-G | E4-C jensen (zugewiesen); E4-D senior nach der ClassiCube-Messung; E1-F yellow; E1-E codex-2; E0-B peter-review (nur lesen); E5-G intake18 nach dem Bridge-Auftrag |
| 1b | Tag `v0.6.1` gesetzt | Etappe 2, drei Bahnen | Bahn B (Inode und Blöcke) senior nach E4-D; Bahn A (Verzeichnis) und C (Formatter) wer dann frei ist |
| 2 | E4-C gemergt | E4-E (Messung); Bahn H: E3-A und E3-C nebeneinander, danach E3-B, danach I1-N | E4-E Agent mit KVM; E3-A senior oder purple; E3-C jensen nach E4-C |
| 3 | Etappe 4 und Bahn H gemergt | Etappe 5 (E5-A, dann B/D, dann C, dann J/H/I; E5-F, E5-K) | E5-A und E5-B senior; E5-D jensen; Messungen Agent mit KVM |
| 4 | Etappe 5 gemergt | E6-A, E6-D, E6-F; Bahn A (I2-A, dann I2-B) und Bahn M (I2-C nach I2-A, dann I2-D, I2-E); I2-F | E6 senior und jensen; I2-A ein Engineer senior; I2-C/E ein Engineer; I2-F scientist |
| 5 | E6-A, Bahn A und Bahn M gemergt | E6-B, E6-C mit `E6-C.<n>`, danach E6-E | wie Welle 4 |
| 6 | Etappe 6 gemergt | Etappe 7 (E7-A, E7-D, E7-B, E7-C, dann E7-E mit I2-Z, dann E7-F) | nach Lage |

Etappe 2 hängt an keiner Multicore-Etappe; sie teilt sich mit den anderen nur den Branch `cosmos` und die
Versionsnummer und läuft neben den Wellen 2 bis 4, sobald der Tag gesetzt ist.

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
   Gastläufe aus; der Engineer beauftragt sie mit Branch, Commit und Befehl. Stand 18. November 2026: Auf dem
   Team-Rechner `nuc` fehlen dotnet und QEMU. Hosttests laufen deshalb über den Lead oder einen Agenten mit SDK
   10.0.404, Gastläufe über einen Agenten mit KVM; wer testet, steht im Kanal.
5. Unabhängiges Review durch einen anderen Agenten, dann PR mit den Reviewern aus `AGENTS.md`. Kein Merge ohne
   Freigabe eines Projektbenutzers.

**Prüfbefehle** (vom Repository-Stamm)

```powershell
dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror
cosmos build --project ProjektZeh/ProjektZeh.csproj --arch x64 --config Release
cosmos run --iso ProjektZeh/output-x64/ProjektZeh.iso --arch x64 -- -action shutdown=poweroff,reboot=reset
```

**Cosmos-Patches** (ab Etappe 1)

- Ein Patch ist genau ein Commit im Branch `cosmos`, eingebracht per PR gegen `cosmos`, mit einer eigenen Datei
  `patches/<Kurzname>.md`: Commit, Dateien, Grund, entfernter ZehOS-Umweg, Prüfung (seit #357; vorher eine Tabellenzeile
  in `PATCHES.md`, die bei jedem Merge Konflikte brachte).
  Im Patch-Branch sind Folgecommits erlaubt (kein Force-Push); gemergt wird per **Squash-Merge**, so wird jeder Patch
  genau ein Commit. Im Feld „Commit“ steht im PR „Squash von #<PR>“; den echten Hash setzt der ZehOS-PR, der
  `deploy/cosmos/COMMIT` umstellt. Gestapelte Patches: nach dem Squash des Vorgängers `origin/cosmos` in den Folge-Branch
  mergen, nicht rebasen (Lead-Entscheid 18. November 2026).
- Geprüft wird ein Patch durch den ZehOS-PR, der ihn verwendet: Er entfernt den Umweg und wiederholt dessen bisherige
  Prüfung. Während der Prüfung bleibt `deploy/cosmos/COMMIT` unverändert; gebaut wird mit
  `scripts/build-cosmos.sh --commit <Kopf des Patch-Branches>` (Versuchsversion `3.0.97-zehos.0.g<sha>`), Kernelbau und
  Gastläufe mit `-p:ZehOSCosmosVersion=<diese Version>`. Erst nach dem Squash-Merge des `cosmos`-PRs setzt der ZehOS-PR
  `COMMIT` auf den entstandenen Hash, `VERSION` auf die nächste `-zehos.N` und `global.json` gleich, baut einmal nativ,
  wiederholt die kürzeste Gastprüfung und wird danach gemergt (Lead-Entscheid 18. November 2026, Befund zu E3-B).
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
| Branch `cosmos` | Cosmos-Quellbaum im ZehOS-Repository; Grundstand `bd5889b46a6b1175ff8b66620437e072b78636de` | E1-A (erledigt) |
| `PATCHES.md` und `patches/<Kurzname>.md` (Branch `cosmos`) | Grundstand und Regel; je Patch eine Datei | E1-A, #357 |
| `deploy/cosmos/COMMIT` | eine Zeile: Commit des Branches `cosmos`, der gebaut wird | E1-B |
| `deploy/cosmos/VERSION` | eine Zeile: Paketversion, Start `3.0.97-zehos.1` | E1-B |
| `deploy/cosmos/SDK` | eine Zeile: .NET-SDK des Referenzbaus, `10.0.404`; `scripts/build-cosmos.sh` bricht bei einem anderen SDK ab (`--any-sdk` nur für lokale Versuche) | E1-B |
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
| Lizenz der eigenen Cosmos-Patches. Der Plan nimmt an: wie ZehOS GPL-3.0, weil der Branch das Repository nie verlässt (E1-A, E2). Alternative: BSD 3-Clause wie Cosmos | Peter und Lars als Rechteinhaber | nichts; ändert nur den Text in `ZEHOS.md` und den Patch-Kommentar |
| Kommt die E4.0-Gastprobe (`origin/task/storage-e4-0-root-probe`) nach `master`? | Storage-Lead | Gastläufe in Etappe 2 (Paket E2-P0) |
| Neue Produktgrenze für Einträge je ext2-Verzeichnis; der Plan schlägt 65.536 vor | Storage-Lead | E2-P7, Schritt 3 |
| TSS mit IST-Stacks als eigenes Vorhaben | Peter | nichts in diesem Plan |
| Einzelbefunde des Audits | ergeben sich aus Etappe 0 | Zuschnitt von E6-C |
| Verhalten unter WHPX, VMware und echter Hardware | Messung in E7-E | Aussagen zu diesen Plattformen |

## Was die Umsetzung bisher ergeben hat

Stand 18. November 2026, 16 Uhr. Quelle sind die genannten Pull Requests und die Meldungen im Kanal `#second`.

| Frage oder Annahme des Plans | Ergebnis | Beleg |
| --- | --- | --- |
| Läuft der Cosmos-Paketbau unter Windows? | Ja, in Git Bash, sobald für die git-Aufrufe im Quellbaum `core.longpaths` gilt; ohne scheitert das Auschecken an einem Fixture mit 128 Zeichen langem Namen. Der Kernelbau mit dem Feed läuft dort ebenfalls (3 min 52 s). Die Patcher-Versionsprüfung ist unter Windows ausgeschaltet | Prüfung auf `925a33b` (E1-C), ein Lauf |
| Dauer des Paketbaus | 53 bis 64 s für 25 Pakete auf einem Entwicklungsrechner; der CI-Job „Cosmos x64 build“ braucht mit Quellbau gut 3 min | #290 |
| Welches SDK baut Cosmos? | Festgelegt auf 10.0.404. Gegen die Pakete 3.0.97 von nuget.org weicht damit nur ein Attributtext in `Cosmos.Kernel.Core.dll` ab; mit 10.0.201 weicht Code in drei DLLs ab. Derselbe Commit zweimal gebaut ergibt keine Abweichung im Code | #296 |
| Gleichstand des Kernels (E1-D) | Hostseitig gleich: 11 gebundene Plugs, Hosttests, ISO-Größe. Die Gastzeilen stehen aus | Kommentar in #290 |
| Paketliste | 25 Pakete statt 23: dazu `_Cosmos.Build.Analyzer.Patcher` und `Cosmos.Build.Analyzer.Patcher.CodeFixes`. Das Paket `Cosmos.Build.Analyzer.Patcher` hat im Quellbau ein anderes Layout als auf nuget.org | #290, #296 |
| `packageSourceMapping` | Erst nach E1-F möglich: Die Hosttests laden bis dahin `Cosmos.Kernel.System` 3.0.97 von nuget.org | #290 |

## Etappe 8: Basiswechsel

Auslöser: Ein veröffentlichtes Cosmos-Release enthält den SMP-Stapel. Ein Paket, ein Bearbeiter.

- [ ] Im Cosmos-Repository den Release-Commit bestimmen (`nuspec` des Pakets `Cosmos.Kernel.Core`, Feld `repository commit`).
- [ ] Im Branch `cosmos` einen neuen Grundstand-Commit mit dem Dateibaum dieses Commits anlegen (ohne `.github/workflows`), wie in E1-A.
- [ ] Jeden Patch aus `patches/` in der Reihenfolge von `git log --first-parent --reverse` darauf übertragen (`git cherry-pick <commit>`, bei den Merge-Commits von #344 bis #346 mit `-m 1`); Patches, die Upstream inzwischen selbst behoben hat, streichen und das in `PATCHES.md` vermerken.
- [ ] `deploy/cosmos/VERSION` auf `<neue Version>-zehos.1`, `deploy/cosmos/COMMIT` auf den neuen Stand.
- [ ] `CosmosClassScheduler.cs` gegen die veröffentlichte `IScheduler`-Schnittstelle übersetzen; Abweichungen dort beheben.
- [ ] Gesamte Host- und Gastsuite samt Matrix aus E7-E; Speicherqualifikation wie in E5-H.
- [ ] Ist `patches/` leer: `deploy/cosmos/`, `scripts/build-cosmos.sh`, `NuGet.Config`-Zuordnung und den Branch `cosmos` entfernen und die Pakete wieder von nuget.org beziehen.
