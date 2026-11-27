# Integration von `story/ladybird` in `master`

Lesender Abgleich für T6 vom 28. November 2026. Untersucht wurden
[`master` `c042ab90`](https://github.com/peterzw98/ProjektZehOS/commit/c042ab90a96b234d8e14eab645d5e57b2ab0979e)
und [`story/ladybird` `3e1026f8`](https://github.com/peterzw98/ProjektZehOS/commit/3e1026f880b1a26fcefd89287ed30b3a701d2571)
mit V47 und V49. [`ff57d65b`](https://github.com/peterzw98/ProjektZehOS/commit/ff57d65b24a233ce4a37e7982e36fac30d59cd06)
bleibt der Referenzstand der V50-Browserabnahme. Nach Peters Entscheidung
vom 28. November wird am 4. Dezember `master` gezeigt. `story/ladybird`
nimmt geprüfte Änderungen regulär auf. Der Vorführcheck auf `master` ist
eine eigene Abnahme.
Gemeinsamer Vorfahr ist `53cba9cd`. `git merge-tree --write-tree --name-only --messages`
lief in separaten Objektverzeichnissen. Für den kombinierten Stand stehen
Build, Hosttests und Gastläufe aus. [N2-Z-Torplan](https://github.com/peterzw98/ProjektZehOS/blob/3e1026f880b1a26fcefd89287ed30b3a701d2571/docs/superpowers/plans/2026-11-28-ladybird-n2z-tor.md)

## Umfang und Konflikte

Seit dem gemeinsamen Vorfahren liegen 24 Commits auf der Master-Seite und
403 auf der Story-Seite. Das Master-Delta umfasst 16 Dateien (+226/−19),
das Story-Delta **292 Dateien (+54.117/−85)**.

| Bereich im Story-Delta | Dateien | Inhalt |
| --- | ---: | --- |
| `experiments/` | 133 | Vergleichsprogramme, Ladybird-Proben, Erwartungen und Referenzdaten |
| `ProjektZeh/` | 80 | Linux-ELF, Linux-ABI, Speicher und Prozesse sowie Änderungen an Scheduler, Terminal und Dateidienst |
| `ProjektZeh.Tests/` | 33 | Hostprüfungen |
| `docs/`, `scripts/` | 43 | 24 Dokumente und 19 Skripte und Tests |
| Cosmos-Pin und SDK-Auflösung | 3 | Commit, Paketversion, `global.json` |

Die ZehOS-Simulation meldete drei Textkonflikte. Sie bilden gemeinsam
einen Pin und müssen auf dieselbe integrierte Cosmos-Paketversion zeigen.

| Konflikt | Master | Story |
| --- | --- | --- |
| `deploy/cosmos/COMMIT` | `9b1f5778` | `f2b64df6` |
| `deploy/cosmos/VERSION` | `3.0.97-zehos.6-smp.17` | `3.0.97-zehos.0.gf2b64df` |
| `global.json`, `msbuild-sdks.Cosmos.Sdk` | `3.0.97-zehos.6-smp.17` | `3.0.97-zehos.0.gf2b64df` |

`deploy/cosmos/SDK` steht auf beiden Seiten auf 10.0.404. Die simulierte
Projektdatei erhält den ZehOS-Versionspräfix 0.7 von Master. `ProjektZeh.csproj`,
`CosmosClassScheduler.cs` und `test-wasm-doom-desktop.py` wurden automatisch
zusammengeführt und brauchen einen inhaltlichen Abgleich.

## Änderungen und Nachweise bis `ff57d65b`

- V46 ergänzt `rt_sigsuspend` für `wait` in BusyBox `ash`. V45-P3 stellt
  TF/RF bei `rt_sigreturn` wieder her und ergänzt die SIGTRAP- und
  Einzelschritt-Proben. Die KVM-Abnahmen `52a2cdc4` und `d0a5b7b7` ergaben
  jeweils 8/8 bei 2 GiB.
- V48 begrenzt Speicherabbildungen auf 65.530 pro Adressraum. Wachstum über
  die Grenze liefert `ENOMEM`. Auf `13d48244` liefen 7/8 KVM-Fälle durch.
  `static` mit vier CPUs blieb einmal beim Screenshot stehen, drei
  Wiederholungen liefen durch.
- V50 löscht in Cosmos beim Sweep die Header toter Objekte. Mit dem
  V50-Pin ergab `ff57d65b` auf KVM 8/8 und zusätzlich 10/10 für `static`
  mit vier CPUs. Die separate Fangschaltung meldete damit 20/20. Die
  Ursache des Stillstands auf `13d48244` bleibt offen.
  [Runbook am erfassten Story-Kopf](https://github.com/peterzw98/ProjektZehOS/blob/4519757a9e75d8af36cc08066bc30fb540aab95e/docs/superpowers/plans/2026-12-04-ladybird-demo-ablauf.md#abendstand-der-abnahme)

## V47 und V49 auf `3e1026f8`

Für den ungemischten Story-Stand meldete der Lead 2.264 Hosttests ohne Fehler.
Die Gastmessungen für T1 auf diesem Kopf stehen noch aus.

- V47 ergänzt einen GC-Versuch bei Frame-Mangel nach Freigabe der
  Seitensperre und eine Sammlung am Prozessende unter Speicherdruck. Die
  separat gemessene V47-Fassung auf `ff57d65b` verlor nach
  1.000 Starts und erzwungener GC 1.300 KiB bei einer und 1.172 KiB bei vier
  CPUs auf KVM mit 512 MiB. Alle Starts liefen durch. Das T1-Ziel unter
  1 MiB blieb offen. T1 wiederholt auf `3e1026f8` mit 100 Aufwärmstarts,
  danach 1.000 und 3.000 Starts auf KVM mit je einer und vier CPUs. Messbau
  und Integrationsnachweis getrennt protokollieren.
  [V47-Branchstand](https://github.com/peterzw98/ProjektZehOS/commit/b9e32706456dba82d84fff289bb6baecd8d687e2)
- V49 bündelt die Löschungen in `AddressSpaceMap.Cut`. Die Freigabe großer
  Bereiche braucht einen Durchgang über die betroffenen Regionen. Der
  abschließende Hosttest zählt Listenverschiebungen. Auf dem gemeinsamen
  Stand `mmap`/`mprotect`/`munmap` nahe der V48-Grenze prüfen, auch mit
  Abbruch und Rollback.
  [V49-Branchstand](https://github.com/peterzw98/ProjektZehOS/commit/b5a40a468abe21ed1553c9ca68058dde0a4ff463)

## Cosmos zuerst integrieren

- Die beiden gepinnten Cosmos-Commits haben `db6cb590` als Vorfahren und
  unterscheiden sich um 1 Commit auf Master-Seite und 53 auf Story-Seite.
  Der simulierte Merge genau dieser beiden Pins hat keine Textkonflikte.
  `9b1f5778` ist der DHCP-Broadcast-Patch aus #721. Ein Wechsel auf den
  Story-Pin allein würde diesen Commit aus der Paketbasis nehmen.
- Der aktuelle Zweig `cosmos` ist weiter als der Master-Pin. Sein Kopf
  `ca7eed5e` enthält zusätzlich den Tick-Beobachter aus #737. Die Simulation
  von `cosmos` gegen `story/ladybird-cosmos` (`f2b64df6`) ergibt 2 zu 53
  Commits und einen Konflikt in `src/Cosmos.Kernel.Core/PublicAPI.Unshipped.txt`.
  Git führt den Scheduler und dessen Threading-Testkernel automatisch zusammen.
- Der kombinierte ZehOS-Scheduler verwendet sowohl `IVoluntaryMove` und
  `VoluntaryMoveIntervalTicks` als auch `PerCpuState.SwitchIsYield`.
  Das erste Paar ist in beiden Pins vorhanden. `SwitchIsYield` fehlt im
  Master-Pin `9b1f5778`. Seine unveränderte Übernahme lässt die
  automatisch zusammengeführte Scheduler-Quelle ohne die benötigte API.
- Zuerst den Cosmos-PR gegen `cosmos` vorbereiten. API-Liste und automatisch
  gemischten Scheduler prüfen, DHCP-Patch, Tick-Beobachter und V50-GC-Patch
  erhalten. Nach Review und Freigabe aus dem integrierten Cosmos-Commit mit
  dem festgelegten SDK Feed, Cosmos-SDK und Patcher bauen. Die tatsächliche
  Auflösung aus diesem Feed für den folgenden ZehOS-Bau protokollieren.
- Danach im ZehOS-PR `deploy/cosmos/COMMIT`, `deploy/cosmos/VERSION` und
  `global.json` gemeinsam auf den integrierten Commit und seine Paketversion
  setzen. Der Story-Pin ist die Ausgangsbasis für den Vergleich. Der
  Integrationspin enthält zusätzlich die Änderungen aus `cosmos`.
  [Cosmos-Patchregeln](../../regeln/COSMOS_PATCHES.md) ·
  [Cosmos-Bauanleitung](../../../deploy/cosmos/README.md)

## Risiken für Desktop und Wasm

| Gemeinsamer Pfad | Änderung | Prüfung auf dem kombinierten Stand |
| --- | --- | --- |
| Desktop, Tastatur, Terminal | `CapturesEscape`, Vordergrund-Prompt und Raw-Eingabe verändern die allgemeine Ereignisweitergabe. | Normales Terminal und mehrere Fenster, Modifier und Zwischenablage, Esc und F4/Recovery. Im LinuxRun-Bau zusätzlich Vordergrundabbruch, Rückkehr zur Shell und Fensterwechsel. |
| Dateidienst und Datenplatte | Neuer ELF-Lesepfad und Prozesswurzelprüfung verwenden den vorhandenen `FileService` und `StorageLock`. | Normales Öffnen, Schreiben und Ersetzen im Desktop. Gast-Schreibtests mit isolierter FAT32-Kopie und sauberem Aushängen vor Neustart/Shutdown. |
| Scheduler, IRQ und Speicher | Zusammengeführte Scheduler-Policy und neuer Cosmos-Pin laufen auch im Standardkernel. | Desktop und `SMP_PROBE` unter KVM und TCG mit je 1/4 CPUs oder die bestehende [E7e-Matrix](../../../scripts/guest-suites/e7e-matrix.json). |
| Wasm und AOT | Kein direkter Story-Diff in `Runtime/Wasm`, `Native/Wasm` oder `ThirdParty/wasm3`. Der Standard aktiviert Wasm und AOT. Beide teilen den Cosmos-Pin, Scheduler und Datenträgerpfad. | wasm3-Loader, Doom und ein AOT-Paket samt Abbruch- und Rückkehrpfad prüfen. Beim automatisch zusammengeführten Doom-Helfer sowohl den Master-Katalogtimeout für TCG als auch die Cache-Option für Wegwerfplatten erhalten. |
| Netzwerk und Paketdienst | Der Cosmos-Pin betrifft die DHCP-Basis. Master enthält zusätzliche Änderungen an Netzwerk und TLS. | DHCP-Start, Paketkatalog und Downloads mit dem kombinierten Stand prüfen. |

`ZehOSLinuxRun=true` bleibt ein separater Probebau. Die Browserläufe prüfen
diesen Bau. Für Desktop und Wasm folgen eigene Gastläufe im Standardbau und
im Recovery-Bau.

## Abnahme vor der Integration

Vor dem PR die Remote-Köpfe erneut erfassen. Alle ZehOS-Zeilen der Matrix
beziehen sich auf denselben kombinierten Stand mit dem neuen Cosmos-Pin.

| Bau und Plattform | Prüfungen |
| --- | --- |
| Cosmos, Host und Gast | GC-Regression für Header am Segmentanfang, Bump-Reset und lebenden Nachbarn. Scheduler-/Threading-Tests und DHCP mit neuer Paketbasis. |
| ZehOS, Host | `dotnet test` in Release mit Warnungen als Fehler. Native Builds für Standard, Recovery und LinuxRun dokumentieren. |
| Standard, QEMU/KVM und TCG je 1 und 4 CPUs | Desktop und `SMP_PROBE` oder E7e-Matrix. Boot, Terminal, Fokus und Eingabe, Datenplatte, Wasm-Rechner, Doom und AOT, DHCP und Paketkatalog prüfen. Je Zelle ein Lauf, bei Fehlschlag zwei Wiederholungen. |
| Recovery, QEMU | Boot, Shell und F4-Rückfall prüfen. `scripts/test-power-commands.ps1` mit isolierter Datenplatte für Aushängen, Neustart und Shutdown ausführen. |
| Standard, VMware | Boot, Bildschirm und Eingabe prüfen. Ergebnisse und Grenzen getrennt von QEMU festhalten. |
| LinuxRun, QEMU/KVM 1 und 4 CPUs, TCG mindestens 1 CPU | Den Linux-ABI-Referenzvergleich (T2), Fehlerisolation (T3) und 1.000 Starts mit Speichermessung nach GC (T1) nach Torplan auf dem integrierten Stand belegen. KVM-Abnahme für `static`, `css`, `js` und `img` mit 2 GiB wiederholen, dazu `static` mit vier CPUs und HTTP-Loopback mit einer CPU. |

Die vorgesehenen T3- und T4-Läufe auf dem Story-Kopf liefern Vergleichswerte.
Für die Integration die Ergebnisse der Matrix auf dem neuen Pin festhalten.
Unabhängiges Review und vorgeschriebene Freigaben gelten für beide PRs.
Der Merge nach `master` wartet auf Peters Freigabe.
[Bau- und Prüfregeln](../../regeln/PRUEFEN.md)
