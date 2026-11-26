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
| 7 | `poll` | höchstens 1024 Einträge; offene fd meldet nur POLLOUT (Lesebereitschaft unbekannt: nie POLLIN), unbekannte fd POLLNVAL; ohne bereite Einträge und mit Frist > 0 schläft sie die Frist über `ILinuxHost.SleepNanos` ab (ohne Host nicht); Frist -1 (unendlich) schläft nie, Ergebnis 0 |
| 28 | `madvise` | Hinweise 0-3, 14, 15 liefern 0; DONTNEED/FREE und Unbekanntes `-EINVAL` (Seiten nullen geht noch nicht); prüft keine Abbildung (Linux `-ENOMEM`) |
| 202 | `futex` | Einzelthread: WAIT/WAIT_BITSET mit abweichendem Wert `-EAGAIN`, mit Frist `-ETIMEDOUT` (ohne zu schlafen), ohne Frist `-EDEADLK` (Linux hängt; glibc bricht bei diesem unbekannten Fehler ab, musl dreht in einer Schleife); Wert wird vor der Frist gelesen: kaputte Frist bei abweichendem Wert liefert `-EAGAIN` statt `-EFAULT`/`-EINVAL` (Abweichung); WAKE* 0; CMP_REQUEUE prüft den Wert; WAKE_OP `-ENOSYS` |
| 334 | `rseq` | `-ENOSYS` (Laufzeitbibliotheken fallen zurück) |

Grenze: `write` verlässt sich auf `ILinuxDescriptor.Write` ≤ angebotene Länge (größere Rückgaben werden nicht abgefangen); je Aufruf ein 4-KiB-Puffer.

Nicht in S1: Dateien (`open`, `read`), Signalzustellung, Threads (`clone`, echtes `futex`-Warten), `MAP_FIXED`, Dateiabbildungen.

## Prozess und Terminal (V3)

- `LinuxUserProcess` (`ProjektZeh/Processes/LinuxUserProcess.cs`, plattformunabhängig, Hosttests `LinuxUserProcessTests`): `AddressSpaceMap` plus `IUserPages`
  (Frames und Abbildung). Beim Laden bekommen nur Seiten mit Dateibytes und der vorbereitete Teil des Stacks einen Frame; alle anderen (.bss, übriger Stack,
  `brk`, `mmap`) entstehen genullt beim ersten Zugriff (Seitenfehler oder Kernelzugriff über `IUserMemory`, der dieselben Rechte prüft und alles oder nichts kopiert).
  `mprotect` behält Frame und Inhalt (auch über `PROT_NONE`). `PROT_WRITE|PROT_EXEC` scheitert (W^X: `mmap` `-ENOMEM`, `mprotect` `-ENOMEM`).
  Anonyme Abbildungen ab 16 TiB (`MmapBase`), `brk` wächst bis an die nächste Belegung; höchstens 512 MiB Frames je Prozess, darüber Abbruch.
