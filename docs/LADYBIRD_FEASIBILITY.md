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

## Ergebnis N1

Stand: 26. November 2026, Endstand für das Tor N1-Z ([#624](https://github.com/peterzw98/ProjektZehOS/issues/624)) und die Vorstellung am 4. Dezember. Grundlage: [Plan N1](superpowers/plans/2026-11-24-ladybird-n1-nachweise.md), die Berichte [Ring 3 (N1-A7)](superpowers/plans/2026-11-26-ladybird-n1-ring3.md) und [Adressräume (N1-B5)](superpowers/plans/2026-11-26-ladybird-n1-adressraum.md) und die PR-Texte und Kommentare, die unten genannt sind (Stand `story/ladybird` `e548f75b`, geprüft am 26. November 2026 gegen `origin`; Gastläufe der Stufen a bis c liegen bei `b91bbe5c` beziehungsweise davor, der Code ist seither gleich). Die Berichte liegen als `plans/…-n1-ring3.md` und `plans/…-n1-adressraum.md`, nicht als `specs/…-n1-<thema>.md` wie im Plan vorgesehen; zu C gibt es den [Bericht N1-C2](superpowers/plans/2026-11-26-ladybird-n1-aufrufe.md), zu D steht der [Bericht N1-D1](https://github.com/peterzw98/ProjektZehOS/blob/8235f651ffa37e12419ae39211cee09e2e0d94ab/experiments/Ladybird.Probe/musl/bericht-n1-d1.md) im gemergten PR #739 (ein Bericht `…-n1-musl.md` zu D2 gibt es noch nicht), zu E gibt es keinen. **Dieser Abschnitt misst nichts neu** (außer „Messung L1“, dort mit eigener Quelle): Jede Zahl und jedes Urteil steht mit Quelle da; was nicht belegt ist, steht als `offen`. Die Urteile `trägt` in A und B sind die der Senior-Agenten laut Berichten (nur gelesen, ohne eigenen Gastlauf, siehe Quellen). **Gastläufe der Stufen a bis c** und der Gast-Teil von E sind Angaben der Senior-Agenten im IRC (`#first`, Zeiten unten), **nicht von laser gemessen**; nachprüfbar sind nur die Köpfe, Skripte und Hosttests im Repository. Das Tor N1-Z entscheidet Peter oder Lars.

### Stand N1

| Gruppe | Inhalt | Urteil (Quelle) | Stand |
| --- | --- | --- | --- |
| A Ring 3 | Cosmos-Patches A1–A4, Gastfälle A6, Messung A7 | **`trägt`** (Senior, N1-A7-Bericht) | Gastläufe und Messung gelaufen; offen: NMI-Rest unter TCG, VMware, echte Hardware |
| B Adressräume | AddressSpaceMap B1, Seitentabellen B2/B3, Gastfälle B4, Messung B5 | **`trägt`** (Senior, N1-B5-Bericht) | Gastläufe gelaufen; **Kosten je Adressraumwechsel nicht gemessen** ([#716](https://github.com/peterzw98/ProjektZehOS/issues/716)) |
| C Aufrufliste | Auswertung C1, Messung C2 | **`trägt`** ([N1-C2-Bericht](superpowers/plans/2026-11-26-ladybird-n1-aufrufe.md)) | C1 gemergt; C2 gelaufen ([#635](https://github.com/peterzw98/ProjektZehOS/issues/635)): 30 S4-Aufrufe, davon 10 Pflicht, kein GPU-Gerät; nur Linux/glibc |
| D musl | LibJS D1, ganzes Ladybird D2 | D1 **`trägt`** (PR #739, gemergt); D gesamt **`offen`** | D1: `js` statisch mit musl, 0 Patchzeilen; `js -l -c 1+1` im Gast `2` (Stufe c, Angabe senior); D2 ([#636](https://github.com/peterzw98/ProjektZehOS/issues/636), nicht gemergt, Branch `task/ladybird-n1d2-alles` `99fb75b7`): Bau laut Kommentar grün, 87 Patchzeilen (unter 1000), headless `static.html` **ohne Screenshot** (`no_screenshot`), Ursache offen |
| E Vergleichsprogramme | Satz S1, S3, `tools` (E1–E3) | Abnahmeregel des Plans erfüllt, förmliches Urteil noch nicht geschrieben | alle drei gemergt und auf Linux gelaufen; im Gast laut Senior-Angabe 23 von 24 Programmen wie unter Linux (KVM, 1 und 4 CPUs), nur `badptr` weicht ab (FAT ohne symbolische Links) |
| Messung L1 | Bau L1-A, Seiten und Messskript L1-B, Messung L1-C ([#450](https://github.com/peterzw98/ProjektZehOS/issues/450)) | PSS ≤ 2 GiB erfüllt (Messung L1) | L1-A, L1-B und L1-C gelaufen; PSS `static.html` 334 MiB (siehe Messung L1) |
| #423 (13.5 AOT) | Voraussetzung des Tors | abgenommen | Entscheidung Peter 27.11. (Option A), Reste in #749 |

### A: Ring 3 unter Cosmos

**Geprüft.** Die Cosmos-Patches A1 bis A4 (GDT/TSS/IST, `fxsave`, Interruptstub mit Herkunft Ring 3, Eintritt/Systemaufruf/Rückweg; PRs #691, #697, #699, #700) liegen laut Kommentar in #704 vom 26.11. in `story/ladybird-cosmos` (`0aa72ea3`) und `story/ladybird` (`3f82af27`). Die Gastfälle A6 (18 Fälle plus `switch` und `fault_avx`, PR #704, Kopf `a456519c1`, Pin `4027f1c9`, QEMU 10.0.13) liefen:

| Lauf | Ergebnis (PR #704, Kommentare 7177818605, 7178349620, 7178644063) |
| --- | --- |
| KVM 1 und 4 CPUs | je 10/10 |
| TCG `-cpu max` 1 und 4 CPUs | je 3/3 |
| Gegenprobe (Kernelthread-#PF hält an) KVM 1 und 4 CPUs | je 2/2 |
| `switch`-Mutanten (Fall muss rot werden) | beide rot auf KVM 1 und 4 CPUs; Mutant 2 auf 4 CPUs nur über einen Absturz, nicht über den Fall selbst, je 1 Lauf |
| Hosttests | 1747/1747 (`-warnaserror`) |

**Messwerte** (N1-A7-Bericht, Kopf `b332f61d6`, KVM, MSI, TSC 2,4996 GHz, 3 Läufe je CPU-Zahl): Systemaufruf hin und zurück **netto 360–378 Ticks ≈ 0,15 µs** (Regel: ≤ 5 µs), bei 1 und 4 CPUs gleich. Doom-Bildzahl im zweiten 60-s-Fenster, Median über 3 Läufe: wasm3 Basis smp.13 4078, mit A4 4060 (**−0,44 %**, Regel: ≤ 5 %); AOT 12594 gegen 12598 Bilder in drei Läufen (+0,03 %, rund 70 Bilder/s). `test-power-commands.py` je Stand 3/3.

**Urteil `trägt`** (Senior, 26.11., Kopf `086f5fe78`; kein eigener Gastlauf, gestützt auf #704 und den Bericht): A6 erfüllt, Median ≤ 5 µs, Doom ≤ 5 % Verlust; kein Fall scheitert an einer Ursache ohne abgegrenzten Patch.

**Grenzen laut Bericht und Reviews:**

- Die Doom-Messung ist wenig empfindlich: AOT läuft an einer Obergrenze von rund 70 Bildern/s, wasm3 hat rund 3 % Spielraum; der Verlust unterhalb der Obergrenze kann sich verstecken. Eine Abschätzung (unter 0,1 % Rechenzeit) ist geschätzt, nicht gemessen. Das Urteil fällt auf `offen` zurück, wenn eine Last ohne Obergrenze mehr als 5 % Verlust zeigt.
- Nur 3 statt 5 Doom-Läufe (Abweichung vom Plan), `rdtsc` ohne `lfence`, `fxsave`/`swapgs` nicht einzeln gemessen, Plan-Skript `test-power-commands.ps1` durch das Python-Gegenstück ersetzt.
- **Offener Befund #700, verschachteltes NMI unter TCG:** Ein Fehler beim NMI-Ausgang ist behoben (Kommentar 7176781445: 7 von 20 Läufen vorher, 0 von 20 nachher); ein verschachteltes NMI auf CPU 0 (TCG `thread=multi`, 5 von 20) ist belegt, die Ursache offen, KVM 0 von 20 (Kommentar 7177195850). Muss laut Senior vor dem Merge der Patches entschieden werden.
- Nicht geprüft: VMware, echte Hardware, AMD, SMEP/UMIP als Gastfall (nur Hosttests in #700).

### B: Eigene Adressräume

**Geprüft.** B1 `AddressSpaceMap` (PR #666, Folge #677) ist auf `master` gemergt; auf `master` `53cba9cd5` `--filter AddressSpaceMapTests` 24 bestanden, 0 fehlgeschlagen. Die Seitentabellen B2/B3 (PRs #692, #701, #710) und die Gastfälle B4 (PR #712, Kopf `d1f3fc7c1`, 27 Fälle) laufen im Probebau; Zahlen laut N1-B5-Bericht (Kopf `36f94913b`): B4 KVM 1 und 4 CPUs je 10/10, TCG 1 und 4 CPUs je 3/3 (smp1 nach dem Device-Fix `d1f3fc7c1`; davor 1/3 wegen `device`).

**Normalbau unverändert** (ohne Probe, KVM, `master` `53cba9cd5` mit smp.13, je 3 Läufe): Desktop, Netz und ClassiCube bei 1 und 4 CPUs 18/18 bestanden, Bau mit `-warnaserror` ohne Warnungen.

**Messwerte:** Seitenfehler (`pfault`, 4000 Seiten, KVM, Median je Lauf) **rund 2030–2100 Ticks** (≈ 0,8–0,85 µs bei 2,5 GHz, eigene Umrechnung) je Fehler bei 1 und 4 CPUs, p99 2880–3406 Ticks. **Zeit je Adressraumwechsel: nicht messbar.** `Thread.Yield` wechselt erst am Timertakt ([#716](https://github.com/peterzw98/ProjektZehOS/issues/716)); eine Runde dauert 25–50 Mio. Ticks, auch bei zwei reinen Kernelthreads, die Zeitscheibe verdeckt den CR3-Aufwand. Belegt ist nur, dass der CR3 in allen 3000 Paarrunden je CPU-Zahl wechselt.

**Urteil `trägt`** (Senior, 26.11.; nur gelesen): B1 und B4 erfüllt, Normalbau unverändert; die Regel von #634 nennt keine Schwelle für die Wechselzeit, die Messung ist keine Bedingung.

**Grenzen:** Zeitfälle nur KVM; `neg` nicht auf `d1f3fc7c1` neu gelaufen; ein Fenster in B3 (Warten auf `Changing`) nur durch Lesereview gestützt; `gc2` mit 4 CPUs `overlapped=0`; `rdtsc` ohne `lfence`; VMware, echte Hardware nicht geprüft. Die Wechselkosten werden nachgetragen, sobald #716 (`yield-now`, #717 geschlossen, Entwurf) einen sofortigen Wechsel liefert.

### C: Aufrufliste von Ladybird

- **Umgesetzt:** C1, die Auswertung von `strace -ff -f -yy -v` (`experiments/Ladybird.Probe/syscalls.py`, PR #665, Kopf `d7442098e`), 16 Tests OK. C2 ([#635](https://github.com/peterzw98/ProjektZehOS/issues/635), PR #728, [Bericht N1-C2](superpowers/plans/2026-11-26-ladybird-n1-aufrufe.md)): die drei L1-Seiten und `js -l -c '1+1'` unter `strace` am 26.11. auf demselben Rechner wie L1, alle vier Läufe Exit 0.
- **Messwerte:** 94 verschiedene Aufrufe (S1 41, S2 4, S3 19, **S4 30**). **S4-Pflicht 10** (`clock_nanosleep`, `landlock_add_rule`, `landlock_restrict_self`, `prctl`, `prlimit64`, `sched_getaffinity`, `seccomp`, `set_robust_list`, `setpgid`, `statx`), höchstens 12 mit den unentschiedenen `fsync` und `restart_syscall`. Kein `io_uring_*`, `bpf`, `userfaultfd`, `ptrace`. GPU-Geräte werden geöffnet, aber mit ganz verborgenen Geräten läuft `static.html` unverändert (2/2, Screenshot bytegleich): kein GPU-Gerät ist Pflicht. Threads je Prozess bis 21 (`WebContent`), größter reservierter Adressraum `WebContent` ≈ 4,06 TiB.
- **Urteil: `trägt`** (Bericht N1-C2): 10 S4-Pflichtaufrufe bei einer Grenze von 40, keiner der ausgeschlossenen Aufrufe, kein GPU-Gerät.
- **Grenzen:** nur Linux mit glibc und ein Rechner; „Pflicht“ heißt, ohne den Aufruf entsteht kein Screenshot; ob für Sandbox-Aufrufe (`seccomp`, `landlock_*`) Erfolg ohne Wirkung reicht, entscheidet die Messung nicht.

### D: Ladybird mit musl

- **Geprüft (D1, nur `js`, unter Linux)** laut [PR #739](https://github.com/peterzw98/ProjektZehOS/pull/739) (Kopf `8235f651`, gemergt als `2e9459fd`; danach Review-Korrekturen bis `2d03e577`) und [Bericht N1-D1](https://github.com/peterzw98/ProjektZehOS/blob/8235f651ffa37e12419ae39211cee09e2e0d94ab/experiments/Ladybird.Probe/musl/bericht-n1-d1.md): Ladybird `6fba5b78a` baut `js` statisch mit musl ohne JIT (Alpine 3.24.4, WSL 2). `1+1` ergab `2`, die Sortierprobe prüfte alle 10.000 Werte. ELF 33.092.712 Bytes, ohne `PT_INTERP` und `DT_NEEDED`; **0 Patchzeilen** in Ladybird und in vcpkg. Dazu kommen die ICU-Daten (27.687.488 Bytes), die separat veröffentlicht sind. Das Binary liegt auf `story/ladybird-bin` (Commit `9421ab54`, [README](https://github.com/peterzw98/ProjektZehOS/blob/9421ab54/ladybird/README.md), SHA-256 `bd719b4f…2804`) und entspricht laut PR dem Blob im Bericht. Bauabweichungen ohne Patch: `ninja-build` vor samurai, `gcompat` für das glibc-`gn` von Skia, statisches `libncursesw.a` für libedit. Der Wiederanlauf (`--check`, `--resume`) meldete `no work to do`; **ein frischer Gesamtbau ist nicht belegt, eine Bauzeit nennt keine Quelle**.
- **Linux-ABI-Lücken laut Bericht (Stand vor V12 und V13, danach im Gast geschlossen, siehe Stufe c)** (Trace von `js -c '1+1'` gegen die Story-ABI `7f4f983f`): `pipe2(O_NONBLOCK)` und `sysinfo` fehlen; `prlimit64` und `mremap` sind in V10; die ICU-Daten werden über `/proc/self/exe` und `mmap(PROT_READ, MAP_SHARED)` gelesen, die Story kennt nur anonyme Abbildungen (Arbeit in V12); `madvise(MADV_FREE)` liefert in Alpine `0`, die Story `EINVAL`.
- **Geprüft im Gast (Stufen a bis c): Angaben der Senior-Agenten im IRC `#first` vom 26.11., nicht von laser gemessen.** Alle Läufe auf dem MSI-Rechner, KVM, Testbau `-p:ZehOSLinuxRun=true`:
  - Stufe a (`hello` im Gast): `agent-peter-senior` 10:43 Uhr, `task/lb-v3-prozess` `1023d219`: `hello` gibt „Hallo aus dem Ring 3!“ aus, Ende 0; Gegenproben `segv` (`signal 11`, Kernel läuft weiter) und `spin` (Zeitlimit, `signal 9`); smp1 und smp4 je 1/1. Der Branch ist per Merge `b52427e5` in `story/ladybird`.
  - Stufe b (die übrigen Programme aus E gegen `expected/`): `agent-peter-senior2` 16:17 Uhr, Kopf `bb832ea9` (#741), 20 von 22 wie die Basis (`badptr` und `files` scheitern an FAT ohne symbolische Links); 18:24 Uhr, #744 Kopf `509c37a5`, KVM smp1 und smp4 je 24 Programme: `v7bopen` 46/46, `v7bnames`, `v7bquota`, `v7bfull`, `v7bpio` Exit 0, nur `badptr` FAIL. Daraus die Zahl **23 von 24**. Weitere Angaben desselben Laufs: volle Platte liefert `EIO` statt `ENOSPC` (Cosmos-Truncate, bekannte Grenze, [#743](https://github.com/peterzw98/ProjektZehOS/issues/743)). Nicht geprüft laut Senior: TCG, VMware.
  - Stufe c (`js`): zuerst 15:17 Uhr (`task/lb-integration-all` `4eb812ee`, `story/ladybird-bin` `e8826578` ohne ICU-Datei): `signal 6`, ICU-Datei nicht gefunden; 15:35 Uhr (V12 `4f3ba869`): ICU-Datei geladen, dann `pipe2` `ENOSYS`. Danach 15:40 Uhr (`task/lb-stufe-c-js` `0af6c128` = V12 + V13, `ladybird-bin` `9421ab54`): `scripts/test-linux-js.py` **smp1 10/10, smp4 10/10** (`js -l -c 1+1` ergibt `2`, REPL `1+1` und `(7*6).toString(2)`, Strg+D, Strg+C bei `while(1)` mit `signal 2`, danach Terminal wieder frei; Host 1965/1965). Nach den Review-Korrekturen zur Datei-Abbildung 17:54 Uhr (`56e5ab44`, Host 1967/1967): **smp1 11/11, smp4 11/11**. Der Branch ist per Merge `2698f898` in `story/ladybird`; der Kopf `b91bbe5c` (poll, V13-Frist) kam danach (Review-Fixes `97dabe75`), ein neuer Lauf mit genau diesem Kopf ist in den Quellen nicht genannt. Die Zeit-Messung zu `js` im Gast (Rechnen etwa so schnell wie unter Linux, Programmstart im Gast langsamer) steht im [js-Gast-Dokument](superpowers/specs/2026-11-26-ladybird-js-gast.md) (smp1, je 3 Läufe, kein Benchmark).
  - **Nicht geprüft** (auch nicht laut Senior): TCG, VMware, echte Hardware, Windows-Host für die Stufe-c-Läufe, 4 CPUs für Stufe b außer dem Lauf 18:24 Uhr; die Zeile „`1+1` im Gast nicht belegt“ aus dem früheren Entwurf gilt nicht mehr.
- **D2 (#636, Stand `99fb75b7`, nicht gemergt):** Der Branch `task/ladybird-n1d2-alles` enthält vier Patches unter `patches/all/` (`0001` Nachrichtenheader nullinitialisieren, `0002` Linux-ioctl-Header, `0003` und `0004` statische Linkzyklen LibGfx/LibUnicode). **87 Patchzeilen** (hinzugefügte plus entfernte Zeilen ohne Dateikopf: 80, 1, 3, 3; von laser aus den Patchdateien gezählt, eine Trennung nach Ladybird und vcpkg ist nicht geprüft), die Grenze der Regel ist 1000. Der Bau läuft über `build.sh --target all`; Qt kommt dynamisch aus Alpine, nur die Ladybird-Bibliotheken sind statisch. Laut Kommentar von `agent-nweh-senior25` in #636 (18:05 Uhr, [7200048117](https://github.com/peterzw98/ProjektZehOS/issues/636#issuecomment-7200048117)): „Ninja Vollbau grün, Headless `static.html` `no_screenshot`, Laufzeitdiagnose offen“; ein Gast- oder Linux-Lauf von `WebContent` mit Screenshot gibt es nicht. Der Plan verlangt für `trägt`, dass `static.html` headless läuft: **das ist nicht erfüllt**.
- **Offen:** Merge des D2-Branches und Bericht `…-n1-musl.md`; Ursache von `no_screenshot` (headless ohne `WebContent`-Prozess oder Screenshot); ein erneuter Stufe-c-Lauf mit `b91bbe5c` oder dem Tor-Stand; TCG und VMware für die Gastläufe; frischer Gesamtbau von `js` (nur Wiederanlauf belegt).
- **Urteil:** D1 für `js` **`trägt`** (PR #739 gemergt; Stufe c im Gast nach Angabe senior). D2 weder `trägt` (headless-Lauf fehlt) noch `glibc` (an einer nicht lösbaren Stelle gescheitert ist nichts belegt): **D gesamt `offen`**. Der glibc-Bau (L1-A, #448) steht (siehe Messung L1) und bleibt die Rückfallstufe, falls D2 an musl scheitert.

### E: Vergleichsprogramme

- **Umgesetzt und auf Linux geprüft** (`experiments/LinuxAbi`, alle gemergt): S1 mit 12 Programmen (PR #667, Kopf `895a4b79d`; Linux 6.12.90, musl 1.2.5; 5 Läufe gleich, 39 verschiedene Aufrufe, 11 Tests OK); S3 mit 9 Programmen (PR #686, Kopf `0cd5d4518`; 20 von 20 Läufen gleich, danach nach einer Änderung erneut 20 von 20, 5 von 5 unter 6 CPU-Schleifen; 69 verschiedene Aufrufe unter `strace -f -c`; 15 Tests OK); `tools` mit toybox 0.8.15 und zwei Rust-Programmen, 9 Programme (PR #696, Kopf `5992af6c4`; Linux 6.12.117; zweimal gleich, 49 Aufrufe; Review ohne P1/P2).
- **Abnahmeregel des Plans** („Gruppe E `trägt`, wenn E1 bis E3 erfüllt sind“): erfüllt, soweit die PR-Texte reichen. Ein eigenes Urteilsdokument gibt es nicht; das Tor N1-Z führt es.
- **Grenzen:** nur Linux 6.12, andere Kernel und Toolchains nicht geprüft; Lauf in ZehOS nur nach Angabe senior2 (siehe Stufe b in D; nicht von laser gemessen), VMware und TCG nicht; einige Erwartungen hängen an Linux-Verhalten (`EBUSY`, `ENOEXEC`, `EADDRNOTAVAIL`); CI führt die Python-Tests nicht aus.

### Messung L1

Messung L1-C (#450) am 26. November 2026 durch `agent-laser-lead` auf Lars' Rechner (Zuteilung durch `agent-nweh-lead1`), mit dem Bau aus L1-A und `measure.py` aus L1-B (`story/ladybird` `21496432`). Je Seite drei Läufe, `--headless=screenshot --force-cpu-painting`, 798×519, Rechner sonst ruhig (Last ≈ 1, kein Bau). Werte sind Mediane über die drei Läufe; „gesamt“ ist die größte gleichzeitige Summe über alle Prozesse in einer Abtastung (alle 100 ms), keine Summe der Einzelspitzen. Die drei JSON-Ausgaben stehen im PR-Text.

| Seite | Prozesse | Threads gesamt | PSS gesamt (MiB) | größter `VmSize` (GiB) | Screenshot (ms) |
| --- | --- | --- | --- | --- | --- |
| `static.html` | 7 | 69 | 334 | 4162 (`WebContent`) | 5272 |
| `css.html` | 7 | 69 | 344 | 4162 (`WebContent`) | 5666 |
| `js.html` | 7 | 69 | 438 | 4162 (`WebContent`) | 5990 |

- **Prozesse je Lauf:** `Hub`, `Compositor`, `SystemProbe`, zwei `RequestServer`, zwei `WebContent`; im ersten `static`-Lauf ein dritter `WebContent` (8 Prozesse, 70 Threads). Größter Einzelprozess: `WebContent` mit 108–141 MiB PSS; auf `js.html` steigt `Hub` von ≈ 55 auf ≈ 180 MiB.
- **Screenshot** enthält den festen Timer `--screenshot-delay 5` (Plan L1-B); das ist keine Zeit bis zum ersten Bild.
- **Bewertungsregel:** PSS gesamt auf `static.html` 334 MiB, also unter 512 MiB und unter 2 GiB: Keiner der beiden Sätze der Regel greift, die Tor-Bedingung „PSS ≤ 2 GiB“ ist erfüllt. `VmSize` ≥ 32 GiB in `WebContent` (≈ 4,06 TiB): **Die GC-Reservierung ist als Messwert bestätigt.**

Bau (L1-A, #448, PR #669, Kommentar 7151270240, Kopf `26f8f188`; Ladybird `6fba5b78a104`, ohne JIT, glibc, `x64-linux-dynamic`, Rechner HPz: Intel Core Ultra 9 185H, 22 Threads, 62 GiB RAM, Manjaro, Kernel 7.3.4, GCC 16.2.1, Rust 1.98.0, Qt 6.12.0):

| Messwert | Wert |
| --- | --- |
| Bauzeit (Schritt `build`, nach `deps`) | 1112 s (`deps` im Vorlauf 630 s) |
| Spitzen-RSS eines Bauprozesses | 4 847 408 KiB (≈ 4,6 GiB) |
| Spitze des belegten Systemspeichers | 26 018 896 KiB (≈ 24,8 GiB) |
| Platz im Arbeitsordner | 7 172 690 403 Byte (≈ 6,7 GiB), davon `build` 5 435 340 251 |
| Binärdateien gesamt (`binaries_total_bytes`) | 638 375 526 Byte in 59 Dateien |
| fünf größte Dateien | `lib/liblagom-web.so.0.1.0` 271 988 272, `lib/liblagom-webcommon.so.0.1.0` 42 564 312, `lib/liblagom-webview.so.0.1.0` 30 447 624, `bin/Ladybird` 28 652 480, `libexec/WebRTCClient` 28 567 000 |

Grenzen: ein Rechner, Linux-Host statt ZehOS, glibc statt musl; drei Läufe je Seite. `build-stats.json` vermerkt `"resumed": true` (Schritt `build` nach Unterbrechung weitergeführt), die Bauzeit ist daher keine Messung eines ganzen kalten Baus; `LAGOM_LINK_POOL_SIZE` nicht gesetzt (Ladybird hat an diesem Commit einen Fehler, siehe PR #669). Nicht gemessen: Verhalten bei mehreren Tabs, längeren Sitzungen oder Netzseiten.

### Tor N1-Z: GO (Peter, 27.11.2026)

Nach der festen Regel des Plans (Tabelle im [Plan](superpowers/plans/2026-11-24-ladybird-n1-nachweise.md), Abschnitt „Tor“) am Stand `story/ladybird` `e548f75b`:

| Bedingung | Stand | Quelle |
| --- | --- | --- |
| A `trägt` | ja (Senior) | Abschnitt A |
| B `trägt` | ja (Senior) | Abschnitt B |
| C `trägt` | ja | Abschnitt C |
| E `trägt` | Abnahmeregel erfüllt (auf Linux; Gast nach Angabe senior2 23/24) | Abschnitt E |
| D `trägt` oder `glibc` | **offen**: D1 `trägt`, D2 ohne Screenshot | Abschnitt D, #636 |
| PSS ≤ 2 GiB | ja, 334 MiB | Messung L1 |
| #423 abgenommen | **ja**, Entscheidung Peter 27.11. (Option A): AOT fertig und Standard; Reste (Schlüsselschutz, `.aot`-Pakete, VMware) in #749, betreffen den nativen Weg nicht | #423, #749 |

Mit der Abnahme von #423 (Peter, 27.11.) greift wegen D `offen` die Zeile „C oder D `offen`, sonst wie Zeile 1: **Peter kann GO geben, Ladybird (N6) wartet**“; mit D `trägt` oder `glibc` wäre es GO. Die Regel wurde nur angewandt, nicht entschieden: **Die Entscheidung trifft Peter oder Lars.**

**Entscheidung: GO** (Peter im Terminal, 27. November 2026): Der Ladybird-Port beginnt, ohne auf D zu warten. D bleibt offen und wird mit dem Port selbst geschlossen (Ladybird ohne Oberfläche im ZehOS-Gast). Nächste Pakete: dynamisches Laden über `PT_INTERP` (die musl-Helfer `WebContent`, `RequestServer` und `ImageDecoder` sind dynamisch gelinkt und brauchen nur `ld-musl`, `libstdc++`, `libgcc_s`, WebContent zusätzlich `libdrm`), Ladybird-Bau ohne Qt für den headless-Start, Fehlersignale (V24).

### Stand nach dem GO am 27. November 2026

Der erste vollständige **Gast-Screenshot von `static.html`** (798 × 4087 Pixel, 50 Absätze und eine Tabelle) gelang auf `cbcdb6bb` mit dem musl-Bündel `99fb75b7`, Hub ohne Qt, QEMU/KVM, 1 CPU und 30 s Screenshot-Verzögerung (senior, #first, 11:23). Dafür nötig waren unter anderem dynamisches Laden (V25/V25b), Dateisperren und die Bündelplatte (V28/V28b), `/proc` (V29), Dateiseiten bei Zugriff (V30), `MAP_SHARED` für Dateien (V32), `FIONBIO` (V33), Eigentümer (V34), Prozessgruppen (V35), Thread-Plätze (V36), Umgebung (V37), AHCI-Flush mit 30 s sowie `fonts.conf` und `conf.d`. [V31 strace-Abgleich](superpowers/plans/2026-11-27-ladybird-v31-strace.md)

| Gastnachweis | Stand und Bedingungen | Ergebnis |
| --- | --- | --- |
| Stabilität nach V38 | `4c8f73e3`, `static.html` | KVM 3/3 mit 1 CPU und 3/3 mit 4 CPUs, je etwa 85 s. TCG mit 1 CPU 3/3. |
| KVM-Abnahme mit V43/V45 | `0760f4b8`, Cosmos `053fb42`, `lb-headless-99fb75b7`, 2 GiB, je 1 und 4 CPUs | `static.html`, `css.html`, `js.html`, `img.html` **8/8** im ersten Versuch, Gastlauf 82–89 s, gleicher Anteil nicht-weißer Pixel je Seite (senior2, #first, 18:02). |
| TCG auf demselben Abnahmestand | `0760f4b8`, `lb-headless-99fb75b7`, 1 CPU, 2 GiB | `static.html` und `js.html` je 1/1, 104 bzw. 120 s im Gast, Pixelanteil wie bei KVM (senior2, #first, 18:11). |
| Neues Standardbündel | Runner `a104d235`, ISO `0760f4b8-run`, `ladybird-static-with-images.tar.gz`, KVM, 1 CPU, 2 GiB | `static.html` und `img.html` je 1/1 (senior2, #first, 18:17). |
| Generalproben | `ca0e3238` und finaler Cache-Check `77194232`, ISO `0760f4b8-run`, Standardbündel | MSI/KVM: static, Projektseite und Canvas je 1/1 und 88 s gesamt. WSL/TCG: static 2/2, je 104 s gesamt (96 s im Gast), PNG bytegleich zur MSI-Referenz. [Demo-Ablauf](superpowers/plans/2026-12-04-ladybird-demo-ablauf.md) |
| Generalprobe am Stück (Freeze-Kandidat) | `9d461fa7`, ISO `0760f4b8-run`, Standard- und HTTP-Bündel aus dem Asset-Ordner, MSI/KVM, 1 CPU, 2 GiB | Alle Schritte im ersten Versuch: static 88 s, Projektseite 87 s, Canvas 89 s, HTTP 71 s; 335 s reine Laufzeit. PNGs bytegleich zu den früheren Läufen (senior2, #first, 19:55).
| KVM-Abnahme mit V46 | `52a2cdc4` (`rt_sigsuspend`), Standardbündel, 2 GiB, je 1 und 4 CPUs | static, css, js, img **8/8** im ersten Versuch, Gastlauf 80–86 s, Pixelanteile wie `0760f4b8` (senior2, #first, 21:40). |
| KVM-Abnahme mit V46 und V45-Nacharbeit | `d0a5b7b7` (sigreturn übernimmt TF/RF), Standardbündel, 2 GiB, je 1 und 4 CPUs | **8/8** im ersten Versuch, 87–104 s gesamt, Pixelanteile unverändert (senior2, #first, 21:59). |
| KVM-Abnahme mit V48 | `13d48244`, Standardbündel, 2 GiB, je 1 und 4 CPUs | **7/8**: `static` mit 4 CPUs einmal Gast-Stillstand beim Screenshot (Ticks enden, kein Halt), 3 Wiederholungen ok; Ursache offen (senior2, #first, 23:01). |
| KVM-Abnahme mit V50 (Freeze-Kandidat) | `ff57d65b`, Cosmos-Pin `gf2b64df` (Sweep löscht Header toter Objekte), 2 GiB | **8/8**, dazu `static` mit 4 CPUs **10/10** (senior2, #first, 28.11. 00:10); Fangschaltung 20/20 auf V50-ISO ohne Stillstand (engineer, 23:37). |
| HTTP über IPv4-Loopback | `e4f5eff6`, `6301341f`, `2dd30960`, kombiniertes `bundle-http.tar.gz`, KVM, 1 CPU, 2 GiB | Projektseite 3/3 (68–91 s, PNG 798 × 720 Pixel), BusyBox-`httpd` und Hub im selben LinuxRun-Prozessbaum (engineer2, #first, 17:34, Befehle von senior2, 18:41). |

**Offene Grenze bei vollem Speicher (laser, 27.11.).** Mit 512 MiB und vielen Unix-Sockets hält der Kernel in einzelnen Läufen an: `LinuxUnixSocket.Close` findet `Link.Gate` leer (4 Belege, 1 und 4 CPUs, `c95ab059` und `d1d06dc0`, jeweils nach mindestens einer GC). Laut Codelesung (senior) hält die Cosmos-GC alle CPUs an und behandelt GC-Handles korrekt; die Ursache ist offen, ein Diagnosebau läuft. Dazu getrennt: Bei erschöpftem Speicher endet der Lauf mit Kernel-OOM statt `ENOMEM`, und `AllocateHandle` liefert ungeprüft `null`. Alle Demo- und Abnahmeläufe mit 2 GiB waren davon frei. Belege: `task/lb-v43-probe` (`5cbcf1a6`, `experiments/LinuxAbi/src/v43mem/befunde/`).

**Grenzen der Demo.** Die Gastläufe verwenden Headless mit `--disable-sandbox`. Der geprüfte HTTP-Pfad bleibt auf IPv4-Loopback beschränkt. Die WSL-HTTP-Zusatzprobe mit dem ursprünglichen Archiv-Wrapper speicherte ein bytegleiches PNG, blieb danach beim Warten auf den Server stehen. Der KVM-Ablauf verwendet den gesondert geprüften Wrapper ohne diesen Warteaufruf. VMware und echte Hardware stehen aus. Die Demo nutzt 2 GiB Gast-RAM. Bei 512 MiB kann V43 `WebContent` per `SIGKILL` beenden. In der V38-Startmessung lud die Seite im Gast in 5–8 s, unter Linux in 0,3 s.

Der [Risiko-Review](superpowers/specs/2026-11-27-ladybird-risk-uebersicht.md) umfasst fünf Blöcke. P1 `readv` und die P2-Befunde bis auf die `SCM_RIGHTS`-Grenze wurden behoben und von laser nachgeprüft.

### Offene Punkte

1. **N1-D2 (#636):** Der Headless-Screenshot ist im [Stand nach dem GO](#stand-nach-dem-go-am-27-november-2026) belegt. Offen sind der Merge von `task/ladybird-n1d2-alles`, der D2-Bericht und das förmliche Urteil D (`trägt` oder `glibc`).
2. **#749:** AOT-Reste nach #423 (Schlüsselschutz §5.1, erste `.aot`-Pakete, VMware); keine Voraussetzung des Tors mehr.
3. **#716:** Kosten je Adressraumwechsel erst nach sofortigem Yield messbar.
4. **#700-Rest:** verschachteltes NMI unter TCG, Ursache offen.
5. **Messung ohne Obergrenze** für A (Doom ist an 70 Bildern/s gedeckelt), VMware und echte Hardware für A und B.
6. **Gastläufe a bis c:** nur Angaben der Senior-Agenten; TCG, VMware und echte Hardware nicht geprüft; ein Lauf von `test-linux-js.py` mit dem Kopf `b91bbe5c` ist nicht genannt; `EIO` statt `ENOSPC` bei voller Platte (#743), `badptr` wegen FAT.
7. **Reviews:** Das Delta `5802b0abe` von #704 (nur `scripts/`) ist nach dem Bericht noch nicht nachgeprüft; die Berichte N1-A7 und N1-B5 sind Entwurf, die Urteile nur gelesen. Ein förmliches Urteil für E fehlt.
