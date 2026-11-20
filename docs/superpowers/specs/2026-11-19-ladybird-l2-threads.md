# Ladybird L2-D: Threads von WebContent

Stand: 20. November 2026. Ladybird `6fba5b78a104` (gelesen, nicht gebaut); ZehOS `origin/master` `bbf64f46`. Paket L2-D des [Lesestudien-Plans](../plans/2026-11-19-ladybird-l2-lesestudie.md), Board-Block #454. Nur gelesen: kein Bau, keine Messung. Pfade ohne Präfix liegen im Ladybird-Stand; wie viele Threads zur Laufzeit wirklich entstehen, misst erst L1.

## Frage

Läuft WebContent mit einem Thread, oder wie viele braucht es mindestens für eine statische Seite ohne Worker?

**Kurzantwort:** Der Code kennt keinen Ein-Thread-Schalter (BELEGT: `Services/WebContent/main.cpp:176-329` setzt keinen). Eine statische Seite ohne Skript, JIT, Medien und Worker braucht im ungeänderten Code **9 bis 11 Threads** (Rechnung unter „Befunde“, HYPOTHESE bis L1 misst). Ein Umbau auf einen Thread ist möglich, aber ein Eingriff in Render-, Paint-, Takt- und IPC-Pfad.

## Befunde

„verzichtbar“ gilt für eine statische Seite ohne Skript, JIT, Medien, Worker.

