# Ladybird-Probe: Bau ohne JIT (L1-A)

Etappe L1 des [Ladybird-Plans](../../docs/superpowers/plans/2026-11-19-ladybird-l1-messung.md), Paket L1-A: Ladybird am festen Commit
`6fba5b78a1043fe93b3451204e04dcc0e3bd983a` auf einem Linux-Host ohne JIT bauen und den Aufwand messen (Bauzeit, Spitzen-RAM, Platz,
Binärgrößen). **Nur Messung, nichts portieren.** Nichts aus diesem Ordner gehört in den Kernel- oder Paketbau von ZehOS. Hintergrund:
[`docs/LADYBIRD_FEASIBILITY.md`](../../docs/LADYBIRD_FEASIBILITY.md).

Diese README beschreibt nur L1-A (`build.sh`). Andere Dateien in diesem Ordner gehören zu eigenen Paketen und PRs, etwa das Messskript aus L1-B
oder `syscalls.py` aus N1-C1.

## Voraussetzungen

- Linux x86_64, mindestens 32 GiB RAM und beim Start 220 GiB frei (200 GiB Grenze laut Plan L1-A plus 20 GiB Puffer); Netz für Git, vcpkg und Cargo.
- Normaler Benutzer, **nicht root** (Ladybirds `Meta/ladybird.py` lehnt root ab, `build.sh` ebenso).
- Werkzeuge laut `Documentation/BuildInstructionsLadybird.md` am festen Commit: CMake ≥ 3.30, Qt ≥ 6.10 (mit Positioning), nasm, ninja,
  gcc ≥ 14 oder clang ≥ 19 (Untergrenzen aus `Meta/Utils/find_compiler.py`), Python ≥ 3.7, rustup mit der Toolchain aus
  `rust-toolchain.toml` (`1.98.0`), dazu GNU time für die Speichermessung.
- Hostwerkzeuge der vcpkg-Ports (am vcpkg-Stand `8f363bdc` aus `vcpkg.json` gelesen): autoconf, automake, libtool mit libltdl und
  **autoconf-archive** für die Ports mit `vcpkg_make_configure(AUTORECONF)`, etwa gperf, libedit, icu, libffi, ncurses
  (`ports/vcpkg-make/vcpkg_make.cmake`); make, pkg-config, nasm, Python; Perl mit IPC::Cmd für OpenSSL
  (`ports/openssl/unix/portfile.cmake`). `build.sh --check` prüft die Autotools-Makros genauso wie vcpkg: `aclocal --dry-run` über
  dessen `configure.ac` (fehlen `AX_CHECK_COMPILE_FLAG`, `LT_INIT` oder `LTDL_INIT`, scheitert sonst erst der Port gperf).

`build.sh` **installiert nichts**. Die Pakete installiert der Mensch einmal von Hand.

### Manjaro / Arch: einmaliger Schritt mit root

Paketliste aus der Ladybird-Bauanleitung (Abschnitt „Arch Linux/Manjaro“), ergänzt um `time` (GNU time unter `/usr/bin/time`) und `rustup`:

```bash
sudo pacman -S --needed autoconf-archive base-devel ccache cmake curl git less libgl libpulse nasm ninja python \
    qt6-base qt6-positioning qt6-tools ttf-liberation tar unzip wayland xcb-util-wm zip time rustup
```

`rustup` ersetzt ein installiertes Paket `rust`; pacman fragt dann nach. `ccache` steht in der Upstream-Liste, `build.sh` schaltet es
aber ab (`LADYBIRD_ENABLE_CCACHE=OFF`, `CCACHE_DISABLE=1`), damit die Bauzeit einen kalten Bau misst. Optional: `glslang`
(`glslangValidator`); ohne ihn baut die Qt-Oberfläche ohne ihren Vulkan-Anzeigeweg, für die Messung ohne Belang. autoconf, automake,
libtool (mit libltdl), make, pkgconf und perl kommen unter Arch über `base-devel` bzw. als dessen Abhängigkeiten; `autoconf-archive` ist
ein eigenes Paket und fehlt oft.

### Als normaler Benutzer

```bash
rustup toolchain install 1.98.0 --profile minimal -c rustfmt,clippy
qmake6 -query QT_VERSION        # muss 6.10 oder neuer sein
```

Ist das Qt der Distribution älter als 6.10, ein neueres Qt installieren und über `CMAKE_PREFIX_PATH` angeben (siehe Ladybird-Bauanleitung);
`build.sh` prüft dann trotzdem die Version über `qmake6`, `qtpaths6` oder `pkg-config Qt6Core`.

## Ablauf

Aus dem Repository-Stamm:

