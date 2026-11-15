# Firefox als Paket: Machbarkeitsprobe (Weg 1)

> **For agentic workers:** Steps use checkbox (`- [ ]`) syntax for tracking. Abstimmung über IRC `#zehos` mit `agent-coordinator`; Teilaufgaben mit Kennzeichen **[GPT]** können über den Koordinator an GPT-Agenten gehen.

**Goal:** Mit Messwerten entscheiden, ob ein vollständig lokaler Firefox als signiertes ZehOS-Paket (bestehender Paketmanager, kein Kernelbestandteil, kein Fremdrechner zur Darstellung) erreichbar ist. Diese Probe portiert Firefox noch nicht.

**Ausgangslage (gemessen am 15. November 2026):** [HeyPuter/firefox-wasm](https://github.com/HeyPuter/firefox-wasm) (MPL-2.0) baut Gecko mit Emscripten 6.0.1 für Browser-Hosts. Release v0.0.1: `gecko.wasm` 150 MB, 194 780 Funktionen, geteilte Memory (min. 512 MiB, max. 4 GiB, kein memory64), SIMD (`-msimd128`), etwa 20 pthread-Worker, Softwaredarstellung. 473 Importe, davon 5 WASI; der Rest ist Emscripten-/JS-spezifisch: etwa 280 `emscripten_*` (überwiegend WebGL), 38 `_wasmfs_*`, 29 `wasmhost_*` (Wasm-in-Wasm), 25 `invoke_*` mit `_emscripten_throw_longjmp` (setjmp/longjmp über JS, keine C++-Exceptions), `wisp_*` (Sockets über Websocket), `__pthread_create_js`. Ohne Threads startet Gecko nicht (scheitert laut Projekt bei `NS_InitXPCOM`).

**Lücken zu ZehOS** (Stand `docs/PAKETE_ERSTELLEN.md`): wasm3 ohne Threads und ohne SIMD; Modul höchstens 64 MiB, Paket 128 MiB, Memory 256 MiB; ein gemeinsamer App-Worker; keine Sockets, keine Maus, kein Schreibzugriff; VM 512 MiB.

**Zielrechner:** Entwicklungsrechner (22 Threads, 64 GB, KVM) und Koordinatorrechner (Celeron J3455, 4 Kerne, 7,6 GiB, VM realistisch 2 GiB, KVM vorhanden). Beide müssen die Abnahme erfüllen.

## Global Constraints

- Keine Updates bestehender Pakete, SDKs, Cosmos-Versionen oder Werkzeuge ohne Rückfrage. Neue Werkzeuge (WAMR, QEMU auf dem Linux-Rechner, ggf. emsdk) nur unter `.tools/` beziehungsweise nach Rückfrage installieren.
- Heruntergeladene Binärdateien (`gecko.wasm`) nie committen; Bezug nur über Skript mit festem SHA-256.
- Probecode liegt unter `experiments/Firefox.WamrProbe/`. Der normale Kernel bleibt unverändert, bis die Entscheidung in Aufgabe 5 gefallen ist; Gastproben nur hinter eigenem Probe-Buildflag wie bei `ZehOSWasmProbe`.
- AOT-kompilierte native Pakete und ein beschreibbares Profilverzeichnis sind **noch nicht beauftragt**; die Probe misst sie nur, damit der Benutzer entscheiden kann.
- Lizenzen: WAMR Apache-2.0 mit LLVM-Ausnahme, Gecko MPL-2.0. Keine GPL-Bestandteile.

## Review Focus

- Messwerte reproduzierbar (Befehl, Rechner, Version, Hash); geplante und gemessene Werte trennen.
- Gastzeiger und Längen im Shim überlaufsicher prüfen, auch in der Hostprobe.
- setjmp/longjmp über Wasm-Rahmen: kein undefiniertes Verhalten verstecken.

## Aufgaben

### 1. Grundlagen und Inventar

- [ ] 1.1 Skript `experiments/Firefox.WamrProbe/fetch.py`: Release v0.0.1 und WAMR-2.4.5 mit festem SHA-256 nach `.tools/firefox-probe/` laden.
- [ ] 1.2 Importinventar als Skript (`imports.py`) einchecken und jeden Import einstufen: *nötig*, *Stub mit Fehler*, *ungenutzt* **[GPT]**.
- [ ] 1.3 Herkunft von `invoke_*`/`_emscripten_throw_longjmp`, `wasmhost_*` und `hostimg_*` im Quellstand klären; feststellen, welche per Buildoption entfallen (WebGL, Wasm-JIT, WebCodecs) **[GPT]**.

### 2. Hostprobe mit WAMR (Linux, kein Kernel)

- [ ] 2.1 Einbetter in C mit WAMR (geteilte Memory, Atomics, `memory.atomic.wait/notify`, SIMD). pthreads: je Emscripten-Thread eine eigene Ausführungsumgebung über dieselbe Instanz.
- [ ] 2.2 Emscripten-Shim: Uhren, Konsole, pthreads, wasmfs mit nur lesend eingebettetem GRE und Profil im Speicher, WebGL als „nicht verfügbar“, Bildausgabe in eine PPM-Datei **[GPT für Teilgruppen]**.
- [ ] 2.3 setjmp/longjmp lösen. Bewerten: (a) Host-`setjmp` um `invoke_*` bei AOT, (b) Neubau mit `-sSUPPORT_LONGJMP=wasm` (Wasm-Exceptions; WAMR unterstützt sie nur im klassischen Interpreter), (c) Neubau des Engines mit eigenem Patch. Ergebnis begründen.
- [ ] 2.4 Falls nötig: eigener Gecko-Build nach `firefox-wasm`-Anleitung auf dem Entwicklungsrechner (64 GB, ohne LTO), mit Änderungen aus 1.3/2.3.
- [ ] 2.5 Messung: Zeit bis zum ersten Bild von `about:blank` und einer lokalen Testseite, Spitzen-Memory, Threadanzahl. Je Variante: WAMR-Fast-Interpreter und WAMR-AOT (`wamrc`). Auf beiden Zielrechnern **[Koordinator für Celeron]**.

### 3. Gastproben in ZehOS (Probe-Buildflag)

- [ ] 3.1 QEMU mit KVM auf Linux: startet Cosmos 3.0.94? (WHPX scheiterte unter Windows.) Ergebnis in `docs/COSMOS_SUPPORT.md`.
- [ ] 3.2 Speicherprobe: eine zusammenhängende Allokation von 512 MiB bis 1,5 GiB in einer VM mit 2 GiB.
- [ ] 3.3 Threadprobe: 24 Cosmos-Threads mit Warten/Wecken auf gemeinsamen Adressen (Futex-Semantik für `memory.atomic.wait`).
- [ ] 3.4 WAMR-Plattformschicht für Cosmos (Speicher, Threads, Zeit, ausführbarer Speicher für AOT) und das vorhandene Doom-Paket damit statt mit wasm3 starten; Bilder/s gegen 23,7 (TCG) und unter KVM vergleichen.

### 4. Bericht

- [ ] 4.1 `docs/FIREFOX_FEASIBILITY.md`: Messwerte, Lösungen für 2.3, Liste aller nötigen neuen Hostfunktionen und Limits, Aufwandsschätzung für den Port.

### 5. Entscheidung (Benutzer)

**Weiter, wenn:** auf beiden Zielrechnern eine lokale Testseite in höchstens 30 s (Celeron, KVM) erscheint, Spitzen-Memory höchstens 1,5 GiB beträgt und 3.1–3.4 bestehen. Zugleich entscheidet der Benutzer über AOT-native Pakete und ein beschreibbares Profil. Sonst: Ergebnis dokumentieren und auf Etappe 11 (eigener Browser) oder NetSurf zurückfallen.
