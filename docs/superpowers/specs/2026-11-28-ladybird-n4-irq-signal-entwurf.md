# Entwurf N4: Signalzustellung bei IRQ-Rückkehr

> Offenes Paket aus dem [N4-SMP-Entwurf](2026-11-28-ladybird-n4-smp-entwurf.md), Abschnitt 6, und der Grenze in `docs/LINUX_ABI.md`, V18.
> Stand der Befunde: ZehOS `origin/story/ladybird` 4a43cde4, Cosmos `57b69576e` (`deploy/cosmos/COMMIT`). Nur gelesen: kein Bau, kein Gastlauf.

## 1. Ausgangslage (belegt am Code)

| Punkt | Stand | Beleg |
| --- | --- | --- |
| Zustellung | nur beim Rücksprung aus einem Aufruf (`DeliverSignals` im Syscall-Handler) oder aus einer Ausnahme (V24, `OnFault`) | `ProjektZeh/Runtime/CosmosLinuxRun.cs:1273`, `:1293-1306` |
| Signal anhängig machen | `Queue` unter `_threadGate`, danach nur `Futexes.Kick()` (weckt Wartende); ein rechnender Thread merkt nichts | `ProjektZeh/Processes/LinuxUserProcess.cs:548-565` |
| Rückweg eines Interrupts in den Ring 3 | Stub ruft nach `__managed__irq` `PrepareReturn`; das prüft den unterbrochenen oder den eingeplanten Rahmen (gestagter Wechsel) mit `UserMode.Prepare` und lenkt ihn nur zum Verlassen um (`s_leaveAddress`, Ring 0) | Cosmos `src/Cosmos.Kernel.Core.X64/Cpu/UserModeX64.cs:150-177`, `src/Cosmos.Kernel.Core/CPU/UserMode.cs:239-258` |
| Kontext dort | maskiert, Interruptkontext, ggf. noch auf dem Stapel des abgebenden Threads: keine Sperre, keine Allokation, kein Schreiben in den Benutzerstapel (Seitenfehler, Bedarfsseiten) | `UserMode.Prepare` (Kommentar „masked … Allocation-free“) |
| Rahmenlage | Interrupt-Stub und Syscall-Eingang legen dasselbe ab: fxsave-Abbild (512 Byte), darüber `UserFrame` (GPRs, 4 Infoworte, iret-Rahmen). Für einen Rahmen aus Ring 3 ist das das Unterste, was auf dem Kernelstapel des Threads lebt | `Interrupts.s:800-810`, `UserMode.s:113-148`; ZehOS nutzt das schon (`CosmosLinuxRun.cs:1290`, `:1765`) |
| Anstoß | `UserMode.Kick(thread)` (Patch `user-leave-kick`) schickt `0xF0` an die CPU des Threads; jeder Rückweg läuft dann durch `Prepare` | `UserMode.cs:163-196` |

Folge: Den Zustellpunkt selbst in `Prepare` zu legen geht nicht (Sperre `_threadGate`, Schreiben in den Benutzerstapel). Er muss
im Threadkontext mit offenen Interrupts laufen, wie der Syscall-Handler.

## 2. Vorschlag: Cosmos-Patch `user-return-work` (Variante A)

Wie Linux `TIF_SIGPENDING`/`exit_to_user_mode_loop`: Ein Merker am Thread lenkt den nächsten Rückweg in den Ring 3 in einen Rückruf um.

1. `SchedulerThread._userWork` (int) und `UserMode.ReturnHandler` (`delegate* unmanaged<UserFrame*, void>`, wie `SyscallHandler`).
2. `UserMode.RequestWork(SchedulerThread)`: `Interlocked.Exchange(ref _userWork, 1)`, dann `Kick(thread)`. Jeder Kontext, ohne Sperre, ohne Allokation.
3. `PrepareReturn` (Interrupt-Rückweg): liefert `Prepare` 0 und ist `_userWork` gesetzt und ein Handler da, sichert es RIP, RFLAGS, RSP des Rahmens in
   Feldern des Threads (CS/SS sind die festen Benutzerselektoren) und lenkt den Rahmen auf eine neue Rampe `_native_x64_user_work` um: CS Kernel,
   RFLAGS ohne IF, **RSP = Anfang des fxsave-Abbilds unter dem Rahmen**. Kein `swapgs` (CS ist Ring 0, wie beim Umlenken zum Verlassen).
4. Rampe (`UserMode.s`): `cld`, `fninit`, Kernel-MXCSR, `rdi = rsp + 512`, Aufruf `__managed__user_work`: iret-Worte zurückschreiben, dann Schleife
   `while (Interlocked.Exchange(ref _userWork, 0) != 0) { sti; ReturnHandler(frame); cli; }`, dann `Prepare(frame)`. Ergebnis ≠ 0 →
   `.Luser_leave_rax`, sonst der vorhandene Weg `.Luser_syscall_iretq` (alle Register, fxrstor aus dem Abbild). Dieselbe Schleife vor `Prepare` in
   `SyscallEntry`, damit ein Merker aus dem Aufruf nicht erst am nächsten Interrupt wirkt.
