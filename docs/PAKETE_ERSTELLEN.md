# Apps für ZehOS bauen und als Paket veröffentlichen

Stand: 15. November 2026. Diese Anleitung beschreibt, was eine App für den ZehOS-Wasm-Loader können darf und wie sie zum signierten Paket wird. Formatdetails stehen in [PACKAGE_FORMAT.md](PACKAGE_FORMAT.md), Messwerte und Hintergründe in [WASM_FEASIBILITY.md](WASM_FEASIBILITY.md). Unter QEMU geprüft sind Doom (C, doomgeneric) und NetSurf 3.11 als Offlinebrowser (siehe [NETSURF.md](NETSURF.md)); die C#-Probe lief nur als Konsolenprogramm in der Gastprobe, nicht als Paket.

## Was eine App ist

- Ein **WebAssembly-Modul (wasm32, WASI Preview 1)** mit Einstiegspunkt `_start`. Kein natives ELF und keine .NET-DLL.
- Der Kernel interpretiert es mit wasm3. Es gibt keinen JIT. Rechenintensive Apps laufen unter QEMU TCG deutlich langsamer als nativ: Doom erreicht dort rund 23 Bilder/s.
- Erprobte Toolchains:
  - **C/C++:** wasi-sdk 25 mit `--target=wasm32-wasip1`. Beispiel: `experiments/Wasm.DoomProbe/build.py`.
  - **C#:** .NET 10 `wasi-experimental` mit dem WASIp1-Umbau aus `experiments/Wasm.CSharpProbe/`. Das ist experimentell und hängt an internen SDK-Targets. Die App wird doppelt interpretiert (Mono in wasm3), und der Start dauert Sekunden.

## Was eine App vom Kernel bekommt

**Grafik, Zeit und Eingabe** (Importmodul `zehos_v1`, Deklarationen siehe `experiments/Wasm.DoomProbe/platform/doomgeneric_zehos.c`):

| Import | Zweck |
| --- | --- |
| `present(pixels, w, h, tic, test)` | 32-Bit-Bild 0x00RRGGBB, höchstens 1920×1200 |
| `present_indexed(indices, palette, w, h, tic)` | 8-Bit-Bild mit 256-Farben-Palette; die Umrechnung macht der Kernel nativ (schneller) |
| `ticks_ms()` | App-Uhr in ms; sie steht still, solange die App pausiert |
| `sleep_ms(ms)` | Warten, höchstens 1000 ms je Aufruf |
| `poll_key()` | -1 ohne Ereignis, sonst Bit 8 = gedrückt, Bits 0–7 = normalisierter ZehOS-Tastencode (Set 1) |
| `poll_event(ptr)` | 1 für ein Ereignis in 16 Byte Gastspeicher, 0 ohne Ereignis, -1 bei Hostfehler; Details unten |
| `should_exit()` | 1, wenn das Fenster geschlossen wird; die App muss dann zügig enden |
| `pointer_capture(on)` | 1 fängt den Zeiger ein, 0 gibt ihn frei; 0 bei Erfolg, -1 wenn das Fenster nicht aktiv ist. Seit ZehOS 0.2.22 |

`poll_event` schreibt vier Little-Endian-`i32` in der Reihenfolge `type, code, x, y`.
Nicht ausgerichtete Adressen sind erlaubt. Ein ungültiger 16-Byte-Bereich trappt **vor** der Entnahme;
bei Rückgabe 0 oder -1 bleibt der Zielbereich unverändert. `poll_key` und `poll_event` lesen unabhängige Puffer.
Sind 64 Einträge von `poll_key` ungelesen (Drücken und Loslassen belegen je einen), werden weitere Drücke verworfen,
das Loslassen einer gemeldeten Taste aber nie, damit keine Taste in der App hängen bleibt.

| type | Bedeutung von code und Koordinaten |
| --- | --- |
| 1 / 2 | Taste gedrückt / losgelassen; `code` = normalisierter ZehOS-Tastencode, `x=y=0` |
| 3 | Text als Unicode-Skalar in `code`, `x=y=0`; ungültige UTF-16-Folgen werden U+FFFD |
| 4 | Zeigerbewegung; `code=0`, `x/y` = Pixel im zuletzt gezeichneten App-Bild |
| 5 / 6 | Maustaste gedrückt / losgelassen; `code` = 1 links, 2 rechts, 4 Mitte, `x/y` wie oben |
| 7 | Mausrad; `code` = Schritte, positiv aufwärts, negativ abwärts, `x/y` wie oben |
| 8 | Eingabe zurücksetzen; `code=x=y=0`, alle gehaltenen Tasten/Maustasten verwerfen |
| 9 | relative Zeigerbewegung bei eingefangenem Zeiger; `code=0`, `x/y` = Pixel seit dem letzten Ereignis (rechts/unten positiv) |
| 10 | Fenstergröße; `code=0`, `x/y` = Breite/Höhe des Fensterinhalts in Pixeln, je Achse auf 1920 bzw. 1200 begrenzt (größere Fenster passt der Kernel ein). Seit ZehOS 0.5.1 |

