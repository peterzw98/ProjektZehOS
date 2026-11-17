# ClassiCube als WASIp1-Paket (Etappen 2, 3 und 6)

[ClassiCube](https://github.com/ClassiCube/ClassiCube) (BSD-3-Clause), ein Klon von Minecraft Classic, als `wasm32-wasip1`-Modul für den ZehOS-Wasm-Loader. Plan: [2026-11-16-classicube.md](../../docs/superpowers/plans/2026-11-16-classicube.md), Lizenzlage: [2026-11-16-classicube-lizenz.md](../../docs/superpowers/specs/2026-11-16-classicube-lizenz.md) (PR #116).

## Aufbau

- `patches/0002-zehos-bitmap-font-only.patch` entfernt für ZehOS die eingebaute Ersatzschrift (`SystemFonts.c`, Lizenz ungeklärt, siehe Lizenznotiz) aus dem Modul und hält die Bitmapschrift aus `default.png` fest; die Option „Use system font“ bleibt wirkungslos.
- `patches/0001-core-zehos-target.patch` ergänzt in `src/Core.h` einen Zielblock `__ZEHOS__`: kooperativ ohne Threads (`CC_BUILD_COOPTHREADED`, wie die DOS- und Konsolenports), Software-Renderer `SoftGPU` (texturiert, mit Alphatest), `Audio_Null`. `build.py --gfx softmin` baut zum Vergleich mit `SoftMin`; der hat keinen Alphatest, Schrift und Oberfläche werden dort zu Balken. Ressourcen-Download, FreeType und Plugins sind per `#undef` aus.
- `platform/Platform_ZehOS.c` und `platform/Window_ZehOS.c` sind von `src/msdos/` abgeleitet. Bild über `zehos_v1.present` (ClassiCubes ARGB-Puffer direkt, der Kernel ignoriert das Alphabyte), Eingabe über `poll_event`, Maus im Spiel über `pointer_capture` und Typ 9, Fenstergröße über Typ 10, Warten über `sleep_ms`. Zeit und Dateien kommen über WASI. Die Patchdatei hält die CRLF-Zeilenenden des Upstreams (`.gitattributes`).
- `patches/0004-zehos-multiplayer.patch` und die Socket-Funktionen in `platform/Platform_ZehOS.c`: Mehrspieler über das Classic-Protokoll und die Netzfunktionen von `zehos_v1` (Entwurf: [2026-11-17-classicube-mehrspieler.md](../../docs/superpowers/specs/2026-11-17-classicube-mehrspieler.md)). `net_open` löst den Namen asynchron auf und verbindet; ClassiCube fragt den Aufbau je Netztick über `net_status` ab und liest und schreibt nicht blockierend. EOF zählt sofort als Verbindungsabbruch. HTTP bleibt ein Stub (keine Skins, keine Server-Texturpakete, keine classicube.net-Anmeldung, keine Verbindungen zu Dritten). Der Launcher zeichnet mit der Bitmapschrift; Kennwort, *Sign in* und *Resume* sind ausgeblendet.
- `platform/Measure_ZehOS.c` misst für den Entscheidungspunkt. Der Linker leitet acht Kernfunktionen per `--wrap` um (Liste `WRAPPED` in `build.py`). Alle 5 s schreibt es in den Chat Bilder/s und je Bild die Anteile Raster (Dreiecke und Pufferlöschen), Mesh (Chunk-Geometrie), Present (Übergabe an den Kernel einschließlich Warten) und Rest. Nach der Weltgenerierung meldet der Chat Gesamt- und Rechenzeit des Generators. stdout von Paket-Apps verwirft der Kernel; deshalb laufen die Werte über die Spielanzeige. `build.py --no-measure` baut ohne Messung (für veröffentlichte Pakete).
- `texpack/`: freies Texturpaket (Schrift aus font8x8, Public Domain; Blockatlas aus Minetest Game, CC BY-SA 3.0; `gui.png`, `icons.png`, `char.png` als eigene Zeichnungen, CC0), Herkunft je Datei in `texpack/TEXTURES.txt`, reproduzierbar mit `texpack/build_texpack.py`.
- Maus: ClassiCube fängt den Zeiger beim Spielstart und nach dem Pausenmenü. Lehnt der Kernel ab (Fenster nicht aktiv), fängt der nächste Klick ins Bild ihn ein, ohne im Spiel zu wirken. Esc gibt ihn frei; erst dann erreicht die Maus den Fensterrahmen.

Das Paket startet ClassiCubes Launcher. *Singleplayer* erzeugt eine Welt im Speicher. *Direct connect* verbindet mit einem Classic-Server: Name, `IP:Port` (etwa `10.0.2.2:25565` für einen Server auf dem Host einer QEMU-VM mit Usermode-Netz) und optional mppass eintragen, dann *Connect*. Das braucht `capability=net` im Paketmanifest (`build-package.py --network`); ohne sie meldet ClassiCube „Failed to connect“, Einzelspieler läuft weiter. Pakete sind nur lesbar. `Platform_ReadonlyFilesystem` unterdrückt Chatprotokolle und Texturcache; Optionen und Welten gehen beim Beenden verloren (dauerhaftes Speichern: Etappe 4).

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

Das Skript installiert das Paket lokal in einer Wegwerfplatte und prüft: Start aus dem Menü, *Singleplayer* im Launcher, Welt sichtbar, Bewegung, Mausblick, Block entfernen und setzen, Schließen und drei Starts mit freiem Speicher.

Mehrspieler gegen den lokalen Testserver `scripts/classic-test-server.py` (nur Standardbibliothek, Protokoll 7 ohne CPE; allein gestartet lauscht er auf `127.0.0.1:25565` und schreibt Ereignisse als JSON-Zeilen):

```powershell
python scripts/test-classicube-multiplayer.py --iso <ISO mit Test-Paketschlüssel> --package experiments/ClassiCube.Wasi/bin/package --key <Test-private.pem> --accel whpx
```

Es baut das Paket mit `--network`, startet den Testserver im Skript, verbindet über *Direct connect* und prüft: Login, Positionen am Server, gesetzter und entfernter Block am Server und bei einem zweiten Client auf dem Host, Blöcke dieses Clients im Bild, Schließen des Fensters bei offener Verbindung, Serverausfall mit Anzeige des Abbruchs, Wechsel Textshell/Desktop danach und sauberes Schließen.

## Messwerte (17. November 2026)

Kernel-ISO von `master` 61bc0be (ZehOS 0.5.3; dieser Branch ändert keinen Kernelcode), QEMU 10.2.2 auf dem Windows-Entwicklungsrechner, Paketfenster 798×519, SoftGPU, Standardwelt (Classic-Generator), Spawnpunkt. Werte aus der Anzeige von `Measure_ZehOS.c` (je zwei 5-s-Fenster), abgelesen von den Fotos des Gastskripts:

| Beschleuniger | Bilder/s | Raster je Bild | Mesh | Present | Rest | Welt erzeugt (Generator-Rechenzeit) |
| --- | --- | --- | --- | --- | --- | --- |
| WHPX, alleinige VM | 5,0–5,1 | 139–180 ms | 15–45 ms | 0 ms | 2–7 ms | 320 ms (220 ms) |
| WHPX, parallel zu einer TCG-VM | 6,2–6,3 | 111–140 ms | 16–33 ms | 0 ms | 2–12 ms | 738 ms (463 ms) |
| WHPX, Blick in Steinboden nach Blockänderung | 3,8–5,7 | 157–235 ms | 13–18 ms | 0 ms | 2–3 ms | – |
| WHPX, Kernel 0.5.7 mit Cosmos 3.0.97, Rechner durch andere Builds/VMs ausgelastet | 1,8–3,9 | 141–470 ms | 44–67 ms | 0–1 ms | 19–45 ms | – |
| TCG, alleinige VM | 1,1–1,2 | 607–655 ms | 0 ms | 2–5 ms | 192–232 ms | 15 674 ms (6 434 ms) |
| TCG, unter Wasser (Wasserfläche über das ganze Bild) | 0,2–0,3 | 2 198–2 985 ms | 253–304 ms | 0 ms | 307–473 ms | – |

Jeder Lauf erzeugt eine andere Zufallswelt; die Werte hängen deshalb auch vom Blickfeld ab. Alle Zeilen außer der mit 0.5.7 liefen auf dem ISO von `master` 61bc0be (Cosmos 3.0.94). Die Werte schwanken stark mit der Last auf dem Entwicklungsrechner; belastbar ist vor allem das Verhältnis der Anteile. Die Rasterisierung (Dreiecke und Pufferlöschen in `Graphics_SoftGPU.c` unter wasm3) belegt unter WHPX rund 70–90 % der Bildzeit, unter TCG gut 70 %. Spiellogik, Eingabe und Oberfläche („Rest“) und die Übergabe an den Kernel fallen unter WHPX kaum ins Gewicht. Die Weltgenerierung ist unter WHPX in unter einer Sekunde fertig. KVM und VMware sind nicht gemessen; die Messung aus Etappe 1 des Plans gibt es noch nicht.

## Grenzen

- Ohne Ton und Speicherstände; Eingaben im Launcher (Name, Server) gehen beim Beenden verloren.
- Mehrspieler nur über IPv4 oder DNS-Namen ohne TLS und ohne HTTP: Mitspieler mit Standardskin, keine Server-Texturpakete. Ein Server, der stumm bleibt, ohne die Verbindung zu schließen, wird nicht erkannt; bei vollem Sendepuffer wartet ClassiCube bis zu 10 s (das Spielbild steht, der Desktop nicht). Solange das Fenster inaktiv ist, pausiert das Spiel und liest keine Netzdaten.
- `particles.png`, `clouds.png` und Mob-Texturen fehlen im Texturpaket; ClassiCube zeichnet dort nichts.
- Ordner legt das Paket nie an (`Directory_Create2` meldet „existiert“), Schreibzugriffe scheitern mit eigenem Fehler.
- Fenster über 960×600 zeichnet ClassiCube in einem ganzzahligen Bruchteil der Größe (maximiert 1920×1200 also in 960×600), der Kernel skaliert hoch.
- SoftGPU wertet VSync nicht aus; die Bildratenbegrenzung von ClassiCube greift nicht. Bei der heutigen Bildrate ohne Folgen.
- Unter TCG ist das Spiel mit gut 1 Bild/s nicht spielbar; unter WHPX/KVM mit rund 6 Bildern/s eingeschränkt.
- Esc bei freiem Zeiger führt laut Vertrag zur Textshell. Pausenmenü, Chat und Inventar schließt man deshalb über ihre Schaltflächen bzw. Enter, nicht mit Esc.
- Bei Fokusverlust öffnet ClassiCube das Pausenmenü; der nächste Klick oder Tastendruck im wieder aktiven Fenster setzt den Fokus zurück.
- Veröffentlichung: `--no-measure` bauen und `build-package.py ... --min-kernel 0.2.22 --network` setzen (Import `pointer_capture`; die Netzfunktionen sind älter).
