# Satz `tools`: toybox und Rust (N1-E3)

Vergleichsprogramme aus **fremder** Quelle, damit der Systemaufruf-Satz nicht nur an eigenem C-Code gemessen wird. Sie laufen wie S1 und S3 mit
`run-linux.py` (`--bin out/tools --expected expected/tools`); der Vergleicher und `build.sh` der Ebene darüber sind unverändert.

## Bau

Voraussetzungen auf dem Rechner: `sh`, **`bash`** (`scripts/single.sh` von toybox ruft es), `make`, `curl` (nur ohne `TOYBOX_TARBALL`), `sha256sum`, `tar`, `sed`, `nproc`.

```sh
TOYBOX_TARBALL=/pfad/toybox-0.8.15.tar.gz RUSTC=/pfad/rustc CC=/pfad/musl-gcc experiments/LinuxAbi/build.sh tools
```

| Eingabe | Stand | Prüfung |
| --- | --- | --- |
| toybox (0BSD) | 0.8.15, `https://landley.net/toybox/downloads/toybox-0.8.15.tar.gz` | SHA-256 `44e2bab43dfe2c171743297631ce558c6d9ed38b87a66fd751cef617574b375e` (`build.sh` bricht bei Abweichung ab; zweite Quelle: Void-Linux-Paketrezept `srcpkgs/toybox/template` (master, 25.11.2026) nennt dieselbe Summe; das Archiv selbst ist nicht signiert, landley.net veröffentlicht keine Prüfsumme dazu) |
| Rust | 1.100.0 (`0ef1bc386`, 2026-11-09), Standalone-Archive vom 2026-11-12 | `rust-1.100.0-x86_64-unknown-linux-gnu.tar.xz` SHA-256 `98ee6bec240e1cfe944f17e7092412dc5ef6e008484bb668c6936d35a7d15acd` und `rust-std-1.100.0-x86_64-unknown-linux-musl.tar.xz` SHA-256 `9d5211bc7b40195f9700a6f9133831832a32f50d0e2a2c0ae8af801e7656de5c`, jeweils aus dem Manifest `channel-rust-stable.toml` und mit `sha256sum -c` bestätigt; Installation mit `install.sh --prefix` in ein eigenes Verzeichnis, ohne rustup |
| musl | 1.2.5, `https://musl.libc.org/releases/musl-1.2.5.tar.gz` | SHA-256 `a9a118bbe84d8764da0ea0d28b3ab3fae8477fc7e4085d90102b8596fc7c75e4`; aus Quelle mit `./configure --prefix=…`; `CC` ist dessen `musl-gcc`. Das Debian-Paket `musl-tools` (ebenfalls 1.2.5) sollte dasselbe liefern; verglichen wurde es hier nicht |

Rust baut mit dem eigenen musl des Rust-Archivs (`rust-std`), nicht mit `CC`; `CC` und `gcc` dienen nur als Linker. Die Rust-Programme sind nicht-PIE und statisch
(`-C relocation-model=static -C target-feature=+crt-static`, `panic=abort`, ohne Symbole). Alles liegt unter `out/` (ignoriert), nichts davon im Repository.

## Programme

| Programm | Satz | Inhalt |
| --- | --- | --- |
| `tbs_cat`, `tbs_ls`, `tbs_wc` | S1 | toybox je Befehl allein gebaut (`scripts/single.sh`, kein `execve`), Aufruf mit den festen Argumenten `a1`, `b 2` (fehlende Dateien: Fehlermeldung, Exit 1) |
| `rs_file_time` | S1 | Rust `std`: Datei anlegen/schreiben/lesen/anhängen/`seek`/umbenennen/löschen, Verzeichnis, Fehlernummern (2, 17), Schlaf ≥ 20 ms, `Instant`, `SystemTime` |
| `tb_cat`, `tb_ls`, `tb_wc` | S3 | C-Treiber legt Dateien im leeren Arbeitsverzeichnis an und startet das Mehrfachprogramm `tb/toybox` (Verknüpfungen `tb/cat` …) per `execve`; Eingabe, Pfade und Ausgabe sind fest |
| `tb_sh` | S3 | toysh (`sh -c`): Pipeline, Umleitung, Unterschale, Exitcode (7), Schleife, `fork`/`pipe`/`wait4`; Befehle über `PATH=tb/` |
| `rs_thread` | S3 | Rust `std`: 4 Threads mit Barriere und `Mutex`, Kanal, `thread_local`, benannter Thread, `scope` |

