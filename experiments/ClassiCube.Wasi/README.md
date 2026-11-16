# ClassiCube als WASIp1-Paket (Etappe 2)

[ClassiCube](https://github.com/ClassiCube/ClassiCube) (BSD-3-Clause), ein Klon von Minecraft Classic, als `wasm32-wasip1`-Modul für den ZehOS-Wasm-Loader. Plan: [2026-11-16-classicube.md](../../docs/superpowers/plans/2026-11-16-classicube.md), Lizenzlage: [2026-11-16-classicube-lizenz.md](../../docs/superpowers/specs/2026-11-16-classicube-lizenz.md) (PR #116).

## Aufbau

- `patches/0001-core-zehos-target.patch` ergänzt in `src/Core.h` einen Zielblock `__ZEHOS__`: kooperativ ohne Threads (`CC_BUILD_COOPTHREADED`, wie die DOS- und Konsolenports), Software-Renderer `SoftMin`, `Audio_Null`. Netz, Ressourcen-Download, FreeType und Plugins sind per `#undef` aus. Ohne Netz liefern alle HTTP-Abrufe (Skins, Texturpakete vom Server) sofort einen Fehler.
- `platform/Platform_ZehOS.c` und `platform/Window_ZehOS.c` sind von `src/msdos/` abgeleitet. Bild über `zehos_v1.present` (ClassiCubes ARGB-Puffer direkt, der Kernel ignoriert das Alphabyte), Eingabe über `poll_event`, Maus im Spiel über `pointer_capture` und Typ 9, Fenstergröße über Typ 10, Warten über `sleep_ms`. Zeit und Dateien kommen über WASI. Die Patchdatei hält die CRLF-Zeilenenden des Upstreams (`.gitattributes`).
- `platform/Measure_ZehOS.c` misst für den Entscheidungspunkt. Der Linker leitet acht Kernfunktionen per `--wrap` um (Liste `WRAPPED` in `build.py`). Alle 5 s zeigt Statuszeile 2 oben rechts Bilder/s und je Bild die Anteile Raster (Dreiecke und Pufferlöschen), Mesh (Chunk-Geometrie), Present (Übergabe an den Kernel einschließlich Warten) und Rest. Nach der Weltgenerierung meldet der Chat Gesamt- und Rechenzeit des Generators. stdout von Paket-Apps verwirft der Kernel; deshalb laufen die Werte über die Spielanzeige.
- `texpack/`: freies Texturpaket (Schrift aus font8x8, Public Domain; Blockatlas aus Minetest Game, CC BY-SA 3.0), Herkunft je Datei in `texpack/TEXTURES.txt`, reproduzierbar mit `texpack/build_texpack.py`.

Gestartet wird immer mit `--singleplayer`: Der Launcher läuft nicht, eine Welt wird im Speicher erzeugt. Pakete sind nur lesbar. `Platform_ReadonlyFilesystem` unterdrückt Chatprotokolle und Texturcache; Optionen und Welten gehen beim Beenden verloren (dauerhaftes Speichern: Etappe 4).

## Bauen

```powershell
git clone https://github.com/ClassiCube/ClassiCube.git <Ordner>
git -C <Ordner> checkout --detach 2b01bee867fe81331d0a898e7ad40a71882131de
python experiments/ClassiCube.Wasi/build.py --source <Ordner> --wasi-sdk <wasi-sdk-25.0>
```

`build.py` kopiert `src/` nach `.tools/classicube-build/`, wendet die Patches dort an (die Arbeitskopie bleibt unverändert) und schreibt `bin/classicube.wasm` mit SHA-256 sowie den unsignierten Paketinhalt `bin/package/`: Modul, `texpacks/default.zip` und `licenses/` (ClassiCube, Texturpaket, wasi-libc, compiler-rt, `SOURCES.txt`). Das Modul importiert nur `zehos_v1` (`present`, `poll_event`, `pointer_capture`, `should_exit`, `sleep_ms`) und lesende WASI-Dateifunktionen. Es braucht damit ZehOS ab 0.2.22; Typ 10 nutzt es ab 0.5.1, ältere Kernel liefern ihn nur nicht.

## Prüfen

```powershell
python scripts/test-classicube-desktop.py --iso <ISO mit Test-Paketschlüssel> --package experiments/ClassiCube.Wasi/bin/package --key <Test-private.pem>
```

Das Skript installiert das Paket lokal in einer Wegwerfplatte und prüft: Start aus dem Menü, Welt sichtbar, Bewegung, Mausblick, Block entfernen und setzen, Schließen und drei Starts mit freiem Speicher.

## Grenzen

- Ohne Ton, Netz und Speicherstände.
- `gui.png`, `icons.png`, `char.png` und weitere Grafiken fehlen noch im Texturpaket. ClassiCube zeichnet an diesen Stellen nichts.
- Die eingebaute Ersatzschrift von ClassiCube (`SystemFonts.c`, Lizenz ungeklärt) erscheint nur ohne `default.png` oder bei gewählter Systemschrift.