```bash
experiments/Ladybird.Probe/build.sh --check                # nur Voraussetzungen prüfen
DRY_RUN=1 experiments/Ladybird.Probe/build.sh              # Prüfung und geplante Schritte, nichts klonen oder bauen
tmux new -s ladybird 'experiments/Ladybird.Probe/build.sh'   # Messung: --workdir ~/ladybird-probe, --jobs = logische Kerne
```

Der Bau dauert Stunden; deshalb in `tmux`/`screen` oder mit `nohup … &` starten. SIGHUP (Terminal geschlossen), SIGINT und SIGTERM beenden
den laufenden Schritt (TERM, nach 30 s KILL) und schreiben `build-stats.json` mit `"reason": "signal"`, Exit 3; nach SIGHUP stehen die
Meldungen des Skripts in `logs/build.sh.log`. Sobald das Ergebnis feststeht (Bau fertig, Abbruch oder erstes Signal), ignoriert das
Skript weitere Signale: Das Zählen und Schreiben von `build-stats.json` (`du` über den Arbeitsordner, einige Minuten) lässt sich dann
nicht mehr abbrechen, und ein Signal nach erfolgreichem Bau ändert den Status `ok` nicht.

Optionen: `--jobs N`, `--workdir DIR` (muss leer sein oder fehlen), `--resume` (nach Unterbrechung weiterbauen, nur in einem von
`build.sh` angelegten Arbeitsordner mit `.ladybird-probe-workdir`; die Zeiten gelten dann nicht als kalter Bau, `build-stats.json`
vermerkt `"resumed": true`). Compiler wählt das Skript selbst (gcc vor clang); `CC` und `CXX`
zusammen gesetzt haben Vorrang.

Schritte, jeder in eigener Prozessgruppe unter `/usr/bin/time -v`:

1. **fetch:** `git fetch --depth 1` genau des festen Commits nach `$WORKDIR/src`, danach Prüfung `git rev-parse HEAD` (Exit 5 bei Abweichung).
   Zusätzlich prüft das Skript, dass `rust-toolchain.toml` den erwarteten Kanal nennt. Weil cargo und rustc im Bauordner außerhalb von
   `src` laufen und rustup die Datei dort nicht fände, setzt das Skript `RUSTUP_TOOLCHAIN=1.98.0`.
2. **deps:** `Meta/ladybird.py deps --preset Release`: baut vcpkg und alle Abhängigkeiten (Skia, ffmpeg, ICU, ANGLE …). Weil Ladybird den
   Release-Bau unter `src/Build/release` erwartet (`Meta/CMake/presets/CMakeBasePresets.json`), zeigt dort ein Verweis auf `$WORKDIR/build`.
3. **configure:** `cmake --preset Release -S src -B build` mit denselben vcpkg-Argumenten wie `Meta/ladybird.py`, dazu
   `-DENABLE_JS_JIT=OFF`, `-DLAGOM_LINK_POOL_SIZE=2`, `-DLADYBIRD_ENABLE_CCACHE=OFF`. `ENABLE_GUI_TARGETS` bleibt an (ohne sie gibt es keine
   Dienste). Danach prüft das Skript `ENABLE_JS_JIT:BOOL=OFF` im CMake-Cache.
4. **build:** `ninja -C build ladybird` (Ziel `ladybird` mit Ausgabename `Ladybird`; es hängt von allen Hilfsprozessen ab:
   WebContent, RequestServer, ImageDecoder usw. landen in `build/libexec/`). Tests und Werkzeuge werden nicht gebaut.
5. **Ergebnis:** Exit 0 nur, wenn `build/bin/Ladybird` existiert; dann und bei jedem Abbruch schreibt das Skript `build-stats.json`.

Fortschritt: `tail -f ~/ladybird-probe/logs/build.log` (bzw. `deps.log`).

## Abbruchregeln

Ein Wächter prüft alle 15 Sekunden:

| Regel | Grenze | Folge |
| --- | --- | --- |
| Zeit | Gesamtlauf ab Start (fetch bis build) über 8 h | laufender Schritt wird beendet, Exit 3, `"reason": "time"` |
| Platz | Arbeitsordner über 200 GiB (`du -sb`, etwa jede Minute, je Zählung höchstens 120 s; danach gilt der letzte Wert) | Exit 3, `"reason": "disk"` |
| Restplatz | weniger als 10 GiB frei auf dem Dateisystem (`df`), auch wenn andere Programme schreiben | Exit 3, `"reason": "disk_full"` |
| OOM | Bau scheitert mit OOM: Zähler `oom_kill` der cgroup steigt, oder das Log zeigt Signal 9/Exit 137 (auch `collect2: fatal error: ld terminated with signal 9 [Killed]` beim Linken) oder die OOM-Meldung von gcc bzw. LLVM | einmal `LAGOM_LINK_POOL_SIZE=1`, neu konfigurieren, weiterbauen; zweites OOM: Exit 3, `"reason": "oom"` |

