# Ladybird L2-C: Prozesse und IPC

Paket L2-C der [Lesestudie](../plans/2026-11-19-ladybird-l2-lesestudie.md), Block #453. Ladybird-Stand `6fba5b78a104` (Upstream, 18.11.2026), ZehOS-Stand `origin/master` `bbf64f46`. Nur gelesen: kein Bau, kein Lauf. Zeilenangaben sind Datei:Zeile in diesen Ständen. Kennzeichnung wie in der [Erstbewertung](../../LADYBIRD_FEASIBILITY.md): **BELEGT** steht im Quelltext, **HYPOTHESE** ist Schluss oder Schätzung.

## Frage

Lassen sich Hub, WebContent, Compositor, RequestServer und ImageDecoder in einen Prozess legen, und was bräuchte der native Weg (mehrere Prozesse) vom ZehOS-Kernel?

## Befunde

### Prozesse

Quelle der Typen: `Libraries/LibWebView/ProcessType.h:13-26` (elf Werte; MediaServer, AudioServer, VideoDecoderServer, WebRTCClient hier nicht betrachtet). Start über `launch_helper_process` (`Libraries/LibWebView/HelperProcess.cpp:189`).

| Prozess | Aufgabe | `main.cpp` (Zeilen) | IPC-Endpunkte (`.ipc`, Zeilen) | Für statische Seite verzichtbar? |
| --- | --- | --- | --- | --- |
| Hub | startet und verbindet alle anderen, hält Cookies, Speicher, Berechtigungen, Systemschriften (`Documentation/ProcessArchitecture.md:51`) | `Services/Hub/main.cpp` (137) | `HubServer.ipc` (300), `HubClient.ipc` (392) | nein |
| WebContent | LibWeb und LibJS (`ProcessArchitecture.md:65`) | `Services/WebContent/main.cpp` (364) | `WebContentServer.ipc` (361), `WebContentClient.ipc` (334), dazu Web-Worker-, Test- und WebUI-Paare | nein |
| Compositor | rastert Anzeigelisten, Leinwände, WebGL und stellt sie dar (`ProcessArchitecture.md:57`) | `Services/Compositor/main.cpp` (143) | `CompositorWebContentServer.ipc` (118), `CompositorControlServer.ipc` (39), `CompositorPresentation{Client,Server}.ipc` (19/14), `RenderClock{Client,Server}.ipc` (21/7) | nein |
| RequestServer | HTTP, DNS, Cache für Renderer (`ProcessArchitecture.md:73`) | `Services/RequestServer/main.cpp` (167) | `RequestServer.ipc` (43), `RequestClient.ipc` (30), `RequestServerControl{,Client}.ipc` (44/32) | HYPOTHESE: ja für `file:`; WebContent verbindet ihn erst bei Bedarf (`Services/WebContent/main.cpp:306-308,355-362`), Dateien liefert der Hub (`ProcessArchitecture.md:65`) |
| ImageDecoder | dekodiert Bilder in geteilten Speicher (`ProcessArchitecture.md:77-79`) | `Services/ImageDecoder/main.cpp` (65) | `ImageDecoderServer.ipc` (18), `ImageDecoderClient.ipc` (11), `ImageDecoderControl{,Client}.ipc` (7/3) | BELEGT: ja, wird erst beim ersten Bild verbunden (`Services/WebContent/main.cpp:311`) |

Der Schriftdienst ist kein eigener Prozess, sondern ein Thread im Hub (`Libraries/LibWebView/FontService.h:78`, `Application.cpp:769`); Renderer und Compositor bekommen eine Verbindung zu ihm als erste Nachricht (`HelperProcess.cpp:344-349`).

### Befunde zum Zusammenlegen