- Deskriptoren 0–2: `TerminalDescriptor` schreibt zeilenweise ins Terminal (UTF-8 erst am Zeilenende, Zeilen über 4096 Bytes werden geteilt); jeder andere ist `-EBADF`.
- Brücke im Kernel: `ProjektZeh/Runtime/CosmosLinuxRun.cs`, nur mit `-p:ZehOSLinuxRun=true`; dann gibt es `run <datei> [argumente...]` (ELF bis 128 MiB,
  ein Programm zugleich, eigener Kernelthread mit eigener `UserPageTable`, Zeitgrenze 120 s, danach `signal 9`). AT_RANDOM aus `HardwareRandom` (ohne Ersatz),
  AT_HWCAP aus CPUID. Ein Fehler im Ring 3, den der Prozess nicht als Seitenfehler bedient, beendet nur das Programm (Status `signal <n>` wie Linux: #PF/#GP 11,
  #UD 4, #DE 8); eine Ausnahme in der Brücke ebenso (`signal 9`).
- Gastprobe: `-p:ZehOSLinuxRunProbe=true -p:ZehOSLinuxRunBundle=<bündel>` startet beim Boot die Programme eines Bündels (`scripts/test-linux-run.py`).
  Pin: `story/ladybird-cosmos` (Patches `user-cr3` u. a.).

## Dateisystem der Prozesse (V7)

- `LinuxFileSystem` (`ProjektZeh/Processes/Vfs/`, plattformunabhängig, Hosttests `LinuxFileSystemTests`) implementiert `ILinuxFileSystem` und `ILinuxFile`
  (Schnittstellen von V2-Dateien, `ProjektZeh/Processes/Syscalls/ILinuxFileSystem.cs`). Die Wurzel "/" liegt auf einem `ILinuxFileStore`:
  `DirectoryFileStore` (Ordner auf der Datenplatte, jeder Zugriff unter `StorageLock`) oder `MemoryFileStore` (Arbeitsspeicher, Tests).
  Darüber virtuell `/dev` mit `null` (liest EOF, verwirft, `lseek` 0) und `tty` (schreibt auf den Terminal-Deskriptor des Prozesses, liest EOF, `lseek` `-ESPIPE`).
- Ausbruchschutz: Pfade kommen absolut und normalisiert vom Verteiler; ".." darin ergibt `-EINVAL` (nicht selbst gedeutet). Symlinks löst `LinuxFileSystem`
  Teil für Teil selbst auf, der Speicher folgt keinem Link (im Kernel `CosmosLinuxDiskLinks` über Cosmos-Inodes, weil Cosmos-System.IO Links in jedem Teil folgt).
  Absolute Linkziele beginnen an der Prozesswurzel, ".." im Linkziel ist physisch und endet an der Wurzel (wie `chroot`). Ein Mount unter der Wurzel ist `-EACCES`.
- Grenzen: Pfad ab 4096 Bytes und Teil über 255 Bytes (UTF-8) `-ENAMETOOLONG`, auch für aufgelöste Pfade; 40 Links `-ELOOP`; je Auflösung höchstens 4096 Teile
  (Eingabe und Linkziele), sonst `-ELOOP` (Abweichung: Linux kennt nur die Linkgrenze); 1024 offene Dateien je Dateisystem `-EMFILE`;
  Datei auf der Platte höchstens 1 GiB (`-EFBIG`, Schreiben bis zur Grenze wird gekürzt); `MemoryFileStore` 16 MiB je Datei, 64 MiB und 65 536 Einträge gesamt (`-ENOSPC`).
- Rechte: der Prozess gilt als Eigentümer, geprüft werden die Eigentümerbits (r, w, x); fehlendes x auf einem Zwischenverzeichnis, fehlendes w im Elternverzeichnis
  beim Anlegen, Entfernen und Umbenennen `-EACCES`. Platte: Ordner 0755, Dateien 0644 (schreibgeschützt 0444), also ohne Ausführungsbit (`access(X_OK)` `-EACCES`).
- Namen auf der Platte: was FAT oder Cosmos anders deuten könnte (`:`, `\`, Steuerzeichen, abschließender Punkt oder Leerzeichen, `.zehos-`-Systemnamen) gibt es nicht:
  Abfragen melden es als fehlend, Anlegen `-EINVAL`, Listen lassen es weg. Groß-/Kleinschreibung bestimmt das Laufwerk (`NameComparison`).
- `open`-Flags: Zugriff, `O_CREAT O_EXCL O_TRUNC O_APPEND O_DIRECTORY O_NOFOLLOW` wirken; `O_NOCTTY O_NONBLOCK O_SYNC O_DSYNC O_LARGEFILE O_NOATIME O_CLOEXEC`
  werden angenommen (CLOEXEC gehört zur Deskriptortabelle); `O_TMPFILE` `-EOPNOTSUPP`, übrige (`O_DIRECT`, `O_PATH`, `O_ASYNC`) `-EINVAL`;
  `O_CREAT|O_DIRECTORY` `-EINVAL` wie Linux ab 6.4. Eine entfernte, offene Datei bleibt les- und abfragbar (`MemoryFileStore`; Platte im Gast noch offen).
- Offen: Anbindung an `run` (Prozesswurzel je Programm auf der Datenplatte), Gastlauf von `DirectoryFileStore` und `CosmosLinuxDiskLinks`, `/dev/zero`, `/dev/urandom`,
  `/tmp` im Speicher, Eingabe über `/dev/tty`.
