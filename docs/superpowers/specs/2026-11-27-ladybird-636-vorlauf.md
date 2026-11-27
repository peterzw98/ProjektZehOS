# Ladybird #636-Vorlauf: Systemaufrufe und Dateien für headless + WebContent

Lesestudie ohne Code und ohne Lauf (agent-peter-senior, 27.11.2026). Ladybird `6fba5b78a104` (teilweiser Klon, nur gelesen), ZehOS
`story/ladybird` `92daa101b` (Verteiler `ProjektZeh/Processes/Syscalls/*.cs`, `docs/LINUX_ABI.md`). **BELEGT** = steht im Quelltext,
**HYPOTHESE** = Schluss, im Gast nicht geprüft. Zeilen sind Datei:Zeile im Ladybird-Stand.

## Ablauf beim Start (headless)

- `--headless` schaltet die Profilkoordination ab (`LibWebView/Application.cpp:677`): kein `flock`, keine PID-Datei, kein
  `create_ipc_socket` mit `fchmod`/`bind` unter `XDG_RUNTIME_DIR` (`BrowserProcess.cpp:170,471`, `Process.cpp:402-433`). BELEGT
- Beim Start immer: RequestServer (`Application.cpp:1984`), Compositor (`:1986`), SystemProbe (`:2061`); WebContent beim ersten Tab (`:1183`),
  ImageDecoder erst beim ersten Bild. BELEGT
- Jeder Helfer startet über `Core::Process::spawn` → unter Linux **nicht** posix_spawn, sondern eigenes `fork_and_exec`
  (`LibCore/Process.cpp:336-412`): `mmap`+`mprotect` für den Kindstack, `pthread_sigmask` alles, `clone(CLONE_VM|CLONE_VFORK|SIGCHLD)` mit
  eigenem Stack (`:391`). Das Kind (`:281-333`): rohes `rt_sigaction` für 1..NSIG, `prctl(PR_SET_PDEATHSIG, SIGKILL)` + `getppid`
  (`:308-313`, `die_with_parent` ist für alle Helfer gesetzt, `LibWebView/Process.cpp:206`), `setpgid(0,0)` (`:315`, ebenfalls gesetzt `:209`),
  Dateiaktionen (`open("/dev/null")` auf fd 0, `Process.cpp:216`), `close_range(next, ~0U, 0)` (`:242-262`), `rt_sigprocmask`, `execve`.
  Fehler im Kind gehen über den geteilten Speicher zurück (`context.child_errno`), der Elternteil erntet mit `waitpid`. BELEGT
- IPC: `socketpair(AF_UNIX/AF_LOCAL, SOCK_STREAM|SOCK_CLOEXEC)` (`HelperProcess.cpp:104-105`, `Process.cpp:307`, `TransportSocket.cpp:79`),
  fd-Nummern über die Umgebung `SOCKET_TAKEOVER` (`Process.cpp:296,318`), Dateien mit `SCM_RIGHTS`, IO-Threads in Rust (`LibIPC/Rust/src/transport_socket.rs:116-118`,
  dupliziert den Socket). Geteilter Speicher `memfd_create` + Seals + `mmap(MAP_SHARED)` (`LibCore/System.cpp:236-300`, `AnonymousBuffer.cpp:23,54,114`). BELEGT
- Ereignisschleife unter Linux: **`ppoll`** mit Zeitspanne oder NULL (unendlich), Weckpipe `pipe2(O_CLOEXEC|O_NONBLOCK)`
  (`LibCore/EventLoopImplementationUnix.cpp:162,256-267`). `poll` nur auf anderen Systemen. BELEGT

## Lücken gegen ZehOS (Rangfolge nach Wirkung)