`tb_*` finden `tb/` über `argv[0]` (Aufruf mit Pfad, wie `run-linux.py` und `strace` es tun). Die Ausgabe enthält nie PIDs, Adressen oder Zeiten.

## Aufrufe je Programm (`strace -f -c`, Linux 6.12.117+deb13, strace 6.13)

`execve` stammt in S1-Programmen nur vom Start durch `strace`; `exit_group` führt `strace -c` nicht auf.

| Programm | Aufrufe |
| --- | --- |
| `tbs_cat` | `arch_prctl brk execve mmap open set_tid_address umask writev` |
| `tbs_wc` | wie `tbs_cat` |
| `tbs_ls` | `arch_prctl brk execve ioctl lstat mmap set_tid_address stat umask writev` |
| `rs_file_time` | `arch_prctl brk clock_nanosleep close execve fcntl fstat getdents64 lseek mkdir mmap mprotect munmap open poll read rename rmdir rt_sigaction rt_sigprocmask set_tid_address sigaltstack stat unlink write` |
| `tb_cat`, `tb_wc` | `arch_prctl brk close execve fcntl mmap open read rt_sigaction rt_sigprocmask set_tid_address umask write writev` |
| `tb_ls` | `arch_prctl brk close dup execve fcntl fstat getdents64 ioctl lstat mkdir mmap munmap newfstatat open openat rt_sigaction rt_sigprocmask set_tid_address stat umask write writev` |
| `tb_sh` | `arch_prctl brk chdir close dup dup2 execve fcntl fork fstat getcwd getdents64 geteuid getpid getppid getuid ioctl lseek mmap munmap newfstatat open pipe read readlinkat rt_sigaction rt_sigprocmask set_tid_address stat umask uname wait4 write writev` |
| `rs_thread` | `arch_prctl brk clone close execve fcntl fstat futex gettid lseek mmap mprotect munmap open poll prctl read rt_sigaction rt_sigprocmask sched_getaffinity set_tid_address sigaltstack stat write` |

Insgesamt 49 verschiedene Aufrufe. **Nicht in S1 bis S3 des Entwurfs** (zur Ergänzung oder Nachweis, dass sie verzichtbar sind):
`umask` (toybox, alle `tb*`), `readlinkat` (`tb_sh`), `clock_nanosleep` (Rust-`sleep`, statt `nanosleep`), `prctl` und `sched_getaffinity` (Rust-Threads; `prctl` benennt den Thread).
**Nicht in der Liste, aber in ZehOS nötig:** `strace -c` zählt `clock_gettime` nicht, weil es hier über das vDSO läuft (`Instant`, `SystemTime`, toybox-Zeitstempel). Ohne vDSO
ruft musl `clock_gettime` als echten Systemaufruf; ZehOS muss ihn deshalb bereitstellen (Rust-`Instant`/`SystemTime`, `rs_file_time`, `rs_thread`, toybox-`ls`/`sh`).
In S2 stehen `dup`, `dup2` (`tb_ls`, `tb_sh`). Zu S3 gehören `fork pipe wait4 getppid clone futex execve`; deshalb liegen die `tb_*`-Treiber in S3, obwohl `cat`, `ls`, `wc` selbst S1 sind (`tbs_*`).

## Stand der Erwartungen

`expected/tools/` entstand mit `--update` auf Linux `6.12.117+deb13-amd64` (Debian 13, x86-64). Danach liefen drei weitere Läufe ohne Abweichung (9 Programme, Exit 0 des Vergleichers).
Gegen andere Kernel-, toybox-, Rust- oder musl-Versionen nicht gegengeprüft; `ls`-Reihenfolge und Fehlertexte kommen von toybox 0.8.15, die Rust-Ausgabe von `std` 1.100.0.

Hinweis zum Vergleicher: `run-linux.py` sucht `strace` mit `PATH=/usr/bin:/bin` der festen Umgebung; liegt `strace` nur nutzerlokal, findet er es nicht (Warnung „nicht auswertbar“, Vergleich selbst läuft).
