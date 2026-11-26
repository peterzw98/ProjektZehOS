# Linux-ABI (x86-64) für Benutzerprogramme

Stand: Stufe S1 (V2 des Plans `docs/superpowers/plans/2026-11-26-ladybird-vorstellung.md`). Code: `ProjektZeh/Processes/Syscalls/`,
Hosttests: `ProjektZeh.Tests/Processes/LinuxSyscallsTests.cs`.

## Aufbau

- `LinuxSyscalls.Dispatch(ref SyscallArgs, ILinuxProcess) -> long`: Nummer in `Number`, Argumente `A0..A5` (rdi, rsi, rdx, r10, r8, r9).
  Rückgabe ist das Ergebnis oder `-errno`; unbekannte Nummern ergeben `-ENOSYS`.
- `IUserMemory`: jeder Zugriff auf Benutzerspeicher geht hierüber (`TryRead`, `TryWrite`, `TryReadCString`); die Implementierung
  prüft Zeiger und Länge überlaufsicher (Hilfe: `UserRange.Contains`) und liefert bei Fehler `false` (Verteiler: `-EFAULT`).
- `ILinuxProcess`: Speicher, Deskriptoren (`ILinuxDescriptor.Write`), `Brk`, `MapAnonymous`/`Unmap`/`Protect`, `FsBase`, `ClearChildTid`, `Exit`.
  Die Brücke zu echten Prozessen (V3) implementiert sie. Abweichung vom Plan: Deskriptoren schreiben Bytes (`ILinuxDescriptor`), weil
  `ITextOutput` nur Zeilen kennt.

## Aufrufe S1