Wiederholte Tastendrücke kommen erneut als Typ 1, Text zusätzlich als Typ 3. Pfeile verwenden 72/75/77/80,
Entf 83, AltGr 96. Ein E0-Merkmal steht nicht zur Verfügung; Ziffernblock und Navigation können denselben
Tastencode haben. Texteingabe deshalb aus Typ 3 lesen. Bei Fokusverlust werden wartende Ereignisse und
angefangene Surrogatpaare verworfen und durch Reset ersetzt. Der Puffer hält 128 Ereignisse;
bei Überlauf bleiben Reset und der neueste Input. Aufeinanderfolgende Bewegungen werden zusammengefasst.

Koordinaten berücksichtigen Bildskalierung und Zentrierung. Druck und Rad in schwarzen Rändern werden
ignoriert, Bewegung und Loslassen am Bildrand begrenzt. Ein begonnenes Ziehen behält alle Maustasten auch
außerhalb des Fensters bis zum letzten Loslassen oder Reset. Die App bestimmt die Bildgröße über `present`.
Typ 10 meldet die Größe des Fensterinhalts beim ersten Zeichnen und nach jeder Änderung; mehrere Änderungen
zwischen zwei Abfragen ergeben eine Meldung mit der letzten Größe, Reset und Überlauf verwerfen sie nicht. Eine App,
die danach Bilder in dieser Größe liefert, wird 1:1 gezeichnet. Apps ohne Typ 10 (oder ältere Kernel) behalten ihre
feste Größe. Bis das nächste Bild kommt, gelten Zeigerkoordinaten weiter für das zuletzt gezeichnete Bild; die App kann solche Ereignisse nicht von späteren unterscheiden. Der Kernel passt das Bild
seitentreu in den Fensterinhalt ein (nächster Nachbar, vergrößernd wie verkleinernd, Rest schwarz); Mauspositionen
kommen in Bildkoordinaten an. Scharf, also 1:1, ist es nur bei genau passender Größe: Ein Paketfenster öffnet mit
798×519 Inhalt.

**Zeiger einfangen:** Nach `pointer_capture(1)` erhält die App alle Zeigerereignisse, auch außerhalb ihres
Fensters; Taskleiste und andere Fenster reagieren dann nicht auf die Maus. Bewegungen kommen nur noch als Typ 9;
aufeinanderfolgende Deltas werden addiert. Maustasten (5/6) zählen überall und tragen auf das Bild begrenzte
Koordinaten; Tasten und Rad bleiben unverändert. Esc, Fokusverlust,
Fensterwechsel, F4 und Schließen geben den Zeiger frei; bei eingefangenem Zeiger erreicht Esc die App als Taste. Ein weiteres Esc führt zur Textshell. Nach der Freigabe
kommen wieder absolute Bewegungen (Typ 4), daran erkennt die App sie; bei Fokusverlust zusätzlich ein Reset.
Eine App ohne Aufruf von `pointer_capture` verhält sich wie bisher. Der Import fehlt in Kerneln vor 0.2.22;
eine App, die ihn importiert, startet dort nicht und setzt deshalb `min-kernel` (siehe unten, Veröffentlichen).
Ab ZehOS 0.5.2 bleiben relative PS/2-Mauspakete beim Zeigerfang auch am Rand des Gastbildschirms als Deltas erhalten. Bei absoluten
Tablets entstehen Deltas weiterhin aus Bildschirmpositionen; ihre Bewegung endet am Gastbildschirm- beziehungsweise
Hostfensterrand. Der Desktopzeiger bleibt sichtbar.

**Rasterisierung über `zehos_v1` (ab ZehOS 0.5.13):**

