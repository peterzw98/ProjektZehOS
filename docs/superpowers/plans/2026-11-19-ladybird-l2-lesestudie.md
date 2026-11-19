# Ladybird Etappe L2: Lesestudie

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-17-ladybird.md) lesen, besonders „Arbeitspakete“ und „Feste Werte“.
> Empfohlene Arbeitsweise: superpowers:executing-plans. Schritte haben Kästchen zum Abhaken.

**Ziel:** Für jede Hürde aus der [Erstbewertung](../../LADYBIRD_FEASIBILITY.md) steht ein Studienbericht mit Belegen (Datei:Zeile im festen Upstream-Stand). Er nennt, was ein Umbau ändern müsste, und schätzt den Aufwand in Personwochen. Aus den Berichten entsteht eine Empfehlung für L3: Wasm-Weg, nativer Weg oder No-Go.

**Regeln für alle L2-Pakete:**

1. **Nur lesen.** Kein Bau, kein `cmake`, kein Skript aus dem Ladybird-Klon. Klon außerhalb des Repositorys, blobless mit voller Historie (L2-B braucht `git log`):
   `git clone --filter=blob:none https://github.com/LadybirdBrowser/ladybird ~/ladybird-src && git -C ~/ladybird-src checkout 6fba5b78a1043fe93b3451204e04dcc0e3bd983a`.
2. **Jeder Satz über Code** nennt Datei und Zeile in `6fba5b78a104` (Ladybird) bzw. `origin/master` (ZehOS, Kopf im Bericht nennen). Kennzeichnung **BELEGT** und **HYPOTHESE** wie in der Erstbewertung.
3. **Ein Paket, eine Datei:** Jedes Paket schreibt nur seinen Bericht `docs/superpowers/specs/2026-11-19-ladybird-l2-<thema>.md`. Die Pakete laufen deshalb nebeneinander. Erstbewertung, Übersicht und `ROADMAP.md` ändert nur L2-Z.
4. **Gliederung jedes Berichts:** „Frage“, „Befunde“ (Tabelle Befund | Beleg | BELEGT/HYPOTHESE), „Was ein Umbau ändern müsste“ (Dateiliste mit Zeilenzahl), „Aufwand“ (Personwochen als Spanne mit Begründung), „Urteil“ (genau einer der Werte aus der Spalte „Urteil“ der Pakettabelle), „Nicht geprüft“. Höchstens 150 Zeilen.
5. **Aufwand nach fester Regel:** Gezählt werden die Zeilen der Dateien, die sich ändern müssten. Ansatz: 1 Personwoche je angefangene 1500 geänderte oder neu zu schreibende Zeilen, mindestens 1. Weicht der Bearbeiter davon ab, nennt er den Grund.
6. **Doku-PR** je Paket (nur `docs/`), Basis `origin/master`, kein unabhängiges Review nötig (`AGENTS.md`, reine Doku). Der PR schließt den Board-Block mit `Closes #<nr>`.

## Pakete

| ID | Thema | Kernfrage | Urteil (einer davon) |
| --- | --- | --- | --- |
| L2-A | GC-Heap | Lässt sich LibGC mit einer Heap-Region ≤ 1 GiB in einem 32-Bit-Adressraum betreiben? | `umbaubar` / `nur mit memory64` / `nicht vertretbar` |
| L2-B | LibJS-Interpreter | Gibt es einen JS-Ausführungsweg ohne flapc-Maschinencode, der nach wasm32 übersetzt? | `vorhanden` / `schreibbar` / `nicht vertretbar` |
| L2-C | Prozesse und IPC | Lassen sich Hub, WebContent, Compositor, RequestServer und ImageDecoder in einen Prozess legen? | `zusammenlegbar` / `nur nativ` / `nicht vertretbar` |
| L2-D | Threads | Läuft WebContent mit einem Thread, oder wie viele braucht es mindestens? | `ein Thread` / `n Threads` / `nicht vertretbar` |
| L2-E | Abhängigkeiten, Zeichnen, Netz | Welche Pflichtabhängigkeiten lassen sich abschalten oder ersetzen, und wie kommt ein CPU-Bild an `zehos_v1`? | `ersetzbar` / `teilweise` / `nicht vertretbar` |
| L2-F | ZehOS-Laufzeit | Trägt WAMR 2.4.5 im ZehOS-Zuschnitt memory64, geteilten Speicher und `wasi-threads` im AOT-Modus? | `vorhanden` / `nachrüstbar` / `fehlt` |
| L2-Z | Zusammenfassung | Welcher Weg (Wasm, nativ) ist gangbar, mit welchem Aufwand, oder No-Go? | Empfehlung für L3 |

