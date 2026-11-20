# ProjektZeh

[![Build and tests](https://github.com/peterzw98/ProjektZehOS/actions/workflows/ci.yml/badge.svg)](https://github.com/peterzw98/ProjektZehOS/actions/workflows/ci.yml)

ProjektZeh ist ein Cosmos-Gen3-Kernel für x64 mit einem grafischen Desktop und einer kleinen Shell in unabhängigen Terminalfenstern. Der Taskmanager zeigt die CPU-Topologie.

## Handbuch

| Teil | Inhalt |
| --- | --- |
| [Bedienung](docs/handbuch/BEDIENUNG.md) | Shell, Benutzer und Dateirechte, Desktop und Fenster, Dateibetrachter, Texteditor und Pong, Moderne Konsole, Netzwerk in der Shell |
| [Apps als Pakete](docs/handbuch/APPS.md) | Apps als Pakete installieren |
| [Entwicklung, Tests und Debuggen](docs/handbuch/ENTWICKLUNG.md) | Dateisystem in QEMU, Hintergrundaufgaben und Round-Robin, Desktop-Dateifehler gezielt prüfen, Automatische Builds und Codeprüfung, Debuggen in Visual Studio mit QEMU, Start in VMware Player aus Visual Studio, Entwicklungsplan |
| [Installation, Updates und Releases](docs/handbuch/INSTALLATION.md) | UEFI-Installation und signierte Kernelupdates, Releases und Updates |
| [Roadmap](ROADMAP.md) | Ziele, Etappen, Abnahme |
| [Cosmos-Unterstützung](docs/COSMOS_SUPPORT.md) | geprüfte Cosmos-Schnittstellen und Grenzen |
| [Pakete erstellen](docs/PAKETE_ERSTELLEN.md) | Wasm-Apps bauen und veröffentlichen |

## Voraussetzungen

- .NET SDK 10; `global.json` wählt die neueste installierte 10.0-Featureversion. Ein allein installiertes SDK 11 reicht für den Cosmos-Bau nicht aus.
- Git, Bash und Netz für den Cosmos-Quellbau. `scripts/build-cosmos.sh` verlangt genau das SDK aus `deploy/cosmos/SDK` (derzeit 10.0.404) und bricht bei einem anderen ab; `--any-sdk` ist nur für lokale Versuche gedacht.
- Cosmos Gen3 CLI und native Bauwerkzeuge (`cosmos check` muss erfolgreich sein)
- Für die Arbeit in Visual Studio: Visual Studio 2026 (Version 18.0 oder neuer). Visual Studio 2022 kann dieses `net10.0`-Projekt nicht bauen.
- Für Breakpoints in Visual Studio: die [Cosmos-Visual-Studio-Erweiterung](https://github.com/CosmosOS/CosmosVsExtension/releases) und der Visual-Studio-Workload **Linux and embedded development with C++**. Der Workload stellt die GDB-Debugengine bereit.

Der Kernel verwendet Cosmos aus dem eigenen Branch `cosmos`; `deploy/cosmos/COMMIT` und `VERSION` legen Quellstand und Paketversion fest. Vor dem ersten Kernelbau und nach jeder Änderung von `COMMIT` im Repository-Stamm ausführen:

```bash
scripts/build-cosmos.sh
export PATH="$PWD/.tools/cosmos-tools/$(cat deploy/cosmos/VERSION):$PATH"
cosmos install --auto --tools
cosmos check
```

Das Skript baut den lokalen Paketfeed und installiert CLI und Patcher je Arbeitskopie unter `.tools/cosmos-tools/<version>`. Den passenden Ordner in jeder Shell vor den Kernelbau an den Anfang von `PATH` setzen, insbesondere bei mehreren Arbeitskopien. `cosmos install` immer mit `--tools` aufrufen; ohne diese Option ersetzt es den Patcher durch die offizielle Version. Für Visual Studio und F5 zusätzlich `scripts/build-cosmos.sh --global` ausführen; nach einem Versionswechsel wiederholen. Unter Windows prüft der Kernelbau die Patcher-Version nicht automatisch; das Skript setzt `core.longpaths` für seine Git-Aufrufe selbst (siehe [Befund](docs/COSMOS_SUPPORT.md#cosmos-aus-quelle-18-november-2026)). Einzelheiten und Voraussetzungen: [Cosmos-Quellbau](deploy/cosmos/README.md). Die offizielle Installationsanleitung steht unter <https://cosmosos.github.io/articles/user/install.html>.

**Fehlermeldung `Unable to find package Cosmos.Sdk with version (= 3.0.97-zehos.1)` oder `The SDK 'Cosmos.Sdk' specified could not be found`:** Der lokale Paketfeed fehlt noch (oder ist veraltet). Abhilfe: `scripts/build-cosmos.sh` im Repository-Stamm ausführen (die Version steht in `deploy/cosmos/VERSION`) und den Werkzeugordner wie oben in `PATH` setzen. `cosmos build` nimmt keine zusätzlichen MSBuild-Eigenschaften (`-p:…`) an; dafür nach dem Setzen von `PATH` direkt `dotnet build` verwenden (siehe Installer-Build).

Die Solution `ProjektZeh.sln` muss ausdrücklich in Visual Studio 2026 geöffnet werden. Visual Studio 2022 meldet bei diesem Projekt einen Fehler zum .NET-10-Ziel und gegebenenfalls einen Folgefehler zur Assembly-Kürzung.

Die Solution in Visual Studio 2026 bauen (`Strg+Umschalt+B`). Das ISO liegt unter `ProjektZeh\bin\Debug\net10.0\linux-x64\cosmos\ProjektZeh.iso`. Die Zielplattform `linux-x64` ist für den Kernel festgelegt.

## Bauen und starten

Im Wurzelverzeichnis des Repositories:

```powershell
cosmos build --project ProjektZeh\ProjektZeh.csproj --arch x64
cosmos run --iso ProjektZeh\output-x64\ProjektZeh.iso --arch x64 -- -action shutdown=poweroff,reboot=reset -device qemu-xhci,id=tabletbus -device usb-tablet,bus=tabletbus.0
```

Die ZehOS-Version entsteht beim Bau aus Git: `<ZehOSVersionPrefix>.<ZehOSPatchBase + first-parent-Commits seit der letzten Änderung dieser beiden Zeilen in ProjektZeh.csproj>`. Ein Bau braucht deshalb einen Git-Arbeitsbaum mit vollständiger Historie; ein flacher Klon bricht mit einer Meldung ab (`git fetch --unshallow`), ohne Git hilft `-p:Version=X.Y.Z`. Die Version eines Stands zeigt `dotnet msbuild ProjektZeh/ProjektZeh.csproj -t:GetAssemblyVersion -getProperty:Version`.

Der erste Befehl erzeugt `ProjektZeh\output-x64\ProjektZeh.iso`. Für einen Start ohne grafisches QEMU-Fenster kann `cosmos run` vor `--` mit `--headless` ausgeführt werden. Die eigentliche Konsole wird dann nicht angezeigt, weil sie auf dem Framebuffer gezeichnet wird.

### Gastproben

Proben bauen den Kernel mit einer Konstante und melden Ergebnisse seriell; die Skripte unter `scripts/` starten das ISO plattenlos unter QEMU. Die SMP-Probe (E6-D) prüft auf mehreren CPUs Zähler, Parallelität, Migration, Sperren, Warten mit Frist (`timedwait`), GC und Threadstart/-ende:

```bash
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSSmpProbe=true -warnaserror
ZEHOS_QEMU_SMP=4 python3 scripts/test-smp-probe.py --iso <ISO> --accel kvm   # ZEHOS_QEMU_SMP leer = eine CPU
```

Erwartet wird `SMP_PROBE PASS`; die CPU-Zahl muss der ersten Zahl aus `ZEHOS_QEMU_SMP` entsprechen, und unter KVM mit mindestens zwei Worker-CPUs muss `parallel speedup_permille` mindestens 1500 betragen. Die Probe übergibt je Sperrenpaar 10.000 Nachrichten.

Die Schritte `placement` und `smt` (E7-D) brauchen Hyperthreading im Gast (`ZEHOS_QEMU_SMP=4,cores=2,threads=2`; ohne `threads=2` meldet `smt` SKIP, das Skript lässt das zu). `placement` belegt, dass ein Interaktiv- und ein Normal-Thread auf getrennten Kernen rechnen. `smt` lässt so viele Threads rechnen wie Worker-CPUs und meldet `INFO smt off=<0|1> used=<CPUs> second=<n>`: Mit `zehos.smt=off` (Zeile `cmdline: zehos.smt=off` im Limine-Eintrag des ISO, nicht committen) darf nie ein zweiter logischer Prozessor eines Kerns auftauchen (`second=0`, sonst FAIL); ohne den Schalter treten sie bei voller Last auf (Messung KVM, 4 CPUs als 2 Kerne × 2: `used=1,2,3 second=2` gegen `used=2 second=0`). `placement` meldet mit dem Schalter SKIP, weil unter den Worker-CPUs nur ein Kern bleibt.

Die Panic-Probe (E7-B) belegt, dass eine Panic alle CPUs anhält (Cosmos-Patch `panic-stop-cpus`): Ein Thread schreibt alle 50 ms `SMP_PROBE tick`, nach 1 s löst ein Thread auf einer anderen CPU einen #PF aus.

```bash
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSSmpProbePanic=true -warnaserror
ZEHOS_QEMU_SMP=4 python3 scripts/test-smp-probe.py --panic --iso <ISO> --accel kvm --runs 5
```

Erwartet werden mindestens fünf `tick`-Zeilen vor dem Fehler, danach `System halted.` und 2 s lang keine weitere `tick`-Zeile. Ein ISO mit `ZehOSSmpProbePanic` führt nur den Schritt `cpus` und die Panic-Probe aus, keinen der übrigen Schritte; `SMP_PROBE PASS` erscheint dort nie, weil das System anhält (das Urteil fällt `--panic`). Gegenprobe: Mit einem Cosmos ohne den Patch `panic-stop-cpus` muss `--panic` FAIL melden (Ticks laufen nach `System halted.` weiter).

### Mehrere CPUs (Entwurf, Stand 20. November 2026)

> **Entwurf zu E7-F (#399).** Gilt nur für den Integrationsbranch `task/mc-e5-smp` (Cosmos-Pin `3.0.97-zehos.6-smp.10`), nicht für `master`. Die mit **offen** markierten Punkte hängen an E7-A bis E7-E und werden erst nach deren Merge ausgefüllt.

- **Parameter der VM:** `ZEHOS_QEMU_SMP` (z. B. `4`, `4,cores=2,threads=2`) setzt `-smp` für `scripts/start-qemu.ps1` (dort auch `-Smp`) und die Testskripte; leer bleibt die QEMU-Vorgabe. VMware: `scripts/start-vmware.ps1 -Cpus <n>` (Standard 1).
- **Kernel-Schalter** `zehos.cpus=<n>` und `zehos.smt=off` stehen in der [Entwicklungsanleitung](docs/handbuch/ENTWICKLUNG.md#kommandozeilenschalter-des-kernels). Beide wirken heute nur auf die Wasm-Worker; die Hauptschleife bleibt auf CPU 0 (E7-A, #394 ist **offen**).
- **Diagnose:** `tasks scheduler` und der Taskmanager (Abschnitt System) zeigen die CPU-Topologie; die Bootzeile nennt `CPUs online=<n>` und den Hyperthreading-Zustand. **Offen (E7-A):** Bootzeile mit „Oberfläche und Worker auf <Liste>“.
- **Gastproben:** `SMP_PROBE` (Abschnitt Gastproben oben); Doom, Isolation, Netz und Speicher laufen mit `ZEHOS_QEMU_SMP=4` (Zahlen in [COSMOS_SUPPORT](docs/COSMOS_SUPPORT.md#mehrere-cpus-smp-stand-und-grenzen-entwurf-e7-f-20-november-2026)).
- **Energieaktionen mit mehreren CPUs:** **offen** (E7-C, #396).
- **Grenzen:** Dass VMware dem Gast keine Hyperthreads zeigt, ist eine Annahme des Plans und **offen** bis E7-E (#398). Leistungsaussagen zu Hyperthreading gelten nur für die gemessene QEMU/KVM-Umgebung; echte Hardware ist **nicht geprüft**. Ein QEMU-Ergebnis belegt kein VMware.

## Lizenz

ZehOS steht unter der GNU General Public License Version 3 ([LICENSE](LICENSE)). Eingebundene Fremdbestandteile behalten ihre eigenen Lizenzen:

- wasm3 (MIT): `ProjektZeh/ThirdParty/wasm3/LICENSE`
- WAMR 2.4.5 (Apache-2.0 WITH LLVM-exception), nur in Builds mit `-p:ZehOSWasmAot=true`: `ProjektZeh/ThirdParty/wamr/LICENSE`, Herkunft in `ORIGIN.txt`
- Rasterizer für Wasm-Apps, übertragen aus ClassiCubes SoftGPU (BSD-3-Clause): `ProjektZeh/Graphics/Raster/LICENSE-ClassiCube.txt`
- BouncyCastle (MIT) als NuGet-Paket
- Cosmos (BSD-3-Clause) als NuGet-Pakete; übernommene Teile aus Cosmos.Network.Http in `ProjektZeh/ThirdParty/CosmosHttp` mit `LICENSE.txt` und `THIRD-PARTY-NOTICES.txt` (darin auch die Mozilla-Zertifikatsliste `cacert.pem`, MPL-2.0)
- Die Doom-Plattformschicht in `experiments/Wasm.DoomProbe` steht wie doomgeneric unter GPL-2.0-or-later ([LICENSE](experiments/Wasm.DoomProbe/LICENSE)).

Das Doom-Paket enthält doomgeneric (GPL-2.0-or-later) und die Spieldaten von Freedoom (BSD), jeweils mit ihren Lizenztexten. Die Quellen liegen öffentlich: doomgeneric am festen Commit sowie Plattformschicht und Buildskript in diesem Repository.
