# Ladybird Etappe L1: Hostmessung

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-17-ladybird.md) lesen, besonders „Feste Werte“ und „Was offen bleibt“.
> Empfohlene Arbeitsweise: superpowers:executing-plans. Schritte haben Kästchen zum Abhaken.

**Ziel:** Zahlen statt Hypothesen. Ladybird ohne JIT auf einem Linux-Host bauen, drei feste Seiten mit `--headless=screenshot --force-cpu-painting` laden und messen: Prozess- und Threadzahl, Speicher (PSS und reservierter Adressraum) je Prozess, Binärgrößen, Zeit bis zum Screenshot, Bauzeit und Platzbedarf. Nichts portieren.

**Voraussetzung für L1-A und L1-C:** Freigabe eines Projektbenutzers für den Bau (siehe „Was offen bleibt“ in der Übersicht). L1-B braucht keine Freigabe.

**Ort:** `experiments/Ladybird.Probe/` (neu). Quellen und Bauordner liegen außerhalb des Repositorys unter `~/ladybird-probe/` und werden nie committed.

## Pakete

### Paket L1-A: Bauskript und Bau

- **Branch:** `task/ladybird-l1a-bau` · **Rolle:** Engineer auf einem Linux-Rechner mit mindestens 32 GiB RAM und 200 GiB freiem Platz · **Voraussetzung:** Bau-Freigabe
- **Dateien (anlegen):** `experiments/Ladybird.Probe/build.sh`, `experiments/Ladybird.Probe/README.md`
- **Schnittstelle:** `build.sh [--jobs N] [--workdir DIR]`; Standard `--workdir ~/ladybird-probe`, `--jobs` = Kernzahl. Quelle in `$WORKDIR/src`, Bau in `$WORKDIR/build` (das Preset allein baut nach `<quelle>/Build/release`, `Meta/CMake/presets/CMakeBasePresets.json:23`). Exit 0 nur, wenn `$WORKDIR/build/bin/Ladybird` existiert (Ziel `Ladybird`, `UI/CMakeLists.txt:45`). Schreibt `$WORKDIR/build-stats.json` mit `{"commit": "...", "seconds": n, "peak_rss_kib": n, "disk_bytes": n, "binaries": {"<name>": bytes, ...}}`; `peak_rss_kib` aus `/usr/bin/time -v`, `disk_bytes` aus `du -sb` über Quelle, vcpkg-Cache und Build.
- **Schritte:**
  - [ ] `build.sh` schreiben: Klon auf den festen Commit (Übersicht, „Feste Werte“), Prüfung `git rev-parse HEAD`; Vorbedingungen laut `Documentation/BuildInstructionsLadybird.md` prüfen (CMake ≥ 3.30, Qt ≥ 6.10, rustup, nasm, Compiler) und bei Fehlen mit Liste abbrechen, **nichts installieren**.
  - [ ] Konfigurieren mit `cmake --preset Release -S $WORKDIR/src -B $WORKDIR/build -DENABLE_JS_JIT=OFF`, `-DLAGOM_LINK_POOL_SIZE=2`; `ENABLE_GUI_TARGETS` bleibt an (ohne sie gibt es keine Services, `CMakeLists.txt:139-141`).
  - [ ] Bau unter `/usr/bin/time -v`; Binärgrößen aller Dateien in `build/bin` und `build/lib` (`stat -c %s`).
  - [ ] **Abbruchregel:** Bau länger als 8 Stunden, Platz über 200 GiB oder OOM auch mit `LAGOM_LINK_POOL_SIZE=1` → Abbruch, `[BLOCKER]` mit Stand und den bis dahin gemessenen Zahlen. Kein dritter Versuch ohne Rückfrage.
  - [ ] README: Zweck, Voraussetzungen, Aufruf, Grenzen; Hinweis, dass nichts davon in den Kernel- oder Paketbau gehört.
- **Abnahme:** `build-stats.json` im PR-Text (nicht committed), Kopf-Hash, Rechner (CPU, RAM, Distribution). Code-PR → unabhängiges Review.

### Paket L1-B: Testseiten und Messskript

- **Branch:** `task/ladybird-l1b-messung` · **Rolle:** Engineer · **Voraussetzung:** keine (läuft neben L1-A)
- **Dateien (anlegen):** `experiments/Ladybird.Probe/pages/static.html`, `pages/css.html`, `pages/js.html`, `experiments/Ladybird.Probe/measure.py`, `experiments/Ladybird.Probe/test_measure.py`
- **Seiten (selbst geschrieben, keine fremden Inhalte):**
  - `static.html`: 50 Absätze Text, 3 Überschriften, eine Tabelle 10×5, kein CSS außer Browserstandard, kein Skript.
  - `css.html`: derselbe Inhalt mit Flexbox-Raster, 2 Webfonts als `@font-face` aus lokalen Dateien unter `pages/fonts/` (freie Schrift, Lizenzdatei daneben), Farbverläufe, `position: sticky`.
  - `js.html`: erzeugt 2000 DOM-Knoten per Skript, sortiert ein Array mit 100 000 Zahlen, setzt danach `document.title = "done"`.
