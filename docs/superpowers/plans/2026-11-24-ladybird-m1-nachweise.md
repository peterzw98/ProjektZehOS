# Ladybird M1: Nachweise vor dem Port

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-17-ladybird.md) lesen, besonders „Feste Werte“. Arbeitsweise: superpowers:executing-plans.

**Ziel:** Die offenen Risiken des Wasm-Wegs aus [Ergebnis L2](../../LADYBIRD_FEASIBILITY.md#ergebnis-l2) durch kleine Proben klären, bevor portiert wird. Kein Paket ändert den Kernel oder Ladybird im Repository.

**Grundlage:** Meilenstein „Ladybird“ (Auftrag Peter, 24.11.2026, als nächster Meilenstein). Wegannahme: `wasm`, weil L2-Z die Wiederaufnahme dort vorsieht; die L3-Antwort von Lars oder Peter steht noch aus. L2-Z empfahl `liegen lassen`, bis 13.5, 12.7 und L1-C stehen; deshalb sind genau diese Punkte Teil des Meilensteins, und das Tor am Ende (M1-Z) entscheidet über den Port.

**Regeln:** Quellen und Bauordner liegen unter `~/ladybird-probe/` (nie committed) und werden nach der Auswertung gelöscht. Werkzeug: wasi-sdk 25.0 wie `experiments/NetSurf.Wasi`; ein anderer Stand braucht Rückfrage. Keine Issues, PRs oder Kommentare bei Upstream-Projekten. Zeitlimit je Probe 3 Arbeitstage, danach Urteil mit dem erreichten Stand.

**Urteil je Probe:** `trägt` / `trägt nicht` / `offen` mit Messwert und Befehl. Bericht: `docs/superpowers/specs/2026-11-24-ladybird-m1-<thema>.md`. Code-PRs brauchen ein unabhängiges Review.

## Pakete

### M1-A: Stack-Scan unter Wasm

- **Branch:** `task/ladybird-m1a-stackscan` · **Rolle:** Senior (Opus) · **Voraussetzung:** keine
- **Dateien:** `experiments/Ladybird.Probe/stackscan/{probe.c,build.sh,README.md}`, Bericht `…-m1-stackscan.md`
- **Frage:** Findet ein konservativer Scan alle lebenden Zeiger, obwohl Wasm-Lokale nicht im linearen Speicher liegen (L2-A, Befund 12)?
- **Probe:** kleiner Mark-Sweep-Heap (Region 64 MiB), rekursiver Aufrufer hält Objekte nur in Lokalen, Sammlung in der Tiefe. Varianten: (1) unverändert `-O2`, (2) Binaryen `wasm-opt --spill-pointers`, (3) Zeiger ausdrücklich auf dem Schattenstapel.
- **Abnahme:** je Variante wasm3 **und** WAMR-AOT, 10 000 Sammlungen: Zahl fälschlich freigegebener lebender Objekte, Laufzeit gegen (1). `trägt`, wenn Variante 2 oder 3 in beiden Laufzeiten 0 falsch freigegebene Objekte hat. Variante 1 muss den Fehler zeigen, sonst ist die Probe ungeeignet.

### M1-B: Skia-CPU mit wasi-sdk

- **Branch:** `task/ladybird-m1b-skia` · **Rolle:** Engineer · **Voraussetzung:** keine
- **Dateien:** `experiments/Ladybird.Probe/skia/{build.sh,probe.cpp,README.md}`, Bericht `…-m1-skia.md`
- **Probe:** Skia (Stand aus Ladybirds `vcpkg.json`) ohne GPU, ohne Fontconfig, ohne Threads; Programm zeichnet Rechteck, Verlauf und Text aus einer eingebetteten Schrift in BGRA8888 798×519.
- **Abnahme:** Bild stimmt mit einem nativen Skia-Bau überein (höchstens 1 Abweichung je Farbkanal und Pixel); Modulgröße in MiB, linearer Speicher, Zeit je Bild; Liste der Patches mit Zeilenzahl.

### M1-C: Pflichtbibliotheken mit wasi-sdk

- **Branch:** `task/ladybird-m1c-bibliotheken` · **Rolle:** Engineer · **Voraussetzung:** keine
- **Dateien:** `experiments/Ladybird.Probe/libs/{build.sh,smoke.c,README.md}`, Bericht `…-m1-bibliotheken.md`
- **Probe:** ICU, OpenSSL (nur libcrypto), SQLite, harfbuzz je einzeln bauen und je einen Aufruf ausführen: ICU Normalisierung, SHA-256, In-Memory-Tabelle, Shaping einer Zeile.
- **Abnahme:** je Bibliothek Urteil, Größe (ICU-Daten getrennt), Patchzeilen, Lizenz. Eine gescheiterte Bibliothek verhindert die Urteile der anderen nicht.

### M1-D: LibJS-Interpreter für wasm32

- **Branch:** `task/ladybird-m1d-libjs` · **Rolle:** Senior (Opus) · **Voraussetzung:** M1-A mit Urteil `trägt`
- **Dateien:** `experiments/Ladybird.Probe/libjs/{build.sh,patches/,README.md}`, Bericht `…-m1-libjs.md`
- **Probe:** Variante 2 klein aus L2-B (Mov, Sprünge und Ganzzahlen in Rust, sonst Slow-Paths), Region 64 MiB, nur LibJS ohne LibWeb, Ziel `wasm32-wasip1`.
- **Abnahme:** `1+1`, eine Schleife mit 1 000 000 Durchläufen und ein Array-Sort (10 000) laufen unter WAMR-AOT mit richtigem Ergebnis; Zeit, Modulgröße, geänderte Zeilen. Bei Abbruch: genaue Stelle und Grund.

### M1-Z: Tor

- **Branch:** `docs/ladybird-m1z-tor` · **Rolle:** Lead · **Voraussetzung:** L1-C, M1-A bis M1-D, Stand von 13.5/12.6/12.7
- **Dateien:** `docs/LADYBIRD_FEASIBILITY.md` (Abschnitt „Ergebnis M1“), Übersicht, `ROADMAP.md`
- **Regel (fest):** `Port beginnen` nur, wenn M1-A, M1-B und M1-D `trägt` sind, in M1-C jede Bibliothek `trägt` ist, L1-C die 2-GiB-Schwelle nicht überschreitet und #423, #417, #418 abgenommen sind. Sonst `liegen lassen` mit dem Punkt, der fehlt. Die Entscheidung trifft Lars oder Peter.
- **Danach:** M2 (Port bis zum ersten Bild einer statischen Seite) wird erst mit diesen Ergebnissen geschnitten; vorher wären die Pakete geraten.

## Nebeneinander

| Block | Issue | wartet auf |
| --- | --- | --- |
| L1-A Bau | #448 | Bau-Freigabe und Rechner (≥ 32 GiB RAM, 200 GiB) |
| L1-B Messskript | #449 | – |
| L1-C Messung | #450 | L1-A, L1-B |
| M1-A Stack-Scan | #611 | – |
| M1-B Skia | #612 | – |
| M1-C Bibliotheken | #613 | – |
| M1-D LibJS | #614 | M1-A |
| 13.5 AOT (P1) | #423 | Sicherheitsabstimmung mit Peter |
| 12.6 Paketgrenzen (P2) | #417 | #423 |
| 12.7 Threads (P3) | #418 | #423, E7-F (#399) |
| M1-Z Tor | #615 | alle |

Alle Blöcke haben getrennte Dateien und laufen nebeneinander, soweit die letzte Spalte nichts anderes nennt. Ohne weitere Entscheidung startbar: #449, M1-A, M1-B, M1-C. M1-A bis M1-D nutzen für WAMR-AOT den vorhandenen Stand; die Abnahme von #423 ist dafür keine Voraussetzung.