Der Kernel zeichnet texturierte Quads nativ in Puffer im Speicher der App, damit 3D-Apps nicht jedes Pixel im Interpreter rechnen. Grundlage und Bildregeln sind die des Software-Renderers SoftGPU von ClassiCube. Den Aufbau beschreibt [der Entwurf](superpowers/specs/2026-11-17-rasterizer-hostfunktion.md).

| Import | Verhalten |
| --- | --- |
| `raster_draw(call)` | Zeichnet nach der 140-Byte-Beschreibung `zehos_raster_call` (Version 1) an Adresse `call`. Erfasst werden Vertices, Farbpuffer, Tiefenpuffer, Textur, Scissor, Viewport, Texturversatz und MVP-Matrix. Flags: Tiefentest, Tiefe schreiben, Farbe schreiben, Alphatest (A < 128), Alphablending, Backface-Culling, 2D, Sprite, texturiert. Je 4 Vertices bilden ein Quad. |
| `raster_fill(ptr, count, value)` | Füllt `count` 32-Bit-Werte ab `ptr` mit `value` (Farb- oder Tiefenpuffer löschen). |

Datenformate (Little Endian):

- **Vertex:** Position `x, y, z` als float an Offset 0, 4 und 8; Farbe als u32 an Offset 12 mit den Bytes R, G, B, A (also `0xAABBGGRR`). Texturiert folgen `u, v` als float an Offset 16 und 20 (24 Byte je Vertex), sonst endet der Vertex nach 16 Byte.
- **Farbpuffer und Textur:** u32 `0xAARRGGBB` (B im niedrigsten Byte), wie bei `present`. Die Textur wird immer gelesen, auch bei farbigen Vertices im Sprite-Pfad; ohne eigene Textur eine weiße 1×1-Textur übergeben.
- **Tiefenpuffer:** float, nur nötig für 3D mit Tiefentest oder Tiefe schreiben; Löschwert üblich 1e8.

Die genaue Feldtabelle steht im Entwurf.

Beide liefern 0 oder `EINVAL` (28) bei unzulässigen Werten. Unzulässig sind unbekannte Version oder Flags, `count` nicht durch 4 teilbar oder über 262 144, Bild über 1920×1200, Zeilenabstand kleiner als die Breite oder über 7680, Textur keine Zweierpotenz und Scissor außerhalb; bei `raster_fill` mehr als 9 216 000 Werte. Liegt ein Bereich (Beschreibung, Vertices, Puffer, Textur) nicht im linearen Speicher, trappt der Aufruf. Der Kernel hält keinen Zustand und keine Handles; alle Puffer bleiben im Speicher der App. Die App sollte bei `EINVAL` selbst zeichnen. Ein Aufruf läuft ohne Unterbrechung im Worker der App; sehr viele große Quads können ihn entsprechend lange belegen, wie jede Rechenschleife der App selbst.

Ob der Kernel die Funktionen hat, meldet die WASI-Umgebung (siehe unten): `ZEHOS_FEATURES` enthält dann `raster_v1`. Ältere Kernel liefern eine leere Umgebung. Ein Modul mit diesen Imports lädt dort trotzdem: Der Loader ignoriert unbekannte Imports, und wasm3 meldet einen fehlenden Import erst, wenn er tatsächlich aufgerufen wird (Trap). Die App ruft die Funktionen deshalb nur, wenn `raster_v1` gemeldet ist, und braucht dann kein höheres `min-kernel`. Beispiel: `experiments/ClassiCube.Wasi/platform/Raster_ZehOS.c`.

**Netzwerk über `zehos_v1`:**

Nur ein signiertes Manifest mit `capability=net` erlaubt TCP-Verbindungen; `scripts/build-package.py package --network` setzt die Zeile. Die Berechtigung umfasst auch localhost und private LAN-Adressen. Es gibt höchstens vier Verbindungen und 64 KiB je Sende- und Empfangspuffer. IPv4-Adressen und ASCII-DNS-Namen sind möglich. TLS gibt es über das Flag `ZEHOS_NET_TLS` (siehe unten); es braucht keine eigene Berechtigung.

Alle Funktionen liefern einen WASI-Preview-1-Fehlercode (`0` bei Erfolg). Gastpuffer und Ausgabezeiger werden vor dem Hostzugriff geprüft; ungültige Bereiche verursachen einen Trap. Ausgabezahlen sind 32-Bit-Werte im Little-Endian-Format und dürfen unausgerichtet sein.

