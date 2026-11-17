# Ladybird als ZehOS-Paket – Erstbewertung

Stand: 17. November 2026. Wunsch von Lars (15:25): Ladybird paketieren. **Erstbewertung, nichts gebaut oder ausgeführt.** Grundlage: Upstream-Quellstand [`LadybirdBrowser/ladybird`](https://github.com/LadybirdBrowser/ladybird) Commit `eb73f2e90ba0` (17.11.2026), nur gelesen. Vergleich mit dem erfolgreichen NetSurf-Port ([NETSURF.md](NETSURF.md)) und der Firefox-Probe ([ROADMAP §12.1](../ROADMAP.md)). Kennzeichnung: **BELEGT** = im Quelltext bzw. in der Upstream-Doku nachgelesen; **HYPOTHESE** = abgeleitet, nicht geprüft.

## Befunde

| Punkt | Befund |
|---|---|
| Lizenz | **BELEGT** BSD-2-Clause (`LICENSE`). Für ein Paket unkritisch; die mitgelieferten Abhängigkeiten haben eigene Lizenzen (unten). |
| Reifegrad | **BELEGT** „pre-alpha, only suitable for use by developers“ (`README.md`). |
| Sprache und Build | **BELEGT** C++23 (`Meta/CMake/compile_options.cmake:2`), CMake ≥ 3.25, zusätzlich ein Rust-Workspace mit rund 30 Crates, darunter Teile von LibJS (auch JIT), LibWeb, LibWasm, LibGfx, LibHTTP, LibIPC (`Cargo.toml`). Upstream-CI nutzt gcc-16/clang-22; Abhängigkeiten über vcpkg. |
| Umfang | **BELEGT** rund 2 Mio. Zeilen C++/Rust ohne Abhängigkeiten, davon LibWeb ≈ 0,95 Mio., LibJS ≈ 0,37 Mio. (Zählung `wc -l`). NetSurf war um eine Größenordnung kleiner (HYPOTHESE, nicht gezählt). |
| Abhängigkeiten | **BELEGT** `vcpkg.json`: u. a. Skia, ANGLE/Vulkan, Qt 6.10 (UI), ffmpeg, ICU, HarfBuzz, fontconfig, OpenSSL, curl, SQLite, simdutf/simdjson, mimalloc, libjpeg-turbo/libpng/libwebp/libavif/tiff, woff2, dbus, SDL3. Für ein WASIp1-Paket müsste jede davon nach WASI portiert, ersetzt oder abgeschaltet werden. |
| Prozessmodell | **BELEGT** mehrere Prozesse mit IPC: UI, Hub, Compositor, WebContent (je Site), RequestServer, ImageDecoder, MediaServer, AudioServer, VideoDecoderServer, WebRTCClient (`Documentation/ProcessArchitecture.md`). `LibCore/Process.cpp` startet Kindprozesse über `fork`/`vfork`/`exec`, `posix_spawn`-artig. Einen Ein-Prozess-Modus fanden wir in `UI`, `Services`, `LibWebView` nicht (Suche nach „single process“). |
| Threads | **BELEGT** `LibThreading`, `pthread` in `vcpkg.json` und `LibCore/Process.cpp`. ZehOS-Pakete laufen ohne Threads (WASIp1, wasm3). |
| JavaScript | **BELEGT** eigene Engine LibJS mit JIT-Teil (`Libraries/LibJS/JIT`, erzeugt ausführbaren Code). In Wasm ist ein JIT nicht möglich; LibJS hätte als Interpreter zu laufen (HYPOTHESE: Upstream-Interpreterpfad existiert, nicht geprüft, ob ohne JIT baubar). |
| Grafik | **BELEGT** Skia mit GPU-Backends (ANGLE/Vulkan) über den Compositor-Prozess. ZehOS bietet Paketen ein CPU-Bild über `zehos_v1` (NetSurf, ClassiCube). Skia hat einen CPU-Rasterizer (HYPOTHESE für Ladybird nutzbar ohne GPU). |
| Netz/TLS | **BELEGT** RequestServer mit curl/OpenSSL. ZehOS bietet Paketen TCP/TLS über `zehos_v1` (`ZEHOS_NET_TLS`, [PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md)); curl/OpenSSL würden ersetzt. |
| WASI-Unterstützung upstream | **BELEGT** keine: kein WASI-/Emscripten-Ziel in den CMake-Dateien (Suche `wasi`, `emscripten`, `__wasm` ergab nur ein vcpkg-Overlay). |

## Zielpfade

1. **WASIp1-Paket wie NetSurf** (heutiger Weg für Apps): Ladybird müsste in **einen** Prozess ohne Threads zusammengelegt, LibJS ohne JIT, Skia nur CPU, Netz/Bilder/Schriften über `zehos_v1` bzw. eingebaute Decoder, ohne Qt, ffmpeg, dbus, SQLite oder mit Stubs gebaut werden. Paketgrenzen: Modul ≤ 64 MiB, lineare Memory ≤ 256 MiB, Wasm-Stack 1 MiB ([PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md#grenzen)). **HYPOTHESE:** Modulgröße und Speicherbedarf liegen deutlich darüber (Firefox-Probe: geteilter Speicher ab 512 MiB); Portaufwand mehrere Monate, ähnlich oder höher als Firefox, weil kein upstream-WASI-Build existiert.
2. **AOT/Kernel (Roadmap 13.5, WAMR-AOT):** nimmt Geschwindigkeit, nicht die fehlenden Prozesse, Threads, GPU oder POSIX-Dienste. Native Ausführung im Ring 0 ist zudem Peters Sicherheitsentscheidung (13.5, Etappe D).
3. **Eigene native ZehOS-App außerhalb von Paketen:** widerspricht der Regel „neue Apps sind Pakete“ (AGENTS.md) und setzt Prozessisolation (12.4) voraus.

## Was ZehOS dafür noch bräuchte

- Threads für Pakete (WASI-Threads/geteilter Speicher) oder ein upstream-fähiger Ein-Thread-Modus von Ladybird.
- Höhere Paketgrenzen (Modulgröße, Speicher) und ein Ausführungsmodell mit genug Leistung (AOT, 13.5).
- Prozesse/IPC oder einen Ein-Prozess-Modus von Ladybird; Prozessisolation ist ohnehin 12.4.
- Schriften (fontconfig-Ersatz), Bilddecoder, eventuell Medien abgeschaltet; ein Zeichenweg für Skia-CPU über `zehos_v1`.

## Empfehlung

Als Kandidat für Roadmap §12 aufnehmen, **nicht** als nächsten Paketauftrag. Ein Paket ist erst sinnvoll, wenn mindestens 12.4 (Isolation), Threads für Pakete und 13.5 (AOT) stehen oder Ladybird upstream einen Ein-Prozess-/Ein-Thread-Build ohne GPU anbietet. Kleiner nächster Schritt mit Erkenntnisgewinn (auf Wunsch): `Utilities/js.cpp` (LibJS-Kommandozeile) bzw. LibJS allein als WASIp1-Modul ohne JIT versuchen – das prüft C++23/Rust nach WASI, Modulgröße und Interpreterleistung, ohne den ganzen Browser.

Offen: tatsächliche Build- und Größenmessung (nichts gebaut), Interpreterpfad von LibJS ohne JIT, Lizenzen jeder mitgelieferten Abhängigkeit im Paket.
