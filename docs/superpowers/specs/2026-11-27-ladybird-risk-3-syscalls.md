# Risiko-Review Block 3: Syscall-Schicht und Zeigerprüfung

Stand: `origin/story/ladybird` 3baf7cb0 gegen `origin/master`. Rein lesend.
Geprüft: `ProjektZeh/Processes/Syscalls/*` (32 Dateien, ~11 900 Zeilen) mit Schwerpunkten, dazu `LinuxUserProcess` (IUserMemory), `LinuxUserProcessShared`,
`LinuxTerminalSession`, `TerminalApp` (wegen #735), `LinuxSignals.Return`, die Syscall-Brücke `CosmosLinuxRun.cs` und `deploy/cosmos/COMMIT`.

Hostproben: drei Wegwerftests (R1–R3) in einer Datei, die gelöscht ist; `nice -n 19`, gefiltert, ohne `-warnaserror`, 3 von 3 bestanden
(sie geben Messwerte aus und prüfen keine Erwartung). Kein Kernelbau, kein Gastlauf.

## Status #735 im Stand 3baf7cb0

- **P2-1 behoben:** `LinuxTerminalSession.cs:93-107`, `Respond` verwirft oberhalb von `MaxInputBytes + ResponseReserve`.
- **P2-2 behoben:** `WriteLine` (`LinuxTerminalSession.cs:121-132`) hält nach dem Abbruch die Grenze von 2048 Zeilen und verwirft
  den Rest. `Aborted(p)` wird je Stück geprüft in `WriteFromUser` (`LinuxSyscalls.cs:138`), `CopyToMapping` (`:525`), dem Datei-`ReadToUser`
  (`LinuxFileSyscalls.cs:217`) und `getrandom` (`LinuxSyscallsMore.cs:197`). Commit 59710181.
- **P2-3 behoben:** Strg+Umschalt+Esc in `TerminalApp.cs:73-79` greift vor `HandleRawKey`, also auch im Rohmodus ohne ISIG.
  Vermutung (P3-9): Unter einem Windows-Host kann Windows Strg+Umschalt+Esc selbst abfangen (Task-Manager); im Gast nicht geprüft.
- Das Issue #735 steht auf GitHub noch auf OPEN.
- P3 aus #735, noch offen: (1) `AddressSpaceMap` ohne Obergrenze für Regionen; (2) Frames für Seitentabellen außerhalb des Budgets;
  (4) Unicode-Formatzeichen; (5) serielle Spiegelung `LINUXRUN OUT` gibt es auch im interaktiven Lauf (`CosmosLinuxRun.cs:323`).
  Behoben: (3) getrandom und (6) Nullen des Puffers (jetzt `CanWrite`, `LinuxSyscallsInput.cs:46`).

## P1

### P1-1 `readv` auf Pipe, Socket, stdin oder eventfd: hintere iovec-Einträge ungekürzt und ungeprüft (belegt, Hostprobe)

`LinuxSyscallsInput.cs:95-109` kürzt die Einträge nur, bis `left` (4096) aufgebraucht ist. Die Schleifenbedingung `left > 0` bricht danach
ab, alle folgenden `lens[i]` behalten die Länge aus dem Benutzerprogramm (je bis `MaxRw`), und für sie läuft kein `CanWrite`.
`ReadToUser` (`:113-121`) addiert danach **alle** Längen und legt `new byte[(int)room]` an.

Der Weg dorthin: `Dispatch` `LinuxSyscalls.cs:43`, `Readv` für jeden `ILinuxInput`-Deskriptor (Pipe, Unix-/TCP-Socket, Terminal, eventfd).

1. **Falsche Daten in gewöhnlichen musl-Programmen.**
   - Aufruf: `readv(fd, [{buf, 8192}, {fbuf, 1024}], 2)`. So liest musl `fread`/`__stdio_read`, wenn mehr als 4 KiB angefordert werden und der
     FILE-Puffer BUFSIZ groß ist.
   - Ergebnis R1 (Terminal, Zeile mit 5000 Zeichen): Rückgabe 5001. `buf[4096..]` bleibt Null, die Bytes ab 4096 landen in `fbuf`.
   - Linux füllt erst `buf`. musl prüft `cnt <= iov[0].iov_len` und gibt dann `cnt` zurück. Das Programm hält Bytes 4096..5000 seines
     Puffers für Daten, liest aber alten Inhalt, und die echten Daten verfallen im FILE-Puffer.
   - Betroffen ist jedes musl-Programm, das mit stdio mehr als 4 KiB aus einer Pipe, einem Socket oder stdin liest.
2. **Kernelheap durch einen Aufruf.**
   - Aufruf: `readv(fd, [{buf, 4096}, {0xdead0000, 256 MiB}], 2)`.
   - Ergebnis R2: 256 MiB Kernelheap angelegt (`GC.GetAllocatedBytesForCurrentThread`), um 2 Bytes zu lesen. Der ungültige Zeiger wird
     nie geprüft (Rückgabe 2).
   - Mit `0x7ffff000 - 4096` ergibt das etwa 2 GiB je Aufruf, im 512-MB-Gast also OOM.
   - Ob der Kernel dabei anhält oder nur das Programm endet (die Brücke fängt `Exception`, `CosmosLinuxRun.cs:1051`), ist **nicht geprüft**.
     Laut der Commit-Nachricht von V43 (9ff7e867) hält der Kernel bei vollem Heap mit `OutOfMemoryException` an.
3. Nebenbefund: Der Kommentar in `:138` („ein anderer Thread desselben Prozesses gibt es (V3) nicht“) ist seit V10 falsch.
   Ein `munmap` zwischen Prüfung und `TryWrite` verliert schon gelesene Daten (`-EFAULT` nach dem Verbrauch).

Vorschlag: In der Kürzschleife alle Einträge durchlaufen, Einträge hinter dem Rest auf 0 setzen, `room` aus den gekürzten Längen bilden.
Dazu ein Test mit `iov0 > 4096` (die bestehenden Tests haben nur kleine `iov0`).

## P2

### P2-1 Nachrichten mit 0 Bytes füllen die Warteschlange ohne Grenze (belegt, Hostprobe R3)

- **SOCK_SEQPACKET:** `LinuxUnixSocket.cs:250-258`. Bei `want == 0` ist `space >= want` immer wahr. `ch.Bytes += 0`, und eine Grenze für die
  Zahl der Einträge gibt es nur für DGRAM (`DgramQueueFull`).
  - Aufruf: `socketpair(AF_UNIX, SOCK_SEQPACKET)`, dann in einer Schleife `send(sv[0], "", 0, 0)`.
  - Ergebnis R3: 1 000 000 von 1 000 000 angenommen, 54 MiB Kernelheap, kein `-EAGAIN`, ohne zu blockieren.
- **UDP-Loopback:** `LinuxUnixSocket.Inet.cs:368-374`. `_inboxBytes + 0 > Capacity` greift nie.
  - Aufruf: `sendto(udp, "", 0, 127.0.0.1:<eigener Port>)` in einer Schleife.
  - Nur aus dem Code gelesen, keine Probe.
- Linux rechnet je skb `truesize` gegen sndbuf/rcvbuf an. Der Heap ist von der V43-Reserve nicht geschützt; Folge ist ein OOM des Kernels.
- Vorschlag: je Eintrag einen Mindestbetrag ansetzen (etwa 256 Byte) oder die Zahl der Einträge begrenzen, für Seqpacket wie für den UDP-Eingang.

### P2-2 SCM_RIGHTS: Zahl der Dateien unterwegs unbegrenzt, Kreise behalten Frames über das Laufende hinaus (Codelesen)

- `ReadRights`/`HoldInFlight` (`LinuxSyscallsSocket.cs:282-306`, `LinuxUserProcess.cs:747`) zählen ohne Obergrenze. Linux begrenzt die
  Dateien unterwegs mit `too_many_unix_fds` (RLIMIT_NOFILE, `-ETOOMANYREFS`).
- Weg zum Heap-DoS:
  - `socketpair` → beide Enden per `sendmsg` mit 1 Byte an ein drittes Socket senden → schließen → wiederholen.
  - Ein Stream-Kanal nimmt 212 992 Einträge à 1 Byte an, denn gezählt werden nur Nutzbytes (`LinuxUnixSocket.cs:250-257`).
  - Jedes mitgeschickte Socket kann vorher selbst 208 KiB Daten halten.
  - Die Fd-Grenze von 1024 greift nicht, weil die Nummern sofort frei werden.
  - `LinuxOpenFiles.Find` sucht linear (`LinuxOpenFiles.cs:98-108`) unter dem FileGate, das der ganze Lauf teilt. Der Aufwand wächst damit
    quadratisch und hält alle Datei-Aufrufe des Laufs auf.
- Kreise (ein Socket trägt sich selbst) sind in `LINUX_ABI.md:403` als Grenze genannt, die Folge nicht:
  - Trägt der Kreis eine memfd oder eine geteilte Seitenablage, werden deren physische Frames nie frei.
  - `CosmosUserPages` (`CosmosLinuxRun.cs:1603-1625`) räumt je Lauf nichts nach; nach `Destroy` hält niemand mehr einen Verweis.
  - Die Frames fehlen also bis zum Neustart, auch späteren Läufen (etwa dem nächsten Ladybird-Start).
- Vorschlag:
  - Dateien unterwegs je Lauf begrenzen (etwa 1024, sonst `-ETOOMANYREFS`).
  - Je Stream-Eintrag einen Mindestbetrag anrechnen.
  - Am Laufende alle verbliebenen Dateien in `LinuxOpenFiles` schließen (räumt auch Kreise ab).

### P2-3 memfd-Frames außerhalb des Prozessbudgets; Speicherbudget ohne Schutz für den Kernel (Codelesen, V43 in Arbeit)

- `LinuxMemFile.FrameLocked` (`LinuxMemFile.cs:697`) holt Frames für `write`/`ftruncate` + `write` direkt aus dem gemeinsamen
  Allokator, ohne `_maxFrames`. Die Grenze gilt je memfd (`CreateMemFile`, `LinuxUserProcessShared.cs:40`: 512 MiB).
- Aufruf: `memfd_create` → `write` 512 MiB. Das ist bei bis zu 1024 Nummern beliebig oft möglich, und das Prozessbudget ist nicht berührt.
- Allgemeiner: Jeder Prozess hat 512 MiB (`CosmosLinuxRun.cs:33`). Ein Lauf kann bis zu 256 Kernelthreads bzw. Prozesse haben (`MaxRunThreads`).
  Schon ein einziges Programm kann damit den 512-MB-Gast füllen.
- Der Schutz für den Kernel (`cosmos-patch/user-frame-reserve` 9ff7e867, „V43, ENOMEM statt Kernel-Halt“) ist **nicht** im Stand:
  `deploy/cosmos/COMMIT` = 63e672db, der Elter dieses Patches. In 3baf7cb0 hält der Kernel also an, wenn ein Programm den Speicher füllt.
- Das ist bekannt und in Arbeit, deshalb P2. Die memfd-Lücke bleibt aber auch mit V43 bestehen (Budget umgangen, andere Läufe und Apps
  verhungern).
- Vorschlag: memfd-Frames gegen ein Budget je Lauf zählen, V43 einspielen.

## P3

1. `LinuxUserProcess.cs:1314-1321` `CopyLocked`: Scheitert `Populate` mitten im Bereich (Budget voll, Lesefehler einer Dateiseite), bleibt der
   Bereich halb geschrieben. Das widerspricht „nichts oder alles“ aus `IUserMemory.cs:7-8`.
2. `LinuxSyscallsSocket.cs:491-499` und `:579-593` `recvmsg`: Ein Fehler beim Verteilen oder beim Schreiben des Kontrollpuffers kommt erst nach
   `Receive`. Die Nachricht ist dann verbraucht, und die Dateien werden geschlossen. Linux liefert die Daten mit `MSG_CTRUNC`; `-EFAULT` gibt es
   dort nur ohne kopierte Bytes und ohne Verbrauch.
3. `LinuxFileLocks.cs` fcntl-Bereichssperren: ohne Obergrenze und linear durchsucht, unter dem FileGate des ganzen Laufs. Sperren auf jedes
   zweite Byte erzeugen Millionen Einträge (Heap- und CPU-DoS).
4. `LinuxSyscallsMore.cs:121` und `LinuxFileSyscalls.cs:199`: Ein `read` bis 2 GiB auf eine große Datei läuft komplett unter dem FileGate des
   Laufs. Abbrechen lässt er sich, alle anderen Datei-Aufrufe des Laufs warten aber so lange.
5. `LinuxSyscallsTerminal.cs:73-91` `ioctl` (FIONREAD auf `LinuxRegularFile`) läuft ohne FileGate. Ein Wettlauf mit `close` aus einem anderen Thread
   ist möglich. Vermutung, nicht geprüft; schlimmstenfalls endet nur das Programm (Brücke fängt Ausnahmen).
6. `LinuxSyscallsSocket.cs:159` und `LinuxSyscalls.cs:203`: iovec-Längen über `MaxRw` ergeben `-EINVAL`, Linux kürzt (dokumentiert).
   `TryReadCString` meldet eine zu lange Zeichenkette als `-EFAULT`, nicht als `-E2BIG` bzw. `-ENAMETOOLONG`, liest Byte für Byte und nimmt dabei
   jedes Mal `_memGate` (`LinuxUserProcess.cs:1209-1241`).
7. `LinuxUnixSocket.Listen.cs:21`: `MaxBacklog` 4096 × 208 KiB ungelesene Daten je wartender Verbindung ergibt etwa 850 MiB je Listener.
   Hängt mit P2-2 zusammen (Budget je Lauf fehlt).
8. #735 P3-1 (AddressSpaceMap) und P3-2 (Frames für Seitentabellen) sind unverändert offen (siehe oben).
9. Die Abbruchtaste Strg+Umschalt+Esc ist unter Windows-Hosts möglicherweise vom Host belegt. Vermutung, in VMware und QEMU unter Windows prüfen.

## Ohne Befund geprüft

- `UserRange.Contains` und `InUserRange`: überlaufsicher.
- `CanWrite`/`PagesAllowLocked`: Die Längen sind überall auf höchstens `MaxRw` bzw. `Capacity` begrenzt.
- `ReadIov`/`GatherIov`/`ScatterIov`: Überläufe geprüft.
- Parsen von `ReadRights`/cmsg: `len` gegen die Puffergrenze geprüft, `CMSG_ALIGN` ohne Endlosschleife, `MaxControl`, `SCM_MAX_FD`.
- `DeliverRights`: Rücknahme bei `-EFAULT`.
- `poll` (nfds ≤ 1024), `epoll`:
  - `MaxEntries` 4096.
  - Verschachtelung bzw. epoll auf sich selbst ergibt `-EINVAL`.
  - Puffer ≤ 48 KiB.
- `clone3` (stackalloc ≤ `CloneArgsMax`), `execve`-argv/envp (`MaxArgCount`/`MaxArgBytes`), `mmap`/`munmap`/`mprotect`/`mremap`-Grenzen
  (`MaxUserSpan`, `EOVERFLOW` bei memfd), Überlauf von timerfd.
- Alle Warteschleifen (Pipe, Socket, eventfd, timerfd, Listen, UDP, futex, wait, fcntl-Wait) warten in Scheiben mit Abbruchprüfung.
- Die Terminalzeile ist auf `MaxLine` 4096 begrenzt.
- `rt_sigreturn` (`LinuxSignals.cs:546`):
  - RFLAGS nur über `FixFlags`, also kein IOPL/IF.
  - CS/SS bleiben die des Kernels.
  - MXCSR maskiert.
- Ausnahmen im Verteiler beenden nur das Programm (`CosmosLinuxRun.cs:1051`). Ein OOM des Heaps ist davon ausgenommen (siehe V43).
- Alle gerade gelesenen Benutzerstrukturen werden einmal in einen Kernelpuffer kopiert; ein TOCTOU über doppeltes Lesen gibt es nicht. Es bleibt
  das Fenster zwischen `CanWrite` und dem Schreiben (P1-1 Punkt 3, P3-2).

## Grenzen

- Kein Gastlauf und kein Kernelbau: Ob ein OOM des Heaps den Kernel anhält oder nur das Programm beendet, ist offen (Vermutung nach der
  Nachricht von V43).
- Nicht jede Zeile vollständig gelesen: `LinuxProcFiles`, `LinuxFileMetaSyscalls`, `LinuxSyscallsSoft`, `LinuxSyscallsStart`, die Vfs-Dateien
  und `LinuxSignals` (außer `Return`) nur stichprobenartig.
- Ring 3, UserMode, ELF-Lader und Cosmos-Patches waren nicht Teil dieses Blocks.