| Import | Verhalten |
| --- | --- |
| `net_open(host, len, port, flags, out_handle)` | Startet DNS und TCP asynchron. `flags` ist 0 oder `ZEHOS_NET_TLS` (1); andere Bits ergeben `ENOTSUP` (58), auch auf älteren Kerneln ohne TLS. Das Handle gehört zur App-Sitzung. Ohne Berechtigung `EACCES` (2). |
| `net_status(handle)` | `EAGAIN` (6) während DNS/Connect und gegebenenfalls TLS-Handshake, danach 0 oder ein Fehler; der Verbindungsaufbau hat einschließlich Handshake ein Zeitlimit von 10 s. |
| `net_send(handle, ptr, len, out_n)` | Übernimmt bis zu `len` Bytes in den begrenzten Sendepuffer; `out_n` meldet die übernommene Anzahl. Bei vollem Puffer `EAGAIN`. Ein erfolgreicher Aufruf bestätigt nicht die Zustellung. |
| `net_recv(handle, ptr, len, out_n)` | Liefert vorhandene Daten; bei leerem Puffer `EAGAIN`. Erfolg und `out_n=0` bei nichtleerem Zielpuffer bedeuten EOF. |
| `net_close(handle)` | Gibt das Handle frei und veranlasst das Schließen der Verbindung. Offene Handles werden beim Ende der Wasm-Sitzung ebenfalls geschlossen. |

Einzelne Übertragungen sind auf 64 KiB begrenzt. Häufige Fehler sind `EBADF` (8), `EINVAL` (28), `EIO` (29), `EMFILE` (33), `ENOENT` (44, DNS-Auflösung fehlgeschlagen), `ENOTSUP` (58) und `ETIMEDOUT` (73). Für Datenverkehr nach dem Verbindungsaufbau muss die Anwendung selbst eine geeignete Frist setzen. Cosmos-interne Connect- und Sendewartephasen sind nicht sofort abbrechbar; währenddessen können andere Netzwerkaufträge warten, die Oberfläche bleibt durch den Netzwerkworker bedienbar. Ein gleichzeitig mit `net_close` beginnender Connect kann das Ziel noch erreichen; der Worker schließt den Socket nach Rückkehr des Cosmos-Aufrufs. Das ABI kennt außerdem `ECONNREFUSED` (14), `ECONNRESET` (15) und `EHOSTUNREACH` (23). Cosmos 3.0.94 unterscheidet diese Fälle nicht: Ein abgelehnter oder unerreichbarer Verbindungsaufbau meldet `EIO`, ein Abbruch durch RST erscheint wie ein normales Verbindungsende (`net_recv` liefert 0 Bytes). Anwendungen sollten die drei Werte trotzdem behandeln.

**TLS (`ZEHOS_NET_TLS`):** Der Kernel führt nach dem TCP-Aufbau einen TLS-1.2/1.3-Handshake mit denselben Suiten, Grenzen und derselben Zertifikatsprüfung wie die Shell aus (eingebautes Mozilla-Wurzelbundle, höchstens 8 Zertifikate je Kette). Der übergebene `host` ist zugleich SNI und Prüfname; ein IPv4-Literal wird ohne SNI gegen die IP-SAN geprüft. Eine Option zum Übergehen der Prüfung oder eigene Vertrauensanker gibt es nicht. `net_send` und `net_recv` übertragen Klartext; die Grenzen von vier Verbindungen und 64 KiB gelten für Klartext. Zusätzliche Fehler:

| Code | Bedeutung |
| --- | --- |
| `ENOSYS` (52) | sofort aus `net_open`: UTC-Uhr unplausibel, kein Hardwarezufall (RDRAND/RDSEED/virtio-rng) oder Wurzelbundle nicht lesbar |
| `EPERM` (63) | aus `net_status`: Zertifikat abgelehnt (Kette, Name, Gültigkeit, Verwendungszweck) |
| `EPROTO` (65) | aus `net_status`, `net_send` oder `net_recv`: sonstiger TLS-Fehler oder Alert des Servers |
| `ECONNRESET` (15) | Verbindung endet ohne `close_notify`, im Handshake oder nach den bereits gelieferten Daten |

Nach `close_notify` meldet `net_recv` EOF wie bei TCP. Endet die Verbindung ohne `close_notify`, liefert `net_recv` erst die restlichen Daten und dann `ECONNRESET`. Viele Server schließen ohne `close_notify`; eine Antwort mit bekannter Länge sollte die App deshalb nach dem letzten Byte selbst beenden. `net_close` sendet nach Möglichkeit `close_notify`. Die Wurzeln werden beim ersten TLS-Aufruf einmal je Kernellauf eingelesen; dieser Aufruf von `net_open` dauert entsprechend länger.

