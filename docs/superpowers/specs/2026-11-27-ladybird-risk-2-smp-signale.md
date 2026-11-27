# Risiko-Review Block 2: SMP-Rennen und Signale (V10, V18, V24, V35, V38)

Stand: `origin/story/ladybird` 3baf7cb0 gegen `origin/master`, Cosmos-Anteile `origin/story/ladybird-cosmos` (UserMode.cs, UserModeX64.cs,
UserMode.s, UserPageTable.cs, ThreadAffinity.cs, SchedulerManager.YieldThread). Nur gelesen; kein Bau, kein Gastlauf, keine Hosttests.

Ergebnis: **kein P1, ein P2, elf P3.**

## Belegte Grundlagen (keine Befunde)

- **Eine CPU je Lauf**: `CosmosLinuxRun.Execute` (CosmosLinuxRun.cs:727-733) setzt die Affinität des Programmthreads auf eine CPU; Kindthreads (ChildBody),
  fork- und execve-Prozesse (ProcessBody) erben sie (ThreadAffinity.cs: "A thread inherits its creator's set", harte Menge). Damit braucht es keinen
  TLB-Shootdown; UserPageTable.Unmap/TryProtect lehnen zusätzlich ab, solange eine andere CPU auf der Tabelle läuft, und `CosmosUserPages.Unmap` meldet das
  als false. CR3 und FPU bei CPU-Wechsel kommen nur beim Umzug vor dem Binden vor. Folge (kein Risiko, nur Einordnung): smp4 bringt Ladybird keine
  Parallelität; die Rennen zwischen Linux-Threads entstehen durch Verdrängung auf einer CPU, echte Parallelität nur gegen Shell-/Abbruchthreads.
