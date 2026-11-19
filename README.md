# ProjektZeh

[![Build and tests](https://github.com/peterzw98/ProjektZehOS/actions/workflows/ci.yml/badge.svg)](https://github.com/peterzw98/ProjektZehOS/actions/workflows/ci.yml)

ProjektZeh ist ein Cosmos-Gen3-Kernel für x64 mit einem grafischen Desktop und einer kleinen Shell in unabhängigen Terminalfenstern.

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

## Lizenz

ZehOS steht unter der GNU General Public License Version 3 ([LICENSE](LICENSE)). Eingebundene Fremdbestandteile behalten ihre eigenen Lizenzen:

- wasm3 (MIT): `ProjektZeh/ThirdParty/wasm3/LICENSE`
- WAMR 2.4.5 (Apache-2.0 WITH LLVM-exception), nur in Builds mit `-p:ZehOSWasmAot=true`: `ProjektZeh/ThirdParty/wamr/LICENSE`, Herkunft in `ORIGIN.txt`
- Rasterizer für Wasm-Apps, übertragen aus ClassiCubes SoftGPU (BSD-3-Clause): `ProjektZeh/Graphics/Raster/LICENSE-ClassiCube.txt`
- BouncyCastle (MIT) als NuGet-Paket
- Cosmos (BSD-3-Clause) als NuGet-Pakete; übernommene Teile aus Cosmos.Network.Http in `ProjektZeh/ThirdParty/CosmosHttp` mit `LICENSE.txt` und `THIRD-PARTY-NOTICES.txt` (darin auch die Mozilla-Zertifikatsliste `cacert.pem`, MPL-2.0)
- Die Doom-Plattformschicht in `experiments/Wasm.DoomProbe` steht wie doomgeneric unter GPL-2.0-or-later ([LICENSE](experiments/Wasm.DoomProbe/LICENSE)).

Das Doom-Paket enthält doomgeneric (GPL-2.0-or-later) und die Spieldaten von Freedoom (BSD), jeweils mit ihren Lizenztexten. Die Quellen liegen öffentlich: doomgeneric am festen Commit sowie Plattformschicht und Buildskript in diesem Repository.
