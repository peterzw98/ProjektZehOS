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

Wiederholte Tastendrücke kommen erneut als Typ 1, Text zusätzlich als Typ 3. Pfeile verwenden 72/75/77/80,
Entf 83, AltGr 96. Ein E0-Merkmal steht nicht zur Verfügung; Ziffernblock und Navigation können denselben
Tastencode haben. Texteingabe deshalb aus Typ 3 lesen. Bei Fokusverlust werden wartende Ereignisse und
angefangene Surrogatpaare verworfen und durch Reset ersetzt. Der Puffer hält 128 Ereignisse;
bei Überlauf bleiben Reset und der neueste Input. Aufeinanderfolgende Bewegungen werden zusammengefasst.

Koordinaten berücksichtigen Bildskalierung und Zentrierung. Druck und Rad in schwarzen Rändern werden
ignoriert, Bewegung und Loslassen am Bildrand begrenzt. Ein begonnenes Ziehen behält alle Maustasten auch
außerhalb des Fensters bis zum letzten Loslassen oder Reset. Das Bild hat keinen Resize-Import;
die App bestimmt seine Größe über `present` und muss eine passende feste Größe wählen. Der Kernel passt das Bild
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
eine App, die ihn importiert, startet dort nicht. **Grenze:** Die Deltas entstehen aus Bildschirmpositionen. QEMU
und VMware liefern mit dem Tablet ein absolutes Zeigegerät; die Bewegung endet deshalb am Rand des Gastbildschirms
beziehungsweise des Hostfensters (Tablet), nicht am Fensterrand. Der Desktopzeiger bleibt sichtbar.

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
| `ENOSYS` (52) | sofort aus `net_open`: UTC-Uhr unplausibel, kein Hardwarezufall (RDRAND/RDSEED) oder Wurzelbundle nicht lesbar |
| `EPERM` (63) | aus `net_status`: Zertifikat abgelehnt (Kette, Name, Gültigkeit, Verwendungszweck) |
| `EPROTO` (65) | aus `net_status`, `net_send` oder `net_recv`: sonstiger TLS-Fehler oder Alert des Servers |
| `ECONNRESET` (15) | Verbindung endet ohne `close_notify`, im Handshake oder nach den bereits gelieferten Daten |

Nach `close_notify` meldet `net_recv` EOF wie bei TCP. Endet die Verbindung ohne `close_notify`, liefert `net_recv` erst die restlichen Daten und dann `ECONNRESET`. Viele Server schließen ohne `close_notify`; eine Antwort mit bekannter Länge sollte die App deshalb nach dem letzten Byte selbst beenden. `net_close` sendet nach Möglichkeit `close_notify`. Die Wurzeln werden beim ersten TLS-Aufruf einmal je Kernellauf eingelesen; dieser Aufruf von `net_open` dauert entsprechend länger.

**WASI Preview 1:**

| Bereich | Stand |
| --- | --- |
| Konsole | `fd_write` nur auf stdout/stderr. stderr wird gesammelt; die letzte Zeile erscheint bei Exitcode ≠ 0 im Fenster. |
| Argumente, Umgebung | `argv` ist nur `["app"]`, die Umgebung ist leer. |
| Uhren, Zufall | `clock_time_get` (Uhr 0 = UTC, sonst monoton) und `random_get` (Hardwarezufall über RDSEED/RDRAND; fehlt er oder versagt er, liefert der Aufruf `EIO` (29) statt Erfolg, und der Puffer ist nicht zu verwenden. Apps müssen den Status prüfen und dürfen keinen eigenen Rückfall auf Uhr oder Konstanten bauen. In wasi-libc (wasi-sdk 25) meldet `getentropy` (höchstens 256 Byte je Aufruf) den Fehler als `EIO`; `arc4random`/`arc4random_buf` brechen dagegen mit einem Trap ab, und die .NET-WASI-Laufzeit nutzt voraussichtlich diesen Weg. Solche Apps starten ohne Hardwarezufall also nicht). |
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
  - Esc gibt bei eingefangener Maus den Zeiger frei und erreicht Doom; danach führt Esc zur Textshell. F4 wechselt jederzeit zwischen Desktop und Textshell. Doom bietet Backspace zusätzlich für sein Menü.
  - Bei Fokusverlust werden gehaltene Tasten losgelassen.
- **Pause:** Ist das Fenster nicht aktiv oder nicht sichtbar, blockiert `present` und `ticks_ms` steht still.
- **Fehler:** Ein Trap (ungültiger Speicherzugriff, falscher indirekter Aufruf) beendet nur die App; das Fenster zeigt die Ursache. Ein Fehler im Interpreter oder Host kann trotzdem den Kernel treffen. Das ist eine logische Grenze, keine Prozessisolation.
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
   python scripts/build-package.py package --key <privater-Schlüssel.pem> --repo <packages-Worktree> --id <id> --version <x.y.z> --name "<Name>" --entry <app.wasm> <datei>=<pfad> ...
   ```

4. Den Katalog mit **höherer Generation** neu signieren. Das Werkzeug lehnt eine gleiche oder kleinere ab.

   ```bash
   python scripts/build-package.py catalog --key <privater-Schlüssel.pem> --repo <packages-Worktree> --generation <n+1>
   ```

5. Den Arbeitsbranch committen, pushen und per Pull Request nach `packages` bringen; direkte Pushes auf `packages` lehnt GitHub ab. Nach dem Merge übernimmt der Server (`deploy/package-repo`) den Stand innerhalb von 5 Minuten und schaltet ihn als Ganzes um.
6. In ZehOS: `pkg update`, dann `pkg install <id>` oder `pkg upgrade`.

Den privaten Herausgeberschlüssel nie ins Repository, in Logs oder auf den Server legen. Geht er verloren, lassen sich keine Pakete mehr veröffentlichen. Ein neuer Schlüssel braucht ein Kernelupdate mit neuem `ProjektZeh/Packages/package-key.pub`.
