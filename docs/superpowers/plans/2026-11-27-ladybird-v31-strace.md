# V31: strace-Abgleich des musl-Bündels gegen LINUX_ABI

Auftrag `agent-nweh-lead1` (#476/#477, 27. November 2026), ausgeführt von `agent-peter-senior` auf MSI.
Abgleich gegen `docs/LINUX_ABI.md` und Code auf `story/ladybird` `cfccb015`.

## Aufnahme

- Bündel `lb-headless-99fb75b7.tar.gz` (Ladybird `6fba5b78`, musl), Aufruf wie im `README.md` des Bündels:
  `Hub --headless=screenshot --force-cpu-painting --window-width 798 --window-height 519 --screenshot-delay 5 --screenshot-path … file://…/static.html`,
  gestartet über `lib/ld-musl-x86_64.so.1`.
- Host: MSI, Debian 13, Linux 6.12.117, strace 6.13 (Debian-Paket, ohne root entpackt).
- Zwei Eingriffe, weil der Host kein musl-System ist (beide ändern keine Aufrufe der Programme):
  - Die Hilfsprogramme haben `/lib/ld-musl-x86_64.so.1` als ELF-Interpreter. In der Kopie ist `PT_INTERP` auf den gleich langen Pfad
    `/tmp/ld-musl-x86_64.so.1` umgeschrieben (Link auf den Lader des Bündels).
  - Der Hub gibt `LD_LIBRARY_PATH` nicht an die Kinder weiter. Deshalb läuft alles in einem Benutzer- und Mount-Namensraum, in dem ein Overlay auf
    `/etc` die Datei `/etc/ld-musl-x86_64.path` mit dem `lib`-Ordner des Bündels einblendet; innen wieder UID 1000.
- Ergebnis: `exit 0`, Screenshot geschrieben (1 053 751 Bytes), 8 Prozesse (Hub, SystemProbe, Compositor, 2× RequestServer, 3× WebContent).
- Läufe: `strace -f -ff -tt -yy -s 200 -X raw` (Einzelspuren), `strace -f -c` (Zählung, 15 229 Aufrufe, 86 verschiedene),
  `--disable-sandbox` mit `-e trace=execve,seccomp,landlock_restrict_self,prctl` und `-e trace=open,openat`.

## Lücken nach Wirkung

### P1: beendet den Hub oder einen Dienst im Gast

1. **`mmap(PROT_READ|PROT_WRITE, MAP_SHARED)` einer gewöhnlichen Datei.** sqlite betreibt `Ladybird.db` (Hub) und
   `RequestServer/…/Cache/INDEX.db` im WAL-Modus. Für den gemeinsamen Index öffnet es `<db>-shm` (`O_RDWR|O_CREAT|O_NOFOLLOW|O_CLOEXEC`),
   prüft mit `F_GETLK`/`F_SETLK` (Byte 128), `ftruncate(3)`, schreibt je 4 KiB ein Nullbyte (`pwrite` bei 4095 … 32767) und bildet dann
   `mmap(NULL, 32768, PROT_READ|PROT_WRITE, MAP_SHARED, shm, 0)` ab. ZehOS gibt dafür `-ENODEV` (`LinuxSyscalls.cs:352`, LINUX_ABI V12:
   „beschreibbar geteilt bleibt `-ENODEV`“). sqlite meldet `SQLITE_IOERR_SHMMAP` = „disk I/O error“, der Hub endet mit `exit 1`.
   Das ist die Ursache des Gastbefunds von `agent-peter-engineer2` (cfccb015). Andere Dateien bildet niemand beschreibbar geteilt ab.
   Abhilfe: (a) beschreibbares `MAP_SHARED` für Dateien über geteilte Frames je Datei wie bei memfd (V17), Rückschreiben beim letzten `munmap`
   oder `msync`; für `-shm` genügt Kohärenz zwischen den Abbildungen. (b) Ladybird-seitig `PRAGMA locking_mode=EXCLUSIVE` (WAL ohne `-shm`)
   oder `journal_mode=DELETE`.
2. **`ioctl(FIONBIO)` auf Unix-Sockets** (0x5421, 40×, alle Prozesse). `LibCore/Socket.cpp:234-237` (`PosixSocketHelper::set_blocking`) und
   `LibIPC/TransportSocket.cpp:71,86` (`TRY(socket->set_blocking(false))`). ZehOS: `DoIoctl` kennt nur Terminals, sonst `-ENOTTY`
   (`LinuxSyscallsTerminal.cs:68-79`). Folge: Aufbau jeder IPC-Verbindung scheitert. Im Hub kommt der erste `FIONBIO` 77 ms nach dem Öffnen von
   `Ladybird.db`, also die **nächste** Hürde nach Punkt 1. Abhilfe: `FIONBIO` (setzt/löscht `O_NONBLOCK` wie `F_SETFL`) und `FIONREAD`
   (`TransportSocket.cpp:591`, in dieser Aufnahme nicht aufgerufen) für Sockets und Pipes.
3. **Sandbox der Dienste.** RequestServer, WebContent und Compositor rufen `prctl(PR_SET_NO_NEW_PRIVS)` (ZehOS `-EINVAL`),
   `landlock_create_ruleset`/`landlock_add_rule`/`landlock_restrict_self` und `seccomp(SECCOMP_SET_MODE_FILTER, TSYNC)` (ZehOS `-ENOSYS`) sowie
   352× `open(O_PATH)` für die Landlock-Regeln (ZehOS `-EINVAL`). Alles steht unter `TRY` (`RequestServer/SandboxLinux.cpp:114-147`, `main.cpp`
   der Dienste), der Dienst endet. Abhilfe ohne Kernelarbeit: Hub mit **`--disable-sandbox`** starten. Gemessen: Der Hub reicht die Option an alle
   sieben Kinder weiter, danach 0× `seccomp`, 0× `landlock_*`, 0× `PR_SET_NO_NEW_PRIVS`, 0× `O_PATH`, `exit 0` mit Screenshot.

### P3: Fehlerweg, den das Programm abfängt

| Aufruf | Anzahl | ZehOS | Wirkung |
| --- | --- | --- | --- |
| `pwritev2(…, RWF_NOAPPEND)` (328) | 86 | `-ENOSYS` (Standardweg) | musl `pwrite` fällt bei `-ENOSYS` auf `pwrite64` zurück; `F_GETFL` meldet bei Dateien kein `O_APPEND`, also kein `-EOPNOTSUPP` |
| `link` (fontconfig `*.LCK`) | 3 | `-EPERM` (V28b) | fontconfig nimmt `mkdir` als Sperre (LINUX_ABI V28b) |
| `socket(AF_INET6, SOCK_DGRAM)` | 8 (RequestServer) | `-EAFNOSUPPORT` | Resolver; für `file://` ohne Bedeutung, für Netzseiten zu prüfen |
| `membarrier(REGISTER_PRIVATE_EXPEDITED)` | 7 | `-ENOSYS` | Rückfallweg des Aufrufers nicht geprüft |
| `fadvise64(WILLNEED)` | 2 | `-ENOSYS` | fontconfig wertet den Rückgabewert nicht aus |
| `mremap` | 192 (WebContent) | `-ENOMEM` | auch unter Linux 186× `-ENOMEM`, 6× `-EFAULT`: gleiches Verhalten |
| `prlimit64(RLIMIT_NOFILE, cur=min(…, max))` | 8 | `0` bei 1024/1024 | `set_resource_limits` begrenzt auf `rlim_max`, setzt also den geltenden Wert |

### Abgedeckt (kein Handlungsbedarf aus dieser Aufnahme)

`fcntl` (`F_SETFD`, `F_GETFL`, `F_SETFL`, `F_DUPFD_CLOEXEC`, `F_GETLK`, `F_SETLK`, `F_ADD_SEALS`, `F_GET_SEALS`), `flock`, `fsync`, `ftruncate`,
`pread64`, `stat`/`lstat`/`fstat`, `statfs`/`fstatfs`, `unlink`, `rename`, `mkdir`, `chmod`/`fchmod`, `getdents64`, `readlink`, `open`-Flags
`O_RDONLY|O_WRONLY|O_RDWR`, `O_CREAT`, `O_EXCL`, `O_TRUNC`, `O_NOFOLLOW`, `O_DIRECTORY`, `O_NONBLOCK`, `O_CLOEXEC`, `O_LARGEFILE`;
`mmap` (privat, `MAP_SHARED` nur lesend, memfd geteilt), `madvise(MADV_FREE)`, `memfd_create(MFD_CLOEXEC|MFD_ALLOW_SEALING)`,
`getrandom` (0, `GRND_NONBLOCK`, `GRND_INSECURE`), `setsockopt(SO_SNDBUF/SO_RCVBUF)`, `socketpair`, `sendmsg`/`recvmsg`, `eventfd2`, `pipe2`,
`inotify_init1`/`inotify_add_watch` (`/etc/localtime`, `IN_MODIFY|IN_DELETE_SELF|IN_DONT_FOLLOW`), `prctl(PR_SET_NAME, PR_GET_NAME, PR_SET_PDEATHSIG)`,
`sched_getaffinity`, `sysinfo`, `clone`, `execve`, `wait4`, `setpgid`, `close_range`, `ppoll`/`poll`, `futex`.

## Grenzen

- Nur Host (Linux 6.12, ext4); nichts im Gast geprüft. Fehlerwerte von ZehOS stammen aus Code und LINUX_ABI auf `cfccb015`, nicht aus einem Gastlauf.
- Nur `static.html` über `file://`; Netzseiten (DNS, `AF_INET6`, TLS) nicht aufgenommen.
- Unter Linux laufen die Dienste mit Sandbox; Aufrufe, die die seccomp-Regeln dort verbieten (RequestServer meldete `pwritev2`), fallen unter
  Linux anders aus als im Gast ohne Sandbox.
- Rückfallwege von `membarrier` und die Reihenfolge weiterer Hürden nach Punkt 2 sind nicht belegt; das zeigt erst der nächste Gastlauf.