| Nr. | Befund | Beleg | Kennz. |
| --- | --- | --- | --- |
| 1 | Es gibt keinen Ein-Prozess-Modus. Jeder der vier Dienste ist ein eigenes Programm mit `ladybird_main`, eigener Sandbox und eigener Ereignisschleife. | `Services/{WebContent,Compositor,RequestServer,ImageDecoder}/main.cpp`, z. B. `RequestServer/main.cpp:109,127-129`, `Compositor/main.cpp:116,124` | BELEGT |
| 2 | Nur der Hub ist schon eine Bibliothek: `hubservice` wird außer unter Linux und macOS in die UI gelinkt; dort läuft er im UI-Prozess. | `Services/Hub/CMakeLists.txt:22`, `UI/CMakeLists.txt:158-166`, `Documentation/ProcessArchitecture.md:47` | BELEGT |
| 3 | Die Hub-Abstraktion ist `Hub::ApplicationHost` (rein virtuelle Schnittstelle der UI, 19 Methoden) und `Hub::Application::create(arguments, host)`; `main.cpp` des Hub übergibt `nullptr` (`Services/Hub/main.cpp:110`). Sie trennt Hub und UI, nicht Hub und Helfer. | `Services/Hub/Application.h:20-43,53`, `UI/Qt/Application.cpp:754` | BELEGT |
| 4 | Auch mit Hub in der UI startet der Hub weiter echte Prozesse für WebContent, Compositor, RequestServer, ImageDecoder. Die Aufrufe sind dieselben (`launch_web_content_process` `HelperProcess.cpp:351`, `launch_compositor_process` `:540`, `launch_request_server_process` `:600`, `launch_image_decoder_process` `:417`). | `HelperProcess.cpp:189-230,305-342` | BELEGT |
| 5 | Selbst die UI-Hub-Verbindung im selben Prozess läuft über ein gepaartes Transportpaar (Socketpaar mit eigenem IO-Thread), obwohl eine direkte Ansicht (`m_view`) daneben besteht. | `UI/Qt/Application.cpp:757-762`, `Libraries/LibIPC/TransportSocket.cpp:79,364` | BELEGT |
| 6 | `IPC::Transport` ist eine konkrete Klasse, zur Übersetzungszeit per `using` gewählt (Mach-Port, Socket, Windows). Eine vierte Auswahl (im Prozess) ist an dieser Stelle möglich; die drei vorhandenen Transporte sind 763, 997 und 667 Zeilen groß. | `Libraries/LibIPC/Forward.h:31-41`, `Transport.h:11-17`, `TransportMachPort.cpp`, `TransportSocket.cpp`, `TransportWindows.cpp` | BELEGT |
| 7 | Verbindungen nehmen ein `NonnullOwnPtr<Transport>`; eine Nachricht kann Dateideskriptoren (`Attachment::from_fd`, `IPC::File`, `Core::AnonymousBuffer`) tragen. Die Zahl der Dateien, die diese Typen nennen: 110 (`AnonymousBuffer`), 40 (`IPC::File`), 68 (`TransportHandle`). | `Libraries/LibIPC/Connection.h:173,188`, `Attachment.h:20-30`, `git grep -l` über `Libraries Services UI` | BELEGT |
| 8 | Die fd-Übergabe ist plattformabhängig bereits vermittelt: Windows nutzt einen Handle-Broker (`HandleBroker.cpp` 238 Zeilen, `HandleBrokerServer.cpp` 243), Mach Ports eigene Anhänge. Unix: `SCM_RIGHTS` in `Libraries/LibCore/Socket.cpp:396,443`. | `Libraries/LibIPC/HandleBroker*.cpp`, `AttachmentMachPort.cpp`, `StartupTransport.cpp:21-37` | BELEGT |
| 9 | Geteilter Speicher ist an Dateideskriptoren gebunden: `AnonymousBuffer` über `anon_create`, Versiegeln mit `fcntl`-Seals, `pread`, `mmap`; `SharedImage` trägt Bitmaps zum Compositor. | `Libraries/LibCore/AnonymousBuffer.cpp:23,51-54,89,114,136`, `Services/Compositor/CompositorPresentationClient.ipc:8-9`, `CompositorWebContentServer.ipc:83-91` | BELEGT |
| 10 | RequestServer reicht Antworttexte als Dateideskriptor an WebContent (`IPC::File fd`) und nutzt eigene Pipe-Klasse. | `Services/RequestServer/RequestClient.ipc:13-15`, `RequestPipe.cpp:17-22` | BELEGT |
| 11 | **Synchrone Nachrichten blockieren den besitzenden Thread auf dem Transport**, ohne Ereignisschleife zu bedienen: `wait_for_specific_endpoint_message_impl` verlangt den Eigentümer-Thread, wartet in `wait_until_readable` und liest erst dann. Gibt es einen Dienst im selben Thread, kommt die Antwort nie. | `Libraries/LibIPC/Connection.cpp:246-248,277-285,194` | BELEGT |
| 12 | Synchron aufgerufen werden unter anderem `decode_first_frame_synchronously` (ImageDecoder), `get_canvas_pixels`, `create_webgl_context`, `reserve_canvas_ids` (Compositor), `is_supported_protocol`, `get_client_id`, `stop_request` (RequestServer) und die Handle-Anfragen an den Hub. | `Services/ImageDecoder/ImageDecoderServer.ipc:11`, `CompositorWebContentServer.ipc:61,69,82`, `RequestServer.ipc:17-28`, `WebContentClient.ipc:196-206` | BELEGT |
| 13 | Ein Dienst je Thread mit eigener Schleife ist vorgesehen: `Core::EventLoop::initialize_for_current_thread` je Hauptprogramm. WebContent hat dagegen einen Prozess-Singleton der JS-Maschine (`main_thread_vm_ptr`, nur einmal, `VERIFY(!main_thread_vm_ptr())`) und `ResourceLoader::the()`; der Compositor `SkiaBackendContext::the_main_thread_context`; RequestServer setzt Prozessglobale (`g_resource_substitution_map`, `set_process_top_level_site`). Für **eine** Instanz je Dienst stört das nicht, für mehrere WebContent oder je Site einen RequestServer schon. | `Libraries/LibWeb/Bindings/MainThreadVM.cpp:103-106,243,911-917`, `Libraries/LibWeb/Loader/ResourceLoader.cpp:82`, `Services/Compositor/main.cpp:114`, `Services/RequestServer/main.cpp:41,138` | BELEGT |
| 14 | Der Compositor ist Skia und GPU-Anbindung plus Rust-Kern (`CompositorCoreRustFFI.h`); Kontakt zu WebContent nur über Nachrichten (`CompositorCoreHost` meldet über `post_message_to_*`). Er braucht zusätzlich Thread und Zeitgeber (`RenderClock`, Thread). | `Services/Compositor/CompositorCoreHost.h:36-50`, `Libraries/LibWeb/Compositor/RenderClock.cpp:73` | BELEGT |
| 15 | Der ImageDecoder ist eine echte Naht: `ImageCodecPlugin` ist rein virtuell, die Remote-Variante nur eine Implementierung; Tests setzen eine eigene (`CountingImageCodecPlugin`). Die Schnittstelle trägt aber `IPC::TransportHandle` (`connect_frame_client`). | `Libraries/LibWeb/Platform/ImageCodecPlugin.h:40-62`, `Tests/LibWeb/TestFaviconDecoding.cpp:23,54` | BELEGT |
| 16 | Die Sandbox ist je Dienst eine eigene Datei; es gibt jeweils eine `SandboxUnimplemented.cpp`, ein Verzicht ist also vorgesehen (`--disable-sandbox`). | `Services/RequestServer/main.cpp:130-135`, `Services/*/SandboxUnimplemented.cpp` | BELEGT |
| 17 | Hohe Änderungsrate: seit dem 18.08.2026 73 Commits an `Libraries/LibIPC`, 67 an `HelperProcess.cpp`; der Hub wurde erst am 08.11.2026 aus dem Qt-Frontend herausgelöst (`d25f1874af`). Ein Zusammenlegen ist ein Dauerpatch gegen bewegliche Dateien. | `git log --since=2026-08-18` im Klon | BELEGT (Zahl), HYPOTHESE (Folge) |

