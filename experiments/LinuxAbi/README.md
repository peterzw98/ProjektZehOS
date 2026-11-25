# LinuxAbi: Vergleichsprogramme für den Linux-Systemaufruf-Satz

Kleine statische C-Programme (musl, x86-64), die nur deterministischen Text schreiben. Sie laufen auf Linux gegen gespeicherte Erwartungen
(`expected/<satz>/<name>.txt` und `<name>.status`) und später im ZehOS-Gast gegen dieselben Dateien (Plan:
[`docs/superpowers/plans/2026-11-24-ladybird-n1-nachweise.md`](../../docs/superpowers/plans/2026-11-24-ladybird-n1-nachweise.md), Gruppe E; Entwurf:
[`docs/superpowers/specs/2026-11-24-native-pakete-design.md`](../../docs/superpowers/specs/2026-11-24-native-pakete-design.md), Abschnitt 6). Dieses Paket
(N1-E1) enthält den Vergleicher und den Satz S1; S3 und die Werkzeuge (toybox, Rust) kommen mit N1-E2 und N1-E3, ohne diese Dateien zu ändern.

## Benutzung

```sh
sudo apt-get install musl-tools strace     # Debian/Ubuntu; $CC überschreibt musl-gcc
experiments/LinuxAbi/build.sh              # baut src/<satz>/*.c nach out/<satz>/ (statisch, musl); ruft src/<satz>/build.sh, falls vorhanden
python3 experiments/LinuxAbi/run-linux.py --bin experiments/LinuxAbi/out/s1 --expected experiments/LinuxAbi/expected/s1 --json /tmp/s1.json
python3 -m unittest discover -s experiments/LinuxAbi      # Tests des Vergleichers (ohne musl und strace)
```

`run-linux.py` startet jedes Programm in `--bin` mit den Argumenten `a1`, `b 2`, der Umgebung `ZEHOS_ABI=1 LANG=C PATH=/usr/bin:/bin`, leerem stdin,
`umask 022` und einem frischen leeren Arbeitsverzeichnis. Verglichen werden stdout und stderr (zusammen, Bytes) und der Status (`exit <n>` oder
`signal <n>`). Danach läuft jedes Programm ein zweites Mal unter `strace -f -c`; der JSON-Bericht (`--json`) nennt je Programm die Aufrufe
und insgesamt ihre Anzahl, ohne dass sie in den Vergleich eingehen. Exit 0: alles gleich. Exit 1: Abweichung, auch bei fehlender Erwartung, fehlendem
Programm oder Zeitüberschreitung (60 s). Exit 2: falsche Benutzung. `--update` schreibt die Erwartungen aus einem echten Lauf neu und ist nur auf Linux
erlaubt; Erwartungen entstehen nie von Hand. Binärdateien (`out/`) werden nicht eingecheckt.

Im Gast kommt dieselbe Ausgabe über denselben Aufruf (Argumente und Umgebung sind fest) zustande; `run-linux.py` selbst läuft nur auf Linux.

## Satz S1

| Programm | prüft |
| --- | --- |
| `args_env_auxv` | `argc`/`argv`/`envp`, Hilfsvektor (`AT_PAGESZ PHENT PHNUM PHDR ENTRY BASE RANDOM UID EUID GID EGID SECURE EXECFN HWCAP`), ohne Werte, die vom Benutzer oder Lader abhängen |
| `files` | `openat`/`open` (`O_EXCL`, `O_NOFOLLOW`), `read write readv writev lseek fstat stat lstat access rename unlink readlink symlink`, Lücke hinter dem Dateiende, Fehlernummern |
| `dirs` | `mkdir rmdir getdents64 getcwd chdir rename unlink`, Fehlernummern (`EEXIST ENOENT ENOTEMPTY ENOTDIR EISDIR`) |
| `mmap_anon` | `mmap` anonym, `MAP_FIXED`, `munmap` (Teile), `mprotect`, `madvise(DONTNEED)` (Nullung), `MAP_NORESERVE`, Fehlerwege |
| `mprotect_fault` | Schreiben auf eine schreibgeschützte Seite: Status `signal 11`, Ausgabe davor vollständig |
| `brk` | Wachsen, Schrumpfen, Nullung beim erneuten Wachsen, Ablehnung unter dem Start und über dem Benutzerraum |
| `clock` | `clock_gettime` (monoton, `CLOCK_REALTIME` nach 2020), `nanosleep` (mindestens 20 ms), Fehlerwege |
| `random` | `getrandom`: zwei Aufrufe verschieden, Flags, Fehlerwege (nie die Werte) |
| `long_double` | x87 80 Bit (`long double`, Exponent bis 4932), SSE, FPU-Zustand über einen Systemaufruf hinweg |
| `tls` | `__thread` (`.tdata`/`.tbss`, 3000-Byte-Feld), FS-Basis über `arch_prctl(ARCH_GET_FS)` und `%fs:0` |
| `badptr` | ungültige, Kernel- und nichtkanonische Zeiger an 14 Aufrufe (inkl. symlink für readlink): `EFAULT` (14), kein Absturz; `mprotect` auf Ungemapptes: `ENOMEM` |
| `unknown_syscall` | unbekannte Nummern (und `-1`): `ENOSYS` (38), der Prozess läuft weiter |

Stand der Erwartungen: erzeugt mit `--update` auf Linux `6.12.90+deb13.1-amd64` (Debian 13, x86-64, musl 1.2.5, musl-gcc von Debian). Auf demselben
Kernel liefen mehrere Läufe hintereinander (`run-linux.py` ohne `--update`) ohne Abweichung; auf anderen Linux-Versionen sind die Erwartungen bisher
nicht gegengeprüft.

### Aufrufe in S1

`strace -f -c` zeigt für die 12 Programme zusammen 39 verschiedene Aufrufe (`execve` stammt vom Start durch `strace`; `exit_group` führt `strace -c` nicht
auf). Alle liegen im Satz S1 des Entwurfs bis auf vier, die als eigener Commit in den Entwurf nachgetragen sind: `symlink` (`files`, nötig für
`lstat`/`readlink`), `chdir`, `rmdir` (beide `dirs`) und `fcntl` (musl-`opendir`, steht dort bisher erst in S2).