| Thread | Erzeuger | Zweck | verzichtbar? |
| --- | --- | --- | --- |
| Hauptthread (Ereignisschleife) | `Services/WebContent/main.cpp:341-345` (`event_loop.exec()`) | DOM, Layout-Aufruf, Prozessstart | nein (1) |
| Render | `Libraries/LibWeb/Rust/src/stage_thread.rs:927-934`, `spawn` `:138-148`, Stack 64 MiB (`:130`) | Style- und Layout-Aufträge je Dokument; `run` wartet blockierend (`:201-226`) | ja, mit Umbau: `on_render_side` führt Aufträge schon jetzt im Aufrufer aus, wenn dieser alle seine Aufträge erledigt sah (`Libraries/LibWeb/Rust/src/render_state.rs:866-889`), und in Tests immer inline (`:870-872`). Ein Dauerschalter fehlt. BELEGT |
| Paint | `Libraries/LibWeb/Rust/src/paint_stage.rs:236-246` (lazy), genutzt `:296-312`, `Libraries/LibWeb/Rust/src/painting/recording_slot.rs:274,1042` | Aufzeichnung und Übergabe des Bildes an den Compositor | ja, mit Umbau wie Render (gleiche Klasse `StageThread`); die Reihenfolge der „gesampelten Frames“ ist Vertrag (`paint_stage.rs:296-299`). HYPOTHESE zur Machbarkeit |
| RenderClock | `Libraries/LibWeb/Compositor/RenderClock.cpp:65-78` (eigene `Core::EventLoop`, `:84-104`), je Dokument bewaffnet `Libraries/LibWeb/HTML/LocalNavigable.cpp:7249`, Anbindung `Libraries/LibWeb/Compositor/CompositorConnection.cpp:75` | Bildtakt zum Compositor; der Konstruktor wartet, bis der Thread läuft (`RenderClock.cpp:77-78`) | ja, wenn der Takt als Timer des Hauptthreads läuft; ZehOS hätte keinen Compositor-Prozess (L2-C). HYPOTHESE |
| IPC IO, einer je Transport | `Libraries/LibIPC/TransportSocket.cpp:364-365` (`io_thread_loop`) | Senden und Empfangen am Socket; Benachrichtigung am Hauptthread bewusst abgeschaltet (`:340-342`) | ja, mit Umbau auf Socket-Benachrichtigung der Ereignisschleife oder den geteilten Thread (`SharedIOThread`, `:225-247`; nur mit `ScopedSharedIOThread`, aktiv in `Libraries/LibWebView/WorkerAgentManager.cpp:136,231` und `Services/WebContent/Worker/WorkerThread.cpp:52`). Transporte einer statischen Seite: UI (`main.cpp:304`), RequestServer (`main.cpp:307-309,341`), Compositor, Fontdienst (`Services/WebContent/ConnectionFromClient.cpp:153-155`), also **4** (HYPOTHESE: Zahl ohne Lauf). Ohne Compositor- und Fontprozess in ZehOS weniger (L2-C) |
| Font service client | `Libraries/LibCompositing/FontServiceClient.cpp:45-49`; `create` wartet blockierend auf die Verbindung (`:35-38`) | Schriftabfragen an den UI-Prozess | ja, wenn Schriften aus Paketdateien kommen (L2-E); das Blockierwarten im Konstruktor ist sonst eine Deadlockgefahr im Ein-Thread-Betrieb |
| DecommitWorker | `Libraries/LibGC/BlockAllocator.cpp:343-355`, ausgelöst `:494,638` | Seiten freier GC-Blöcke zurückgeben, lazy beim ersten Freigeben | ja: reine Speicherrückgabe; der Code läuft auch ohne ihn (`the_if_started`, `:301,338`). BELEGT |
| ThreadPool „Pool/0-3“ | `Libraries/LibThreading/ThreadPool.cpp:11-12,17-33` (fest 4, je 8 MiB), lazy beim ersten `the()` | Arbeit über `run_on_thread_pool` (`Libraries/LibWeb/Platform/ThreadPoolWork.h:38`): externes Stylesheet (`Libraries/LibWeb/CSS/Parser/RustSyntaxParsing.cpp:176-191`, aufgerufen `Libraries/LibWeb/HTML/HTMLLinkElement.cpp:1567`, `Libraries/LibWeb/CSS/StyleSheetImport.cpp:326`), Skriptübersetzung (`Libraries/LibWeb/HTML/Scripting/Fetching.cpp:134`), Webfont (`Libraries/LibWeb/CSS/FontLoading.cpp:49`) | entfällt ohne externes CSS, Skript und Webfont; sonst ersetzbar durch `deferred_invoke` auf dem Hauptthread, denn die Arbeit ist eine reine Funktion mit Rückgabewert (`ThreadPoolWork.h:38-48`). Der DNS-Auftrag im Pool (`Libraries/LibRequests/RequestControlClient.cpp:74`) trifft WebContent nicht: kein Grep-Treffer in `Services/WebContent` und `Libraries/LibWeb`. BELEGT |
| „Style UA index“ | `Libraries/LibWeb/Rust/src/css/style/process_style.rs:306-318`, aufgerufen `Libraries/LibWeb/Rust/src/css/style_sheet.rs:580` | Index der UA-Regeln vorab; wer ihn braucht, wartet auf die Sperre (`:303-305`) | ja: Vorberechnung. HYPOTHESE (Ersatzpfad nicht gelesen) |
| „CSS source text“ | `Libraries/LibWeb/Rust/src/css/parser/sheet_source_text.rs:146-165`, Anstoß `:49` | Quelltext komprimieren (Speicher sparen) | ja: Optimierung. HYPOTHESE zum Verhalten ohne Thread |
| „LibJS source parking“ | `Libraries/LibJS/Rust/src/source_text.rs:336-342` | Quelltext auslagern; scheitert der Start, läuft es ohne Thread weiter (`:340-342`) | ja. BELEGT |
| LibJS JIT-Pool | `Libraries/LibJS/JIT/Rust/src/compile_pool.rs:116-127` (bis `max_thread_count`, Zahl `Libraries/LibJS/JIT/Rust/src/options.rs:442-445`, Kerne minus 1), Pool `Libraries/LibJS/Rust/src/jit/compile_queue.rs:25-38` | JIT-Übersetzung | entfällt mit `ENABLE_JS_JIT=OFF` (`Libraries/LibJS/CMakeLists.txt:15-16`, `Libraries/LibJS/Rust/Cargo.toml:10-13`). BELEGT |
| WebWorker | `Services/WebContent/Worker/WorkerThread.cpp:96-109` | Web Worker je Agent | entfällt ohne Worker (Vorgabe) |
| Medien und Audio | z. B. `Libraries/LibMedia/Producers/DecodedVideoProducer.cpp:26`, `Libraries/LibMedia/PlaybackManager.cpp:222`, `Libraries/LibAudio/ClientConnection.cpp:70`, `Libraries/LibWeb/Rust/src/web_audio/rendering/offline_audio_renderer.rs:111` | Dekodieren, Wiedergabe, WebAudio | entfällt ohne Medienelemente |
| OpenSSL | `Services/WebContent/main.cpp:282` | `OSSL_set_max_threads` erlaubt nur Threads, erzeugt keine | nicht gezählt |