5. Reihenfolge in `Prepare` bleibt: Verlassen > Fehler > Arbeit. Paranoide Vektoren (NMI, #DF, #MC) bleiben unberührt (`PrepareReturn` kehrt vorher zurück).

ZehOS (Brücke): `UserMode.ReturnHandler = &OnReturnWork`, das `DeliverSignals(me, frame, Number = -1, 0, 0)` ruft (wie nach `rt_sigreturn`,
`CosmosLinuxRun.cs:1326`: kein Neustart, kein EINTR-Pfad). `LinuxUserProcess` bekommt einen Rückruf „Signal anhängig“ (plattformunabhängig, Brücke setzt
ihn), aufgerufen neben `Futexes.Kick()` (`LinuxUserProcess.cs:564`) **außerhalb** von `_threadGate`: für `tgkill` der Zielthread, sonst alle Threads des
Prozesses (Maske und Anhängiges sind je Prozess; wer zuerst zurückkehrt, nimmt es, die anderen finden nichts). Damit wird auch der in S3 entfallene `Kick`
nach `DeliverSignal` wirksam; auf einer CPU wirkt der Merker beim nächsten Takt oder Wechsel.

Verworfen, Variante B: Verlassen mit eigenem Wert und Neueintritt über ein neues `Enter(UserFrame*)`. Braucht Sicherung des ganzen Rahmens und der FPU
außerhalb des Stapels und ändert die Laufschleife der Brücke; mehr Code im ZehOS-Pfad, gleicher Patchumfang.

## 3. Risiken

| Risiko | Wirkung | Gegenmittel |
| --- | --- | --- |
| Rampe überschreibt den Rahmen | Register des Programms falsch | Rampe läuft strikt unter dem Abbild; der Rahmen ist das Unterste Lebende auf dem Stapel (aus Ring 3 an RSP0, beim gestagten Wechsel der gesicherte Kontext des Zielthreads). Testzelle prüft alle GPRs und XMM nach der Rückkehr |
| FPU des Programms im Kernel | unmaskierte Ausnahme im Ring 0 | `fninit`/`ldmxcsr` in der Rampe wie im Syscall-Eingang; Abbild bleibt maßgeblich |
| gesicherte iret-Worte am Thread | ein zweiter Rückweg vor der Rampe überschreibt sie | Rampe läuft maskiert bis zum Zurückschreiben; ein Thread hat höchstens einen Rückweg in den Ring 3 gleichzeitig |
| Merker beim Eintritt (`Enter`) | Signal bleibt bis zum nächsten Interrupt liegen | harmlos; `Enter` lässt den Merker stehen |
| Interrupt-Last durch `Kick` | Reschedule-IPIs bei vielen Signalen (`SIGCHLD`, `SIGALRM` je Takt) | nur bei Signal mit Handler und nicht blockiert; Zahl im Gast zählen |
| gestagter Wechsel | Rahmen gehört dem neuen Thread, Merker dessen | `PrepareReturn` liest schon den gestagten Rahmen und `CurrentThread` der CPU; Merker und Felder vom selben Thread |
| Sperrreihenfolge | neu nur der Rückruf außerhalb `_threadGate` | keine neue Sperre; Handler nimmt `_threadGate` wie der Syscall-Pfad |

## 4. Prüfung

- Cosmos-Testkern `Cosmos.Kernel.Tests.UserMode`: Zelle „Arbeit bei IRQ-Rückkehr“ (Programm rechnet ohne Aufruf, zweiter Thread ruft `RequestWork`,
  Handler setzt ein Register; alle anderen GPRs und XMM0-15 unverändert), smp1 und smp4; Gegenprobe ohne Kick (`TestSkipKick`) kommt erst am Takt.
- ZehOS: neues `experiments/LinuxAbi/src/v18/sig_spin.c` (Rechenschleife ohne Aufruf, `setitimer`/`SIGALRM` und `pthread_kill` aus einem zweiten Thread,
  Handler setzt `volatile sig_atomic_t`; FPU-Rechnung prüft sich nach dem Handler), Erwartung Linux 6.12. Satz v18/n4smp mit linuxsmp=0 und 1, kvm:1 und kvm:4.
- Host- und nativer Bau `-warnaserror`, `scripts/check-plug-binding.py` entfällt (kein Plug). Doku: V18-Grenze in `LINUX_ABI.md`, `patches/user-return-work.md`.

## 5. Abgrenzung zu S4

S4 (futex-Eimer) ändert `ProjektZeh/Processes/LinuxThreads.cs`. Dieses Paket ändert `LinuxThreads.cs` nicht; `Futexes.Kick()` bleibt, wie es ist.
Berührt: Cosmos `UserMode.cs`, `UserModeX64.cs`, `UserMode.s`, `SchedulerThread.cs`, Testkern; ZehOS `CosmosLinuxRun.cs`, `LinuxUserProcess.cs` (eine Stelle),
`docs/LINUX_ABI.md`, `experiments/LinuxAbi/`. Pin-Nummer (`deploy/cosmos/VERSION`) nach Absprache mit S4 und weiteren Cosmos-Patches.
