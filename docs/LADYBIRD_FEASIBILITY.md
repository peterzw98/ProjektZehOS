# Ladybird als ZehOS-Paket – Erstbewertung

Stand: 17. November 2026. Wunsch von Lars (15:25): Ladybird paketieren. **Erstbewertung, nichts gebaut oder ausgeführt.** Grundlage: Upstream-Quellstand [`LadybirdBrowser/ladybird`](https://github.com/LadybirdBrowser/ladybird) Commit `eb73f2e90ba0` (17.11.2026), nur gelesen. Vergleich mit dem erfolgreichen NetSurf-Port ([NETSURF.md](NETSURF.md)) und der Firefox-Probe ([ROADMAP §12.1](../ROADMAP.md)). Kennzeichnung: **BELEGT** = im Quelltext bzw. in der Upstream-Doku nachgelesen; **HYPOTHESE** = abgeleitet, nicht geprüft.

## Befunde

| Punkt | Befund |
|---|---|
| Lizenz | **BELEGT** BSD-2-Clause (`LICENSE`). Für ein Paket unkritisch; die mitgelieferten Abhängigkeiten haben eigene Lizenzen (unten). |
| Reifegrad | **BELEGT** „pre-alpha, only suitable for use by developers“ (`README.md`). |
| Sprache und Build | **BELEGT** C++23 (`Meta/CMake/compile_options.cmake:2`) und Rust als gleichwertiger Teil, nicht nur Ergänzung: Rust-Quellen sind dem Umfang nach größer als C++ (huang: `.rs` 44,8 MB, C++ 34,6 MB; GitHub nennt Rust als Hauptsprache). Rust-Workspace mit rund 30 Crates (`Cargo.toml`), u. a. LibJS selbst (`Libraries/LibJS/Rust/src`: AST, Bytecode, Compiler) und `LibJS/JIT/Rust`, LibWeb, LibWasm, LibGfx, LibHTTP, LibIPC; Cargo/`build.rs` sind Pflicht. CMake ≥ 3.25, Abhängigkeiten über vcpkg. |
| Umfang | rund 2,0–2,1 Mio. Zeilen ohne Abhängigkeiten, LibWeb ≈ 0,95–1,07 Mio., LibJS ≈ 0,37–0,39 Mio. (eigene Zählung `wc -l` über `.cpp/.h/.rs` bzw. huangs Zählung; die Spanne hängt von den mitgezählten Dateiarten ab). NetSurf war um eine Größenordnung kleiner (HYPOTHESE, nicht gezählt). |
| Abhängigkeiten | **BELEGT** `vcpkg.json` (gepinnte Stände laut huang: qtbase 6.11.3, Skia 148, ffmpeg 9.0.2, ICU 78.3, OpenSSL 3.6.5, SQLite 3.54.1; die Bauanleitung verlangt Qt ≥ 6.10): u. a. Skia, ANGLE/Vulkan, Qt (UI), ffmpeg, ICU, HarfBuzz, fontconfig, OpenSSL, curl, SQLite, simdutf/simdjson, mimalloc, libjpeg-turbo/libpng/libwebp/libavif/tiff, woff2, dbus, SDL3. Für ein WASIp1-Paket müsste jede davon nach WASI portiert, ersetzt oder abgeschaltet werden. |
| Prozessmodell | **BELEGT** mehrere Prozesse mit IPC: UI, Hub, Compositor, WebContent (je Site), RequestServer, ImageDecoder, MediaServer, AudioServer, VideoDecoderServer, WebRTCClient (`Documentation/ProcessArchitecture.md`). `LibCore/Process.cpp` startet Kindprozesse über `fork`/`vfork`/`exec`, `posix_spawn`-artig. Hub und UI lassen sich zusammenlegen (`--headless`, Windows-Weg; huang), einen Ein-Prozess-**Browser** gibt es nicht: Renderer und Dienste bleiben eigene Prozesse. |
| Threads | **BELEGT** `LibThreading`, `pthread` in `vcpkg.json` und `LibCore/Process.cpp`. ZehOS-Pakete laufen ohne Threads (WASIp1, wasm3). |
| JavaScript | **BELEGT** eigene Engine LibJS mit JIT-Teil (`Libraries/LibJS/JIT`, erzeugt ausführbaren Code). In Wasm ist ein JIT nicht möglich; LibJS hätte als Interpreter zu laufen (HYPOTHESE: Upstream-Interpreterpfad existiert, nicht geprüft, ob ohne JIT baubar). |
| Grafik | **BELEGT** Skia mit GPU-Backends (ANGLE/Vulkan) über den Compositor-Prozess. ZehOS bietet Paketen ein CPU-Bild über `zehos_v1` (NetSurf, ClassiCube). Skia hat einen CPU-Rasterizer (HYPOTHESE für Ladybird nutzbar ohne GPU). |
| Netz/TLS | **BELEGT** RequestServer mit curl/OpenSSL. ZehOS bietet Paketen TCP/TLS über `zehos_v1` (`ZEHOS_NET_TLS`, [PAKETE_ERSTELLEN.md](PAKETE_ERSTELLEN.md)); curl/OpenSSL würden ersetzt. |
| WASI-Unterstützung upstream | **BELEGT** kein WASI-Buildziel für Ladybird selbst (kein WASI-/Emscripten-Ziel in den CMake-Dateien). Ladybirds eigene Rust-Wasm-Engine **hostet** WASI-Module (`Libraries/LibWasm/Engine/src/wasi.rs` mit Tests) – die Gegenrichtung, beim Paketieren keine Hilfe. |

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

Als Kandidat für Roadmap §12 aufnehmen, **nicht** als nächsten Paketauftrag. Ein Paket ist erst sinnvoll, wenn mindestens 12.4 (Isolation), Threads für Pakete und 13.5 (AOT) stehen oder Ladybird upstream einen Ein-Prozess-/Ein-Thread-Build ohne GPU anbietet. Den Etappenplan mit Go/No-Go enthält [docs/superpowers/plans/2026-11-17-ladybird.md](superpowers/plans/2026-11-17-ladybird.md); er nennt zusätzlich die 32-GiB-Adressraumreservierung des LibGC-Heaps und stellt die folgende LibJS-Probe hinter seine Lesestudie L2 zurück. Ursprünglicher Vorschlag: `Utilities/js.cpp` (LibJS-Kommandozeile) bzw. LibJS allein als WASIp1-Modul ohne JIT versuchen. Weil große Teile von LibJS in Rust liegen, heißt das: Rust-Crates für das Rust-Ziel `wasm32-wasip1` und den C++23-Teil mit wasi-sdk bauen und zusammen linken – das prüft genau die schwierigste Kombination, Modulgröße und Interpreterleistung, ohne den ganzen Browser.

**Nicht geprüft:** genaue Zeilenzahlen (Zählweise), CI-Compilerstände (gcc-16/clang-22 laut Bauanleitung), NetSurf-Größenvergleich, ob LibJS ohne JIT baubar ist, tatsächliche Build- und Größenmessung (nichts gebaut), Lizenzen jeder mitgelieferten Abhängigkeit. Korrekturen nach huangs Stichprobe (15:30) eingearbeitet.