### Paket L2-A: GC-Heap ohne 32-GiB-Region

- **Branch:** `docs/ladybird-l2a-gc-heap` · **Rolle:** Senior Engineer (Opus) · **Voraussetzung:** keine
- **Datei (anlegen):** `docs/superpowers/specs/2026-11-19-ladybird-l2-gc-heap.md`
- **Ausgangslage (BELEGT, Bericht vom 19.11.):** `HEAP_REGION_SIZE = 32ull * GiB` (`Libraries/LibGC/HeapRegion.h:20`); reserviert werden 2 × 32 GiB zum Ausrichten (`Libraries/LibGC/BlockAllocator.cpp:74-103`), Abbruch per `MUST` bei Fehlschlag (`:81`). Komprimierte Zeiger: u32 mal 8, `static_assert` auf 2^35 (`Libraries/LibGC/CompressedPointer.h:16-31`). NaN-Boxing mit 48-Bit-Nutzlast relativ zur Regionsbasis (`Libraries/LibGC/NanBoxedValue.h:41-55`). Zweiter Käfig: PrimitiveStorage 4 TiB + 32 GiB Guard (`Libraries/LibGC/PrimitiveStorage.h:34,40`), ENOMEM bei `FlatPtr` < 64 Bit (`PrimitiveStorage.cpp:73`).
- **Schritte:**
  - [ ] In dieser Reihenfolge lesen: `LibGC/HeapRegion.h`, `CompressedPointer.h`, `NanBoxedValue.h`, `BlockAllocator.cpp/.h`, `Internals.h`, `HeapBlock.h/.cpp`, `CellTypeInfo.h`, `CellAllocator.cpp`, `PrimitiveStorage.h/.cpp`, `Member.h`, `Ptr.h`, `Heap.cpp` (Stack-Scan), `LibCore/System.cpp:163-181`, `LibGC/CAPI.cpp:195-205`.
  - [ ] Drei Varianten bewerten: (1) Region ≤ 1 GiB mit unverändertem Shift (Offsets passen in u32, Schutzbereich unten kleiner), (2) unkomprimierte 32-Bit-Zeiger unter wasm32, (3) unverändert unter memory64. Für jede: betroffene Dateien, Zeilen, Bruchstellen (Asserts, JIT-Annahmen, Rust-Seite in `Libraries/LibJS/Rust`).
  - [ ] PrimitiveStorage gesondert: Ist der Guard ohne JIT verzichtbar (`PrimitiveStorage.h:37-41`)? Wie groß muss der Käfig ohne JIT sein?
  - [ ] Zählen: Wie viele Stellen in LibJS/LibWeb (C++ und Rust) verwenden `CompressedPointer`, `NanBoxedValue` oder Regionsbasis direkt (`git grep -c`, Zahl je Bibliothek).
- **Abnahme:** Bericht mit Urteil; jede der drei Varianten hat Dateiliste und Aufwand nach Regel 5.

### Paket L2-B: LibJS ohne flapc-Interpreter

