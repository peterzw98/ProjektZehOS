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
| 334 | `rseq` | `-ENOSYS` (Laufzeitbibliotheken fallen zurück) |

Grenze: `write` verlässt sich auf `ILinuxDescriptor.Write` ≤ angebotene Länge (größere Rückgaben werden nicht abgefangen); je Aufruf ein 4-KiB-Puffer.

Nicht in S1: Dateien (`open`, `read`), Signale, Threads (`clone`, `futex`), `MAP_FIXED`, Dateiabbildungen.

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
