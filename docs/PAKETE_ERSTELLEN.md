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

Wiederholte Tastendrücke kommen erneut als Typ 1, Text zusätzlich als Typ 3. Pfeile verwenden 72/75/77/80,
Entf 83, AltGr 96. Ein E0-Merkmal steht nicht zur Verfügung; Ziffernblock und Navigation können denselben
Tastencode haben. Texteingabe deshalb aus Typ 3 lesen. Bei Fokusverlust werden wartende Ereignisse und
angefangene Surrogatpaare verworfen und durch Reset ersetzt. Der Puffer hält 128 Ereignisse;
bei Überlauf bleiben Reset und der neueste Input. Aufeinanderfolgende Bewegungen werden zusammengefasst.

Koordinaten berücksichtigen Bildskalierung und Zentrierung. Druck und Rad in schwarzen Rändern werden
ignoriert, Bewegung und Loslassen am Bildrand begrenzt. Ein begonnenes Ziehen behält alle Maustasten auch
außerhalb des Fensters bis zum letzten Loslassen oder Reset. Das Bild hat keinen Resize-Import;
die App bestimmt seine Größe über `present` und muss eine passende feste Größe wählen.

**Netzwerk über `zehos_v1`:**

Nur ein signiertes Manifest mit `capability=net` erlaubt TCP-Verbindungen; `scripts/build-package.py package --network` setzt die Zeile. Die Berechtigung umfasst auch localhost und private LAN-Adressen. Es gibt höchstens vier Verbindungen und 64 KiB je Sende- und Empfangspuffer. IPv4-Adressen und ASCII-DNS-Namen sind möglich; TLS ist noch nicht über diese Schnittstelle verfügbar.

Alle Funktionen liefern einen WASI-Preview-1-Fehlercode (`0` bei Erfolg). Gastpuffer und Ausgabezeiger werden vor dem Hostzugriff geprüft; ungültige Bereiche verursachen einen Trap. Ausgabezahlen sind 32-Bit-Werte im Little-Endian-Format und dürfen unausgerichtet sein.

| Import | Verhalten |
| --- | --- |
| `net_open(host, len, port, flags, out_handle)` | Startet DNS und TCP asynchron; `flags` muss 0 sein. Das Handle gehört zur App-Sitzung. Ohne Berechtigung `EACCES` (2). |
| `net_status(handle)` | `EAGAIN` (6) während DNS/Connect, danach 0 oder ein Fehler; der Verbindungsaufbau hat ein Zeitlimit. |
| `net_send(handle, ptr, len, out_n)` | Übernimmt bis zu `len` Bytes in den begrenzten Sendepuffer; `out_n` meldet die übernommene Anzahl. Bei vollem Puffer `EAGAIN`. Ein erfolgreicher Aufruf bestätigt nicht die Zustellung. |
| `net_recv(handle, ptr, len, out_n)` | Liefert vorhandene Daten; bei leerem Puffer `EAGAIN`. Erfolg und `out_n=0` bei nichtleerem Zielpuffer bedeuten EOF. |
| `net_close(handle)` | Gibt das Handle frei und veranlasst das Schließen der Verbindung. Offene Handles werden beim Ende der Wasm-Sitzung ebenfalls geschlossen. |

Einzelne Übertragungen sind auf 64 KiB begrenzt. Häufige Fehler sind `EBADF` (8), `EINVAL` (28), `EIO` (29), `EMFILE` (33), `ENOENT` (44, DNS-Auflösung fehlgeschlagen), `ENOTSUP` (58) und `ETIMEDOUT` (73). Für Datenverkehr nach dem Verbindungsaufbau muss die Anwendung selbst eine geeignete Frist setzen. Cosmos-interne Connect- und Sendewartephasen sind nicht sofort abbrechbar; währenddessen können andere Netzwerkaufträge warten, die Oberfläche bleibt durch den Netzwerkworker bedienbar. Ein gleichzeitig mit `net_close` beginnender Connect kann das Ziel noch erreichen; der Worker schließt den Socket nach Rückkehr des Cosmos-Aufrufs. Das ABI kennt außerdem `ECONNREFUSED` (14), `ECONNRESET` (15) und `EHOSTUNREACH` (23). Cosmos 3.0.94 unterscheidet diese Fälle nicht: Ein abgelehnter oder unerreichbarer Verbindungsaufbau meldet `EIO`, ein Abbruch durch RST erscheint wie ein normales Verbindungsende (`net_recv` liefert 0 Bytes). Anwendungen sollten die drei Werte trotzdem behandeln.

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
  - Esc ist für die Textshell reserviert, F4 für den Wechsel zwischen Desktop und Textshell. Doom legt sein Menü deshalb auf Backspace.
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