## Was ein Umbau ändern müsste

### Weg A: zusammenlegen (ein Prozess, Dienste als Bibliotheken in Threads)

Zeilen sind die heutigen Dateigrößen (Regel 5). Neue Dateien sind mit der Größe des nächstähnlichen Transports angesetzt (**HYPOTHESE**).

| Datei | Zeilen | Änderung |
| --- | --- | --- |
| `Libraries/LibIPC/TransportInProcess.{h,cpp}` (neu) | ca. 820 | Warteschlange mit Weckruf statt Socket; Größe wie `TransportWindows.{h,cpp}` (156+667) |
| `Libraries/LibIPC/Forward.h`, `Transport.h`, `TransportHandle.{h,cpp}`, `StartupTransport.cpp`, `SingleServer.h`, `Attachment.{h,cpp}`, `Connection.cpp` | 827 | Auswahl des Transports, Handle als Zeiger statt Deskriptor, Aufnahme statt `SOCKET_TAKEOVER`; `Connection.cpp` nur, falls ein Thread mehrere Endpunkte bedient (Nr. 11) |
| `Libraries/LibCore/AnonymousBuffer.{h,cpp}`, `Libraries/LibGfx/SharedImage.{h,cpp}` | 523 | Heap statt Deskriptor und Versiegelung; 110 und 40 weitere Dateien nennen die Typen, sollen aber unverändert bleiben |
| `Libraries/LibWebView/HelperProcess.cpp`, `Process.cpp`, `RequestServerManager.cpp` | 1730 | `launch_*` startet Dienst-Thread statt Prozess; Prozessliste, Absturzbehandlung, Reaper entfallen |
| `Services/{WebContent,Compositor,RequestServer,ImageDecoder}/main.cpp` | 739 | je eine Einstiegsfunktion statt `ladybird_main`; Argumente als Struktur |
| `Services/RequestServer/RequestPipe.cpp` | 68 | Pipe durch Speicherpuffer ersetzen (Nr. 10) |
| **Summe** | **ca. 4700** | |