OOM schon bei den Abhängigkeiten bricht ohne zweiten Versuch ab. **Nach einem Abbruch kein weiterer Versuch ohne Rückfrage** (Plan L1-A);
Stand und die bis dahin gemessenen Zahlen melden. Andere Exit-Codes: 1 Aufruf- oder Umgebungsfehler, 2 Voraussetzungen fehlen,
4 Bau aus anderem Grund fehlgeschlagen (`"reason"` nennt den Schritt, `unexpected_exit_<code>` bei unerwartetem Skriptende), 5 falscher
Commit. Ab dem Start des Laufs schreibt das Skript `build-stats.json` in jedem dieser Fälle.

## Ergebnisse

Alles liegt außerhalb des Repositorys und wird nie committet:

| Pfad (Standard `~/ladybird-probe`) | Inhalt |
| --- | --- |
| `build-stats.json` | Messwerte, auch bei Abbruch |
| `logs/{fetch,deps,configure,build}.log` | vollständige Ausgaben; nach OOM zusätzlich `configure-retry.log`, `build-retry.log`; nach SIGHUP `build.sh.log` |
| `src/`, `build/` | Quelle mit vcpkg und Binärcache unter `src/Build/`, Bauordner |

Felder in `build-stats.json` (Plan: `commit`, `seconds`, `peak_rss_kib`, `disk_bytes`, `binaries`; dazu):

- `status` (`ok`, `aborted`, `failed`), `reason`, `phase` (letzter Schritt), `commit_ok`.
- `seconds` gesamt, `phase_seconds` je Schritt.
- `peak_rss_kib`: größter Wert von „Maximum resident set size“ aus `/usr/bin/time -v` über alle Schritte. Das ist der **größte einzelne
  Prozess** (etwa ein Linker), nicht die Summe paralleler Jobs. Dafür steht `peak_system_used_kib` (Spitze von `MemTotal − MemAvailable`,
  alle 15 s abgetastet, enthält alles, was sonst auf dem Rechner läuft). Beide Werte sind Abtastungen bzw. Prozessspitzen: kurze
  Spitzen zwischen zwei Proben fehlen in `peak_system_used_kib`.
- `disk_bytes`: `du -sb` über `src` (mit vcpkg-Werkzeug und Binärcache) und `build`, aufgeschlüsselt in `disk_bytes_detail`;
  `peak_workdir_bytes` ist die größte Abtastung von `du -sb` über den Arbeitsordner während des Laufs, `peak_disk_growth_bytes` der
  Spitzenzuwachs belegten Platzes auf dem ganzen Dateisystem laut `df` (enthält fremde Schreibvorgänge).
- `binaries`: Größe jeder Datei in `build/bin`, `build/lib`, `build/libexec` (Verweise auf `.so` nicht doppelt), Summe in `binaries_total_bytes`.
- `host`: CPU, logische Kerne, RAM, Distribution, Kernel, Compiler, CMake, Ninja, Qt, rustc (unter `RUSTUP_TOOLCHAIN`), Python; `probe_script_head` ist der
  ZehOS-Commit des Skripts; `cmake_args`, `jobs`, `link_pool_size`, `build_attempts`, `limits`.

Für den Bericht im PR: `build-stats.json` vollständig, die letzten 50 Zeilen des Logs des letzten Schritts, bei Abbruch zusätzlich die Zeile
aus der Ausgabe von `build.sh`.

## Erwartungen und Grenzen

- **HYPOTHESE (nicht gemessen):** auf 16 Kernen/22 Threads und 62 GiB eher Stunden als Minuten; die Abhängigkeiten (Skia, ffmpeg, ICU, Qt
  kommt vom System) brauchen einen großen Teil davon. Zahlen liefert erst der Lauf.
- Nicht im Arbeitsordner und deshalb nicht in `disk_bytes`: die Rust-Toolchain unter `~/.rustup` und Cargo-Downloads unter `~/.cargo`.
- Die Zeitmessung ist nur aussagekräftig auf einem sonst ruhigen Rechner und ohne `--resume`.
- Die `LADYBIRD_PROBE_*`-Umgebungsvariablen im Skript sind nur zum Testen des Skripts selbst; geänderte Grenzen stehen in `limits` und
  machen den Lauf ungültig für die Abnahme.
- Geprüft ist das Skript bisher nur ohne echten Bau (Syntax, shellcheck, Trockenlauf, Ablauf mit Attrappen); der eigentliche Bau steht aus.
