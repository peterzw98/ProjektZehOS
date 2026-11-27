# LinuxAbi: Vergleichsprogramme für den Linux-Systemaufruf-Satz

Kleine statische C-Programme (musl, x86-64), die nur deterministischen Text schreiben. Sie laufen auf Linux gegen gespeicherte Erwartungen
(`expected/<satz>/<name>.txt` und `<name>.status`) und später im ZehOS-Gast gegen dieselben Dateien (Plan:
[`docs/superpowers/plans/2026-11-24-ladybird-n1-nachweise.md`](../../docs/superpowers/plans/2026-11-24-ladybird-n1-nachweise.md), Gruppe E; Entwurf:
[`docs/superpowers/specs/2026-11-24-native-pakete-design.md`](../../docs/superpowers/specs/2026-11-24-native-pakete-design.md), Abschnitt 6). Der Vergleicher
(N1-E1) kam mit dem Satz S1; S3 (N1-E2) liegt als eigener Satz daneben; die Werkzeuge (toybox, Rust) folgen mit N1-E3, ohne den Vergleicher zu ändern.

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

### Alle Gastsätze mit einem Befehl

```sh
CC=<musl-gcc> python3 -I scripts/test-linux-all.py [--sets s1 s3 ...] [--configs kvm:1 kvm:4 tcg:1] [--work <ordner>]
```

Baut je Satz mit `build.sh`, packt das Bündel, baut den Kernel (`-warnaserror`, `-p:ZehOSLinuxRunProbe=true`) und fährt das ISO je Stufe
(Standard KVM smp1, KVM smp4, TCG smp1) gegen `expected/`; am Ende steht eine Tabelle Satz × Stufe, Exit 0 nur bei lauter PASS. Logs und ISOs liegen im
`--work`-Ordner (Standard `.tools/linux-all/<Zeit>`). Besonderheiten (spin aus `v3neg`, `v7b` nur mit Status, `v7bfull` mit Füllplatte nur KVM) stehen in
`SPECIAL` des Skripts. Nicht enthalten: Desktop-Terminal-Tests (`test-linux-stdin.py`, `test-linux-js.py`) und der Satz `tools`.

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

## Satz S3

Gemeinsame Hilfen stehen in `src/s3/util.h` (ungepufferte Ausgabe, `T(label, aufruf)` schreibt Rückgabewert und `errno`; jedes Programm setzt einen Wecker von 30 s,
ein Hänger endet als `signal 14`). Kindprozesse und Threads schreiben nichts, sie melden sich über Exitcode, geteilten Speicher oder Deskriptoren. Gezeigt werden nie PIDs,
Adressen, Ports oder Zeiten; zeitabhängige Aussagen sind nur Untergrenzen (mindestens 15 ms), Wartezeiten ohne Ereignis haben 10 s Obergrenze und werden nicht erreicht.

| Programm | prüft |
| --- | --- |
| `threads` | `clone` (musl-Threads), Mutex, Bedingungsvariable, `__thread` je Thread (inkl. 2000-Byte-Feld), `gettid`, geerbte Signalmaske, abgekoppelter Thread, `tgkill` |
| `futex` | `WAIT`/`WAKE` privat und geteilt (zwischen Prozessen), `EAGAIN`, Zeitüberschreitung relativ und absolut (`WAIT_BITSET`), Bitmaske 0, `CMP_REQUEUE`, mehrere Wartende |
| `fork_cow` | `fork`/`vfork`/`clone3`: Copy-on-Write privat, geteilt sichtbar, `wait4`/`waitpid` (Exit, Signal, `WNOHANG`, `ECHILD`, `rusage`), `getppid`, Fehlerwege von `clone3` |
| `exec` | `execve`: Argumente und Umgebung, ignoriertes Signal bleibt, behandeltes wird Standard, Maske bleibt, Deskriptoren (ohne/mit `CLOEXEC`), Fehlernummern; aus einem `fork`-Kind und im selben Prozess (Exitcode 33) |
| `pipe_epoll` | `pipe2`, `epoll` (Level, `EPOLLET`, `EPOLLONESHOT`, `MOD`/`DEL`, `HUP`), `poll`/`ppoll`, `eventfd2` (Zähler, Semaphor), `timerfd` (einmalig, periodisch), `EPIPE`, volle Pipe, Kind weckt `epoll_wait` |
| `unix_fdpass` | `socketpair` (STREAM, DGRAM, SEQPACKET), `SCM_RIGHTS` (ein/zwei Deskriptoren, `MSG_CMSG_CLOEXEC`, `MSG_CTRUNC`, an ein Kind), `SO_PEERCRED`, benannter Socket mit `accept4`, Fehlernummern |
| `memfd_seal` | `memfd_create` (Flags, Namen), `F_ADD_SEALS`/`F_GET_SEALS` für `SEAL`, `SHRINK`, `GROW`, `WRITE` und die Folgen für `write`, `ftruncate`, `mmap`, `mprotect`; Kind sieht die Siegel |
| `tcp_echo` | `AF_INET` nur auf 127.0.0.1: Echo (200006 Byte) zwischen Elternteil und Kind, `shutdown`/EOF, Sockoptionen, `EADDRINUSE`, `ECONNREFUSED`, `EADDRNOTAVAIL`, nichtblockierendes `connect` mit `poll`, UDP |
| `sigsegv_status` | `SIGSEGV`-Handler (`si_code`, `si_addr`), `sigaltstack`, `raise`/`kill`/`sigprocmask`/`sigpending`, `EINTR`, Kind stirbt an `SIGSEGV`/`SIGKILL`, zuletzt eigener Absturz: Status `signal 11`, Ausgabe davor vollständig |