| Nr | Name | Verhalten |
| --- | --- | --- |
| 0 | `read` | nur Eingaben (`ILinuxInput`, fd 0 des Terminals): wartet wie an einer Pipe auf wenigstens ein Byte, höchstens 4 KiB je Aufruf; 0 einmal je Strg+D; `-EINTR` nach Strg+C/Esc; Puffer wird vor dem Warten geprüft (`-EFAULT`); andere fd gehen an die Dateiaufrufe (V2 unten) |
| 19 | `readv` | wie `read`, ein Lesen verteilt auf die Einträge (musl liest stdin so); höchstens 1024 Einträge, vorab gelesen |
| 1 | `write` | Stückweise Kopie (4 KiB); Länge auf 0x7ffff000 begrenzt; `-EBADF`, `-EFAULT`; ein Fehler nach erfolgreichen Stücken liefert die bisherige Anzahl |
| 20 | `writev` | höchstens 1024 Einträge, Summe über 0x7ffff000 ergibt `-EINVAL` (Abweichung: Linux kürzt stattdessen), Einträge werden vorab gelesen |
| 60, 231 | `exit`, `exit_group` | `ILinuxProcess.Exit(code & 0xff)` |
| 12 | `brk` | wie Linux: bei Fehler der alte Wert, nie ein Fehlercode |
| 9 | `mmap` | nur `MAP_ANONYMOUS` mit genau einem von `MAP_PRIVATE`/`MAP_SHARED`; Länge wird auf Seiten aufgerundet; unbekannte `prot`-Bits `-EINVAL`; `MAP_FIXED` `-ENOSYS` (Abweichung: Linux liefert je nach Fall `-EINVAL`/`-ENOMEM`); Datei-fd `-ENODEV` (fd < 0 ohne ANONYMOUS `-EBADF`); `-EINVAL`, `-ENOMEM` |
| 11, 10 | `munmap`, `mprotect` | Länge wird auf Seiten aufgerundet; `-EINVAL` bei unausgerichteter Adresse, Länge oder Adresse über 2^47, unbekannten `prot`-Bits, `munmap` mit Länge 0; `mprotect` mit Länge 0 liefert 0; `mprotect` ohne Abdeckung `-ENOMEM` |
| 158 | `arch_prctl` | `ARCH_SET_FS` (nur Adressen der Benutzerhälfte, sonst `-EPERM`), `ARCH_GET_FS`; sonst `-EINVAL` |
| 218 | `set_tid_address` | merkt die Adresse, liefert die Thread-ID |
| 39, 186 | `getpid`, `gettid` | Prozess-ID |
| 273 | `set_robust_list` | `0` (ohne Wirkung) |
| 16 | `ioctl` | `-ENOTTY` für gültige fd, sonst `-EBADF` (musl fragt damit nach dem Terminal) |
| 228 | `clock_gettime` | Uhren 0-7; 0 und 5 Wanduhr, übrige monoton (CPU-Zeituhren 2, 3 ebenfalls monoton: Abweichung); Uhren 8, 9, 11 (BOOTTIME-Familie) `-EINVAL` (Abweichung); sonst `-EINVAL`. Quelle `ILinuxHost` (optional), sonst DateTime/Stopwatch |
| 318 | `getrandom` | Flags nur GRND_NONBLOCK/RANDOM/INSECURE (RANDOM+INSECURE `-EINVAL`); füllt in 4-KiB-Stücken aus `ILinuxHost.TryFillRandom`; ohne Host `-ENOSYS` (nie schwacher Zufall), Quelle nicht bereit `-EAGAIN` |
| 63 | `uname` | Linux / zehos / 6.1.0-zehos / x86_64 |
| 13, 14 | `rt_sigaction`, `rt_sigprocmask` | nur Verwaltung je Prozess (keine Zustellung); sigsetsize 8, SIGKILL/SIGSTOP nicht abfangbar und nicht blockierbar |
| 131 | `sigaltstack` | setzen/abfragen/abschalten; Größe < 2048 `-ENOMEM`; ohne Zustellung wirkungslos; SS_AUTODISARM wird nicht gemerkt (Abweichung) |
| 24 | `sched_yield` | `0` |
| 7 | `poll` | höchstens 1024 Einträge; offene fd melden POLLOUT, POLLIN nur eine Eingabe mit Bytes, Eingabeende oder Abbruch (sonst nie POLLIN), unbekannte fd POLLNVAL; wartet ein Eintrag auf Eingabe, wartet poll darauf in 50-ms-Scheiben (auch mit Frist -1, Abbruch beendet); ohne bereite Einträge und mit Frist > 0 schläft sie die Frist über `ILinuxHost.SleepNanos` ab (ohne Host nicht); Frist -1 (unendlich) schläft nie, Ergebnis 0 |
| 28 | `madvise` | Hinweise 0-3, 14, 15 liefern 0; DONTNEED/FREE und Unbekanntes `-EINVAL` (Seiten nullen geht noch nicht); prüft keine Abbildung (Linux `-ENOMEM`) |
| 202 | `futex` | Einzelthread: WAIT/WAIT_BITSET mit abweichendem Wert `-EAGAIN`, mit Frist `-ETIMEDOUT` (ohne zu schlafen), ohne Frist `-EDEADLK` (Linux hängt; glibc bricht bei diesem unbekannten Fehler ab, musl dreht in einer Schleife); Wert wird vor der Frist gelesen: kaputte Frist bei abweichendem Wert liefert `-EAGAIN` statt `-EFAULT`/`-EINVAL` (Abweichung); WAKE* 0; CMP_REQUEUE prüft den Wert; WAKE_OP `-ENOSYS` |
| 334 | `rseq` | `-ENOSYS` (Laufzeitbibliotheken fallen zurück) |

Grenze: `write` verlässt sich auf `ILinuxDescriptor.Write` ≤ angebotene Länge (größere Rückgaben werden nicht abgefangen); je Aufruf ein 4-KiB-Puffer.

Nicht in S1: Dateien (siehe V2 unten), Signalzustellung, Threads (`clone`, echtes `futex`-Warten), `MAP_FIXED`, Dateiabbildungen.

## Dateien (V2)

Nur aktiv, wenn der Prozess `ILinuxFiles` anbietet (Dateisystem, Arbeitsverzeichnis, Deskriptortabelle); sonst liefern alle Aufrufe dieses Abschnitts `-ENOSYS`.
Verteiler: `LinuxFileSyscalls.cs` (Inhalt und Deskriptoren) und `LinuxFileMetaSyscalls.cs` (Metadaten und Verzeichnisbaum), Dateisystem hinter `ILinuxFileSystem`.