**WASI Preview 1:**

| Bereich | Stand |
| --- | --- |
| Konsole | `fd_write` nur auf stdout/stderr. stderr wird gesammelt; die letzte Zeile erscheint bei Exitcode ≠ 0 im Fenster. |
| Argumente, Umgebung | `argv` ist nur `["app"]`. Die Umgebung enthält ab ZehOS 0.5.13 genau `ZEHOS_FEATURES=<Liste>`, eine kommagetrennte Liste optionaler `zehos_v1`-Fähigkeiten (heute `raster_v1`). Ältere Kernel liefern eine leere Umgebung. |
| Uhren, Zufall | `clock_time_get` (Uhr 0 = UTC, sonst monoton) und `random_get` (Hardwarezufall über RDSEED/RDRAND, sonst virtio-rng; fehlt er oder versagt er, liefert der Aufruf `EIO` (29) statt Erfolg, und der Puffer ist nicht zu verwenden. Apps müssen den Status prüfen und dürfen keinen eigenen Rückfall auf Uhr oder Konstanten bauen. In wasi-libc (wasi-sdk 25) meldet `getentropy` (höchstens 256 Byte je Aufruf) den Fehler als `EIO`; `arc4random`/`arc4random_buf` brechen dagegen mit einem Trap ab, und die .NET-WASI-Laufzeit nutzt voraussichtlich diesen Weg. Solche Apps starten ohne Hardwarezufall also nicht). |
| Dateien | Nur lesend. Das eigene Paketverzeichnis `files/` ist als `/` freigegeben. Pfade relativ ohne `..`. `path_open`, `fd_read`, `fd_pread`, `fd_seek`, `fd_tell`, `fd_filestat_get`, `path_filestat_get`, `fd_close`; höchstens 16 offene Dateien. |
| Nicht vorhanden | Schreiben, Anlegen, Löschen, Verzeichnisse lesen, `poll_oneoff` (also kein `sleep`/`nanosleep` der libc; `zehos_v1.sleep_ms` verwenden), WASI-Sockets, Threads. Diese Aufrufe liefern `ENOSYS` oder `EBADF`. |

`path_filestat_get` fragt Dateien und Ordner innerhalb des eigenen Pakets über den vorgeöffneten
Ordner fd 3 ab. `.` bezeichnet die Paketwurzel. Größe und Dateityp sind vorhanden; Zeitstempel und
Inodewerte sind null. Alle Gastbereiche werden vor dem Hostzugriff geprüft. Pfade verwenden dieselben
Schutzprüfungen wie `path_open`: keine absoluten Pfade, `..`, leeren Komponenten, Backslashes, Doppelpunkte
oder Nullbytes. Pakete haben keine symbolischen Links. `path_readlink` meldet für einen bestehenden
Nicht-Link `EINVAL`; fehlende Dateien bleiben `ENOENT`, Zugriffsfehler `EACCES`, sonstige Dateifehler `EIO`.
Verzeichnislisten (`fd_readdir`) und Öffnen von Verzeichnis-Handles bleiben nicht verfügbar.

## Grenzen

- **Grafische Apps:** Eine App startet nur als Fenster aus dem Startmenü. Reine Konsolen-Apps und einen Shellbefehl `run` gibt es noch nicht.
- **Eine App zur Zeit:** Alle Paket-Apps teilen einen Worker. Läuft eine, startet keine zweite.
- **Ressourcen:** höchstens 256 MiB lineare Memory, 1 MiB Wasm-Stack, Modul höchstens 64 MiB, kein Gaslimit für Paket-Apps.
- **Beenden:** Eine App, die `should_exit()` nie abfragt, lässt sich nicht beenden. Das Paket bleibt dann bis zum Neustart gesperrt (kein Entfernen, kein Upgrade). Die Hauptschleife muss die Abfrage enthalten.
- **Keine Speicherstände:** Apps können nichts dauerhaft speichern.
- **Tasten:**
  - Das erste Esc bei eingefangener Maus gibt den Zeiger frei und erreicht die App als Taste; Wiederholungen eines gehaltenen Esc gehen nicht weiter. Ein erneutes Esc bei freiem Zeiger führt zur Textshell. F4 wechselt jederzeit zwischen Desktop und Textshell.
  - Bei Fokusverlust werden gehaltene Tasten losgelassen.