- **Branch:** `docs/ladybird-l2b-libjs` · **Rolle:** Senior Engineer (Opus) · **Voraussetzung:** keine
- **Datei (anlegen):** `docs/superpowers/specs/2026-11-19-ladybird-l2-libjs.md`
- **Ausgangslage (BELEGT):** Der JIT ist abschaltbar (Cargo-Feature `jit`, `Libraries/LibJS/Rust/Cargo.toml:9-13`; `ENABLE_JS_JIT`, `Libraries/LibJS/CMakeLists.txt:15-16`; Stub `Libraries/LibJS/Rust/src/lib.rs:117-122`). Der Bytecode-Interpreter selbst wird aus `Libraries/LibJS/Interpreter/interpreter.flap` als Maschinencode erzeugt (`lib.rs:9-10`); `flapc` kennt nur x86_64/aarch64 und ELF/MachO/COFF und bricht sonst ab (`Libraries/LibJS/Rust/build.rs:83-93`). **Folge:** Ohne Umbau gibt es für wasm32 gar keinen JS-Interpreter. Das ist eine neue Hürde gegenüber der Erstbewertung.
- **Schritte:**
  - [ ] `build.rs`, `interpreter.flap` und die Flap-Werkzeuge lesen: Was erzeugt flapc, wie wird es eingebunden, gibt es einen zweiten (portablen) Ausführungsweg, etwa in der Git-Historie (`git log --oneline -- Libraries/LibJS/Interpreter` bis zu einem Stand mit C++-Interpreter, Commit nennen)?
  - [ ] Umfang zählen: Zeilen von `interpreter.flap`, Zahl der Opcodes, Schnittstelle zwischen erzeugtem Code und Rust/C++.
  - [ ] Bewerten: (1) flapc um ein Wasm-Ziel erweitern, (2) portablen Interpreter in Rust schreiben, der dieselben Opcodes ausführt, (3) älteren C++-Interpreter wiederbeleben. Je Variante Aufwand nach Regel 5 und Wartungslast bei Upstream-Änderungen (Commits je Monat an `interpreter.flap` im letzten Vierteljahr).
  - [ ] Für den nativen Weg festhalten: Der flapc-Code braucht ausführbaren Speicher? Was müsste ZehOS dafür bieten (ELF-Laden, W^X)?
- **Abnahme:** Bericht mit Urteil, Opcode-Zahl, Commit-Rate und Aufwand je Variante.

### Paket L2-C: Prozesse und IPC

- **Branch:** `docs/ladybird-l2c-prozesse` · **Rolle:** Engineer · **Voraussetzung:** keine
- **Datei (anlegen):** `docs/superpowers/specs/2026-11-19-ladybird-l2-prozesse.md`
- **Ausgangslage (BELEGT):** Prozesstypen in `Libraries/LibWebView/ProcessType.h:15-25`; Start über `launch_helper_process` (`Libraries/LibWebView/HelperProcess.cpp:189`, WebContent `:351`, Compositor `:540`, RequestServer `:600`); `socketpair(AF_LOCAL, SOCK_STREAM)` (`Libraries/LibWebView/Process.cpp:307`), Kind per `clone(CLONE_VM|CLONE_VFORK)` + exec (`Libraries/LibCore/Process.cpp:384-391`); Transport `Libraries/LibIPC/TransportSocket.cpp`, fd-Übergabe per SCM_RIGHTS in `Libraries/LibCore/Socket.cpp:396,443` und `Libraries/LibIPC/Rust/src/socket.rs:44`. Kein Single-Process-Modus; Hub läuft nur außerhalb von Linux/macOS im UI-Prozess (`UI/CMakeLists.txt:162-165`).
- **Schritte:**
  - [ ] Je Prozess aus `ProcessType.h` eine Zeile: Aufgabe, `main.cpp`, IPC-Endpunkte (`.ipc`-Dateien), verzichtbar für eine statische Seite ja/nein.
  - [ ] Den Windows- und Android-Weg lesen, auf dem Hub im UI-Prozess läuft: Welche Abstraktion macht das möglich, lässt sie sich auf WebContent und Compositor ausdehnen?
  - [ ] Bewerten: In-Prozess-Transport für LibIPC (Datei, Schnittstelle, Zeilen), Weitergabe von Dateideskriptoren (SCM_RIGHTS, geteilte Puffer `AnonymousBuffer`) ohne Kernel-Sockets.
  - [ ] Für den nativen Weg festhalten, welche Kernelfunktionen ZehOS bieten müsste (Prozesse, lokale Sockets, fd-Übergabe, geteilter Speicher) und welches Roadmap-Projekt sie liefert (12.4, 12.5, 12.9).
- **Abnahme:** Bericht mit Urteil, Prozesstabelle und Liste der nötigen ZehOS-Kernelfunktionen.

### Paket L2-D: Threads