Pfade (`LinuxPath.Resolve`, für alle Aufrufe gleich): relativ zu cwd oder zum Verzeichnis-fd (`*at`, nur bei relativem Pfad; ungültiger fd `-EBADF`, kein Verzeichnis
`-ENOTDIR`); leerer Pfad `-ENOENT`; ohne NUL in 4096 Bytes `-ENAMETOOLONG`, sonst nicht lesbar `-EFAULT`. Leere Teile und `.` fallen weg, `..` wird **textuell**
aufgelöst, bevor das Dateisystem Symlinks sieht (Abweichung: `link/..` ist der Ordner, in dem der Link liegt, nicht der Elternordner des Ziels); `..` an der Wurzel
bleibt die Wurzel. Ein abschließendes `/` (auch aus `.`/`..` am Ende) bleibt erhalten und verlangt ein Verzeichnis.

| Nr | Name | Verhalten |
| --- | --- | --- |
| 2, 257 | `open`, `openat` | Flags gehen unverändert ans Dateisystem, `mode & 07777`; kleinste freie Nummer; Tabelle voll `-EMFILE` (die Datei wird dann gleich geschlossen) |
| 3 | `close` | entfernt die Nummer; die Datei schließt erst mit der letzten Nummer, die auf sie zeigt (`dup`) |
| 0, 19 | `read`, `readv` | Länge auf 0x7ffff000 begrenzt, Stücke zu 64 KiB; Verzeichnis `-EISDIR`; Terminal liest über `ILinuxInput` (S1-Tabelle, V3 stdin); scheitert das Zurückschreiben, wird die Position zurückgesetzt (`-EFAULT` oder die bisherige Anzahl); `readv` wie `writev` |
| 17 | `pread64` | über Seek nachgebildet (Position bleibt); nicht atomar gegen einen zweiten Thread (es gibt noch keine Threads); Terminal `-ESPIPE`, Versatz < 0 `-EINVAL` |
| 8 | `lseek` | whence 0-2 (`SEEK_DATA`/`SEEK_HOLE` `-EINVAL`); Terminal `-ESPIPE`; bei Verzeichnissen ist die Position der `getdents64`-Index (`rewinddir`, `seekdir`, `telldir`), `SEEK_END` `-EINVAL` |
| 32, 33, 292 | `dup`, `dup2`, `dup3` | teilen die offene Datei; `dup2` schließt das Ziel still; `dup3` mit gleichen Nummern oder Flags außer `O_CLOEXEC` `-EINVAL` |
| 72 | `fcntl` | `F_DUPFD`, `F_DUPFD_CLOEXEC`; `F_GETFD` 0, `F_SETFD` ohne Wirkung (kein `execve`); `F_GETFL` immer `O_RDWR` (Abweichung: Öffnungsflags sind nicht sichtbar); `F_SETFL` ohne Wirkung (Abweichung); sonst `-EINVAL` |
| 79 | `getcwd` | Rückgabe Länge mit NUL; zu kleiner Puffer `-ERANGE` |
| 80, 81 | `chdir`, `fchdir` | Ziel muss Verzeichnis sein (`-ENOTDIR`) und Suchrecht haben (`access X_OK`); cwd ist der textuell normalisierte Pfad (Symlinks bleiben darin stehen) |
| 4, 5, 6, 262 | `stat`, `fstat`, `lstat`, `newfstatat` | `struct stat` 144 Bytes; `fstat` auf Terminal: Zeichengerät 136:0; `AT_EMPTY_PATH` mit leerem Pfad wie `fstat`, mit `AT_FDCWD` das Arbeitsverzeichnis |
| 21, 269, 439 | `access`, `faccessat`, `faccessat2` | Modus nur `F_OK`/`R_OK`/`W_OK`/`X_OK`; Flags ohne Wirkung |
| 217 | `getdents64` | Position = Index am Deskriptor, `d_off` = nächster Index; erster Eintrag passt nicht `-EINVAL` |
| 89, 267 | `readlink`, `readlinkat` | gekürzt ohne NUL wie Linux |
| 83, 258, 84, 87, 263 | `mkdir(at)`, `rmdir`, `unlink(at)` | `AT_REMOVEDIR`; andere Flags `-EINVAL` |
| 82, 264, 316 | `rename`, `renameat`, `renameat2` | `renameat2` nur mit Flags 0 (`RENAME_NOREPLACE`/`EXCHANGE` `-EINVAL`) |
| 88, 266 | `symlink`, `symlinkat` | Ziel ist beliebiger Text und wird nicht aufgelöst |

Grenzen (Review V2, bewusst offen):