Stand der Erwartungen: wie bei S1 mit `--update` auf Linux `6.12.90+deb13.1-amd64` erzeugt (musl 1.2.5). Auf demselben Kernel liefen 20 Läufe hintereinander (und fünf weitere
unter voller CPU-Last) ohne Abweichung; andere Kernelversionen sind nicht gegengeprüft. `memfd_seal`, `exec` und `tcp_echo` hängen an Linux-Verhalten (EBUSY bei offener
Schreibabbildung, `ENOEXEC` für leere ausführbare Dateien, `EADDRNOTAVAIL` für 192.0.2.1 ohne `ip_nonlocal_bind`), das die Gastimplementierung bewusst nachbilden muss.

Annahmen und bekannte Schwächen von S3:
- `pipe_epoll`: `EPOLLET` auf einer Pipe verhält sich in Linux 5.5 bis 5.13 anders (zweite Meldung ohne neue Daten); die Erwartung gilt für Kernel ab 5.14.
- `exec`: setzt voraus, dass das Arbeitsverzeichnis nicht `noexec` eingehängt ist und kein `binfmt_misc` eine leere ausführbare Datei abfängt (sonst weicht `ENOEXEC` ab).
- `tcp_echo`: braucht eine aktive Loopback-Schnittstelle. `connect ohne Server` nimmt den Port eines eben geschlossenen Sockets; eine Wiederverwendung durch einen fremden Prozess in dieser Zeit ist möglich, aber sehr unwahrscheinlich. `SO_ERROR=111` setzt voraus, dass das nichtblockierende `connect` zunächst `EINPROGRESS` meldet und den Fehler später liefert.
- `memfd_seal`: scheitert bei `vm.memfd_noexec=2` (dann ist `MFD_ALLOW_SEALING` ohne `MFD_NOEXEC_SEAL` verboten).
- Endet ein Programm durch den Wecker (`signal 14`), können Kinder verwaist zurückbleiben; `run-linux.py` beendet nach einer Zeitüberschreitung die Prozessgruppe, nach regulärem Ende aber nicht (nur erwähnt, nicht behoben).

### Aufrufe in S3

`strace -f -c` zeigt für die 9 Programme zusammen 69 verschiedene Aufrufe. Alle liegen in S1 bis S3 des Entwurfs bis auf 26, die als eigener Commit in die Zeile S3 nachgetragen sind
(Signale, Sockets, `getppid`, `ppoll`, `pipe`, `pwrite64`, `ftruncate`, `setitimer`; `alarm` in `util.h` läuft in musl über `setitimer`).

## Satz V3 (Ladybird-Vorstellung, Stufe a)

`src/v3`: `hello` (Text ausgeben, Ende mit 0) und die Gegenprobe `segv` (Schreiben auf eine nur lesbare Seite, `signal 11`), Erwartungen wie oben
mit `--update` auf Linux erzeugt. `src/v3neg/spin` dreht ohne Systemaufruf und hat keine Erwartung: Im Gast muss der Kernel es nach dem Zeitlimit
beenden (`signal 9`). Gastlauf: `scripts/test-linux-run.py` (Bündel bauen, ISO mit `-p:ZehOSLinuxRunProbe=true -p:ZehOSLinuxRunBundle=<bündel>`,
dann `run --expected experiments/LinuxAbi/expected/v3 --status "spin=signal 9" --programs hello segv spin`).