- **Branch:** `docs/ladybird-l2d-threads` · **Rolle:** Engineer · **Voraussetzung:** keine
- **Datei (anlegen):** `docs/superpowers/specs/2026-11-19-ladybird-l2-threads.md`
- **Ausgangslage (BELEGT):** ThreadPool mit fest 4 Threads zu 8 MiB (`Libraries/LibThreading/ThreadPool.cpp:11-12,23-33`), genutzt über `Libraries/LibWeb/Platform/ThreadPoolWork.h:38`; weitere Threads: IPC IO (`Libraries/LibIPC/TransportSocket.cpp:245,364`), RenderClock (`Libraries/LibWeb/Compositor/RenderClock.cpp:73`), Web Worker (`Services/WebContent/Worker/WorkerThread.cpp:96`), DecommitWorker (`Libraries/LibGC/BlockAllocator.cpp:351`), JIT-Pool (`Libraries/LibJS/JIT/Rust/src/compile_pool.rs:120`), Media (`Libraries/LibMedia/PlaybackManager.cpp:222`), OpenSSL (`Services/WebContent/main.cpp:282`). Kein Ein-Thread-Schalter.
- **Schritte:**
  - [ ] `git grep -n` nach `Threading::Thread`, `ThreadPool`, `std::thread`, `pthread_create`, Rust `thread::spawn` in `Libraries/` und `Services/`; Tabelle Thread | Prozess | Zweck | entfällt ohne JIT/Media/Worker | ersetzbar durch Ereignisschleife ja/nein.
  - [ ] Für jeden nicht entfallenden Thread: Wie würde die Arbeit synchron oder in der Ereignisschleife laufen (Beleg der Aufrufstelle), und wo wartet Code blockierend auf ihn (Deadlock-Gefahr im Ein-Thread-Betrieb)?
  - [ ] Ergebnis als Zahl: Mindestzahl Threads für WebContent mit statischer Seite ohne Worker.
- **Abnahme:** Bericht mit Urteil und Thread-Tabelle; Mindestzahl begründet.

### Paket L2-E: Abhängigkeiten, Zeichnen und Netz

- **Branch:** `docs/ladybird-l2e-abhaengigkeiten` · **Rolle:** Engineer · **Voraussetzung:** keine
- **Datei (anlegen):** `docs/superpowers/specs/2026-11-19-ladybird-l2-abhaengigkeiten.md`
- **Ausgangslage (BELEGT):** `vcpkg.json` hat keine optionalen Features. Pflicht laut `Meta/CMake/check_for_dependencies.cmake`: FFmpeg (unter Linux über pkg-config, `:32-34`), ANGLE (`:81-87`), Skia (`:97ff`), ICU 78.3 EXACT, SDL3, curl, OpenSSL, SQLite, harfbuzz, Fontconfig, libedit; Vulkan optional (`:45-68`). Qt nur über `ENABLE_GUI_TARGETS` abschaltbar, dann ohne Services (`CMakeLists.txt:139-141`, `UI/CMakeLists.txt:144-148`).
- **Schritte:**
  - [ ] Tabelle je Pflichtabhängigkeit: wer nutzt sie (Bibliothek, Zahl der Einbindungen per `git grep -l`), abschaltbar/stubbar/ersetzbar, Ersatz unter ZehOS (`zehos_v1`-Funktion aus `docs/PAKETE_ERSTELLEN.md` mit Zeile oder „fehlt“), Lizenz (SPDX aus dem jeweiligen vcpkg-Port).
  - [ ] Zeichenweg: Wie kommt das Bild von Skia-CPU (`--force-cpu-painting`, `Libraries/LibWebView/Application.cpp:507,511`) in einen Puffer? Welche Stelle müsste stattdessen `zehos_v1.present` aufrufen (`docs/PAKETE_ERSTELLEN.md:19`)?
  - [ ] Netz: Wie spricht RequestServer curl an (Dateien)? Was müsste ein Ersatz über `zehos_v1.net_*` (`docs/PAKETE_ERSTELLEN.md:106-112`, Übertragungen ≤ 64 KiB) liefern?
  - [ ] Schriften: Was braucht LibGfx ohne Fontconfig (feste Schriftliste aus Paketdateien)?
- **Abnahme:** Bericht mit Urteil, vollständiger Abhängigkeitstabelle samt Lizenzen und je einem Absatz zu Zeichnen, Netz und Schriften.

### Paket L2-F: ZehOS-Laufzeit für ein Paket dieser Größe