Abgrenzung (nicht WebContent): `Services/Hub/main.cpp:93`, `Services/ImageDecoder/ConnectionFromClient.cpp:159,276`, `Services/MediaServer/ConnectionFromClient.cpp:180-252`, `Libraries/LibSandbox/ConnectBroker.cpp:127` (Hilfsprozess-Start). Threads in Tests (`Libraries/LibJS/Rust/src/compile.rs:1648`, `Libraries/LibCompositing/Rust/src/display_list/storage.rs:458`, `AK/Rust/src/waiter_list.rs`) zählen nicht. Die Rust-Transportvariante mit „IPC reader/writer“ (`Libraries/LibIPC/Rust/src/transport_socket.rs:120,126`) ist in `Libraries/LibIPC/Rust/src/lib.rs:51-56` nur im Test eingebunden; ob WebContent sie nutzt, ist nicht gezeigt (HYPOTHESE: nein).

**Mindestzahl (Rechnung, HYPOTHESE bis L1):** Hauptthread 1 + Render 1 + RenderClock 1 + IPC IO 4 + Font service client 1 + DecommitWorker 1 = **9**. Mit Paint (sobald ein Bild aufgezeichnet wird) **10**, mit „CSS source text“ **11**. Dazu kommen 4 Pool-Threads, sobald die Seite externes CSS lädt (`HTMLLinkElement.cpp:1567`), und der „Style UA index“. „Mindestens“ gilt für den ungeänderten Code: Er legt diese Threads fest an, und die blockierenden Warteschritte (`stage_thread.rs:915-922`, `RenderClock.cpp:77-78`, `FontServiceClient.cpp:35-38`) setzen sie voraus.

**Deadlockgefahr im Ein-Thread-Betrieb (BELEGT):** `StageThread::run` (`stage_thread.rs:201-226`) und `wait_for` (`:915-922`, `park`) warten blockierend auf einen anderen Thread; läuft der Auftrag nicht inline, hängt der Aufrufer. Gleiches gilt für den Konstruktor von `RenderClock` (`RenderClock.cpp:77-78`) und `FontServiceClient::create` (`FontServiceClient.cpp:35-38`).

## Was ein Umbau ändern müsste

Ziel „ein Thread“ (alles auf dem Hauptthread). Gezählt wird die volle Länge der Dateien, die sich ändern (Regel 5, grob; teils nur Ausschnitte).

| Datei | Zeilen | Änderung |
| --- | --- | --- |
| `Libraries/LibWeb/Rust/src/stage_thread.rs` | 1263 | `spawn`, `run`, `submit`, `post` mit Inline-Betrieb (Schalter wie `cfg!(test)`) |
| `Libraries/LibWeb/Rust/src/render_state.rs` | 1415 | `on_render_side`, `wait_for_render_side`, `post_to_render_side` ohne Render-Thread |
| `Libraries/LibWeb/Rust/src/paint_stage.rs` | 430 | Paint inline |
| `Libraries/LibWeb/Compositor/RenderClock.cpp` und `.h` | 244 + 129 | Takt als Timer der Hauptschleife |
| `Libraries/LibIPC/TransportSocket.cpp` und `.h` | 997 + 238 | IO über Socket-Benachrichtigung statt Thread |
| `Libraries/LibCompositing/FontServiceClient.cpp` | 201 | ohne Thread und ohne Blockierwarten |
| `Libraries/LibGC/BlockAllocator.cpp` | 641 | Decommit synchron oder weglassen |
| `Libraries/LibThreading/ThreadPool.cpp` und `.h`, `Libraries/LibWeb/Platform/ThreadPoolWork.cpp` und `.h` | 77 + 38 + 33 + 52 | `submit` führt per `deferred_invoke` aus |
| `Libraries/LibWeb/Rust/src/css/style/process_style.rs` | 380 | UA-Index ohne Thread |
| `Libraries/LibWeb/Rust/src/css/parser/sheet_source_text.rs` | 221 | Komprimierung ohne Thread |
| `Libraries/LibJS/Rust/src/source_text.rs` | 417 | Parken ohne Thread |
| **Summe** | **6 756** | ohne `Libraries/LibWeb/Rust/src/painting/recording_slot.rs` (Aufrufe an Paint `:274,1042,1095`) und weitere Aufrufer von `render_thread()` |