- Der Pfad einer offenen Datei (`ILinuxFile.Path`) ist der beim Öffnen. Wird ein offenes Verzeichnis oder ein Ordner darüber umbenannt, lösen `openat`/`*at` mit diesem
  Deskriptor und `fchdir` weiter gegen den alten Pfad auf (Linux folgt dem Verzeichnis).
- Scheitert bei `read`/`readv` das Zurückschreiben in den Prozess (`-EFAULT`), wird die Position per Seek zurückgesetzt. Bei Dateien ohne Seek (Pipes, Geräte, sobald es sie gibt)
  sind die gelesenen Bytes dann verloren.
- `close` schließt die Datei, wenn keine andere Nummer **dieser** Tabelle (0–1023) mehr auf sie zeigt. Das stimmt, solange Nummern unter 1024 bleiben und kein zweiter Prozess
  die Tabelle oder Dateien teilt (`fork`, Übergabe); dann braucht `ILinuxFile` einen Referenzzähler.

## Prozess und Terminal (V3)

- `LinuxUserProcess` (`ProjektZeh/Processes/LinuxUserProcess.cs`, plattformunabhängig, Hosttests `LinuxUserProcessTests`): `AddressSpaceMap` plus `IUserPages`
  (Frames und Abbildung). Beim Laden bekommen nur Seiten mit Dateibytes und der vorbereitete Teil des Stacks einen Frame; alle anderen (.bss, übriger Stack,
  `brk`, `mmap`) entstehen genullt beim ersten Zugriff (Seitenfehler oder Kernelzugriff über `IUserMemory`, der dieselben Rechte prüft und alles oder nichts kopiert).
  `mprotect` behält Frame und Inhalt (auch über `PROT_NONE`). `PROT_WRITE|PROT_EXEC` scheitert (W^X: `mmap` `-ENOMEM`, `mprotect` `-ENOMEM`).
  Anonyme Abbildungen ab 16 TiB (`MmapBase`), `brk` wächst bis an die nächste Belegung; höchstens 512 MiB Frames je Prozess, darüber Abbruch.
- Deskriptoren 0–2: `TerminalDescriptor` schreibt zeilenweise ins Terminal (UTF-8 erst am Zeilenende, Zeilen über 4096 Bytes werden geteilt); jeder andere ist `-EBADF`.
  Lesen (fd 0) über `LinuxTerminalSession`: Enter-Zeilen des Fensters mit `\n`, Strg+D Eingabeende; ohne Sitzung (Gastprobe) leer wie `/dev/null`.
  Wartet das Programm auf Eingabe, wird die angefangene Zeile (`> `) zur Eingabeaufforderung im Fenster. `ioctl(TCGETS)` bleibt `-ENOTTY`
  (kein Terminal: musl puffert stdout voll, Programme müssen vor dem Lesen `fflush` rufen).
- Brücke im Kernel: `ProjektZeh/Runtime/CosmosLinuxRun.cs`, nur mit `-p:ZehOSLinuxRun=true`; dann gibt es `run <datei> [argumente...]` (ELF bis 128 MiB,
  ein Programm zugleich, eigener Kernelthread mit eigener `UserPageTable`). Aus der Shell läuft es neben der Ereignisschleife (V3 stdin): der Befehl kehrt
  sofort zurück, die Shell holt die Ausgabe je Bild ab, Enter-Zeilen gehen an fd 0, Strg+C oder Esc bricht ab (`signal 2`), ein zweites Strg+C gibt das
  Fenster frei, falls das Programm nicht endet; ohne Zeitgrenze. Die Gastprobe läuft weiter synchron mit Zeitgrenze (120 s, danach `signal 9`). AT_RANDOM aus `HardwareRandom` (ohne Ersatz),
  AT_HWCAP aus CPUID. Ein Fehler im Ring 3, den der Prozess nicht als Seitenfehler bedient, beendet nur das Programm (Status `signal <n>` wie Linux: #PF/#GP 11,
  #UD 4, #DE 8); eine Ausnahme in der Brücke ebenso (`signal 9`).
- Gastprobe: `-p:ZehOSLinuxRunProbe=true -p:ZehOSLinuxRunBundle=<bündel>` startet beim Boot die Programme eines Bündels (`scripts/test-linux-run.py`).
  Pin: `story/ladybird-cosmos` (Patches `user-cr3` u. a.).
