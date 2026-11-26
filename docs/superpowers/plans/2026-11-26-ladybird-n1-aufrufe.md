# Ladybird N1-C2: Aufrufliste und Urteil

Paket N1-C2 (#635), Plan [`2026-11-24-ladybird-n1-nachweise.md`](2026-11-24-ladybird-n1-nachweise.md). Messung und Urteil von `agent-laser-lead` am 26. November 2026 auf Lars' Rechner, demselben wie #448 und #450 (Zuteilung durch `agent-nweh-lead1`). Sätze S1–S3 nach [Entwurf native Pakete](../specs/2026-11-24-native-pakete-design.md), Abschnitt 6.

## Aufbau

- **Programm:** Ladybird `6fba5b78a104` aus L1-A (#448), ohne JIT, glibc, `x64-linux-dynamic`; dazu `js` aus demselben Bauordner (`ninja js`, 29 s, nachgebaut für dieses Paket).
- **Rechner:** Intel Core Ultra 9 185H, 22 Threads, 62 GiB, Manjaro, Kernel 7.3.4, strace 7.3; Intel- und NVIDIA-Grafik vorhanden.
- **Aufnahme:** je L1-Seite `strace -ff -f -yy -v -s 4096 -o <dir>/t Ladybird --headless=screenshot --force-cpu-painting --window-width 798 --window-height 519 --screenshot-delay 5 …` (Optionen wie `measure.py`), dazu `strace … js -l -c '1+1'` (Ausgabe `2`). Alle vier Läufe Exit 0, alle drei Screenshots vorhanden.
- **Auswertung:** `experiments/Ladybird.Probe/syscalls.py` (N1-C1) je Thread-Datei; Threads über die Rückgabewerte von `clone`/`clone3` mit `CLONE_THREAD` ihrem Prozess zugeordnet, Prozessname aus dem `execve` des Prozesses. `Ladybird` ist der Prozess, den `measure.py` unter `/proc/<pid>/comm` als `Hub` sieht.
- **Satz:** erster Satz aus S1, S2, S3, der den Aufruf nennt (`fcntl` steht in S1 und S2 und zählt als S1; `epoll_*` und `timerfd_*` als S3); was keiner nennt, ist S4.
- **Pflicht oder verzichtbar:** je S4-Aufruf `strace -f -e trace=<name> -e inject=<name>:error=ENOSYS` auf `static.html`. **Pflicht**, wenn danach kein Screenshot entsteht; **verzichtbar**, wenn der Screenshot entsteht. Bei allen verzichtbaren Aufrufen war der Screenshot bytegleich mit dem Vergleichslauf unter strace (MD5 `4b5dcf80…`). Fehlschläge ohne Absturzmeldung (die vier mit „The displayed page changed before taking the screenshot“) sind zweimal wiederholt, 3/3 gleich.

## Ergebnis

- **94 verschiedene Aufrufe:** S1 41, S2 4, S3 19, **S4 30**.
- **S4-Pflicht: 10** (`clock_nanosleep`, `landlock_add_rule`, `landlock_restrict_self`, `prctl`, `prlimit64`, `sched_getaffinity`, `seccomp`, `set_robust_list`, `setpgid`, `statx`). Nicht entschieden: `fsync` (im Probelauf nicht aufgerufen) und `restart_syscall` (setzt der Kernel selbst ein, nicht injizierbar). Selbst wenn beide als Pflicht zählen: 12.
- **Keiner** der ausgeschlossenen Aufrufe: kein `io_uring_*`, `bpf`, `userfaultfd`, `ptrace` in keinem Lauf.
- **GPU-Geräte:** `SystemProbe` öffnet `/dev/nvidiactl` und `/dev/dri/renderD128/129` (Vulkan-Erkennung), `Compositor` öffnet `/dev/dri`, `/dev/udmabuf` und `/dev/nvidia*` mit `O_PATH` für die Landlock-Regeln (`Libraries/LibSandbox/GpuDriverPaths.cpp:160-161`, `Sandbox.cpp:874`). Werden die Geräte ganz verborgen (`ENOENT` auf alle Pfadaufrufe für `/dev/dri*`, `/dev/nvidia*`, `/dev/udmabuf`, `/dev/char/195:*`), läuft `static.html` unverändert, 2/2 Läufe, Screenshot bytegleich. **Kein GPU-Gerät ist Pflicht.** Nur das Öffnen zu verbieten, während `stat` das Gerät meldet, bricht den Lauf ab (`open(O_PATH)` in `Compositor`); das ist ein Widerspruch, den der Test erzeugt, kein Bedarf.
- Ohne `seccomp`, `landlock_*`, `prctl`, `setpgid` oder `set_robust_list` kann Ladybird seine Hilfsprozesse nicht starten („Failed to connect to RequestServer“, „Could not launch any of …RequestServer“); ohne `clock_nanosleep`, `prlimit64`, `sched_getaffinity` oder `statx` läuft es, liefert aber keinen Screenshot. Ob ZehOS diese Aufrufe echt umsetzen muss oder Erfolg ohne Wirkung reicht (Sandbox), entscheidet dieser Test nicht; für die Regel zählen sie als Pflicht.

## Urteil

**`trägt`**: 10 S4-Pflichtaufrufe (höchstens 12 mit den beiden unentschiedenen), Grenze 40; darunter keiner von `io_uring_*`, `bpf`, `userfaultfd`, `ptrace` und kein GPU-Gerät.

## Prozesse, Threads, Adressraum

Threads unter strace aus der Aufnahme `static.html`; Threads und `VmSize` ohne strace aus der Messung L1 (#450, `static.json`, Lauf 2, Höchstwerte je Prozess).

| Prozess | Threads (strace) | Threads (L1) | größter `VmSize` (L1) |
| --- | --- | --- | --- |
| `Ladybird` (`Hub`) | 16 | 13 | 1,31 GiB |
| `Compositor` | 11 | 9 | 1,28 GiB |
| `SystemProbe` | 2 | 1 | 1,50 GiB |
| `RequestServer` (2×) | 6 und 4 | 5 und 4 | 1,11 GiB |
| `WebContent` (2× in L1, 3× unter strace) | 19, 19, 21 | 18 und 20 | **4162 GiB** (≈ 4,06 TiB, GC-Reservierung) |
| `js -l -c '1+1'` | 1 | – | nicht gemessen |

## Gelesene Pfade unter `/proc`, `/sys`, `/etc`, `/dev`

Aus `paths` von `syscalls.py` über alle vier Läufe (auch fehlgeschlagene Zugriffe; `/proc/<pid>` zusammengefasst).

- **`/proc`:** `self/{cgroup,cmdline,comm,exe,maps,mem,stat,status,task}` und dasselbe unter `/proc/<pid>`; `meminfo`, `stat`, `filesystems`; `sys/vm/overcommit_memory`, `sys/dev/i915/perf_stream_paranoid`; `driver/nvidia/params`, `driver/nvidia/gpus/…/numa_status`.
- **`/sys`:** 300 Pfade: `devices/system/cpu/cpu*/{cache/index*/{id,level},cpu_capacity,topology/{core_cpus,physical_package_id}}` (CPU-Topologie), PCI- und DRM-Geräte (`bus/pci/devices/*/boot_vga`, `dev/char/226:*`, `devices/pci0000:00/…/{vendor,device,revision,subsystem_*,uevent,drm}`).
- **`/etc`:** 445 Pfade, davon 356 CA-Zertifikate (`ca-certificates/extracted/cadir/*.pem`, `ssl/certs`, `ssl/cert.pem`); sonst `fonts/fonts.conf`, `fonts/conf.d/*` und `fonts/local.conf`, `ssl/openssl.cnf`, `localtime`, `ld.so.cache`, `ld.so.preload`, `drirc`, `egl`, `glvnd/egl_vendor.d`, `vulkan/*` und `xdg/*/vulkan/*`, `nvidia/nvidia-application-profiles-rc*`.
- **`/dev`:** `null`, `udmabuf`, `dri/{card0,card1,renderD128,renderD129}`, `nvidia0`, `nvidiactl`, `nvidia-modeset`, `nvidia-uvm`, `nvidia-uvm-tools`, `nvidia-caps`, `char/195:*`.
- **Sockets:** `AF_UNIX` (Hilfsprozesse), `AF_UNIX:/run/dbus/system_bus_socket`, ein `UDPv6`-Socket.

## Aufruf × Prozess × Satz

Läufe: `static`, `css`, `js` = L1-Seiten, `js1` = `js -l -c '1+1'`. Aufrufe gezählt über alle vier Läufe; „Fehler“ sind Rückgaben mit Fehlercode.

| Aufruf | Satz | Aufrufe (4 Läufe) | Prozesse | Läufe | Fehler | ENOSYS auf `static.html` |
| --- | --- | --- | --- | --- | --- | --- |
| `access` | S1 | 893 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | ENOENT 116 | – |
| `arch_prctl` | S1 | 28 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `brk` | S1 | 236 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `close` | S1 | 37525 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `exit` | S1 | 101 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | – | – |
| `exit_group` | S1 | 22 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `fcntl` | S1 | 1230 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | – | – |
| `fstat` | S1 | 62992 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `getdents64` | S1 | 597 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | – | – |
| `getegid` | S1 | 30 | SystemProbe | css,js,static | – | – |
| `geteuid` | S1 | 48 | Ladybird (Hub),RequestServer,SystemProbe | css,js,static | – | – |
| `getgid` | S1 | 30 | SystemProbe | css,js,static | – | – |
| `getpid` | S1 | 625 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | – | – |
| `getrandom` | S1 | 397 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `gettid` | S1 | 55 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `getuid` | S1 | 42 | Ladybird (Hub),SystemProbe | css,js,static | – | – |
| `ioctl` | S1 | 1963 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | EINVAL 3, ENOTTY 4, EOPNOTSUPP 3, EPERM 9 | – |
| `lseek` | S1 | 2547 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `madvise` | S1 | 959 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | EINVAL 3 | – |
| `mkdir` | S1 | 282 | Compositor,Ladybird (Hub),RequestServer,SystemProbe | css,js,static | EEXIST 282 | – |
| `mmap` | S1 | 39283 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `mprotect` | S1 | 3328 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `munmap` | S1 | 29174 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `newfstatat` | S1 | 33924 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | ENOENT 346 | – |
| `open` | S1 | 28 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `openat` | S1 | 38915 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | EACCES 60, ENOENT 2262 | – |
| `poll` | S1 | 143 | Compositor,Ladybird (Hub),RequestServer,WebContent | css,js,static | ERESTART_RESTARTBLOCK 3 | – |
| `read` | S1 | 10998 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `readlink` | S1 | 30121 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | EINVAL 29163 | – |
| `rename` | S1 | 3 | Ladybird (Hub) | css,js,static | – | – |
| `rmdir` | S1 | 48 | SystemProbe | css,js,static | ENOTEMPTY 48 | – |
| `rt_sigaction` | S1 | 1752 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | – | – |
| `rt_sigprocmask` | S1 | 1138 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `sched_yield` | S1 | 4653 | SystemProbe,WebContent | css,js,static | – | – |
| `set_tid_address` | S1 | 28 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `sigaltstack` | S1 | 21 | Compositor,Ladybird (Hub),RequestServer,WebContent | css,js,static | – | – |
| `stat` | S1 | 825 | SystemProbe | css,js,static | ENOENT 12 | – |
| `symlink` | S1 | 30 | SystemProbe | css,js,static | EEXIST 30 | – |
| `uname` | S1 | 3 | Ladybird (Hub) | css,js,static | – | – |
| `unlink` | S1 | 87 | Ladybird (Hub),RequestServer,SystemProbe | css,js,static | EACCES 30, EISDIR 48, ENOENT 6 | – |
| `write` | S1 | 2026 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `dup2` | S2 | 42 | Compositor,RequestServer,SystemProbe,WebContent | css,js,static | – | – |
| `memfd_create` | S2 | 68 | Compositor,Ladybird (Hub),SystemProbe,WebContent | css,js,static | – | – |
| `pipe2` | S2 | 325 | Compositor,Ladybird (Hub),RequestServer,WebContent,js | css,js,js1,static | – | – |
| `pread64` | S2 | 184 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `clone` | S3 | 243 | Compositor,Ladybird (Hub),RequestServer,WebContent | css,js,static | – | – |
| `clone3` | S3 | 66 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | ENOSYS 18 | – |
| `connect` | S3 | 3 | SystemProbe | css,js,static | – | – |
| `eventfd2` | S3 | 72 | RequestServer | css,js,static | – | – |
| `execve` | S3 | 28 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | – |
| `ftruncate` | S3 | 77 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | – | – |
| `futex` | S3 | 4178 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | EAGAIN 232, EPERM 3, ERESTARTSYS 6, ETIMEDOUT 9 | – |
| `getppid` | S3 | 21 | Compositor,RequestServer,SystemProbe,WebContent | css,js,static | – | – |
| `getsockname` | S3 | 3 | SystemProbe | css,js,static | – | – |
| `ppoll` | S3 | 593 | Compositor,Ladybird (Hub),RequestServer,WebContent | css,js,static | ERESTARTNOHAND 5 | – |
| `pwrite64` | S3 | 87 | Compositor,Ladybird (Hub),RequestServer,WebContent | css,js,static | – | – |
| `recvmsg` | S3 | 1848 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | EAGAIN 813 | – |
| `rt_sigreturn` | S3 | 7 | Ladybird (Hub) | css,js,static | EINTR 2 | – |
| `sendmsg` | S3 | 283 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | – | – |
| `sendto` | S3 | 999 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent | css,js,static | – | – |
| `setsockopt` | S3 | 276 | Compositor,Ladybird (Hub),RequestServer,WebContent | css,js,static | – | – |
| `socket` | S3 | 39 | RequestServer,SystemProbe | css,js,static | – | – |
| `socketpair` | S3 | 69 | Compositor,Ladybird (Hub),RequestServer | css,js,static | – | – |
| `wait4` | S3 | 12 | Ladybird (Hub) | css,js,static | – | – |
| `clock_nanosleep` | S4 | 3 | WebContent | css,js,static | – | **Pflicht** (3/3 ohne Screenshot) |
| `close_range` | S4 | 54 | Compositor,RequestServer,SystemProbe,WebContent | css,js,static | – | verzichtbar (Screenshot gleich) |
| `fallocate` | S4 | 3 | SystemProbe | css,js,static | – | verzichtbar (Screenshot gleich) |
| `fchmod` | S4 | 27 | Ladybird (Hub) | css,js,static | – | verzichtbar (Screenshot gleich) |
| `flock` | S4 | 3 | Ladybird (Hub) | css,js,static | – | verzichtbar (Screenshot gleich) |
| `fstatfs` | S4 | 240 | Compositor,Ladybird (Hub) | css,js,static | – | verzichtbar (Screenshot gleich) |
| `fsync` | S4 | 9 | RequestServer | css,js,static | – | im Probelauf nicht aufgerufen: unbestimmt |
| `getresgid` | S4 | 3 | SystemProbe | css,js,static | – | verzichtbar (Screenshot gleich) |
| `getresuid` | S4 | 3 | SystemProbe | css,js,static | – | verzichtbar (Screenshot gleich) |
| `inotify_add_watch` | S4 | 3 | Ladybird (Hub) | css,js,static | – | verzichtbar (Screenshot gleich) |
| `inotify_init1` | S4 | 3 | Ladybird (Hub) | css,js,static | – | verzichtbar (Screenshot gleich) |
| `landlock_add_rule` | S4 | 1611 | Compositor,RequestServer,WebContent | css,js,static | – | **Pflicht** (1/1 ohne Screenshot) |
| `landlock_create_ruleset` | S4 | 36 | Compositor,RequestServer,WebContent | css,js,static | – | verzichtbar (Screenshot gleich) |
| `landlock_restrict_self` | S4 | 18 | Compositor,RequestServer,WebContent | css,js,static | – | **Pflicht** (1/1 ohne Screenshot) |
| `prctl` | S4 | 607 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | **Pflicht** (1/1 ohne Screenshot) |
| `prlimit64` | S4 | 104 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | **Pflicht** (3/3 ohne Screenshot) |
| `restart_syscall` | S4 | 4 | Ladybird (Hub) | css,js,static | ERESTART_RESTARTBLOCK 1 | nicht injizierbar (Kernel-intern) |
| `rseq` | S4 | 298 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | verzichtbar (Screenshot gleich) |
| `sched_get_priority_max` | S4 | 6 | SystemProbe | css,js,static | – | verzichtbar (Screenshot gleich) |
| `sched_get_priority_min` | S4 | 6 | SystemProbe | css,js,static | – | verzichtbar (Screenshot gleich) |
| `sched_getaffinity` | S4 | 41 | Ladybird (Hub),SystemProbe,WebContent,js | css,js,js1,static | – | **Pflicht** (3/3 ohne Screenshot) |
| `sched_setaffinity` | S4 | 3 | SystemProbe | css,js,static | – | verzichtbar (Screenshot gleich) |
| `sched_setscheduler` | S4 | 3 | SystemProbe | css,js,static | – | verzichtbar (Screenshot gleich) |
| `seccomp` | S4 | 18 | Compositor,RequestServer,WebContent | css,js,static | – | **Pflicht** (1/1 ohne Screenshot) |
| `set_robust_list` | S4 | 298 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | **Pflicht** (1/1 ohne Screenshot) |
| `setpgid` | S4 | 21 | Compositor,RequestServer,SystemProbe,WebContent | css,js,static | – | **Pflicht** (1/1 ohne Screenshot) |
| `setpriority` | S4 | 3 | SystemProbe | css,js,static | – | verzichtbar (Screenshot gleich) |
| `statfs` | S4 | 15 | Ladybird (Hub),RequestServer,SystemProbe | css,js,static | ENOENT 6 | verzichtbar (Screenshot gleich) |
| `statx` | S4 | 18 | WebContent | css,js,static | – | **Pflicht** (3/3 ohne Screenshot) |
| `sysinfo` | S4 | 28 | Compositor,Ladybird (Hub),RequestServer,SystemProbe,WebContent,js | css,js,js1,static | – | verzichtbar (Screenshot gleich) |

## Grenzen

- Ein Rechner, Linux 7.3.4 mit glibc; musl (D1/D2) und ZehOS nicht geprüft. Andere Seiten, Netzseiten, Eingabe, Medien und WebRTC rufen weitere Aufrufe auf; die Liste gilt nur für die drei L1-Seiten und `js 1+1`.
- Pflicht oder verzichtbar nur auf `static.html` und nur mit `ENOSYS`; andere Fehlercodes oder ein Erfolg ohne Wirkung sind nicht geprüft. Ein Aufruf, der auf `css.html` oder `js.html` Pflicht wäre, ist hier verzichtbar eingetragen.
- Pflicht heißt hier: ohne den Aufruf entsteht kein Screenshot. Abweichungen im Bild hat nur der MD5-Vergleich geprüft (alle verzichtbaren gleich).
- strace verlangsamt die Läufe; die Thread-Zahlen unter strace liegen 1–3 über denen ohne strace (L1).
- Die Zuordnung zu den Sätzen und die Tabellen erzeugte ein Hilfsskript außerhalb des Repositorys (Einlesen über `syscalls.py`, Gruppierung über `clone`-Rückgaben); die Rohdaten liegen unter `~/ladybird-probe/c2/` auf Lars' Rechner und werden nicht committet.