- **Branch:** `docs/ladybird-l2f-laufzeit` · **Rolle:** Engineer (ZehOS-Kenntnis) · **Voraussetzung:** keine
- **Datei (anlegen):** `docs/superpowers/specs/2026-11-19-ladybird-l2-laufzeit.md`
- **Ausgangslage (BELEGT, `origin/master` f6bbaed):** WAMR 2.4.5 nur als AOT-Lader für x86-64 übernommen (`ProjektZeh/ThirdParty/wamr/ORIGIN.txt`); Plattformschicht ohne Threads (`docs/superpowers/plans/2026-11-17-aot-laufzeit-handoff.md:46`); Shared-Memory-Code nur mit `WASM_ENABLE_SHARED_MEMORY` (`docs/SMP_AUDIT.md:406`). Grenzen: Stack 1 MiB und Memory 256 MiB (`ProjektZeh/Runtime/Wasm/WasmRuntime.cs:28-29`), 65536 Seiten (`ProjektZeh/Native/Wasm/host/zehos_host_wamr.c:17`), Modul 64 MiB an drei Stellen (`ProjektZeh/Apps/Wasm/WasmGameApp.cs:29`, `ProjektZeh/Runtime/Wasm/WasmEngineSelection.cs:37`, `ProjektZeh/Runtime/CosmosAotProbe.cs:102`).
- **Schritte:**
  - [ ] Im WAMR-2.4.5-Tarball (SHA-256 aus `ORIGIN.txt` prüfen) lesen: memory64 (`WASM_ENABLE_MEMORY64`) im AOT-Lader und in `wamrc`; Shared Memory und `wasi-threads` (`WASM_ENABLE_SHARED_MEMORY`, `WASM_ENABLE_LIB_WASI_THREADS`) im AOT-Modus; welche Dateien fehlen im ZehOS-Zuschnitt, welche Plattformfunktionen (`os_thread_*`, `os_mmap`) müsste `ProjektZeh/Native/Wasm/wamr-platform/zehos_platform.c` zusätzlich liefern.
  - [ ] Liste der ZehOS-Stellen, die sich für ein Ladybird-Paket ändern müssten (Grenzen oben, Paketgröße `ProjektZeh/Packages/PackageManifest.cs:19-22`), mit Bezug auf Roadmap 12.6/12.7.
  - [ ] Festhalten, ob wasi-sdk 25.0 (`experiments/NetSurf.Wasi/build.py:28-30`) memory64 (`wasm64-wasip1`) und Threads (`wasm32-wasip1-threads`) erzeugt, mit Beleg aus der wasi-sdk-Doku.
- **Abnahme:** Bericht mit Urteil je Funktion (memory64, Shared Memory, wasi-threads) und Liste der ZehOS-Änderungen.

### Paket L2-Z: Zusammenfassung und Empfehlung

- **Branch:** `docs/ladybird-l2z-empfehlung` · **Rolle:** Lead oder `agent-orchestrator-junior` · **Voraussetzung:** L2-A bis L2-F gemergt; L1-C, falls bis dahin fertig (sonst ohne Messzahlen, ausdrücklich vermerkt)
- **Dateien (ändern):** `docs/LADYBIRD_FEASIBILITY.md` (Abschnitt „Ergebnis L2“), `docs/superpowers/plans/2026-11-17-ladybird.md` (Stand der Etappen), `ROADMAP.md` (Absatz „Kandidat Ladybird“ in §12)
- **Schritte:**
  - [ ] Urteile aus L2-A bis L2-F als Tabelle; Weg „Wasm“ ist nur gangbar, wenn L2-A nicht `nicht vertretbar`, L2-B nicht `nicht vertretbar`, L2-C `zusammenlegbar` und L2-F nicht `fehlt` (bei `fehlt`: Aufwand für 12.6/12.7 aus dem Roadmap-12-Plan übernehmen). Weg „nativ“ ist nur gangbar, wenn 12.4 und 12.5 als Voraussetzung angenommen werden; Aufwand dafür aus dem Roadmap-12-Plan.
  - [ ] Gesamtaufwand je Weg als Summe der Spannen, Empfehlung in einem Satz.
  - [ ] Entscheidungsfrage für L3 so formulieren, dass Lars oder Peter mit einem Wort antworten können (`wasm`, `nativ`, `liegen lassen`).
- **Abnahme:** Doku-PR; die Entscheidungsfrage steht in `#general` mit `@Peter @Lars` und dem PR-Link.
