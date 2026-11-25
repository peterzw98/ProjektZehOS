# Native Pakete mit Linux-ABI: Entwurf

Stand 24. November 2026. ZehOS `master` `57ceabee`, Cosmos-Quelle `e9915bc4` (smp.11, Branch `cosmos`), Ladybird `6fba5b78a104`. **Nur gelesen und entworfen: nichts davon ist gebaut oder im Gast geprüft.** Abschnitt 3 ist an den genannten Stellen gelesen (BELEGT). Alles andere ist HYPOTHESE, auch jeder Aufwand.

Pläne: [Übersicht](../plans/2026-11-24-ladybird-nativ.md). Ersetzt den Wasm-Weg für Ladybird ([Ergebnis L2](../../LADYBIRD_FEASIBILITY.md#ergebnis-l2)).

## 1 Auftrag

Peter, 24. November 2026:

- „am besten wäre nativ, wenn dafür nötig unseren paketdienst umbauen und den plan neu entwerfen. … Das Endziel sollte sein, dass wir keinen interpreter mehr brauchen, sondern einfach pakete installieren wie auf normalen systemen“
- „Das endziel sollte sein, einen ähnlichen paketdienst zu haben wie ein standard linux system, sodass das porten von sachen einfacher wird“ (aus dem Lead-Chat weitergegeben)
- „Bitte aot vor ladybird abschließen und zum standard machen“ (#423; hier erledigte Voraussetzung)
- Schnittstelle: „Ja, Linux-ABI (C)“ (im Planungschat)

**Kriterium ist die Portierbarkeit.** Ladybird ist das erste große Programm, nicht der einzige Zweck.

**Ziele:** (a) Programme laufen im Ring 3 in eigenen Adressräumen. (b) Vorhandene Software wird mit normalen Linux-Zielen (`x86_64-linux-musl`) gebaut und braucht wenige oder keine Patches. (c) `pkg` kennt Abhängigkeiten, gemeinsame Bibliotheken, Upgrade, Entfernen und Signaturen; Pakete entstehen aus Bauvorschriften. (d) Ladybird zeigt eine statische Seite im ZehOS-Fenster. (e) wasm3 und WAMR entfallen.

**Nicht-Ziele:** jedes Linux-Programm ausführen; fremde Paketbestände laden (Alpine, Debian); Linux-Kernelcode übernehmen; Kernelmodule; GPU, WebGL, Medien und LibJS-JIT im ersten Ladybird; 32-Bit-Programme; ARM64.

**Darf nicht passieren:** Ein Programm im Ring 3 liest Kernelspeicher oder hält das System an. Doom, ClassiCube, NetSurf oder Rechner verschwinden, bevor ihr nativer Ersatz abgenommen ist. Ein vorhandenes Datenlaufwerk wird durch den Umbau unlesbar.

## 2 Wege

| Weg | Folge | Entscheidung |
| --- | --- | --- |
| A: fremde Paketbestände direkt | Schlüssel, Updates und Breite bestimmen andere; nicht abgrenzbar | verworfen |
| B: eigene Systemaufrufe, portierte libc | eigenes Clang- und Rust-Ziel, dauerhafte Ports | verworfen |
| **C: Auswahl der Linux-x86-64-Systemaufrufe, eigene signierte Pakete** | einmaliger Aufwand im Kernel; gegen echtes Linux prüfbar; musl und Rust-Ziel unverändert | **gewählt (Peter)** |

Binärkompatibilität ist Mittel, keine Zusage: Unterstützt ist, was in der Aufrufliste (Abschnitt 6) steht und einen Test hat. Alles andere liefert `-ENOSYS`.

## 3 Ausgangslage in Cosmos

| Befund | Beleg | Folge |
| --- | --- | --- |
| Keine eigene GDT, kein TSS, kein `syscall`-Eintritt, kein `swapgs`: „kernel has no user mode“ | `Native.X64/CPU/CpuOps.s:18-20`; Suche nach `lgdt`, `ltr`, `swapgs` ohne weiteren Treffer | neuer Cosmos-Patch |
| Alle 256 Gates `0x8E00` (DPL 0), Stub auf dem aktuellen Stack, kein IST | `Core.X64/Cpu/Idt.cs:49`, `Interrupts.s` `IRQ_STUB` | Ring-3-Eintritt, IST für #DF, NMI, #MC |
| Stub liest `gs:[…]`, ohne die Herkunft zu prüfen | `Interrupts.s` `PERCPU_*` | `swapgs` bei Herkunft Ring 3; NMI und #MC prüfen die MSR |
| Stub sichert nur XMM0–15; x87-Zustand und MXCSR fehlen | `Interrupts.s:742-759`, `ThreadContext.X64.cs` | musl rechnet `long double` mit x87: `fxsave64` nötig |
| `CR4` setzt OSFXSR/OSXMMEXCPT, nicht OSXSAVE | `SIMD.s` | Programme sehen kein AVX und wählen SSE2; AVX ist nicht nötig |
| Neue Threads springen mit `jmp` an, fortgesetzte mit `iretq` | `Interrupts.s` `.Lnew_thread` | Ein Programmthread beginnt als Kernelthread und geht selbst nach Ring 3 |
| `DeviceMapper` nutzt die Tabellen des aktuellen CR3 und legt PML4-Einträge an | `DeviceMapper.cs` `MapBlock` | obere PML4-Hälfte vor dem ersten Prozess vollständig anlegen und teilen |
| Threadstacks 256 KiB, höchstens 256 Threads | `SchedulerThread.cs:258,263` | Grenze für Ladybird; anheben, wenn L1 mehr zeigt |
| `PerCpuBlock` ist 128 Byte groß und fast voll | `PerCpuBlock.cs:27` | vergrößern |
| Fehler-Hook kann den Thread aufgeben | Patch `ausnahme-hook` | Anschluss für Fehler aus Ring 3 |
| GC durchsucht Stacks präzise ab dem gesicherten Rahmen | `GarbageCollector.PreciseStack.cs` | Ein Ring-3-RIP darf nicht als verwalteter Rahmen gelten: Probe N1-A |

## 4 Prozesse und Speicher

- **Prozess** = Adressraum, Deskriptortabelle, Threads, Benutzer, Arbeitsverzeichnis. Verwaltung in C# unter `ProjektZeh/Processes/`. Cosmos liefert nur Mechanik; Policy bleibt in ZehOS (`docs/regeln/COSMOS_PATCHES.md`).
- **Adressraum:** untere Hälfte je Prozess, obere Hälfte gemeinsam und ohne User-Bit. Bereiche werden reserviert, Seiten erst beim Zugriff belegt. Damit kosten die 32-GiB-Reservierung von LibGC und der 4-TiB-Bereich von PrimitiveStorage nur Verwaltung. Seiten sind nie zugleich schreib- und ausführbar (LibJS ohne JIT).
- **Zugriff auf Programmspeicher:** Der Kernel greift nie über die Programmadresse zu. Er läuft die Tabellen des Prozesses ab, belegt fehlende Seiten und kopiert über die HHDM-Adresse. Ein falscher Zeiger ergibt `-EFAULT`, keinen Kernelfehler. Adresse und Länge werden überlaufsicher geprüft; die Seiten bleiben während der Kopie gehalten.
- **Mehrere CPUs:** In N1 bis N3 sind alle Threads eines Prozesses an eine CPU gebunden (Rundlauf über die Compute-CPUs, CPU 0 nur ohne weitere CPU). Dadurch ist kein TLB-Abgleich nötig; verschiedene Prozesse laufen trotzdem nebeneinander. N4 hebt die Bindung mit TLB-Abgleich per IPI auf.
- **Stacks:** je Programmthread ein Kernelstack und ein Programmstack mit Schutzseite.
- **Ende:** Ein Fehler im Ring 3 beendet nur diesen Prozess (Status wie ein Signal, Meldung im Fenster oder Terminal). Seiten, Deskriptoren und Sockets werden frei. Ein Abbruch von außen wirkt beim nächsten Eintritt in den Kernel; wartende Aufrufe werden geweckt, der Timer fängt reine Rechenschleifen.
- **Speichergrenze:** je Prozessgruppe (Paketstart) aus dem Manifest, höchstens ein Systemwert. Damit ist 12.6 für native Pakete enthalten.

## 5 Dateien, Rechte, Anzeige

- **Wurzel eines Prozesses:** `/usr`, `/lib`, `/etc` kommen aus installierten Paketen, `/save` ist der Speicher der App, `/tmp` liegt im Speicher, `/dev` enthält `null`, `zero`, `urandom`, `tty`, `zehos`. `/proc` nur, soweit N1-C es verlangt. `/data` sieht ein Programm nur mit `capability=files` und mit den Rechten des angemeldeten Benutzers (6c).
- **Pfadindex statt Vereinigungsdateisystem:** Pakete bleiben in ihren versionierten Ordnern. Der Kernel baut aus den Manifesten einen Index `Pfad → Paketdatei`. Es braucht keine Symlinks auf FAT32; ein Absturz mitten in der Installation lässt den alten Bestand gültig. Jede Prozessgruppe behält den Index ihres Starts, damit späteres Nachladen nicht zwei Paketstände mischt.
- **Rechte:** `capability=net`, `storage`, `files`. Ohne `net` liefert `socket(AF_INET…)` `-EACCES`; `AF_UNIX` innerhalb der Prozessgruppe ist immer erlaubt. Kein Setuid; Kinder erben höchstens die Rechte des Elternprozesses.
- **Anzeige und Eingabe:** Gerät `/dev/zehos`. Das Programm übergibt einen Bildpuffer aus `memfd_create` per `ioctl` und liest Ereignisse (Typen 1–10 wie `poll_event`) per `read`/`epoll`. Die C-Bibliothek `libzehos` im sysroot kapselt das. `/dev/zehos` gehört zu N3 (Anzeige kopierend), `epoll` darauf zu N4. Wayland und X11 sind kein Ziel.
- **Ladybird-Oberfläche:** eigenes Frontend `UI/ZehOS` auf der Naht `Hub::ApplicationHost` (19 Methoden, L2-C Nr. 3) statt Qt.

## 6 Systemaufrufe

Nummern, Register, Fehlerwerte und Strukturen sind die von Linux x86-64; `MAP_FIXED` ersetzt wie dort. Verbindlich wird `docs/LINUX_ABI.md` (entsteht in N2): je Aufruf Nummer, unterstützte Flags, Abweichungen, Test. Nicht unterstützte Flags ergeben `-EINVAL`, nie stilles Ignorieren.

| Satz | Inhalt | Phase |
| --- | --- | --- |
| S1 | `read write readv writev openat open close fstat newfstatat lseek mmap` (anonym) `munmap mprotect brk exit exit_group arch_prctl set_tid_address clock_gettime nanosleep getrandom uname getcwd getdents64 ioctl` (Terminal) `rt_sigaction rt_sigprocmask sigaltstack` (Standardverhalten) `poll` (auf 0–2) `getpid gettid getuid geteuid getgid getegid madvise sched_yield stat lstat access faccessat unlink mkdir rename readlink symlink chdir fcntl` (die letzten drei ergänzt aus N1-E1: nötig für `lstat`/`readlink`, `chdir` und musl-`opendir`); Startstack mit `AT_PHDR PHENT PHNUM PAGESZ ENTRY BASE RANDOM UID EUID GID EGID SECURE EXECFN HWCAP`. N1-E prüft die Vollständigkeit | N2 |
| S2 | dynamischer Lader (`ld-musl` als Paket), `mmap` von Dateien, `memfd_create` ohne Versiegelung, `ioctl` auf `/dev/zehos`, `pread64 fcntl dup dup2 dup3 pipe2` | N3 |
| S3 | `clone clone3 futex`, Signale, `fork vfork execve wait4`, `epoll_* eventfd2 timerfd_* poll` (alle Deskriptoren), Versiegelung, `socketpair`/`AF_UNIX` mit `SCM_RIGHTS`, `AF_INET` | N4 |
| S4 | was N1-C für Ladybird zusätzlich zeigt | N6 |

## 7 Paketdienst v2

| Punkt | Festlegung |
| --- | --- |
| Manifest | `zehos-package 2`, `runtime=linux-x64-v1`, kanonischer Text und Ed25519 wie heute. Neu: `release=<n>`, `license=<SPDX>`, `depends=<id>>=<version>`, `provides=so:<soname>`, `exec=<sha256> <bytes> <pfad>` für ausführbare Dateien, `link=<pfad> <ziel>` (nur im Index), `memory-mib=<n>`. `entry` und `name` fehlen bei Bibliotheken. Pfade liegen unter `usr/`, `lib/` oder `etc/` |
| Grenzen | 4096 Dateien, Manifest 1 MiB, Inhalt 1 GiB je Paket; nach L1-C prüfen |
| Katalog | `catalog2` und `catalog2.sig` neben dem alten `catalog`. Je Paket genau eine Version mit `depends` und `provides`. Alte Kernel lesen weiter `catalog` |
| Auflösen | Der Katalog führt je Paket eine Version, also ist die Auflösung der Abschluss über `depends`, ohne Suche. Fehlende oder zu alte Abhängigkeit, Zyklus, doppelter Pfad: Abbruch vor dem ersten Inhaltsdownload |
| Installieren | Platz prüfen, alle neuen Versionen laden und prüfen, dann **ein** Wechsel der aktiven Menge (Datei `active2`, ersetzt mit der vorhandenen Sicherungslogik) |
| Entfernen | abgelehnt, solange ein anderes Paket es braucht; `pkg remove --unused` räumt automatisch installierte, nicht mehr gebrauchte Pakete ab |
| Upgrade | alle betroffenen Pakete in einer Transaktion. Laufende Programme behalten die alte Version; sie wird entfernt, wenn niemand sie mehr nutzt |
| Vertrauen | derselbe Herausgeberschlüssel; Generation für `catalog2` getrennt gezählt |
| Bauvorschriften | `pkgs/<id>/package.toml` (Quelle mit URL und SHA-256, Version, Lizenz, Abhängigkeiten, Rechte), `build.sh`, `patches/*.patch`. `scripts/build-native-package.py` baut in einem festen Container, nach dem Laden der Quellen ohne Netz |
| Zielumgebung | sysroot als Pakete: `musl`, `libcxx`, `libzehos`, Kopfdateien in `<id>-dev`. Ziel `x86_64-linux-musl` für Clang und Rust |
| Server | `deploy/package-repo/` liefert weiter statische Dateien. Branch `packages` bleibt; Dateien über 100 MiB (GitHub-Grenze) brauchen eine andere Ablage, Entscheidung in N3 |

## 8 Übergang

| Heute | Wann ersetzt | Bis dahin |
| --- | --- | --- |
| WAMR-AOT | zuletzt, nach Tor N5-Z | Standardlaufzeit nach #423 |
| wasm3 | nach der Abnahme von NetSurf nativ. NetSurf lädt unter WAMR 2.4.5 nicht (`docs/PACKAGE_FORMAT.md`) und braucht wasm3 deshalb am längsten | bleibt |
| Rechner, Doom, ClassiCube, NetSurf | N5, in dieser Reihenfolge; gleiche `id`, `/save` bleibt erhalten | Wasm-Pakete bleiben installierbar |
| Manifest und `catalog` v1 | nach Entfernen der Laufzeiten. Alte Pakete werden gemeldet, nie von selbst gelöscht | beide Formate nebeneinander |
| I2-Stapel von Team laser (#401–#408, PRs #598, #601, #603, #604, #606) | fällt mit der Wasm-Laufzeit weg | schützt die Wasm-Apps in der Übergangszeit. Empfehlung: fertige PRs normal zu Ende bringen, keine neue Wasm-Härtung beginnen. Team laser entscheidet mit Lars |
| #418 (Wasm-Threads), #420 (Wasm-Instanzen) | durch N4 ersetzt | nicht mehr umsetzen |
| #417 (Paketgrenzen) | durch Abschnitte 4 und 7 ersetzt | für Wasm nicht mehr umsetzen |
| #419 (POSIX-Breite) | für native Pakete durch Abschnitt 6 ersetzt | P4-A bis P4-F sind gemergt; offen ist nur die Abnahme P4-Z |

Peter hat den Plan am 24. November 2026 freigegeben; Issues und Meilenstein sind angepasst.

## 9 Sicherheit

Die Grenze bilden Ring 3, NX und der Kopierweg ohne Programmadressen. Offen bleiben: gemeinsame Seitentabellen von Kernel und Programm (Meltdown auf betroffenen Intel-CPUs), kein ASLR in N2, kein seccomp. Nur der Herausgeber signiert; fremde Autoren erst nach einer eigenen Sicherheitsprüfung (Tor N5-Z). WAMR-AOT bleibt bis zum Entfernen Code im Ring 0.

## 10 Aufwand und Risiken

Personwochen, alles HYPOTHESE. L2 nennt für die 12.4-Umsetzung „Monate“; das bestätigt sich.

| Phase | Inhalt | Wochen |
| --- | --- | --- |
| N1 | Nachweise | 4–7 |
| N2 | Prozesse, ELF, S1 | 8–12 |
| N3 | Paketdienst v2, sysroot, S2 | 6–10 |
| N4 | Threads, IPC, Netz, S3 | 10–16 |
| N5 | Apps umstellen, Interpreter weg | 6–10 |
| N6 | Ladybird | 8–16, nach oben offen |
| **Summe** | | **42–71** |

| Risiko | Wo es früh sichtbar wird |
| --- | --- |
| Ring 3 verträgt sich nicht mit Cosmos (GC, Stubs, SMP): **größtes Einzelrisiko** | N1-A |
| Eigene Seitentabellen verletzen Annahmen (Identitätsabbildung, `DeviceMapper`, GC) | N1-B |
| Kosten im Normalbetrieb (`fxsave`, `swapgs` in jedem Interrupt) | N1-A |
| Ladybird oder Abhängigkeiten bauen nicht mit musl | N1-D; Ausweg glibc, die Kernel-ABI bleibt gleich |
| Ladybird braucht viel mehr Aufrufe als S1–S3 | N1-C |
| Datenlaufwerk zu langsam oder zu klein | N3, Werte aus L1-C |
| Linux-Verhalten falsch nachgebildet | N1-E: jedes Testprogramm läuft auf Linux und ZehOS mit derselben Ausgabe |
| Überschneidung mit Multithreading (`Interrupts.s`, Scheduler) | die Cosmos-Pakete aus N1 beginnen erst nach #523 |
| Ladybird bewegt sich schnell | fester Stand bis zum ersten Bild |

## 11 Für die Umsetzung offen

Wer umsetzt, entscheidet über innere Klassenaufteilung, Datenstruktur der Bereiche, Reihenfolge innerhalb eines Aufrufsatzes und den Bau-Container. Fest sind die Schnittstellen in N1 und Abschnitte 4 bis 8.

**Festlegungen dieses Entwurfs, nicht von Peter vorgegeben:** CPU-Bindung bis N4, Pfadindex statt Vereinigungsdateisystem, `/dev/zehos` statt Wayland, eigenes Frontend statt Qt, musl vor glibc, eine Version je Paket im Katalog, derselbe Herausgeberschlüssel.
