# Apps für ZehOS bauen und als Paket veröffentlichen

Stand: 15. November 2026. Diese Anleitung beschreibt, was eine App für den ZehOS-Wasm-Loader können darf und wie sie zum signierten Paket wird. Formatdetails stehen in [PACKAGE_FORMAT.md](PACKAGE_FORMAT.md), Messwerte und Hintergründe in [WASM_FEASIBILITY.md](WASM_FEASIBILITY.md). Geprüft ist bisher nur Doom (C, doomgeneric) unter QEMU; die C#-Probe lief nur als Konsolenprogramm in der Gastprobe, nicht als Paket.

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
| `poll_key()` | -1 ohne Ereignis, sonst Bit 8 = gedrückt, Bits 0–7 = PS/2-Set-1-Scancode |
| `should_exit()` | 1, wenn das Fenster geschlossen wird; die App muss dann zügig enden |

**WASI Preview 1:**

| Bereich | Stand |
| --- | --- |
| Konsole | `fd_write` nur auf stdout/stderr. stderr wird gesammelt; die letzte Zeile erscheint bei Exitcode ≠ 0 im Fenster. |
| Argumente, Umgebung | `argv` ist nur `["app"]`, die Umgebung ist leer. |
| Uhren, Zufall | `clock_time_get` (Uhr 0 = UTC, sonst monoton) und `random_get` (Hardwarezufall; ohne ihn Nullen, nicht für Geheimnisse geeignet). |
| Dateien | Nur lesend. Das eigene Paketverzeichnis `files/` ist als `/` freigegeben. Pfade relativ ohne `..`. `path_open`, `fd_read`, `fd_pread`, `fd_seek`, `fd_tell`, `fd_filestat_get`, `fd_close`; höchstens 16 offene Dateien. |
| Nicht vorhanden | Schreiben, Anlegen, Löschen, Verzeichnisse lesen, `poll_oneoff` (also kein `sleep`/`nanosleep` der libc; `zehos_v1.sleep_ms` verwenden), Sockets und Netzwerk, Threads. Diese Aufrufe liefern `ENOSYS` oder `EBADF`. |

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
- **Dauer unter QEMU TCG:** Jede Datei wird beim Installieren und vor jedem Start per SHA-256 geprüft. 28 MB brauchen beim Installieren rund 2 Minuten, beim Start bis zu etwa 30 s.

## Veröffentlichen

1. App bauen (z. B. `python experiments/Wasm.DoomProbe/build.py --source <doomgeneric>`).
2. Lizenztexte aller Bestandteile als Paketdateien beilegen. GPL-Code verlangt zusätzlich, dass die Quellen öffentlich erreichbar sind.
3. Im Worktree des Branches `packages` eine **neue** Version bauen. Veröffentlichte Versionen sind unveränderlich; jede Änderung braucht eine höhere Version.

   ```bash
   python scripts/build-package.py package --key <privater-Schlüssel.pem> --repo <packages-Worktree> --id <id> --version <x.y.z> --name "<Name>" --entry <app.wasm> <datei>=<pfad> ...
   ```

4. Den Katalog mit **höherer Generation** neu signieren. Das Werkzeug lehnt eine gleiche oder kleinere ab.

   ```bash
   python scripts/build-package.py catalog --key <privater-Schlüssel.pem> --repo <packages-Worktree> --generation <n+1>
   ```

5. Im Branch `packages` committen und pushen. Der Server (`deploy/package-repo`) übernimmt den Stand innerhalb von 5 Minuten und schaltet ihn als Ganzes um.
6. In ZehOS: `pkg update`, dann `pkg install <id>` oder `pkg upgrade`.

Den privaten Herausgeberschlüssel nie ins Repository, in Logs oder auf den Server legen. Geht er verloren, lassen sich keine Pakete mehr veröffentlichen. Ein neuer Schlüssel braucht ein Kernelupdate mit neuem `ProjektZeh/Packages/package-key.pub`.