- **Schnittstelle:** `measure.py --binary PATH --page FILE [--runs 3] [--width 798] [--height 519] [--timeout 120] --out FILE.json`.
  - Startet `<binary> --headless=screenshot --force-cpu-painting --window-width W --window-height H --screenshot-delay 5 --screenshot-path <tmp>/shot.png file://<page>` (Optionen laut `Libraries/LibWebView/Application.cpp:390-417,507`). Der Screenshot zeigt die ganze Seite; `--window-height` begrenzt die Bildgröße nicht.
  - Tastet alle 100 ms den Prozessbaum ab (`/proc/<pid>/task/*/children` rekursiv): je Prozess Name (`/proc/<pid>/comm`), Threads (`status: Threads`), `VmSize`, `VmRSS` (`status`), PSS (`smaps_rollup: Pss`).
  - Zeit bis Screenshot: Start bis `shot.png` existiert und Größe > 0. Der Wert ist **keine** Zeit bis zum ersten Bild: Ladybird löst den Screenshot über einen festen Timer nach `--screenshot-delay` aus (Standard 1 s, `Libraries/LibWebView/Options.h:98`; `Application.cpp:2475-2494`). 5 s geben `js.html` Zeit zum Fertigwerden. Abgetastet wird bis zum Ende aller Prozesse (Ladybird beendet sich nach dem Screenshot selbst); läuft danach noch etwas, nach 10 s SIGTERM, nach weiteren 5 s SIGKILL.
  - Ausgabe JSON: `{"page": ..., "runs": [{"screenshot_ms": n, "processes": [{"name":..., "max_threads": n, "max_pss_kib": n, "max_vmsize_kib": n}], "total_max_pss_kib": n}], "median": {...}}`. Median über die Läufe.
  - Zeitlimit überschritten → Lauf mit `"error": "timeout"`, Exit 2.
- **Tests (`test_measure.py`, `python3 -m unittest`, ohne Ladybird):**
  - `test_tree_collects_children`: startet `sh -c 'sleep 5 & sleep 5; wait'`, erwartet 3 Prozesse im Baum (ohne `wait` ersetzt `sh` sich per exec, dann sind es 2).
  - `test_pss_parsed`: Beispieltext für `smaps_rollup` mit `Pss: 1234 kB` → 1234.
  - `test_median_of_three`: Läufe 100/300/200 ms → Median 200.
  - `test_timeout_exit_code`: Attrappe ohne Screenshot, `--timeout 1` → Exit 2.
- **Abnahme:** 4/4 Tests grün auf Linux, Seiten im Firefox des Bearbeiters sichtbar geprüft (`js.html` zeigt Titel `done`). Code-PR → unabhängiges Review.

### Paket L1-C: Messung und Nachtrag

- **Branch:** `docs/ladybird-l1c-ergebnis` · **Rolle:** Engineer oder Clerk auf dem Rechner aus L1-A · **Voraussetzung:** L1-A und L1-B gemergt, Bau aus L1-A vorhanden
- **Datei (ändern):** `docs/LADYBIRD_FEASIBILITY.md` (neuer Abschnitt „Messung L1“)
- **Schritte:**
  - [ ] Je Seite `measure.py --runs 3`, Rechner sonst ruhig (keine anderen Bauten; sonst „Zeiten keine Messung“ vermerken).
  - [ ] Nachtrag: Tabelle Seite | Prozesse | Threads gesamt | PSS gesamt (MiB) | größter `VmSize` (GiB) | Screenshot (ms); dazu Bauzeit, Platz, Binärgrößen der fünf größten Dateien, Rechner, Kopf.
  - [ ] **Bewertungsregel** (fest, damit niemand zurückfragen muss): PSS gesamt auf `static.html` über 512 MiB (eigene Festlegung dieses Plans: das Doppelte der heutigen Memory-Grenze von 256 MiB) → Satz „liegt über den heutigen Paketgrenzen, Wasm-Weg nur mit 12.6“. Über 2 GiB → Satz „No-Go-Kriterium aus dem Plan erfüllt“ und in `#general` an Lars und Peter melden. Ein `VmSize` ≥ 32 GiB in WebContent bestätigt die GC-Reservierung als Messwert.
- **Abnahme:** Doku-PR mit den drei JSON-Ausgaben im PR-Text.

## Nebeneinander

| Paket | Dateien | läuft neben |
| --- | --- | --- |
| L1-A | `build.sh`, `README.md` | L1-B, alle L2 |
| L1-B | `pages/*`, `measure.py`, `test_measure.py` | L1-A, alle L2 |
| L1-C | `docs/LADYBIRD_FEASIBILITY.md` | L2-A bis L2-F (nicht L2-Z: gleiche Datei) |