## Satz V7b (Dateisystem der Prozesse, nur Gast)

`src/v7b` prüft, was es so nur im ZehOS-Gast gibt, und hat deshalb **keine** Linux-Erwartung: Die Programme prüfen sich selbst (`src/v7b/check.h`), jede abweichende
Zeile endet auf ` FEHLER`, der Exitcode ist dann 1. Ein Gastlauf braucht nur `--status <name>=exit 0`.

| Programm | prüft |
| --- | --- |
| `v7bnames` | FAT findet Einträge auch über den 8.3-Kurznamen; über Linux trifft nur der eigene Name (`ABCDEF~1.TXT` `-ENOENT` für open, stat, lstat, access, readlink, unlink, chdir, rmdir, opendir), Anlegen, `mkdir`, `symlink`, `rename` auf den Kurznamen `-EEXIST`; die Liste zeigt keinen Kurznamen |
| `v7bopen` | Entfernte, offene Datei: `stat`/`open`/`unlink`/`rename` `-ENOENT`, nicht in der Liste, Anlegen und `mkdir` des Namens `-EEXIST` bis zum Schließen, `rmdir` des Ordners `-ENOTEMPTY`, Lesen und Schreiben über den Deskriptor; `rename` eines Ordners mit offenem oder entferntem, offenem Kind (auch Enkel) `-EBUSY`, Geschwister mit gleichem Präfix und die offene Datei selbst umbenennbar, nach dem Schließen alles frei (PR #741) |
| `v7bquota` | Kontingent je Wurzel (64 MiB Nettowachstum): `ftruncate` und Lücke darüber `-ENOSPC`, Füllen bis `-ENOSPC`, Freigabe durch `unlink` und `ftruncate` nach unten, Lücken lesen sich als Nullbytes, `pwrite` nach der Freigabe |
| `v7bfull` | Schreiben bis zum Ende mit `-ENOSPC` (nicht `-EIO`), Länge gleich dem Geschriebenen, nach dem Löschen wieder Platz. Mit `run-guest-full.py` ist die Platte voll, bevor das Kontingent greift |

```sh
python3 -I scripts/test-linux-run.py run --iso <iso> --expected experiments/LinuxAbi/expected/s1 \
    --status "v7bnames=exit 0" --status "v7bopen=exit 0" --status "v7bquota=exit 0" --status "v7bfull=exit 0" --programs ... v7bnames v7bopen v7bquota v7bfull --accel kvm
FILLER_MIB=478 python3 -I experiments/LinuxAbi/run-guest-full.py run --iso <iso> ...   # gleiche Argumente, Seed mit Fülldatei (504-MiB-Platte)
```

Grenzen: `v7bquota` setzt voraus, dass vorher im selben Gastlauf höchstens 24 MiB Wachstum stehen geblieben sind (es prüft „zwischen 40 und 64 MiB geschrieben“).
`v7bfull` mit Fülldatei: `write` am Ende liefert `-ENOSPC` (PR #741, im Gast belegt); `write` hinter einer Lücke liefert bei voller Platte noch `-EIO`
(bekannte Grenze, `docs/LINUX_ABI.md`), das Programm nimmt dort `-EIO` oder `-ENOSPC` an und meldet `-EIO` mit „bekannte Grenze“.
`rename` einer Datei auf ihren eigenen Kurznamen ist `-EEXIST` (vfat unter Linux benennt um), bekannte Abweichung aus PR #741.

## Satz V14 (Schreiben an fester Stelle, Dateilänge)

`src/v14/pio`: `pwrite64`, `preadv`/`pwritev` (Position bleibt, auch bei `O_APPEND`), `ftruncate` (kürzen, verlängern mit Nullbytes, Fehlerwege einschließlich
nur lesend offen, Verzeichnis, `/dev/null`, stdout), `fsync`/`fdatasync` (Datei, nur lesend offen, Verzeichnis, `/dev/null`, stdout). Erwartung mit `--update` auf
Linux `6.12.117+deb13-amd64` erzeugt (musl 1.2.5), drei weitere Läufe ohne Abweichung; `strace` war dabei nicht installiert (keine Aufrufliste). stdout ist unter
`run-linux.py` eine Pipe, im Gast das Terminal; beide liefern für `pwrite` `ESPIPE` und für `ftruncate`/`fsync` `EINVAL`.
