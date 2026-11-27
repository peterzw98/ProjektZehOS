# Integration von `story/ladybird` in `master`

Lesender Abgleich vom 27. November 2026. Untersucht wurden
[`master` `c042ab90`](https://github.com/peterzw98/ProjektZehOS/commit/c042ab90a96b234d8e14eab645d5e57b2ab0979e)
und [`story/ladybird` `9d461fa7`](https://github.com/peterzw98/ProjektZehOS/commit/9d461fa733646c7ed00e871b99d687c0e58ed5f2).
Gemeinsamer Vorfahr ist `53cba9cd`. `git merge-tree --write-tree --name-only --messages`
lief in separaten Objektverzeichnissen. Für den kombinierten Stand stehen
Build, Hosttests und Gastläufe aus.

## Umfang und Konflikte

Seit dem gemeinsamen Vorfahren liegen 24 Commits auf der Master-Seite und
370 auf der Story-Seite. Das Master-Delta umfasst 16 Dateien (+226/−19),
das Story-Delta **279 Dateien (+53.004/−65)**.

| Bereich im Story-Delta | Dateien | Inhalt |
| --- | ---: | --- |
| `experiments/` | 124 | Vergleichsprogramme, Ladybird-Proben, Erwartungen und Referenzdaten |
| `ProjektZeh/` | 79 | Linux-ELF, Linux-ABI, Speicher und Prozesse sowie Änderungen an Scheduler, Terminal und Dateidienst |
| `ProjektZeh.Tests/` | 31 | Hostprüfungen |
| `docs/`, `scripts/` | 42 | 23 Dokumente und 19 Skripte und Tests |
| Cosmos-Pin und SDK-Auflösung | 3 | Commit, Paketversion, `global.json` |

Die ZehOS-Simulation meldete drei Textkonflikte. Sie bilden gemeinsam
einen Pin und müssen auf dieselbe integrierte Cosmos-Paketversion zeigen.

| Konflikt | Master | Story |
| --- | --- | --- |
| `deploy/cosmos/COMMIT` | `9b1f5778` | `053fb42d` |
| `deploy/cosmos/VERSION` | `3.0.97-zehos.6-smp.17` | `3.0.97-zehos.0.g053fb42` |
| `global.json`, `msbuild-sdks.Cosmos.Sdk` | `3.0.97-zehos.6-smp.17` | `3.0.97-zehos.0.g053fb42` |

`deploy/cosmos/SDK` steht auf beiden Seiten auf 10.0.404. Die simulierte
Projektdatei erhält den ZehOS-Versionspräfix 0.7 von Master. `ProjektZeh.csproj`,
`CosmosClassScheduler.cs` und `test-wasm-doom-desktop.py` wurden automatisch
zusammengeführt und brauchen einen inhaltlichen Abgleich.

## Cosmos zuerst integrieren

- Die beiden gepinnten Cosmos-Commits haben `db6cb590` als Vorfahren und
  unterscheiden sich um 1 Commit auf Master-Seite und 49 auf Story-Seite.
  Der simulierte Merge genau dieser beiden Pins hat keine Textkonflikte.
  `9b1f5778` ist der DHCP-Broadcast-Patch aus #721. Ein Wechsel auf den
  Story-Pin allein würde diesen Commit aus der Paketbasis nehmen.
- Der aktuelle Zweig `cosmos` ist weiter als der Master-Pin. Sein Kopf
  `ca7eed5e` enthält zusätzlich den Tick-Beobachter aus #737. Die Simulation
  von `cosmos` gegen `story/ladybird-cosmos` (`053fb42d`) meldet einen Konflikt
  in `src/Cosmos.Kernel.Core/PublicAPI.Unshipped.txt`. Der gemeinsame
  Scheduler und dessen Threading-Testkernel werden automatisch gemischt.
- Der kombinierte ZehOS-Scheduler verwendet sowohl `IVoluntaryMove` und
  `VoluntaryMoveIntervalTicks` als auch `PerCpuState.SwitchIsYield`.
  Das erste Paar ist in beiden Pins vorhanden. `SwitchIsYield` fehlt im
  Master-Pin `9b1f5778`. Seine unveränderte Übernahme lässt die
  automatisch zusammengeführte Scheduler-Quelle ohne die benötigte API.
- Der vorgesehene Weg ist ein eigener Cosmos-PR auf dem Projektzweig
  `cosmos`. Danach aus dem integrierten Cosmos-Commit mit dem festgelegten
  SDK Feed, SDK und Patcher bauen. Den ZehOS-PR auf **diesen** Commit und
  eine neue zusammengehörige Paketversion pinnen.
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

1. Vor dem PR beide Remote-Köpfe erneut erfassen und alle drei Pin-Stellen
   zusammen auflösen. Cosmos-Paketversion und Patcher mit dem Referenz-SDK
   bauen und im Kernel tatsächlich verwenden.
2. Hosttests mit Warnungen als Fehler sowie native Release-, Recovery- und
   LinuxRun-Bauten auf demselben integrierten Stand durchführen. Standard-
   und Recovery-Boot samt Desktop, Wasm und Datenträgern separat prüfen.
3. Die QEMU-Matrix und den VMware-Standardstart prüfen, dazu die Ladybird-
   Seiten und KVM-HTTP im LinuxRun-Bau. Bekannte Grenzen der Demo und noch
   ausstehende Prüfungen mit Stand im PR festhalten.
4. Unabhängiges Review und die vorgeschriebenen Freigaben für die beiden
   PRs einholen. [Bau- und Prüfregeln](../../regeln/PRUEFEN.md)