| Nr. | Aufruf / Datei | Ladybird | ZehOS heute | Folge |
| --- | --- | --- | --- | --- |
| 1 | `ppoll` (271) | jede Ereignisschleife, jeder Prozess (`EventLoopImplementationUnix.cpp:264`) | nicht im Verteiler → `-ENOSYS` | `wait_for_file_descriptors` liefert Fehler, Schleife bricht ab (MUST); trifft Hub/UI, WebContent, alle Helfer. Muss `DoPoll` samt unendlichem Warten auf Pipes/Sockets teilen; `sigmask` NULL reicht zunächst |
| 2 | `prctl(PR_SET_PDEATHSIG)` (157, Option 1) | jedes Helferkind vor `execve` | `prctl` kennt nur `PR_SET_NAME`/`PR_GET_NAME`, sonst `-EINVAL` (LINUX_ABI.md V9) | `report_errno_and_exit` → jeder Helferstart scheitert mit `EINVAL`. Mindestens annehmen (0) und Signal beim Ende des Elternteils zustellen oder als Abweichung dokumentieren |
| 3 | `close_range` (436) | jedes Helferkind (`Process.cpp:243`) | fehlt → `-ENOSYS` | Ausweichweg öffnet `/proc/self/fd` (`:201`), das es nicht gibt → `ENOENT`, Start scheitert. `close_range(first,last,0)` genügt (Flags 0) |
| 4 | `eventfd2` (290) | HYPOTHESE: libcurl-Wecker (`curl_multi_wakeup`, bei `HAVE_EVENTFD`), RequestServer startet immer | fehlt | RequestServer könnte schon beim Start scheitern; im Bau von #636 prüfen (`curl_config.h`), sonst `socketpair`-Weg |
| 5 | `fstatfs`/`statfs` (138/137) | HYPOTHESE: fontconfig `FcIsFsMmapSafe` für Cachedateien | fehlen | weich: fontconfig liest dann ohne mmap |
| 6 | fontconfig-Dateien | `FcInit()` mit `VERIFY` im Hub (`LibGfx/Font/GlobalFontConfig.cpp:28-29`), Font-Verzeichnisse aus der Konfiguration (`FontDatabase.cpp:105`) | keine `/etc/fonts`, kein Cache-Ordner | HYPOTHESE: ohne `fonts.conf` lädt fontconfig eine leere Konfiguration (keine Schrift); `fonts.conf` mit `<dir>` auf das Bundle und `XDG_CACHE_HOME` auf `/tmp` beilegen. Nur relevant, wenn `USE_FONTCONFIG` im musl-Bau an ist |
| 7 | `socket(AF_INET)`, `SOCK_DGRAM`, `connect`, DNS (UDP) | nur RequestServer bei `http(s)` (libcurl, LibDNS) | nur `AF_UNIX` (V16), kein `SOCK_DGRAM` | für `file:`/`static.html` nicht nötig (HYPOTHESE: curl öffnet beim Start keine INET-Sockets) |
| 8 | Sandbox (seccomp, Landlock, `PR_SET_NO_NEW_PRIVS`) | je Dienst, `LibSandbox` (nicht gelesen) | fehlt | `--disable-sandbox` setzen (`Application.cpp:489`), dann entfällt der Weg |
| 9 | `inotify_init1` (294) | `TimeZoneWatcherUnix` über `FileWatcherInotify.cpp:94` | fehlt | nur Warnung (`Application.cpp:1970`), weich |
| 10 | `fchmod` (91), `utimensat` (280), `sendfile` (40), `flock` (73) | `LocalServer`, CrashReport, Profilkoordination, Kopieren | fehlen | nicht auf dem headless-Weg (Koordination aus); bei Absturz schreibt `CrashReport` mit `fchmod` (`CrashReport.cpp:51`), weich |
| 11 | `/proc/self/status`, `/proc/stat`, `/proc/<pid>/stat`, `/proc/self/mem` | `is_being_debugged`, Prozessstatistik, Absturzbehandler | nur `/proc/self/exe` (V12) | weich, außer Nr. 3 (`/proc/self/fd`) |
| 12 | SystemProbe | eigener Helfer beim Start (GPU-Fakten) | – | nicht gelesen; Ausgang offen |

Vorhanden und passend (laut Verteiler und LINUX_ABI.md): `clone` vfork-Kind mit eigenem Stack, `execve` mit `FD_CLOEXEC`, `wait4`/`waitid`, `kill`, `setpgid`,
`getppid`, `socketpair`/`SCM_RIGHTS`/`recvmsg`/`sendmsg` (V16), `memfd_create` + `F_ADD_SEALS` + `MAP_SHARED` (V17), Signale mit `SA_RESTART` (V18),
`readlink("/proc/self/exe")` + ICU-Datei per `mmap` (V12), `pipe2`, Threads bis 64 je Prozess (V10), `/dev/null`.

## Doku-Lücken in LINUX_ABI.md

- Kein Abschnitt V15 (vfork/`execve`/`wait4`/`kill`, `FD_CLOEXEC`); Zeilen 72 (`F_SETFD` „ohne Wirkung (kein `execve`)“) und 325 (`pipe2`
  „kein execve“) sind seit V15 überholt (`FD_CLOEXEC` wird beim `execve` ausgewertet, `LinuxOpenFiles`/Deskriptortabelle).
- Zeile 26 (S1-`mmap`: `MAP_FIXED` `-ENOSYS`, Datei-fd `-ENODEV`) widerspricht V2c/V12/V17.

## Nicht geprüft

Kein Gastlauf, kein Bau, kein strace. `LibSandbox`, SystemProbe, Compositor-Zeichenweg (Skia CPU), libcurl-Konfiguration und die Rust-Seite
von LibIPC (Flags von `recvmsg`/`sendmsg`, `MSG_CMSG_CLOEXEC`) sind nicht gelesen. Ein `strace -f` des headless-Laufs von #636 auf dem
Linux-Host würde die Liste schließen (vorhandenes `experiments/Ladybird.Probe/syscalls.py`).
