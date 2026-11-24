# Ladybird als ZehOS-Paket – Erstbewertung

Stand: 17. November 2026. Wunsch von Lars (15:25): Ladybird paketieren. **Erstbewertung, nichts gebaut oder ausgeführt.** Grundlage: Upstream-Quellstand [`LadybirdBrowser/ladybird`](https://github.com/LadybirdBrowser/ladybird) Commit `eb73f2e90ba0` (17.11.2026), nur gelesen. Vergleich mit dem erfolgreichen NetSurf-Port ([NETSURF.md](NETSURF.md)) und der Firefox-Probe ([ROADMAP §12.1](../ROADMAP.md)). Kennzeichnung: **BELEGT** = im Quelltext bzw. in der Upstream-Doku nachgelesen; **HYPOTHESE** = abgeleitet, nicht geprüft.

> **Entscheidung L3 (Peter, 24. November 2026): nativ**, mit Linux-kompatiblen Systemaufrufen und einem Paketdienst nach dem Vorbild von Linux. Entwurf: [Native Pakete](superpowers/specs/2026-11-24-native-pakete-design.md), Plan: [Ladybird über native Pakete](superpowers/plans/2026-11-24-ladybird-nativ.md). Empfehlungen, Zielpfade und Aufwände unten sind der Stand vor dieser Entscheidung; die Befunde bleiben gültig.

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

## Ergebnis L2

Stand: 20. November 2026. Zusammenfassung der Lesestudien L2-A bis L2-F (alle gemergt, nichts gebaut, Ladybird-Stand `6fba5b78a104`) nach Paket L2-Z des [L2-Plans](superpowers/plans/2026-11-19-ladybird-l2-lesestudie.md). **L1-C (Messung, #450) ist nicht fertig: Es gibt keine Messzahlen** (Speicher, Threads, Binärgrößen); alles Folgende beruht auf Lesen. Jede Zahl steht mit Abschnitt oder Zeile im jeweiligen Bericht (`superpowers/specs/2026-11-19-ladybird-l2-<thema>.md`); eigene Aussagen über Ladybird kommen hier nicht hinzu.

### Urteile

| Paket | Bericht | Urteil | Kernaussage (Beleg) | Aufwand (Personwochen) |
| --- | --- | --- | --- | --- |
| L2-A GC-Heap | [gc-heap](superpowers/specs/2026-11-19-ladybird-l2-gc-heap.md) | `umbaubar` | Region ≤ 1 GiB mit Shift 3 geht in Konstanten, Asserts und Plattformschicht (Urteil, Z. 91-97). Offen: konservativer Stack-Scan unter wasm, Wasm-Lokale sind unsichtbar (Befund 12, HYPOTHESE) | 4–6 (Variante 1, Z. 85) |
| L2-B LibJS | [libjs](superpowers/specs/2026-11-19-ladybird-l2-libjs.md) | `schreibbar` | Interpreter kommt als `flapc`-Maschinencode, für wasm32 gibt es ohne Umbau keinen (Urteil, Z. 71-75); Wartung 95 Commits im Quartal an `interpreter.flap` | 2–6 (Varianten 1 und 2, Z. 63-67) |
| L2-C Prozesse | [prozesse](superpowers/specs/2026-11-19-ladybird-l2-prozesse.md) | `zusammenlegbar` | Naht für alle fünf Teile; Bedingungen: eine Instanz je Dienst, mehr als ein Thread oder Schleifenverschachtelung (hängt an L2-D und 12.7), dauerhafter Fork-Patch (Z. 94-102) | 4–6 (Weg A, Z. 85-92) |
| L2-D Threads | [threads](superpowers/specs/2026-11-19-ladybird-l2-threads.md) | `n Threads` | Unverändert 9 bis 11 Threads je WebContent; „ein Thread“ erst nach Umbau (Urteil, Z. 68-72) | 5–8 (nur für „ein Thread“, Z. 58-60) |
| L2-E Abhängigkeiten | [abhaengigkeiten](superpowers/specs/2026-11-19-ladybird-l2-abhaengigkeiten.md) | `teilweise` | ICU, OpenSSL, SQLite, harfbuzz, Skia bleiben Pflicht und müssten für wasi-sdk gebaut werden; Skia-Portierung ist das Risiko (Urteil, Z. 66-70) | 10–14 (Quellumbau, ohne Bibliotheksportierung, Z. 62-64) |
| L2-F ZehOS-Laufzeit | [laufzeit](superpowers/specs/2026-11-19-ladybird-l2-laufzeit.md) | `nachrüstbar` (memory64, geteilter Speicher, wasi-threads; keines `fehlt`) | wasi-sdk 34.0 hat kein `wasm64`-Ziel (Z. 29); `os_mutex_*`/`os_cond_*` sind Leerfunktionen (Z. 39) | 4–10 (Z. 75-90) |

### Regel für die Wege

**Weg „Wasm“: gangbar, unter Vorbehalten.** Alle vier Bedingungen sind erfüllt: L2-A ist nicht `nicht vertretbar` (`umbaubar`), L2-B ist nicht `nicht vertretbar` (`schreibbar`), L2-C ist `zusammenlegbar`, L2-F ist nicht `fehlt` (`nachrüstbar`). Weil L2-F nicht `fehlt`, geht dessen Spanne (4–10) ein und nicht der Aufwand für 12.6/12.7 aus dem [Plan Roadmap 12](superpowers/plans/2026-11-17-roadmap-12.md); L2-F nennt dazu selbst „12.6/12.7 mit 4 bis 10 Personwochen“ (Gesamturteil, Z. 90). Vorbehalte laut Berichten: L2-C Bedingung 2 hängt an L2-D und 12.7; L2-A Vorbehalt (a) (Stack-Scan) ist ungelöst; L2-E ist nur `teilweise`.

**Weg „nativ“: gangbar nur mit 12.4 und 12.5 als angenommener Voraussetzung.** L2-C: Der native Weg ist „kein Gegenurteil, sondern der Weg ohne diesen Umbau“ und verlangt 12.4, 12.5 und 12.9 vollständig (Prozesse, Abschnitt „Was der native Weg vom Kernel bräuchte“, Z. 83, 104); Ladybird bleibt dabei fast unverändert, der Aufwand liegt im Kernel (Z. 90). Der Interpreter braucht keinen ausführbaren Speicher, ZehOS müsste einen statischen x86-64-Abschnitt laden (L2-B, Z. 69). Threads laufen im Kernel, aber Cosmos 3.0.97 nutzt genau eine CPU, also Zeitscheiben ohne Parallelität (L2-D, Z. 64). 12.4 ist im [Roadmap-12-Plan](superpowers/plans/2026-11-17-roadmap-12.md) (Zeile P6) als „größtes Einzelrisiko“ geführt; I0 hat zunächst die Wasm-Grenze gehärtet und Ring 3 auf später gelegt (`ROADMAP.md` 12.4).

### Gesamtaufwand je Weg

| Weg | Summe der Spannen | Zusammensetzung |
| --- | --- | --- |
| Wasm | **24–42 Personwochen** | A 4–6 + B 2–6 + C 4–6 + E 10–14 + F 4–10. L2-D ist nicht addiert: Bei `n Threads` tragen 12.7 und L2-F die Threads; wer stattdessen „ein Thread“ will, setzt 5–8 obendrauf. |
| nativ | **14–23 Personwochen bezifferbar, nach oben offen** | 12.5/12.9 (P5) 6–10, Threads 12.7 (P3, Voraussetzung von P5) 6–10, Studie 12.4 2–3 (Plan Roadmap 12, Tabelle). Dazu die **Umsetzung von 12.4 in „Monaten“** und die libc-Anbindung für Ladybird (L2-C, Z. 66, HYPOTHESE): beides nicht beziffert. L2-E ist für wasi-sdk geschrieben und für den nativen Bau nicht bewertet. |

Eingehende Annahmen und was **nicht** enthalten ist:

- Die Spannen folgen der Regel 1 Woche je angefangene 1500 Zeilen und zählen **ganze Dateien**, überschätzen also dort, wo nur einzelne Stellen berührt werden (L2-A, Z. 89); Annahmen aus Sonnet-Bearbeitung und HYPOTHESEN sind vor L3 gegenzulesen (L2-A, Kopf).
- **Nicht enthalten:** die Portierung von Skia, ICU, OpenSSL und harfbuzz auf wasi-sdk (unerprobt, „Bauarbeit statt Zeilenzahl“, L2-E, Z. 64); die obere Grenze ist dort offen.
- **Ungelöstes Hauptrisiko (Wasm):** der konservative Stack-Scan. Register und Wasm-Lokale sind unsichtbar, „ohne einen Weg … wäre jede Variante falsch“ (L2-A, Z. 97 und Befund 12). Das kostet in keiner Spanne Wochen, die man kennt.
- **wasm64 fehlt in wasi-sdk** (L2-F, Z. 29). Der Wasm-Weg rechnet deshalb mit Heap-Variante 1 (wasm32); die memory64-Werkzeugkette (eigene Libc) ist nicht gezählt (L2-F, Z. 73).
- 12.6 (Paketgrenzen, 2–4 Wochen laut Plan Roadmap 12) ist in der Wasm-Summe nicht getrennt ausgewiesen; ob die 256-MiB-Grenze für die Region reicht, ist offen (L2-A Befund 17-18).
- Ohne L1-C: kein gemessener Speicher- und Threadbedarf, kein Vergleich mit der Grenze des Referenzrechners.

### Empfehlung

**Liegen lassen:** Gangbar ist formal nur der Wasm-Weg (24–42 Personwochen ohne Bibliotheksportierung), sein Hauptrisiko (Stack-Scan unter wasm) ist ungelöst, der native Weg hängt an einer nicht bezifferbaren 12.4-Umsetzung, und L1-C, 12.7 und 13.5 fehlen noch; der Kandidat bleibt liegen, bis 13.5 und 12.7 stehen und L1-C Zahlen liefert, und wird dann auf dem Wasm-Weg wieder aufgenommen.

### Entscheidungsfrage für L3

Beantwortet: `nativ` (Peter, 24. November 2026). Die Frage lautete:

> Ladybird (Block #457, L3): Welchen Weg sollen wir verfolgen? `wasm` (24–42 Personwochen ohne Bibliotheksportierung, Stack-Scan ungelöst), `nativ` (mindestens 14–23 Wochen Kernel plus 12.4-Umsetzung in Monaten) oder `liegen lassen` (Empfehlung)? Ein Wort genügt.