- **rt_sigreturn ohne Rechteausweitung**: CS/SS werden nie aus dem Rahmen übernommen (LinuxSignals.cs:656, 1110 CosmosLinuxRun.cs); RIP und RSP prüft
  `UserModeX64.IsValid` (< 2^47, CS 0x4B, SS 0x43, IF=1, nur AllowedFlags) vor jedem Rücksprung, auch vor `sysretq` (UserMode.cs `SyscallEntry` → `Prepare`);
  RFLAGS nur FixFlags (LinuxSignals.cs:546), MXCSR mit der Hardwaremaske gefiltert (549-561, kein #GP im fxrstor64 im Ring 0); SFMASK löscht TF/IF/DF/NT/AC;
  nichtkanonischer RSP → SIGBUS, Kernelhälfte → InvalidReturn. Rahmenschreiben geht über die Prozesskarte (TryWrite), nicht in Kernelspeicher.
- **futex**: Wert lesen und Einreihen unter `_gate`, Wecken nimmt dieselbe Sperre (LinuxThreads.cs:185-237, 241-272) → kein verlorenes Wecken; Kick/Abort
  ebenso unter `_gate`. Sperrordnung `_gate` → `_memGate`; keine Stelle gefunden, die `_memGate` hält und Futexes.* aufruft.
- **Signalmaske je Thread (V38)**: nur der eigene Thread schreibt `Blocked`; DeliverSignal/SignalStateChanged unter `_threadGate`. Beide Reihenfolgen
  (Entblocken vor/nach Zustellung) enden mit Kick, kein verlorenes Signal.
- **exit_group gegen clone**: Dekker-Paar Sched schreiben/Cancelled lesen (CosmosLinuxRun.cs:887-898 gegen 380-389), CloneThread prüft ExitCode unter
  `_threadGate`, TakeSlot prüft Cancelled unter Sperre. Abbruch (Strg+C, Zeitlimit) erreicht alle Wartenden über Futexes.Abort/IsAborted je Scheibe.
- **V38 Seitenfehler-Rennen**: nur nicht-präsente Fehler, Rechte vorher gegen die Karte geprüft (LinuxUserProcess.cs:947-971); MapsTo als Abschluss korrekt.

## P2

**P2-1 rt_sigreturn kehrt per `sysretq` zurück: rcx und r11 aus dem Rahmen gehen verloren (V18/V24).**
`CosmosLinuxRun.SignalReturn` (CosmosLinuxRun.cs:1083-1101) schreibt die Register in den Syscall-Frame; der Rückweg ist immer
`_native_x64_user_syscall` → `pop rcx // RIP`, `pop r11 // RFLAGS`, `sysretq` (story/ladybird-cosmos `UserMode.s`, Syscall-Exit). Danach gilt rcx = RIP,
r11 = RFLAGS statt der gesicherten Werte. LINUX_ABI.md:474 begründet das mit "ein unterbrochener Aufruf hatte sie ohnehin so" – das stimmt für Rahmen an
Aufrufgrenzen, **nicht für V24-Ausnahmerahmen** (beliebiger Befehl, rcx/r11 lebendig). Linux nimmt dafür iret.
Ablauf (belegt am Code): `mov rcx, p; mov [rcx], eax` auf schreibgeschützter Seite → #PF → V24-Handler (SIGSEGV, z. B. mprotect RW) → `rt_sigreturn` →
sysret setzt rcx = RIP des Befehls → der wiederholte Befehl schreibt an die Codeadresse → zweiter Fehler/Absturz oder stille Registerverfälschung.
Gleiches für SIGFPE-Handler, die zurückkehren, und für jedes nach einem Ausnahmerahmen zugestellte weitere Signal. LINUX_ABI.md:632-634 verspricht
"kehrt er per rt_sigreturn zurück, läuft der Befehl noch einmal". Vermutung zur Wirkung auf die Vorstellung: gering, solange Ladybird/LibJS aus SIGSEGV nur
abbrechen (Stapelüberlauf) statt zurückzukehren; nicht geprüft. Abhilfe braucht Cosmos (Rückweg über iretq, wenn rcx≠rip oder r11≠rflags) oder mindestens
die Doku-Korrektur als Grenze.

## P3

1. **sigaction ohne `_threadGate`** (LinuxSyscallsMore.cs:320-323) gegen TakeSignal/TakeFaultSignal (LinuxSignals.cs:209-233, 257-273) auf einem anderen
   Thread: Handler neu, Flags/Restorer noch alt → `Setup` ohne SA_RESTORER → ForceSegv (Prozess endet SIGSEGV) oder SA_SIGINFO falsch. Fenster sehr klein.
   Ebenso SA_RESETHAND-Schreiben in TakeSignal gegen sigaction.
2. **Alternativstack je Prozess** (LinuxSignals.cs:591-696, RestoreAltStack 742-771), unsynchronisiert; SS_AUTODISARM schreibt AltSp/AltSize/AltFlags
   ohne Sperre. Zwei Threads mit SA_ONSTACK legen Rahmen auf denselben Stack; ruft ein Nebenthread sigaltstack und endet (Stack unmapped), scheitert
   jeder spätere SA_ONSTACK-Rahmen → SIGSEGV. Dokumentierte Abweichung; strace zeigt 21 sigaltstack in 4 Ladybird-Prozessen (ob je Thread: offen).
3. **SA_RESETHAND + doppelt anhängig** (Prozess- und Thread-Pending): TakeSignal setzt Handler=0 und nimmt nur ein Bit; das andere bleibt anhängig mit
   SIG_DFL, `SignalWaiting` (LinuxSignals.cs:186-193) bleibt true, TakeSignal nimmt es nie (Handler ≤ 1), kein SignalStateChanged → jede Warte-
   operation dieses Threads liefert sofort -EINTR (Spin) statt Standardaktion. Selten.
4. **Beendeter Thread nimmt Prozesssignale**: nach ExitThread (nicht letzter) läuft in OnSyscall noch `DeliverSignals` (CosmosLinuxRun.cs:1049);
   TakeSignal prüft `me.Exited` nicht (LinuxSignals.cs:199-205) → Rahmen auf den Stack, dann LeaveCurrent → Signal verloren. musl blockiert in
   pthread_exit alle Anwendungssignale → für Ladybird/musl vermutlich ohne Wirkung; rohes SYS_exit betroffen.
5. **Kick weckt alle Threads**: ein prozessgerichtetes Signal lässt jeden nicht blockierenden Wartenden mit -EINTR zurückkehren (LinuxUserProcess.cs:536,
   LinuxThreads.cs:207); für nicht neu startbare Aufrufe (poll, epoll_wait, nanosleep, futex mit Frist, sem_wait bei `__eintr_valid_flag`) sieht jeder
   Thread EINTR, Linux nur der gewählte. Anwendungen müssen EINTR schleifen.
6. **eventfd/timerfd blockierend lesen** prüfen nur IsAborted, nicht SignalWaiting (LinuxEventFd.cs:131-141, 96-108; LinuxTimerFd.cs:126-131): Signal
   mit Handler wird erst nach dem Lesen zugestellt (Linux: -EINTR/-ERESTARTSYS).
7. **Neue Threads starten mit FPU-Grundzustand** statt des Zustands des Elternthreads (StartChild CosmosLinuxRun.cs:807 ohne StartFpu; fork 1193 kopiert):
   MXCSR (FTZ/DAZ, Rundung) und x87-CW werden nicht vererbt (Linux copy_thread kopiert fpu).
8. **execve gegen gerade beendeten Thread**: OnlyMainThreadLives (LinuxSpawn.cs:776) zählt einen Thread schon nach `Exited` (LinuxUserProcess.cs:423-436)
   als weg, sein Kernelthread ist aber bis ChildBody.finally (CosmosLinuxRun.cs:919-927) an die alte Tabelle gebunden. pthread_join kehrt nach dem
   clear_child_tid-Wecken zurück, main ruft execve → ReplaceOwnImage → Destroy → `UserPageTable.Dispose` wirft "A thread is bound" (LinuxUserProcess.cs:1264)
   → Brückenfehler, Lauf Killed, Frames der alten Tabelle bleiben belegt (kein Use-after-free, weil Dispose vor der Freigabe wirft). Ladybird startet
   Kinder per posix_spawn (vfork-Weg), deshalb vermutlich nicht im Demo-Pfad.
9. **TerminatedBySignal ohne Sperre** (LinuxUserProcess.cs:647) gegen `ExitCode ??=` unter Sperre (827): exit_group(0) und gleichzeitiges SIGPIPE-Ende
   → ExitCode 0, aber Describe meldet "signal 13" (CosmosLinuxRun.cs:588).
10. **kill(-pgid)/kill(0) sammelt ohne GroupGate** (LinuxWait.cs:178-198): ein gleichzeitig per fork erzeugtes Kind oder ein setpgid dazwischen entgeht dem
    Gruppensignal (Linux schließt das in copy_process aus). V35.
11. **SignalProcess**: InVfork-Prüfung unter target._threadGate, DeliverSignal danach ohne Sperre (LinuxWait.cs:239-260); fällt die execve-Übergabe
    (HandOverKill) dazwischen, landet das Signal beim alten vfork-Objekt und geht verloren.

## Nicht geprüft / offen

- Kein Gastlauf, kein Bau, keine Hosttests (Auftrag); P2-1 nur aus Code und Assembler abgeleitet, keine Gastprobe.
- V30-Dateiseiten im Seitenfehler unter `_memGate` (Plattenlesen unter futex-`_gate` beim futex-Wertlesen) nur gestreift: Latenz, kein Befund.
- Cosmos-Patches user-cr3 (Option B, Protect-Wächter) und yield-now nur auf Rückwege/Sperren überflogen; mit der Einzel-CPU-Bindung nicht kritisch.
- VMware und echte Hardware: keine Aussage.