Nicht in der Summe (nicht gezählt): CMake-Dateien der vier Dienste, UI-Seite für ZehOS (Anbindung an `zehos_v1`, gehört zu L2-E), `UI/Qt` (entfällt oder wird ersetzt).

### Weg B: nativ (mehrere Prozesse, Ladybird unverändert)

Keine Änderung an Ladybird außer einem Betriebssystem-Anschluss. Er braucht unter `Libraries/LibCore/Process.cpp` (691 Zeilen), `LibCore/System.cpp`, `LibCore/Socket.cpp` (523) und `Libraries/LibIPC/TransportSocket.cpp` (997) POSIX-Aufrufe, die ZehOS nicht kennt (Zeilen 79, 391 und 396/443 oben). **HYPOTHESE:** Die Anbindung geht über eine libc-Schicht, nicht über Änderungen an Ladybird; der Aufwand liegt im Kernel.

## Was der native Weg vom Kernel bräuchte

Alle Punkte gelten für einen unveränderten Stand `6fba5b78a104`; der ZehOS-Stand ist Roadmap `origin/master` `bbf64f46`.

| Funktion | Ladybird-Beleg | ZehOS heute | Roadmap |
| --- | --- | --- | --- |
| Prozess erzeugen, Programm laden (ELF), Umgebung und Argumente, Kinder beenden | `LibCore/Process.cpp:384-391`, `HelperProcess.cpp:189`, `Process.cpp:307` | nein: Apps laufen im Kernel, höchstens eine Paket-App gleichzeitig (`docs/PAKETE_ERSTELLEN.md:159`), `argv` nur `["app"]` (`:130`) | 12.5 (Programmlader, [ROADMAP.md:334](../../../ROADMAP.md)), Vorstufe 12.9 (`:338`) |
| Lokale Sockets (`socketpair`, `AF_LOCAL`, `poll`) | `TransportSocket.cpp:79`, `Process.cpp:307` | `sock_*` ist `ENOSYS` (`ProjektZeh/Native/Wasm/host/zehos_host_core.h:101-104`); nur `net_*` für TCP ≤ 64 KiB | 12.9 (Nachrichtenkanäle) |
| Dateideskriptor-Übergabe | `Socket.cpp:396,443` | nein | 12.9 (geteilte Puffer), 12.5 |
| Geteilter Speicher (`memfd`, `mmap`, Versiegeln) | `AnonymousBuffer.cpp:23,51-54,136` | ein linearer Speicher je Modul (256 MiB, `WasmRuntime.cs:28-29`), keine Teilung | 12.9, 12.7 (geteilter Speicher unter AOT) |
| Threads | `TransportSocket.cpp:245,364` (IO-Thread je Transport), `RenderClock.cpp:73` | Wasm-Plattformschicht ohne Threads | 12.7, 13.5; Zahl siehe L2-D |
| Speicher- und Prozessisolation | Sandbox je Dienst (Nr. 16, abschaltbar) | Ring 0, nur Wasm als Grenze | 12.4 (`ROADMAP.md:321`) |
| Prozessende melden, Absturz eines Helfers überleben | Hub startet Compositor neu (`ProcessArchitecture.md:61`) | P5-Abnahme: Absturz des einen Moduls lässt das andere laufen ([Plan Roadmap 12](../plans/2026-11-17-roadmap-12.md), Zeile P5) | 12.9 |

Fazit der Tabelle: Der native Weg verlangt 12.4, 12.5 und 12.9 vollständig (zusammen laut Plan mindestens P5 mit 6-10 Wochen plus die Studie zu 12.4 mit 2-3 Wochen und anschließender Umsetzung in Monaten, **Aufwand dort geschätzt, nicht von diesem Bericht**).

