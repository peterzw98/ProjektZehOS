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
| 1 | `write` | Stückweise Kopie (4 KiB); Länge auf 0x7ffff000 begrenzt; `-EBADF`, `-EFAULT`; ein Fehler nach erfolgreichen Stücken liefert die bisherige Anzahl; nach einem Abbruch (Strg+C, Zeitlimit) endet die Kopie vor dem nächsten Stück mit der bisherigen Anzahl oder `-EINTR` (#735) |
| 20 | `writev` | höchstens 1024 Einträge, Summe über 0x7ffff000 ergibt `-EINVAL` (Abweichung: Linux kürzt stattdessen), Einträge werden vorab gelesen |
| 60, 231 | `exit`, `exit_group` | `ILinuxProcess.Exit(code & 0xff)` |
| 12 | `brk` | wie Linux: bei Fehler der alte Wert, nie ein Fehlercode |
| 9 | `mmap` | nur `MAP_ANONYMOUS` mit genau einem von `MAP_PRIVATE`/`MAP_SHARED`; Länge wird auf Seiten aufgerundet; unbekannte `prot`-Bits `-EINVAL`; `MAP_FIXED` `-ENOSYS` (Abweichung: Linux liefert je nach Fall `-EINVAL`/`-ENOMEM`); Datei-fd `-ENODEV` (fd < 0 ohne ANONYMOUS `-EBADF`); `-EINVAL`, `-ENOMEM` |
| 11, 10 | `munmap`, `mprotect` | Länge wird auf Seiten aufgerundet; `-EINVAL` bei unausgerichteter Adresse, Länge oder Adresse über 2^47, unbekannten `prot`-Bits, `munmap` mit Länge 0; `mprotect` mit Länge 0 liefert 0; `mprotect` ohne Abdeckung `-ENOMEM` |
| 158 | `arch_prctl` | `ARCH_SET_FS` (nur Adressen der Benutzerhälfte, sonst `-EPERM`), `ARCH_GET_FS`; sonst `-EINVAL` |
| 218 | `set_tid_address` | merkt die Adresse je Thread, liefert die Thread-ID (V10) |
| 39, 186 | `getpid`, `gettid` | Prozess-ID; `gettid` die Thread-ID (V10, Hauptthread = PID) |
| 273 | `set_robust_list` | `0` (ohne Wirkung) |
| 111, 121, 124 | `getpgrp`, `getpgid`, `getsid` | Prozess-ID (V3 raw: allein in Gruppe und Sitzung, führt beide wie ein Programm in einem neuen pty); andere und negative PID `-ESRCH` |
| 109, 112 | `setpgid`, `setsid` | `-EPERM` wie bei Linux für Sitzungs- bzw. Gruppenführer (auch ohne Änderung); `setpgid` mit fremder PID `-ESRCH`, negativer Gruppe `-EINVAL` |
| 16 | `ioctl` | fd 0–2 am Fenster sind ein Terminal (V3 termios): `TCGETS`, `TCSETS`/`TCSETSW`/`TCSETSF`, `TIOCGWINSZ`, `TIOCSWINSZ`, `TIOCGPGRP`, `TIOCSPGRP`; sonst `-ENOTTY` für gültige fd, `-EBADF` für ungültige |
| 228 | `clock_gettime` | Uhren 0-7; 0 und 5 Wanduhr, übrige monoton (CPU-Zeituhren 2, 3 ebenfalls monoton: Abweichung); Uhren 8, 9, 11 (BOOTTIME-Familie) `-EINVAL` (Abweichung); sonst `-EINVAL`. Quelle `ILinuxHost` (optional), sonst DateTime/Stopwatch |
| 318 | `getrandom` | Flags nur GRND_NONBLOCK/RANDOM/INSECURE (RANDOM+INSECURE `-EINVAL`); füllt in 4-KiB-Stücken aus `ILinuxHost.TryFillRandom`; ohne Host `-ENOSYS` (nie schwacher Zufall), Quelle nicht bereit `-EAGAIN`; Abbruch je Stück wie `write` (#735) |
| 63 | `uname` | Linux / zehos / 6.1.0-zehos / x86_64 |
| 13, 14 | `rt_sigaction`, `rt_sigprocmask` | nur Verwaltung je Prozess (keine Zustellung); sigsetsize 8, SIGKILL/SIGSTOP nicht abfangbar und nicht blockierbar |
| 131 | `sigaltstack` | setzen/abfragen/abschalten; Größe < 2048 `-ENOMEM`; ohne Zustellung wirkungslos; SS_AUTODISARM wird nicht gemerkt (Abweichung) |
| 24 | `sched_yield` | `0` |
| 7 | `poll` | höchstens 1024 Einträge; offene fd melden POLLOUT, POLLIN nur eine Eingabe mit Bytes, Eingabeende oder Abbruch (sonst nie POLLIN), unbekannte fd POLLNVAL; wartet ein Eintrag auf Eingabe, wartet poll darauf in 50-ms-Scheiben (auch mit Frist -1, Abbruch beendet), warten mehrere (Pipe-Enden, V13), reihum in 10-ms-Scheiben; ohne bereite Einträge und mit Frist > 0 schläft sie die Frist über `ILinuxHost.SleepNanos` ab (ohne Host nicht); Frist -1 (unendlich) schläft nie, Ergebnis 0 |
| 28 | `madvise` | Hinweise 0-3, 14, 15 liefern 0; DONTNEED/FREE und Unbekanntes `-EINVAL` (Seiten nullen geht noch nicht); prüft keine Abbildung (Linux `-ENOMEM`) |
| 202 | `futex` | mit Threads siehe V10; ohne Thread-Fähigkeit (Hosttests) wie S1, Einzelthread: WAIT/WAIT_BITSET mit abweichendem Wert `-EAGAIN`, mit Frist `-ETIMEDOUT` (ohne zu schlafen), ohne Frist `-EDEADLK` (Linux hängt; glibc bricht bei diesem unbekannten Fehler ab, musl dreht in einer Schleife); Wert wird vor der Frist gelesen: kaputte Frist bei abweichendem Wert liefert `-EAGAIN` statt `-EFAULT`/`-EINVAL` (Abweichung); WAKE* 0; CMP_REQUEUE prüft den Wert; WAKE_OP `-ENOSYS` |
| 334 | `rseq` | `-ENOSYS` (Laufzeitbibliotheken fallen zurück) |

Grenze: `write` verlässt sich auf `ILinuxDescriptor.Write` ≤ angebotene Länge (größere Rückgaben werden nicht abgefangen); je Aufruf ein 4-KiB-Puffer.

Nicht in S1: Dateien (siehe V2 unten), Signalzustellung, Threads (siehe V10), `MAP_FIXED`, Dateiabbildungen.

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
| 18 | `pwrite64` | V14: wie `write` ab dem Versatz, über Seek nachgebildet wie `pread64` (Position bleibt, auch nach einem Fehler); Versatz < 0 `-EINVAL` vor der Prüfung des Deskriptors, Versatz + Länge über `INT64_MAX` `-EINVAL`; Terminal `-ESPIPE`; Verzeichnis und nur lesend offen `-EBADF`; mit `O_APPEND` hängt es wie Linux ans Ende an |
| 295, 296 | `preadv`, `pwritev` | V14: wie `readv`/`writev` ab dem Versatz `pos_l` (`pos_h` hat auf x86-64 keine Bedeutung), Position bleibt; Fehlerwege wie `pwrite64`, `preadv` auf ein Verzeichnis `-EISDIR`; mehr als 1024 Einträge `-EINVAL` |
| 77 | `ftruncate` | V14: setzt die Länge, die Position bleibt; Verlängern liest sich als Nullbytes und läuft auf der Platte in Schritten mit Kontingent (V7, `-ENOSPC`); Länge < 0 `-EINVAL` vor der Prüfung des Deskriptors; nicht zum Schreiben offen, Verzeichnis, Gerät, Terminal `-EINVAL` wie Linux; über der Dateigrenze des Speichers `-EFBIG` |
| 74, 75 | `fsync`, `fdatasync` | V14: Datei und Verzeichnis `0` (beide Speicher schreiben ungepuffert durch: `DirectoryFileStore` öffnet mit Puffergröße 1, Cosmos-FAT schreibt jeden Cluster sofort); `/dev/null`, `/dev/zero`, `/dev/tty` und das Terminal `-EINVAL` wie Linux für Zeichengeräte und Pipes |
| 8 | `lseek` | whence 0-2 (`SEEK_DATA`/`SEEK_HOLE` `-EINVAL`); Terminal `-ESPIPE`; bei Verzeichnissen ist die Position der `getdents64`-Index (`rewinddir`, `seekdir`, `telldir`), `SEEK_END` `-EINVAL` |
| 32, 33, 292 | `dup`, `dup2`, `dup3` | teilen die offene Datei; `dup2` schließt das Ziel still; `dup3` mit gleichen Nummern oder Flags außer `O_CLOEXEC` `-EINVAL` |
| 72 | `fcntl` | `F_DUPFD`, `F_DUPFD_CLOEXEC`; `F_GETFD` 0, `F_SETFD` ohne Wirkung (kein `execve`); `F_GETFL` immer `O_RDWR` (Abweichung: Öffnungsflags sind nicht sichtbar), bei Pipe-Enden (V13) `O_RDONLY`/`O_WRONLY` mit `O_NONBLOCK`; `F_SETFL` ohne Wirkung (Abweichung), bei Pipe-Enden setzt oder löscht es `O_NONBLOCK`; sonst `-EINVAL` |
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
- Deskriptoren 0–2: `TerminalDescriptor` schreibt zeilenweise ins Terminal (UTF-8 erst am Zeilenende, Zeilen über 4096 Bytes werden geteilt); weitere Nummern sind Dateien (V8).
  Lesen (fd 0) über `LinuxTerminalSession`: Enter-Zeilen des Fensters mit `\n`, Strg+D Eingabeende; ohne Sitzung (Gastprobe) leer wie `/dev/null`.
  Wartet das Programm auf Eingabe, wird die angefangene Zeile (`> `) zur Eingabeaufforderung im Fenster. Ausgabe: ANSI-Folgen fallen weg, `\r` beginnt die
  Zeile erst beim nächsten sichtbaren Zeichen neu, die Teilung nach 4096 Bytes trennt keine ESC-Folge und kein UTF-8-Zeichen.
- termios (V3 termios): Mit Sitzung (Fenster) sind fd 0–2 ein Terminal, `isatty` ist wahr und musl puffert stdout zeilenweise (musl prüft `TIOCGWINSZ`).
  `TCGETS` liefert die Kernelstruktur (36 Bytes) eines frischen Linux-Terminals: kanonisch mit Echo (`ICANON ECHO ISIG IEXTEN …`, `OPOST ONLCR`, `^C ^D`, VMIN 1).
  `TCSETS*` setzt den Zustand für fd 0–2; davon wirken `ICANON`, `ISIG` und `ICRNL` (V3 raw), der Rest wird nur gespeichert;
  `TCSETSF` verwirft keine getippten Zeilen. `TIOCGWINSZ` aus dem Fenster (sichtbare Zeilen, Spalten; ohne Meldung 24 × 80, Pixel 0), `TIOCSWINSZ` ohne Wirkung.
  `TIOCGPGRP` liefert die PID, `TIOCSPGRP` nimmt nur sie an (sonst `-ESRCH`, negativ `-EINVAL`).
- Rohmodus (V3 raw, für Zeileneditoren wie LibLine der js-REPL): Ohne `ICANON` editiert die Shell nichts, jede Taste geht sofort als Bytes an fd 0 (Zeichen
  UTF-8, Enter CR, mit `ICRNL` LF, Backspace DEL, Pfeile/Pos1/Ende `ESC [ A–D/H/F`, Entf `ESC [ 3 ~`, Esc `ESC`, Strg+Buchstabe 1–26, Einfügen wie Tippen);
  `ECHO` wirkt nicht (die Shell zeigt nur, was das Programm zeichnet). Strg+C bricht mit `ISIG` ab, ohne geht 0x03 an das Programm; Strg+Umschalt+Esc bricht immer ab
  (#735). Die angefangene Zeile zeigt das Fenster wie ein VT100 (`TerminalLine`): Überschreiben an der Cursorspalte, `\r`, Backspace, Tab, CSI `K J C D G H f s u`,
  `ESC 7/8`; Zeilenwechsel (`A`, `B`, Zeile von `H`), Farben und private Folgen fallen weg. Am Zeilenende wird der gezeichnete Stand ausgegeben, nicht die
  Neuzeichnungen; über 4096 Bytes ohne Zeilenende wird die Zeile auf ihren Stand verdichtet. `ESC [ 6 n` (auch über zwei `write` verteilt) beantwortet das
  Terminal in die Eingabe mit `ESC [ <letzte Fensterzeile> ; <Spalte> R` und zeigt die Frage nicht. Antworten dürfen die Eingabegrenze (64 KiB) um 64 Bytes
  überschreiten; darüber verwirft das Terminal sie wie ein volles Linux-tty (ein Programm, das fragt und nie liest, füllt keinen Kernelspeicher).
  Abbruch: Im Rohmodus ist Esc eine Taste wie jede andere (`ESC` an fd 0) und bricht nicht ab; mit `ISIG` bleibt Strg+C der Abbruch. Die reservierte Taste
  Strg+Umschalt+Esc erreicht nie das Programm und bricht unabhängig von `ISIG` und Rohmodus ab (ein zweiter Druck gibt das Fenster frei wie ein zweites
  Strg+C). Nach dem Abbruch verwirft das Terminal Ausgabezeilen über der Grenze (2048), statt das Programm warten zu lassen. Grenze Spalte: Die Cursorspalte zählt
  UTF-16-Einheiten, nicht Zeichenbreiten; Zeichen außerhalb der BMP (Emoji) oder breite CJK-Zeichen verschieben Cursor und `ESC [ 6 n`-Spalte.
  Nicht umgesetzt: VMIN/VTIME (immer wie VMIN 1), mehrzeilige Bearbeitung, Echo im Rohmodus.
  Ohne Sitzung (Gastprobe über die serielle Schnittstelle) bleibt `-ENOTTY`: stdout voll gepuffert wie bei einer Umleitung, die erwarteten Ausgaben bleiben gleich.
- Brücke im Kernel: `ProjektZeh/Runtime/CosmosLinuxRun.cs`, nur mit `-p:ZehOSLinuxRun=true`; dann gibt es `run <datei> [argumente...]` (ELF bis 128 MiB,
  ein Programm zugleich, eigener Kernelthread mit eigener `UserPageTable`). Aus der Shell läuft es neben der Ereignisschleife (V3 stdin): der Befehl kehrt
  sofort zurück, die Shell holt die Ausgabe je Bild ab, Enter-Zeilen gehen an fd 0, Strg+C oder Esc bricht ab (`signal 2`), ein zweites Strg+C gibt das
  Fenster frei, falls das Programm nicht endet; ohne Zeitgrenze. Die Gastprobe läuft weiter synchron mit Zeitgrenze (120 s, danach `signal 9`). AT_RANDOM aus `HardwareRandom` (ohne Ersatz),
  AT_HWCAP aus CPUID. Ein Fehler im Ring 3, den der Prozess nicht als Seitenfehler bedient, beendet nur das Programm (Status `signal <n>` wie Linux: #PF/#GP 11,
  #UD 4, #DE 8); eine Ausnahme in der Brücke ebenso (`signal 9`).
- Gastprobe: `-p:ZehOSLinuxRunProbe=true -p:ZehOSLinuxRunBundle=<bündel>` startet beim Boot die Programme eines Bündels (`scripts/test-linux-run.py`).
  Pin: `story/ladybird-cosmos` (Patches `user-cr3` u. a.).

## Dateisystem der Prozesse (V7)

- `LinuxFileSystem` (`ProjektZeh/Processes/Vfs/`, plattformunabhängig, Hosttests `LinuxFileSystemTests`) implementiert `ILinuxFileSystem` und `ILinuxFile`
  (Schnittstellen von V2-Dateien, `ProjektZeh/Processes/Syscalls/ILinuxFileSystem.cs`). Die Wurzel "/" liegt auf einem `ILinuxFileStore`:
  `DirectoryFileStore` (Ordner auf der Datenplatte, jeder Zugriff unter `StorageLock`) oder `MemoryFileStore` (Arbeitsspeicher, Tests).
  Darüber virtuell `/dev` mit `null` (liest EOF, verwirft, `lseek` 0), `zero` (liest Nullbytes in jeder Länge, verwirft) und `tty` (schreibt auf den Terminal-Deskriptor des Prozesses, liest EOF, `lseek` `-ESPIPE`).
- Ausbruchschutz: Pfade kommen absolut und normalisiert vom Verteiler; ".." darin ergibt `-EINVAL` (nicht selbst gedeutet). Symlinks löst `LinuxFileSystem`
  Teil für Teil selbst auf, der Speicher folgt keinem Link (im Kernel `CosmosLinuxDiskLinks` über Cosmos-Inodes, weil Cosmos-System.IO Links in jedem Teil folgt).
  Absolute Linkziele beginnen an der Prozesswurzel, ".." im Linkziel ist physisch und endet an der Wurzel (wie `chroot`). Ein Mount unter der Wurzel ist `-EACCES`.
- Grenzen: Pfad ab 4096 Bytes und Teil über 255 Bytes (UTF-8) `-ENAMETOOLONG`, auch für aufgelöste Pfade; 40 Links `-ELOOP`; je Auflösung höchstens 4096 Teile
  (Eingabe und Linkziele), sonst `-ELOOP` (Abweichung: Linux kennt nur die Linkgrenze); 1024 offene Dateien je Dateisystem `-EMFILE`;
  Datei auf der Platte höchstens 1 GiB (`-EFBIG`, Schreiben bis zur Grenze wird gekürzt); `MemoryFileStore` 16 MiB je Datei, 64 MiB und 65 536 Einträge gesamt (`-ENOSPC`).
- Wachstum auf der Platte: Cosmos-FAT kennt keine Lücken (Sparse) und nullt beim Verlängern sofort jeden Cluster. Deshalb wächst eine Datei (Lücke vor einem
  `write` nach `lseek`, `ftruncate` nach oben) in Schritten von höchstens `StorageLock.ChunkBytes` (1 MiB), jeder unter einer eigenen Sperre, damit Desktop und
  Shell dazwischen drankommen. Dazu ein Kontingent je `DirectoryFileStore`, also je Start eines Programms (nicht je Wurzel auf der Platte: jeder Start und jeder
  zweite Prozess auf derselben Wurzel hat wieder 64 MiB, was ein Start hinterlässt, bleibt): höchstens 64 MiB Nettowachstum (`DirectoryFileStore.DefaultQuotaBytes`;
  vorhandene Dateien wie Pakete zählen nicht, Löschen und Kürzen geben frei). Ein Wachstum, das nicht mehr hineinpasst, ist `-ENOSPC`; vor einem `write` hinter
  dem Ende wird die Lücke plus ein Byte geprüft, passt das nicht, entsteht nichts. Ein Schreiben am Rand wird gekürzt.
  Entfernte oder durch `rename` ersetzte Dateien, die noch offen sind, belegen unter Cosmos weiter Platz (das Entfernen kommt erst beim letzten Schließen,
  `VfsManager.Paths.cs` `MarkPendingIfOpen`); ihre Länge wird deshalb erst beim letzten Schließen gutgeschrieben, die Gutschrift hängt am Eintrag, nicht am Namen.
  Cosmos lässt dabei auch den Namen bis zum letzten Schließen stehen (sichtbar, ein weiteres `unlink` gelänge). `DirectoryFileStore` führt solche Pfade
  deshalb eigens (sie wandern bei `rename` mit): wie bei Linux sind sie sofort weg (`stat`/`open`/`unlink`/`rename` `-ENOENT`, nicht in Listen), ohne zweite
  Gutschrift. Anlegen, `mkdir`, `symlink` und `rename` auf einen solchen Namen sind `-EEXIST`, solange das Laufwerk ihn noch führt (Abweichung: Linux erlaubt das
  sofort; ein Linux-Host entfernt sofort, dann ist der Name frei), und `rmdir` des Ordners ist bis dahin `-ENOTEMPTY`, ebenso `rename` eines Ordners auf
  ein solches, leer wirkendes Ziel. Einen Ordner, unter dem eine Datei offen oder entfernt, aber noch offen ist, benennt `rename` nicht um (`-EBUSY`; Abweichung:
  Linux benennt um): Cosmos zieht nur die Handles des umbenannten Eintrags selbst mit (`MoveOpenHandles`, `VfsManager.Paths.cs:369-383`, Pin 5790fa74), die
  Kinder behielten ihren alten Pfad; das aufgeschobene Entfernen träfe beim letzten Schließen eine neue Datei unter dem alten Namen, und ein `unlink` unter dem
  neuen Namen fände den offenen Handle nicht (`MarkPendingIfOpen` vergleicht `OpenedPath`, `:425`) und gäbe dessen Cluster frei. Den Namen `<name>.~replace`, unter dem
  Cosmos ein durch `rename` ersetztes, noch offenes Ziel bis zum Schließen beiseitelegt (`DropDisplacedEntry`), sieht das Programm nicht und kann ihn nicht anlegen. Scheitert ein Wachstumsschritt (Platte voll), setzt `DirectoryFileStore` die Datei auf die alte Länge zurück und bucht das
  Kontingent zurück; gelingt auch das nicht, bleibt die Datei halb gewachsen (erreichte Länge, im Kontingent gebucht) und der Fehler des Schritts gilt.
  Grenze: Ändert ein anderer Akteur (Shell, zweiter Prozess) die Länge zwischen der Abfrage und den Schritten, setzen die Schritte auf die alte Länge auf und
  können die Datei kürzen.
  Volle Platte ist `-ENOSPC`: `IOException` mit HResult 28 (nur auf einem Nicht-Windows-Host), 0x70/0x27 (nur unter Windows; unter Linux ist 39 `ENOTEMPTY`) oder unter Cosmos dem PAL-Wert 0x10034 (`PalError.ENOSPC`; Cosmos
  setzt `ConvertErrorPalToPlatform` als Identität ein, `InteropSysFilePlug.cs:336`, Wert aus `Interop.Error.cs:37` im Pin 5790fa74, gemeldet von
  `FileDescriptorTable.cs:248`); `PalError.EFBIG` (0x10016, FAT-Grenze 4 GiB) ist `-EFBIG`, unbekannte Werte `-EIO`. .NET meldet EFBIG aus
  `FileStream.SetLength` unter Unix als `ArgumentOutOfRangeException` (dann `-EINVAL`); die 1-GiB-Grenze fängt das vorher ab. Im Gast belegt (V7b, KVM smp1): `write` am Ende bei voller Platte `-ENOSPC`.
  Bekannte Grenze: Wächst eine Datei über eine Lücke (`write` hinter dem Ende, `ftruncate` nach oben) und die Platte wird dabei voll, kommt `-EIO` statt `-ENOSPC`.
  Cosmos' `Truncate` meldet beim Verlängern jeden Fehler als EIO (`FileDescriptorTable.cs:440-442` im Pin 5790fa74), nicht den PAL-Wert wie `PWrite` (`:248`).
  Behebung als Cosmos-Patch nach der Vorstellung (#743); `v7bfull` nimmt dort bis dahin `-EIO` an.
- Rechte: der Prozess gilt als Eigentümer, geprüft werden die Eigentümerbits (r, w, x); fehlendes x auf einem Zwischenverzeichnis, fehlendes w im Elternverzeichnis
  beim Anlegen, Entfernen und Umbenennen `-EACCES`. Platte: Ordner 0755, Dateien 0644 (schreibgeschützt 0444), also ohne Ausführungsbit (`access(X_OK)` `-EACCES`).
- Symlinks auf der Platte nur, wo das Laufwerk sie kann: FAT unter Cosmos nicht (`FatInodeOperations.Symlink` liefert immer false), dann `symlink` `-EPERM` wie Linux auf vfat.
- `close`: die Datei ist danach immer zu und zählt nicht mehr zum Budget; ein Fehler beim Schließen (Platte, etwa `-EIO` beim Zurückschreiben) kommt beim letzten Verweis als Rückgabe von `close` beim Programm an (`ILinuxFile.Close` liefert 0 oder `-errno` und wirft bei Dateifehlern nicht). `dup2`/`dup3` auf ein offenes Ziel und das Prozessende verwerfen ihn wie Linux.
- Namen auf der Platte: was FAT oder Cosmos anders deuten könnte (`:`, `\`, Steuerzeichen, abschließender Punkt oder Leerzeichen, `.zehos-`-Systemnamen) gibt es nicht:
  Abfragen melden es als fehlend, Anlegen `-EINVAL`, Listen lassen es weg. Groß-/Kleinschreibung bestimmt das Laufwerk (`NameComparison`).
  FAT findet einen Eintrag auch über seinen 8.3-Kurznamen (`_ZEHOS~1.DB` für `.zehos-security.db`); `CosmosLinuxDiskLinks` meldet den geführten Namen, und ein
  Treffer, dessen Name nicht (bei FAT ohne Groß-/Kleinschreibung) gleich dem angefragten ist, gilt als fehlend (`-ENOENT`); Anlegen, `mkdir`, `symlink` und
  `rename` auf einen solchen Zweitnamen sind `-EEXIST` (Abweichung von vfat, das den Zweitnamen auf denselben Eintrag deutet), damit nie ein vorhandener Eintrag über
  ihn überschrieben wird. `/dev` verdeckt auf einem solchen Laufwerk jede Schreibweise (`/DEV`, `/Dev`).
- `open`-Flags: Zugriff, `O_CREAT O_EXCL O_TRUNC O_APPEND O_DIRECTORY O_NOFOLLOW` wirken; `O_NOCTTY O_NONBLOCK O_SYNC O_DSYNC O_LARGEFILE O_NOATIME O_CLOEXEC`
  werden angenommen (CLOEXEC gehört zur Deskriptortabelle); `O_TMPFILE` `-EOPNOTSUPP`, übrige (`O_DIRECT`, `O_PATH`, `O_ASYNC`) `-EINVAL`;
  `O_CREAT|O_DIRECTORY` `-EINVAL` wie Linux ab 6.4. Eine entfernte, offene Datei bleibt les- und abfragbar (`MemoryFileStore`; Platte im Gast noch offen).
- Bekannte Abweichung FAT-Datenplatte: Cosmos legt auf FAT keine Symlinks an (`FatInodeOperations.Symlink` liefert immer false), `symlink`/`symlinkat`
  auf der Platte enden deshalb mit `-EPERM` (wie Linux auf vfat); folgende `readlink`, `lstat` und `unlink` des Links finden nichts. Im Gast weicht `s1/files` darum in den
  Symlink-Zeilen ab (erwartete Ausgabe bleibt die von Linux). Im Arbeitsspeicher (`MemoryFileStore`) gibt es Symlinks; ext2 implementiert `Symlink` in Cosmos, als Prozesswurzel im Gast ist das ungeprüft.
- Offen: Gastlauf von `DirectoryFileStore` und `CosmosLinuxDiskLinks`, `/dev/urandom`,
  `/tmp` im Speicher, Eingabe über `/dev/tty`. Weiter offen aus dem Review von #724:
  - Wechsel zwischen Auflösung und Zugriff wird nur am letzten Teil erneut geprüft; Cosmos folgt beim Öffnen des Elternordners Links. Das gilt deshalb nur für einen
    Prozess je Wurzel auf einem Laufwerk ohne Links (FAT); ein zweiter Akteur, der Zwischenordner durch Links ersetzt, ist nicht abgedeckt.
  - FAT-Sonderzeichen `? * " < >`, reservierte Namen (`CON`) und ein führendes Leerzeichen werden angenommen (Cosmos deutet sie nicht, chkdsk/Windows bemängeln sie).
  - Inodenummern falten Groß-/Kleinschreibung nach Unicode, FAT nur ASCII; `rename("/a", "/A")` ist ein No-op statt eine Änderung der Schreibweise.
  - Kosten je Pfadteil im Kernel (Mounts und Elternordner je Abfrage) sind im Gast zu messen; `.`/`..` aus Linkzielen prüfen kein x-Bit.
  - Gastlauf V7b (senior2, KVM): Kurznamen, Kontingent und Lücke geprüft; volle Platte (smp1) mit der bekannten Grenze `-EIO` hinter einer Lücke; volle Platte auf smp4, TCG und VMware nicht.
  - Neu Anlegen unter dem Namen einer entfernten, noch offenen Datei ist unter Cosmos `-EEXIST` bis zum letzten Schließen (siehe oben; Linux: Erfolg), ein übliches
    Muster für temporäre Dateien. Im Gast belegt (`v7bopen`, KVM smp1 und smp4), ebenso `-ENOENT` für den entfernten Namen und `-EBUSY` für `rename` eines Ordners mit offenem Kind.
  - 64 MiB reichen für die Testprogramme und js; Ladybird mit Platten-Cache (HTTP-Cache, Profil) kann sie erreichen: beim Anbinden von `run` für Ladybird prüfen.

## Dateien im Prozess (V8)

- `LinuxUserProcess` ist `ILinuxFiles`: Deskriptortabelle mit 1024 Nummern (0–2 auf das Terminal, darüber `-EMFILE`), Arbeitsverzeichnis ab "/",
  Dateisystem aus dem Konstruktor (ohne Angabe ein leeres `MemoryFileStore`, das mit dem Prozess endet). `close` auf 0–2 entfernt nur die Nummer, das Terminal
  bleibt offen. Beim Ende (`Destroy`) schließt der Prozess jede noch offene Datei genau einmal, auch über `dup` geteilte, danach erst gibt er den Speicher frei.
- `run` aus der Shell: Das aktuelle Verzeichnis der Shell ist die Prozesswurzel "/" und das Arbeitsverzeichnis (`cd /data/s1`, dann `run files`); in "/"
  und direkt auf einem Einhängepunkt (`/data`: dort lägen Konten und Pakete) bekommt das Programm ein leeres Dateisystem im Arbeitsspeicher. Lässt sich die Wurzel nicht öffnen, startet das Programm nicht.
  Namen ohne Groß-/Kleinschreibung (FAT-Datenplatte), auch wenn die Wurzel auf einem anderen Laufwerk liegt.
- Rechte der Prozesswurzel (Review V8): Das Programm schreibt im ganzen Unterbaum ohne eigene Rechteprüfung, `cd` prüft aber nur Lesen. Deshalb startet `run`
  auf der Platte nur mit angemeldeter Sitzung, Schreibrecht auf die Wurzel und Verwaltungsrecht (Eigentümer oder Administrator) auf die Wurzel und jeden zugeordneten
  Eintrag darunter (höchstens 4096 Einträge). Nie Wurzel: die Verwaltungswurzel selbst, Komponenten mit `.zehos` am Anfang und im Root-Profil alles unter `/etc`, `/var`
  und `/boot`. Sonst `cause='ProcessRootDenied'`. Einträge ohne Rechtezuordnung (vom Programm angelegt) bleiben erlaubt; zwischen Prüfung und Lauf kann sich der Baum ändern.
- Gastprobe: jedes Programm bekommt wie bei `run-linux.py` ein frisches Verzeichnis, `/data/linuxrun/<name>` (wenn `/data` eingehängt ist, sonst Arbeitsspeicher);
  die serielle Zeile `LINUX_RUN root <name> <wurzel|memory>` nennt es. Arbeitsverzeichnis ist der Unterordner `/w` darin (wird angelegt), weil `run-linux.py`
  ebenfalls in einem Unterordner startet: In "/" zeigte `ls .` den virtuellen Ordner `dev`, und `s1/dirs` erwartet vor `/d/e` noch einen Ordnernamen. Das Gastskript startet mit einem frischen Datenimage; ein zweiter Lauf auf demselben Image
  fände die Dateien des ersten.

## js-Vorbereitung (V9)

Gelesen: Ladybird `6fba5b78a104` (`AK/StackInfo.cpp`, `AK/Rust/src/stack.rs`, `AK/kmalloc.cpp`, `Libraries/LibCore/System.cpp`, `Libraries/LibGC/BlockAllocator.cpp`,
`Libraries/LibGC/PrimitiveStorage.cpp`, `Libraries/LibThreading/Thread.cpp`, `Libraries/LibJS/Rust/src/{source_text,parser/mod}.rs`, `Libraries/LibJS/Rust/src/utilities/{js,line_editor}.rs`,
`Utilities/js.cpp`), dazu musl 1.2 (`pthread_getattr_np`, `getrlimit`, `sysconf`). Nichts davon ist mit einem echten js belegt; das Binary kommt aus V4.
Code: `ProjektZeh/Processes/Syscalls/LinuxSyscallsLimits.cs`, Hosttests in `LinuxUserProcessTests` und `LinuxSyscallsTests`, Gastprogramm `experiments/LinuxAbi/src/v9/jsprep.c`.

| Nr | Name | Verhalten |
| --- | --- | --- |
| 25 | `mremap` | nur mit `ILinuxAddressSpace`, sonst `-ENOSYS`. Alter Bereich nicht ganz abgebildet `-EFAULT`; Verkleinern gibt das Ende frei; gleiche Größe liefert die Adresse; Vergrößern (auch mit `MREMAP_MAYMOVE`) und `MREMAP_FIXED`/`DONTUNMAP` `-ENOMEM` (Abweichung: musl-`realloc` und mimalloc kopieren dann); unausgerichtete Adresse, alte oder neue Größe 0 (nur private Abbildungen), unbekannte Flags, `FIXED`/`DONTUNMAP` ohne `MAYMOVE` `-EINVAL` |
| 302, 97, 160 | `prlimit64`, `getrlimit`, `setrlimit` | nur lesen: `RLIMIT_STACK` = Größe der Stackabbildung (Standard 8 MiB), `RLIMIT_NOFILE` 1024, `RLIMIT_CORE` 0, alle übrigen unbegrenzt; Setzen auf den geltenden Wert `0` ohne Wirkung, jeder andere Wert `-EPERM` (Abweichung: Linux erlaubt Senken), `old` nur bei Erfolg geschrieben (wie Linux); Nullzeiger bei `getrlimit`/`setrlimit` `-EFAULT`, bei `prlimit64` erlaubt; fremde PID `-ESRCH`, Ressource ≥ 16 `-EINVAL` |
| 204 | `sched_getaffinity` | eine CPU (Maske 1), Rückgabe 8; Länge < 8 oder nicht durch 8 teilbar `-EINVAL`, fremde PID `-ESRCH` |
| 157 | `prctl` | `PR_SET_NAME` prüft nur den Zeiger (ohne Wirkung), `PR_GET_NAME` liefert einen leeren Namen (Abweichung); alles andere `-EINVAL` (auch mimallocs `PR_SET_VMA`) |

Warum diese Aufrufe: musls `pthread_getattr_np` misst den Hauptstack, indem es unterhalb des Stackanfangs Seite um Seite `mremap(p-l-PAGE, PAGE, 2*PAGE, 0)`
versucht, solange `-ENOMEM` kommt. Mit `-ENOSYS` meldet es eine Seite; Ladybirds Rust-Seite (`AK/Rust/src/stack.rs:25-38`, genutzt in
`LibJS/Rust/src/parser/mod.rs:186-189`) setzte die Stackgrenze dann über den Stackzeiger, jeder JS-Aufruf wäre „Maximum call stack size exceeded“. Die Schutzseite
unter dem ZehOS-Stack ist eine Abbildung (PROT_NONE) und zählt mit (eine Seite mehr als Linux; LibJS hält `VM_STACK_SPACE_LIMIT` Abstand). Die C++-Seite
(`AK/StackInfo.cpp:122-135`) nimmt bei musl für den Hauptthread `RLIMIT_STACK`; ohne `prlimit64`/`getrlimit` bliebe die Struktur dort uninitialisiert.

### Lücken für js (Stand dieses Abschnitts)

Muss vor dem ersten js-Lauf geschlossen sein:

1. **Threads.** `GC::DecommitWorker` startet beim ersten freigegebenen Block mit aufgeschobenem Decommit einen Thread über `Threading::Thread::start()`,
   und das ist `MUST(try_start())` (`LibGC/BlockAllocator.cpp:349-357,494,638`; `LibThreading/Thread.cpp:58`). Ohne `clone` endet js beim ersten GC mit Abbruch.
   Gebraucht: `clone` mit `CLONE_VM|FS|FILES|SIGHAND|THREAD|SYSVSEM|SETTLS|PARENT_SETTID|CHILD_CLEARTID` (musl `pthread_create`), je Thread eigene FS-Basis,
   eigene TID (`gettid` ≠ `getpid`), `set_tid_address`/`CHILD_CLEARTID` mit futex-Wecken beim Threadende (musl `pthread_join`/`detach`), `exit` (nur der Thread)
   gegen `exit_group` (alle), echtes `futex` WAIT/WAKE mit Frist zwischen Threads (Bedingungsvariable mit `wait_for`), `tgkill` für `pthread_kill`/Abbruch,
   gemeinsamer Adressraum mit Sperre um Karte und Seitentabelle. Der zweite Thread (`LibJS source parking`, `LibJS/Rust/src/source_text.rs:336-342`) ist
   optional: scheitert sein Start, läuft js weiter.
2. **V2c** (`task/lb-v2c-aufrufe`, Review offen): `MAP_FIXED` ist Ladybirds `commit_memory`/`decommit_memory` (`LibCore/System.cpp:193-221`),
   `madvise(MADV_FREE)` der Block-Decommit (`BlockAllocator.cpp:225-260`). Ohne V2c scheitert der erste GC-Chunk mit `MUST`.
3. **Rohmodus** (`task/lb-v3-raw`, Review offen): js liest über libedit (`Utilities/js.cpp:10-48`); LibLine gibt es in diesem Stand nicht mehr.

Sollte vorher geklärt sein (Frage an den V4-Bau):

4. **libedit und terminfo.** Die Umgebung von `run` setzt weder `TERM` noch `HOME` (`CosmosLinuxRun.Environment`). Ohne `TERM` nimmt libedit ein dummes
   Terminal (keine Zeilenbearbeitung); mit `TERM` braucht ein statisches ncurses eine eingebaute Beschreibung (`--with-fallbacks`) oder eine terminfo-Datei
   im Prozessdateisystem. Die Historie geht nach `$HOME/.js-history`, ohne `HOME` (und ohne `/etc/passwd`) nach `/.js-history` (`js.rs:2017-2030`).
5. **ICU-Daten.** Liest das js-Binary ICU-Daten aus einer Datei, braucht es Dateiabbildungen (`mmap` mit fd). Erledigt mit V12 (unten).
6. **Signale.** libedit setzt Handler für SIGINT, SIGWINCH, SIGTSTP u. a.; zugestellt wird nichts. Strg+C beendet das Programm (`signal 2`) statt die Zeile zu verwerfen.

Geprüft, kein Handlungsbedarf: Reservierungen von 64 GiB (GC-Region, 2 × 32 GiB, Kopf und Ende per `munmap`) und 4 TiB + Rand (`PrimitiveStorage`) mit
`PROT_NONE|MAP_NORESERVE` passen in den Bereich ab `MmapBase` und kosten keine Frames; `mprotect`/`munmap` laufen über die Frames, nicht über die Länge.
`MADV_POPULATE_WRITE` (`-EINVAL`) wird ignoriert (`BlockAllocator.cpp:145-148`). `sysconf(_SC_NPROCESSORS_ONLN)` zählt die Maske aus `sched_getaffinity`.
Nicht an diesem Stand gelesen (mimalloc und Rust-`std` liegen außerhalb des Ladybird-Baums), nur erwartet: mimalloc öffnet `/sys/...`-Dateien optional,
Rust-`std` legt unter musl keine Stackwache für den Hauptthread an und braucht `sigaltstack`, `sigaction` und `poll` auf fd 0–2 (gibt es).

## Threads (V10)

Plattformunabhängig in `ProjektZeh/Processes/LinuxThreads.cs` und `LinuxUserProcess` (Hosttests `LinuxThreadsTests`), im Kernel in
`ProjektZeh/Runtime/CosmosLinuxRun.cs`. Gastprobe: `experiments/LinuxAbi/src/v10` (threads, thrxit, thrmain, thrsegv), Erwartungen aus Linux.

| Nr. | Aufruf | Verhalten |
| --- | --- | --- |
| 56 | `clone` | nur Threads: `CLONE_VM|FS|FILES|SIGHAND|THREAD|SYSVSEM` müssen alle gesetzt sein, dazu erlaubt `SETTLS`, `PARENT_SETTID`, `CHILD_SETTID`, `CHILD_CLEARTID`, `DETACHED`, Signalbyte (ignoriert wie bei Linux); `THREAD` ohne `SIGHAND` oder `SIGHAND` ohne `VM` `-EINVAL`; fork, vfork, posix_spawn und unbekannte Bits `-ENOSYS`; über 64 Threads oder nach `exit_group` `-EAGAIN`. Stack 0 heißt Stack des Elternthreads. TIDs ab PID+1, frei werdende TIDs beendeter Threads nach dem Umlauf bei 2^22 wieder ab 1 (PID und laufende übersprungen) |
| 435 | `clone3` | `-ENOSYS` (glibc fällt auf `clone` zurück) |
| 60 | `exit` | beendet nur den Thread: `clear_child_tid` wird 0 und ein Wartender geweckt (pthread_join); der letzte Thread beendet den Prozess mit seinem Code |
| 231 | `exit_group` | beendet alle Threads; futex-Wartende und Schlafende kehren mit `-EINTR` zurück, ein Lesen auf fd 0 bekommt Dateiende, dann verlassen alle den Ring 3. `clear_child_tid` der übrigen Threads bleibt ungeschrieben: Linux schreibt es nur, solange ein anderer den Adressraum nutzt, und das kann ohne zweiten Prozess, gemeinsamen Speicher oder ptrace niemand sehen |
| 202 | `futex` | WAIT/WAIT_BITSET schlafen wirklich, bis ein anderer Thread weckt (`0`), die Frist abläuft (`-ETIMEDOUT`; WAIT relativ, WAIT_BITSET absolut auf MONOTONIC oder mit `FUTEX_CLOCK_REALTIME` auf der Wanduhr) oder der Prozess endet (`-EINTR`); abweichender Wert `-EAGAIN`; WAKE/WAKE_BITSET wecken in Wartereihenfolge und liefern die Anzahl; REQUEUE/CMP_REQUEUE hängen um; WAKE_OP, PI-Futexe `-ENOSYS`; `FUTEX_PRIVATE_FLAG` ohne Wirkung (ein Adressraum) |
| 200, 234 | `tkill`, `tgkill` | Ziel prüfen (`-ESRCH`, `-EINVAL`), Signal 0 `0`; ignorierte Signale (SIG_IGN, SIGCHLD/CONT/URG/WINCH) `0`; SIGTSTP/TTIN/TTOU mit Standardaktion `0` und verworfen (verwaiste Prozessgruppe, wie Linux); blockierte Signale bleiben anhängig, bis `rt_sigprocmask` sie freigibt (dann Standardaktion) oder `rt_sigaction` sie ignoriert (verworfen); Standardaktion beendet den Prozess (Status `signal <n>`), SIGSTOP ebenfalls (kein Anhalten); mit Handler `-ENOSYS`, auch blockiert (keine Signalzustellung) |
| 62 | `kill` | V15: Prozesse des Laufs (Baum ab dem ersten Prozess); `pid > 0` ein Prozess (Zombie `0` ohne Wirkung, geerntet `-ESRCH`), `0` und `-<erste PID>` alle, `-1` alle außer Aufrufer und erstem Prozess, andere Gruppen `-ESRCH`; Zustellung wie `tgkill`, an einen anderen Prozess verlassen dessen Threads den Ring 3 (nicht der Aufrufer); während vfork gemerkt und nach `execve` dem neuen Abbild zugestellt (endet das Kind vor `execve`, verfällt es). Keine Rechteprüfung (eine UID je Lauf) |
| 35, 230 | `nanosleep`, `clock_nanosleep` | Uhren 0, 1, 4–7, 9, 11 (REALTIME, MONOTONIC, BOOTTIME, TAI, grobe), mit `TIMER_ABSTIME`; CPU-Zeituhren und unbekannte `-EINVAL`; nach vollem Schlaf `rem` 0 (V2c); mit Threads schläft der Aufruf auf der futex-Tabelle und endet mit `exit_group` (`-EINTR`, `rem` dann unberührt) |
| 34 | `pause` | schläft bis zum Prozessende (`-EINTR`), da keine Signale zugestellt werden |

Umsetzung und Grenzen:

- Je Linux-Thread ein Kernelthread auf derselben `UserPageTable`. Der Kindthread betritt den Ring 3 auf dem `syscall`-Befehl des Elternthreads (RIP − 2) mit
  genullten Registern; sein erster Aufruf übernimmt den gesicherten Frame des Elternthreads (rax 0, neuer Stack) statt verteilt zu werden. Das geht ohne
  Cosmos-Patch, weil der Rückweg aus `syscall` jedes Register aus dem Frame herstellt. FS-Basis je Thread (`CLONE_SETTLS`, `arch_prctl`).
- Der FPU-/SSE-Zustand des Kindes ist der Startzustand (FCW 0x37F, MXCSR 0x1F80), nicht die Kopie des Elternthreads (musl und glibc setzen nichts voraus).
- Alle Threads eines Programms laufen auf **einer** CPU (die erste der Menge des Programmthreads; Kinder erben sie): `UserPageTable` hat noch keinen TLB-Shootdown
  (N4) und lehnt `munmap`/`mprotect` ab, solange eine andere CPU auf der Tabelle läuft. Nebenläufig ja, parallel nein.
- Speicher (Karte, Frames, brk) schützt eine Sperre im Prozess; futex liest den Wert unter der Sperre der futex-Tabelle (Reihenfolge: futex-Tabelle vor
  Speicher vor `UserPageTable` vor `PageAllocator`). Schlafen in Scheiben von 50 ms, damit Abbruch und Zeitlimit jeden Thread erreichen.
- Ein Absturz in einem Thread (Ausnahme, ungültige Register, Fehler der Brücke) beendet den ganzen Prozess wie bei Linux. `pthread_exit` in `main` lässt die
  anderen weiterlaufen; der Prozess endet mit dem letzten Thread.
- Signalmaske, anhängige Signale und Alternativstack sind noch je Prozess, nicht je Thread; keine Signalzustellung (auch nicht pthread_cancel, `SIGCANCEL`). Ein anhängiges Signal, für das danach ein Handler gesetzt wird, bleibt anhängig und wird nie zugestellt; `rt_sigpending` fehlt.
- Dateien (V11): Deskriptortabelle, Arbeitsverzeichnis, `LinuxFileSystem` und die offenen Dateien schützt eine Sperre je Prozess (`ILinuxFiles.FileGate`). Jeder Datei- und Pfadaufruf läuft ganz unter ihr, auch `pread` (Position sichern, lesen, zurücksetzen) ist dadurch atomar; Dateiaufrufe verschiedener Threads laufen nacheinander, auch während einer langsamen Platte. Schreiben auf das Terminal und Lesen von fd 0 halten sie nicht (Lesen wartet auf Eingabe). Reihenfolge: Dateisperre vor Speichersperre und vor `StorageLock`, nie zusammen mit der futex-Tabelle. Grenze: Ersetzt ein Thread fd 0 per `dup2`, während ein anderer `read` darauf beginnt, kann dieser `-EBADF` sehen (die Art des Deskriptors wird vor der Sperre bestimmt).
- Ein Thread, der in `read` auf fd 0 wartet, endet beim Prozessende über das Dateiende der Sitzung; ohne Sitzung (Gastprobe) liest fd 0 ohnehin leer.

## /proc/self/exe und Dateiabbildung (V12)

Ladybird sucht seine ICU-Daten (`icudt78l.dat`, 27 MB) über `readlink("/proc/self/exe")` unter `<bindir>/../share/Lagom/icu/` und bildet sie mit
`mmap(PROT_READ, MAP_SHARED)` ab; die Datei wird danach geschlossen (`LibCore/MappedFile.cpp`).

- `readlink`/`readlinkat` auf `/proc/self/exe` oder `/proc/<eigene PID>/exe` (nach Normalisierung) liefern den Programmpfad im Prozess. `run` setzt ihn
  aus dem Pfad der Shell relativ zur Prozesswurzel: `cd /data/app`, `run bin/js` ergibt `/bin/js`. Liegt das Programm außerhalb der Wurzel
  (`run /data/t/js` aus `/data/app`) oder läuft der Prozess ohne Wurzel auf der Platte (Shell in `/` oder direkt auf einem Laufwerk), gibt es den
  Link nicht (`-ENOENT`). Sonst gibt es kein `/proc`.
- `mmap` mit Datei-fd ist eine **Kopie**: Der Bereich wird anonym belegt, ab dem Versatz (seitenausgerichtet, sonst `-EINVAL`) wie mit `pread` gefüllt,
  hinter dem Dateiende mit Nullen, und erst danach auf den gewünschten Schutz gesetzt. Die Dateiposition bleibt. Das ist `MAP_PRIVATE` bis auf einen
  Punkt: Spätere Änderungen an der Datei sieht die Abbildung nicht. `MAP_SHARED` geht deshalb nur ohne `PROT_WRITE`; beschreibbar geteilt bleibt
  `-ENODEV` (nichts flösse in die Datei zurück). Weitere Fehler: Terminal und Verzeichnis `-ENODEV`, nur zum Schreiben geöffnet `-EACCES`,
  unbekannter fd `-EBADF`. Seiten hinter dem Dateiende lesen sich als Nullen statt `SIGBUS`; über 2 GiB hinaus bleibt der Rest Null.
- `MAP_FIXED` mit Datei-fd liefert `-EINVAL` (Abweichung; der alte Bereich bleibt unberührt). Nur ein dynamischer Lader bräuchte es, und `PT_INTERP`
  wird abgelehnt. Scheitert die Kopie mittendrin (Speichermangel `-ENOMEM`, Lesefehler, Abbruch `-EINTR`), gibt `mmap` den Bereich frei und meldet den Fehler;
  eine teilweise gefüllte Abbildung bleibt nie zurück.
- Die Kopie belegt sofort Speicher in voller Länge (ICU: 27 MB von 512 MiB je Programm) und läuft unter der Dateisperre (V11), darin die Speichersperre.
- Geprüft im Gast (KVM, smp1): `js` findet und lädt die ICU-Daten; der nächste fehlende Aufruf ist `pipe2` (Ereignisschleife, `EventLoopImplementationUnix.cpp:165`).

## Pipes und sysinfo (V13)

Plattformunabhängig in `ProjektZeh/Processes/Syscalls/LinuxPipe.cs`, `LinuxFileSyscalls.cs` und `LinuxSyscallsMore.cs` (Hosttests `LinuxPipeTests`); Speicher im
Kernel aus `CosmosSystemInfo.PhysicalMemory` (`CosmosLinuxRun.CosmosLinuxHost`). Anlass: der strace von js (V4) zeigt `pipe2` und `sysinfo`; LibCore weckt seine
Ereignisschleife aus anderen Threads über eine Pipe.

| Nr. | Aufruf | Verhalten |
| --- | --- | --- |
| 22, 293 | `pipe`, `pipe2` | Pipe innerhalb des Prozesses (kein fork): kleinste freie Nummern für Lese- und Schreibende; Flags `O_CLOEXEC` (angenommen, nicht geführt: kein `execve`) und `O_NONBLOCK`, andere (`O_DIRECT`) `-EINVAL`; `-EFAULT` gibt beide Nummern wieder frei, `-EMFILE` ebenso |
| 0, 19 | `read`, `readv` auf dem Leseende | blockiert ohne Daten bis zu einem Schreiben, bis kein Schreibende mehr offen ist (`0`, EOF) oder zum Abbruch (`-EINTR`); mit `O_NONBLOCK` `-EAGAIN`; liefert das Verfügbare, höchstens 4096 Bytes je Aufruf; auf dem Schreibende `-EBADF` |
| 1, 20 | `write`, `writev` auf dem Schreibende | Puffer 64 KiB; bis 4096 Bytes (`PIPE_BUF`) ganz oder gar nicht; voll blockiert es, mit `O_NONBLOCK` `-EAGAIN`; ohne Leseende `-EPIPE` (kein `SIGPIPE`, Signale werden nicht zugestellt); auf dem Leseende `-EBADF` |
| 7 | `poll` | Leseende `POLLIN` mit Daten, `POLLHUP` ohne Schreibende; Schreibende `POLLOUT` bei mindestens 4096 Bytes Platz, `POLLERR` ohne Leseende; wartet auf Pipe-Enden ohne Dateisperre und wacht beim Schreiben, Lesen oder Schließen eines anderen Threads auf |
| 3 | `close` | erst das letzte `close` eines Endes (nach `dup`) meldet dem anderen EOF beziehungsweise `EPIPE`; Ungelesenes wird mit dem Leseende verworfen |
| 5, 8 | `fstat`, `lseek` | `S_IFIFO` mit 0600, Größe 0; `lseek` `-ESPIPE` |
| 99 | `sysinfo` | `uptime` aus der monotonen Uhr in Sekunden, `totalram`/`freeram` in Bytes (`mem_unit` 1) aus dem Seitenverwalter von Cosmos, `procs` 1; Lastwerte, Swap, `sharedram`, `bufferram` 0 |

Grenzen: Ein `write` über 4096 Bytes mit `O_NONBLOCK` wird in Stücken zu 4096 Bytes geschrieben; ist weniger als ein Stück frei, kommt `-EAGAIN` (Linux schriebe
den freien Rest). Schließt ein Thread das Schreibende, während ein anderer noch hineinschreibt, sieht der Leser schon EOF (Linux hält die Datei bis zum Ende
des Aufrufs). `ioctl(FIONREAD)`, `select`, `ppoll` und `epoll` gibt es nicht. Die Pipe-Sperre ist eine Blattsperre: Dateisperre vor Pipe-Sperre, darin weder
Benutzerspeicher noch eine andere Sperre. `read` auf dem Leseende prüft den Puffer vor dem Entnehmen; gibt ein anderer Thread ihn danach per `munmap` oder
`mprotect` frei, scheitert das Zurückschreiben mit `-EFAULT`, und die entnommenen Bytes sind verloren (Abweichung: Linux rückt in `pipe_read` erst nach erfolgreichem Kopieren vor und behält sie).