- **Pause:** Ist das Fenster nicht aktiv oder nicht sichtbar, blockiert `present` und `ticks_ms` steht still.
- **Fehler:** Ein Trap (ungültiger Speicherzugriff, falscher indirekter Aufruf) beendet nur die App; das Fenster zeigt die Ursache. Ein Fehler im Interpreter oder Host kann trotzdem den Kernel treffen. Das ist eine logische Grenze, keine Prozessisolation.
- **AOT (in Arbeit, noch nicht für Apps freigegeben):** Ein Paket darf `<entry ohne .wasm>.wamr245-aot5-x64-bc.aot` enthalten ([Paketformat](PACKAGE_FORMAT.md#aot-datei-optional-aot-plan-etappe-e)). Nur ein mit `-p:ZehOSWasmAot=true` gebauter Kernel nutzt es, und erst nach `wasmaot on` (Standard aus); sonst, bei fremder Kennung, bei einem `.aot` über 64 MiB und wenn das `.aot` schon beim Laden scheitert, läuft das `.wasm` unter wasm3. Scheitert es erst beim Instanziieren oder am Einstieg, bricht der Start ohne zweiten Versuch ab. Ältere Kernel installieren solche Pakete und starten das `.wasm`. Dieselbe App muss deshalb unter beiden Laufzeiten gleich funktionieren. Unterschiede unter AOT: kein Gasbudget (wie heute bei Paket-Apps), und eine WASI-Funktion, die ZehOS nur als Stub anbietet, trappt bei einer ungültigen Gastadresse, statt wie unter wasm3 `EBADF`/`ENOSYS` zu liefern. NetSurf und andere Apps mit Ausnahmen im exnref-Format laden unter WAMR 2.4.5 nicht und bleiben bei wasm3.
- **Paketformat:**
  - höchstens 128 Dateien und 128 MiB je Paket
  - Pfade nur aus `A–Z a–z 0–9 . _ -`, ohne Groß-/Kleinschreibungs-Duplikate
  - Kennung `[a-z0-9][a-z0-9-]{0,47}`, Anzeigename höchstens 32 ASCII-Zeichen
- **Dauer unter QEMU TCG:** Jede Datei wird beim Installieren und beim ersten Start nach jedem Neustart per SHA-256 geprüft. 28 MB brauchen beim Installieren rund 2 Minuten, beim ersten Start bis zu etwa 30 s.

## Veröffentlichen

1. App bauen (z. B. `python experiments/Wasm.DoomProbe/build.py --source <doomgeneric>`).
2. Lizenztexte aller Bestandteile als Paketdateien beilegen. GPL-Code verlangt zusätzlich, dass die Quellen öffentlich erreichbar sind.
3. Einen Arbeitsbranch von `packages` abzweigen (etwa `git worktree add -b pakete/doom-1.1.0 ../pakete origin/packages`) und darin eine **neue** Version bauen. Veröffentlichte Versionen sind unveränderlich; jede Änderung braucht eine höhere Version.

   ```bash
   python scripts/build-package.py package --key <privater-Schlüssel.pem> --repo <packages-Worktree> --id <id> --version <x.y.z> --name "<Name>" --entry <app.wasm> --min-kernel <x.y.z> <datei>=<pfad> ...
   ```

   `--min-kernel` nennt die ZehOS-Version, die den neuesten verwendeten Import eingeführt hat (die Hostfunktionen oben nennen sie). Ohne neuere Importe kann der Parameter entfallen. Kernel ab 0.3.0 lehnen Installation, Upgrade und Start mit einer Meldung ab, wenn sie älter als dieser Wert sind. Kernel vor 0.3.0 kennen das Feld nicht und verwerfen jedes Manifest mit `min-kernel`, auch wenn sie die Importe hätten; Einzelheiten in [PACKAGE_FORMAT.md](PACKAGE_FORMAT.md).

4. Den Katalog mit **höherer Generation** neu signieren. Das Werkzeug lehnt eine gleiche oder kleinere ab.

   ```bash
   python scripts/build-package.py catalog --key <privater-Schlüssel.pem> --repo <packages-Worktree> --generation <n+1>
   ```

5. Den Arbeitsbranch committen, pushen und per Pull Request nach `packages` bringen; direkte Pushes auf `packages` lehnt GitHub ab. Nach dem Merge übernimmt der Server (`deploy/package-repo`) den Stand innerhalb von 5 Minuten und schaltet ihn als Ganzes um.
6. In ZehOS: `pkg install <id>` oder `pkg upgrade`; beide laden den signierten Katalog automatisch. `pkg update` bleibt als freiwilliger Einzelabruf verfügbar.

Den privaten Herausgeberschlüssel nie ins Repository, in Logs oder auf den Server legen. Geht er verloren, lassen sich keine Pakete mehr veröffentlichen. Ein neuer Schlüssel braucht ein Kernelupdate mit neuem `ProjektZeh/Packages/package-key.pub`.

## AOT-Datei erzeugen (Werkzeug, noch ohne Kernelnutzung)

Übersetzen und Signieren sind zwei getrennte Schritte, weil `wamrc` fremde `.wasm` liest und deshalb ohne Zugriff auf den privaten Schlüssel laufen soll (Sicherheitsdokument, Abschnitt 5, Punkt 2). `scripts/build-package.py aot <wamrc> <app.wasm> -o <app.aot>` übersetzt den Einstiegspunkt mit `wamrc` in nativen x86-64-Code und braucht keinen Schlüssel. `package … --aot-file <app.aot>` legt die fertige Datei danach als `<entry ohne .wasm>.wamr245-aot5-x64-bc.aot` ins Paket, etwa `doom.wamr245-aot5-x64-bc.aot` neben `doom.wasm`. Die Datei steht als gewöhnliche, ordinal einsortierte `file`-Zeile im signierten Manifest; ein neues Manifestfeld gibt es nicht. Hintergrund und Sicherheitsabwägung: [AOT-Plan](superpowers/plans/2026-11-17-aot-kernel.md), Etappe E, und [Sicherheitsdokument](superpowers/specs/2026-11-17-aot-sicherheit.md).

- **Kennung `wamr245-aot5-x64-bc`:** WAMR 2.4.5, AOT-Formatversion 5 (`AOT_CURRENT_VERSION` in 2.4.5), Ziel x86-64, mit Speicher- und Stackprüfung. Ein anderes WAMR ergibt eine neue Kennung. Die Kennung ist nur eine Behauptung im Dateinamen; der Kernel kann sie nicht prüfen. Dass Schalter und `wamrc` stimmen, stellt allein das Werkzeug beim Übersetzen sicher (siehe Beleg).
- **Feste Schalter:** `--target=x86_64 --cpu=x86-64 --bounds-checks=1 --stack-bounds-checks=1 --size-level=0 --disable-simd`, gleich `ProjektZeh/Native/AotProbe/fixture/compile-aot.sh` im Branch `feature/wasm-aot-runtime`. Weitere Schalter nimmt das Werkzeug nicht an.
- **Festes Werkzeug:** `aot` kopiert die angegebene `wamrc`-Datei in ein temporäres Verzeichnis und führt nur diese Kopie mit absolutem Pfad aus, also ohne Suche im `PATH` und ohne Austausch zwischen Prüfung und Start. Vorher prüft es den SHA-256 der Kopie gegen die Liste `WAMRC_SHA256` im Skript (derzeit nur das `wamrc` der Hostprobe 5b, `6b400e9cf9cf46fe96b38b1800bd0149f9da903ac6e95ad011a16e5dca1fa774`) und danach `wamrc --version` auf 2.4.5. Ein unbekanntes `wamrc` läuft nicht einmal für `--version`. Der Hash deckt nur diese eine Datei: `wamrc` muss LLVM statisch gelinkt enthalten (Standard eines LLVM-Baus ohne `BUILD_SHARED_LIBS`/`LLVM_LINK_LLVM_DYLIB`). Gemeinsame Systembibliotheken wie die C-Bibliothek deckt die Prüfung nicht ab; das bleibt eine Grenze. `aot` lehnt eine Ausgabe ab, die gleich der Eingabe ist, und löscht eine Teil-Ausgabe, wenn `wamrc` scheitert.
- **Beleg:** `aot` schreibt neben die Ausgabe `<app.aot>.json` mit Kennung, `wamrc`-Version, SHA-256 von `wamrc`, `.wasm` und `.aot` sowie den festen Schaltern. `package --aot-file` verlangt diesen Beleg und bricht ab, wenn der Hash der `.aot` nicht passt, das `.wasm` nicht genau der Einstiegsdatei dieses Aufrufs entspricht, das `wamrc` nicht zugelassen ist oder Schalter oder Kennung abweichen. Der Beleg schützt gegen Verwechslung und veraltete Dateien, etwa eine `.aot` aus einer älteren App-Version. Er ist nicht signiert und schützt nicht gegen einen Angreifer, der auf der Übersetzungs- oder Signiermaschine Dateien ändern kann.
- **Temporäres Verzeichnis:** `aot` führt `wamrc` aus dem temporären Verzeichnis aus. Ist `/tmp` mit `noexec` eingehängt, `TMPDIR` auf ein ausführbares Verzeichnis setzen.
- **Grenzen:** Das `.aot` zählt gegen 128 Dateien und 128 MiB Paketinhalt und vergrößert den Download auch für Kernel, die es nicht nutzen. Der Pfad mit Suffix darf wie jeder Paketpfad höchstens 200 Zeichen lang sein, sonst bricht `package` ab. Ohne `--aot-file` arbeitet `package` unverändert.
- **Kernelstand:** Der Kernel nutzt `.aot` erst mit der Kernelseite von Etappe E; bis dahin startet jede App unter wasm3. Heutige Kernel behandeln die Datei als gewöhnliche Paketdatei: `PackageManifest.Parse` nimmt jeden gültigen Pfad an, `pkg install` lädt und prüft sie wie jede andere Datei per SHA-256, gestartet wird nur `entry`. Die Datei kostet dort also nur Download, Speicherplatz und Prüfzeit.
- **Nur unter Linux:** Für Windows gibt es kein `wamrc`, und der Windows-Entwicklungsrechner hat kein WSL. `.aot`-Dateien entstehen deshalb bis auf Weiteres nur auf einem Linux-Rechner; signiert werden kann danach wie bisher.

### wamrc 2.4.5 und LLVM 18.1.8 bauen

Die Werkzeuge liegen unter `.tools/aot/` und gehören nicht ins Repository. Grundlage ist die Hostprobe 5b (Branch `experiments/aot-hostprobe`, `experiments/AOT.Probe/README.md`).

1. **LLVM 18.1.8 aus den Quellen:** `llvm-project-18.1.8.src.tar.xz` vom GitHub-Release `llvmorg-18.1.8` laden und die beiliegende Signatur (Tom Stellard) prüfen. Die Hostprobe hat den SHA-256 nur gekürzt festgehalten (`0b58557a…db2f2a`); er muss an Anfang und Ende übereinstimmen. Bauen nur für X86, ohne Clang und Tests, mit `-DCMAKE_CXX_FLAGS="-include cstdint"`, weil GCC 16 `<cstdint>` nicht mehr implizit einbindet. Das vorgebaute LLVM-18.1.8-Paket (Ubuntu 18.04) passt nicht (`wamrc` stürzt in `X86TargetMachine` ab); mit LLVM 23 baut WAMR 2.4.5 nicht (API-Brüche).
2. **WAMR 2.4.5:** `WAMR-2.4.5.tar.gz` von GitHub laden und den SHA-256 `1ab09d51099f276ca4a1d6629f6b589aab2bd0caa01445e05031a4bed22c199b` prüfen. `wamr-compiler` gegen das eigene, statisch gebaute LLVM bauen.
3. **Prüfen:** `wamrc --version` muss 2.4.5 melden. Ein neu gebautes `wamrc` hat fast sicher einen anderen SHA-256 als das der Hostprobe und wird vom Skript abgelehnt. Es in `WAMRC_SHA256` aufzunehmen ist eine eigene Änderung mit Rückfrage; vorher belegen Trap-Tests mit genau diesem `wamrc` (Speicherzugriff außerhalb, tiefe Rekursion) die Prüfungen im erzeugten Code (Sicherheitsdokument, Abschnitt 5, Punkt 3; Fixtures unter `ProjektZeh/Native/AotProbe/fixture/` im Branch `feature/wasm-aot-runtime`).

Ablauf:

```bash
# 1. Übersetzen unter Linux in einer getrennten Umgebung (etwa einem Container) ohne den privaten Schlüssel:
python scripts/build-package.py aot .tools/aot/<wamrc> <app.wasm> -o <app.aot>
# 2. Signieren wie unter „Veröffentlichen“, mit der übersetzten Datei:
python scripts/build-package.py package --key <privater-Schlüssel.pem> --repo <packages-Worktree> --id <id> --version <x.y.z> --name "<Name>" --entry <app.wasm> --aot-file <app.aot> <datei>=<pfad> ...
```

Die Belegdatei `<app.aot>.json` muss neben `<app.aot>` mit auf die Signiermaschine; ist das `.wasm` in Schritt 2 ein anderes als in Schritt 1, bricht `package` ab.