## Aufwand

| Weg | Rechnung nach Regel 5 | Personwochen |
| --- | --- | --- |
| A, zusammenlegen | ca. 4700 Zeilen: 1 Woche je angefangene 1500 gibt 4 | **4 bis 6** |
| B, nativ | Ladybird selbst fast unverändert; Aufwand liegt im Kernel (12.4, 12.5, 12.9) | aus dem Roadmap-Plan, nicht hier geschätzt |

Begründung der Spanne bei A: 4 ist der Wert der festen Regel. Bis 6 (Abweichung begründet, **HYPOTHESE**), weil das Debuggen der Verklemmungen aus Nr. 11 (synchrone Nachricht im selben Thread) in der Regel nicht in den Zeilen steht und weil bei 73 Commits je Quartal an `LibIPC` jeder Rebase auf einen neueren Stand Arbeit kostet (Nr. 17). Nicht enthalten sind die Threads: Weg A hat mindestens einen Thread je Dienstschleife und die IO-Threads entfallen mit dem neuen Transport, die genaue Mindestzahl liefert L2-D. Ohne Threads (nur eine Schleife) müsste `Connection.cpp:246-296` die Schleife der anderen Dienste während des Wartens bedienen (verschachtelte Schleifen); das ist ein Eingriff in den Kern von LibIPC und nicht in der Zeilenzahl abgebildet.

## Urteil

**zusammenlegbar**

Begründung: Es gibt eine Naht für jeden der fünf Teile. Der Hub ist bereits Bibliothek (Nr. 2), der ImageDecoder hat eine virtuelle Schnittstelle (Nr. 15), die vier anderen sind dünne Hauptprogramme von 65 bis 364 Zeilen um Verbindungsklassen (Nr. 1), der Transport ist an genau einer Stelle gewählt und dreifach ausgetauscht (Nr. 6, 8). Das Urteil gilt unter drei Bedingungen, die der Zusammenfassung L2-Z weitergegeben werden:

1. Es bleibt bei **einer** Instanz je Dienst (Nr. 13); mehrere WebContent oder ein RequestServer je Site brauchen die Singletons aus `MainThreadVM.cpp` und `ResourceLoader.cpp`, die hier nicht umgebaut sind.
2. Mehr als ein Thread ist nötig oder die Schleifenverschachtelung in `Connection.cpp` (Nr. 11); damit hängt Weg A von L2-D und von 12.7 (Threads) ab.
3. Es ist ein dauerhafter Fork-Patch an beweglichen Dateien (Nr. 17). Upstream kennt keinen Ein-Prozess-Modus (Nr. 1), und der Compositor (Skia, Rust-Kern) ist hier nur von der Prozessseite, nicht von der Zeichenseite bewertet (L2-E).

Zum nativen Weg: Er ist kein Gegenurteil, sondern der Weg ohne diesen Umbau; er kostet den Kernel 12.4, 12.5, 12.9 (Tabelle oben).

## Nicht geprüft

- Ob Compositor und WebContent in einem Prozess ohne GPU Skia-CPU-Pfade ohne eigenen Thread des Compositors betreiben können (L2-E, L2-D).
- Die Mindestzahl der Threads und die Ersetzbarkeit der IO-Threads (L2-D).
- MediaServer, AudioServer, VideoDecoderServer, WebRTCClient, ProcessReaper, WebDriver: für eine statische Seite nicht gelesen; `ProcessArchitecture.md:26-37` nennt sie als je Renderer gestartet, beim ersten Bedarf. Ob WebContent ohne sie startet, wurde nicht im Code verfolgt.
- Die Rust-Seite von LibIPC (`Libraries/LibIPC/Rust/src/socket.rs:44`) und LibWebView (Rust-FFI): nicht gelesen, sie könnte weitere Transportstellen enthalten (zählt dann zusätzlich zu Weg A).
- Die 110 Dateien, die `AnonymousBuffer` nennen, wurden gezählt, nicht gelesen; ob alle Stellen mit einem Heap-Puffer auskommen (etwa Versiegelung, `AnonymousBuffer.cpp:51-54,114`), ist offen.
- Ob WebContent mehrere Seiten einer Site in einem Prozess verträgt, wurde nur über den VM-Singleton belegt (Nr. 13).
- Kein Gast- und kein Hosttest; keine Messung. Die Zeilenzahlen der neuen Dateien sind Schätzungen nach ähnlichen Dateien.