## Aufwand

Regel 5: 6 756 Zeilen geteilt durch 1500 sind 4,5, also **5 Personwochen**; Spanne **5 bis 8**. Der obere Wert kommt von den nicht gezählten Aufrufern in `recording_slot.rs`, `render_state/clock.rs` und weiteren Rust-Dateien und von Upstream-Änderungen an diesen Dateien (HYPOTHESE, Änderungsrate nicht gemessen). Der untere Wert setzt voraus, dass der vorhandene Inline-Pfad (`render_state.rs:866-889`) als Muster trägt.

## ZehOS-Stand und Bezug

- **Threads im Kernel (BELEGT, `ROADMAP.md` 6a):** Cosmos-Round-Robin mit echten Kernelthreads, 20-ms-Zeitscheibe (`ROADMAP.md:142`). Im Kernel (nativer Weg) gibt es also Threads, aber Cosmos 3.0.97 läuft auf genau einer CPU (`docs/superpowers/specs/2026-11-18-multicore-design.md:21-23`): Zusätzliche Threads brächten Zeitscheiben, keine Parallelität. Die Multicore-Basis (`docs/SMP_AUDIT.md`, [Multicore-Entwurf](2026-11-18-multicore-design.md)) steht als Audit und Plan; offen bleibt dort, ob Cosmos' `Monitor` und die Thread-Wartelisten über CPUs tragen (`docs/SMP_AUDIT.md:243`).
- **Pakete (Wasm-Weg):** 12.7 „Threads für Pakete“ ist offen (`ROADMAP.md:336`); Pakete laufen heute ohne Threads (`docs/LADYBIRD_FEASIBILITY.md:15`). `wasi-threads` ist im Repository weder umgesetzt noch gemessen; es steht nur als Prüffrage im Plan (`docs/superpowers/plans/2026-11-17-roadmap-12.md:25,59`), L2-F klärt es für WAMR 2.4.5. Für den Wasm-Weg heißt das: Entweder trägt 12.7 die 9 bis 11 Threads (Render und Paint mit je 64 MiB Stack, `stage_thread.rs:130`, gegen die Paketgrenze lineare Memory 256 MiB, `docs/LADYBIRD_FEASIBILITY.md:23`), oder der Ein-Thread-Umbau (5 bis 8 Personwochen) wird Voraussetzung. Der Stack wird reserviert, nicht belegt (`stage_thread.rs:126-129`); ob er unter Wasm im linearen Speicher zählt, ist HYPOTHESE.
- **Folgerung für L2-Z:** Die Mindestzahl hängt an L2-C (Prozesse zusammenlegen verkürzt die IPC-Threads) und L2-F (`wasi-threads`); dieser Bericht trägt weder Go noch No-Go allein.

## Urteil

`n Threads`

Begründung: Unverändert braucht WebContent 9 bis 11 Threads für eine statische Seite; „ein Thread“ ist erst nach einem Umbau von 5 bis 8 Personwochen erreichbar und kein Upstream-Zustand. „nicht vertretbar“ gilt nicht, weil für Render ein Inline-Pfad (`render_state.rs:866-889`) und für den ThreadPool reine Funktionsaufträge (`ThreadPoolWork.h:38-48`) bereits bestehen.

## Nicht geprüft

- Wie viele Threads zur Laufzeit wirklich entstehen (L1, Hostmessung; hier kein Bau).
- Ob der Render-Pfad im Inline-Betrieb ohne Stacküberlauf bleibt (`stage_thread.rs:126-129`: bei 8 MiB etwa 5700 verschachtelte Elemente).
- Ob WebContent die Rust-IPC-Transportvariante nutzt; Windows- und macOS-Pfade (z. B. `Libraries/LibIPC/TransportMachPort.cpp:165`).
- Ersatzpfade von „Style UA index“ und „CSS source text“ ohne Thread; `recording_slot.rs` und `render_state/clock.rs` im Einzelnen.
- `wasi-threads` im ZehOS-Zuschnitt (L2-F) und das Zusammenlegen der Prozesse (L2-C).
