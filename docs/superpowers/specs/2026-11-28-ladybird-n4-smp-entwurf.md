# Entwurf N4-SMP: Threads und Prozesse eines Linux-Laufs auf mehreren CPUs

> Entwurf zu T5 im [Plan nach dem Vorzeigestand](../plans/2026-11-28-ladybird-n2z-tor.md); gehört zur Bahn „Threads“ in [N4](../plans/2026-11-24-ladybird-n4-posix.md)
> („danach mehrere CPUs je Prozess mit TLB-Abgleich per IPI“) und zum Tor N4-Z aus dem [Gesamtplan](../plans/2026-11-24-ladybird-nativ.md) („ein `pthread`-Programm nutzt zwei CPUs“).
> Stand der Befunde: ZehOS `origin/story/ladybird` 4519757a, Cosmos `f2b64df6` (`deploy/cosmos/COMMIT`). Nur gelesen: kein Bau, kein Gastlauf, keine Hosttests.

Der Entwurf beschreibt den Ausgangsstand. Merge- und Gastbefunde vom 28. November stehen im [N4-Plan](../plans/2026-11-24-ladybird-n4-posix.md#stand-28-november-2026).

## 1. Ausgangslage (belegt am Code)

| Punkt | Stand | Beleg |
| --- | --- | --- |
| Bindung an eine CPU | `Execute` setzt die Affinität des Programmthreads auf `CpuSet.Single(cpus.First)`; Kindthreads, fork- und execve-Prozesse erben die Menge | `ProjektZeh/Runtime/CosmosLinuxRun.cs:756-762`, Cosmos `ThreadAffinity` |
| Kein TLB-Abschuss | `UserPageTable.Unmap` und `TryProtect` invalidieren nur auf der eigenen CPU und **lehnen ab**, solange eine andere CPU die Tabelle in CR3 hat (`TryBeginChange`: `_active > here`) | Cosmos `src/Cosmos.Kernel.Core/Memory/UserPageTable.cs:219-275`, `:460-498` |
| Zähler statt Maske | `_active` zählt CPUs mit der Tabelle in CR3, weiß aber nicht welche; `Changing` (Bit 30) hält jeden Wechsel auf die Tabelle an, während ein Eintrag umgeschrieben wird | `UserPageTable.cs:119-136`, `:445-456` |
| CR3-Wechsel | `SwitchFor` lädt beim Threadwechsel unter der Queue-Sperre die Tabelle des nächsten Threads oder die Kernelwurzel der CPU; jeder Wechsel weg von einer Benutzertabelle leert deren TLB-Einträge (kein PCID, Benutzerseiten nie global) | `UserPageTable.cs:401-438` |
| IPI | `LocalApic.SendIpi(apicId, vector)` (xAPIC, Aufrufer maskiert); belegt sind `0xF0` Reschedule, `0xF1` Stop, NMI für Panik | `src/Cosmos.Kernel.Core.X64/Cpu/LocalApic.cs:290-304`, `X64InterruptController.cs:31-38`, `:86-96` |
| Kernelzugriff auf Benutzerspeicher | über das HHDM (Frame + `hhdm`), nie über die Benutzerabbildung; der TLB der Benutzertabelle betrifft nur Ring 3 | `CosmosLinuxRun.cs:1665` (`CosmosUserPages.Frame`) |
| FPU bei CPU-Wechsel | `fxsave64`/`fxrstor64` im Interrupt-Stub, sofort (nicht lazy) | Cosmos `patches/fxsave.md` |
| Ring-3-Eintritt je CPU | Kernel-RSP über `PerCpuBlock` und TSS der CPU | `src/Cosmos.Kernel.Core.X64/Cpu/UserModeX64.cs:104-111` |
| Verlassen des Ring 3 | `UserMode.RequestLeave(thread, value)` wirkt „spätestens mit dem nächsten Interrupt in Ring 3“, also am Timertakt der CPU, auf der der Thread läuft | Cosmos `src/Cosmos.Kernel.Core/CPU/UserMode.cs:146-160` |
| Sperren der Prozesse | `Monitor`-Sperren (`_memGate`, `_threadGate`, futex `_gate`, Datei- und Socketsperren); die Rennen zwischen Linux-Threads sind heute schon da (Verdrängung auf einer CPU), echte Parallelität gab es nur gegen Shell- und Abbruchthreads | `LinuxUserProcess.cs:53`, [Risiko-Review 2](2026-11-27-ladybird-risk-2-smp-signale.md) |
| Monitor über CPUs | `ConditionVariable.Wait` reiht unter `_lockGuard` (IRQ-sicherer Spinlock) ein, gibt den Mutex frei und blockiert im selben Bereich: kein verlorenes Pulse. **Im Gast auf mehreren CPUs nicht belegt** | Cosmos `src/Cosmos.Kernel.Core/Scheduler/ConditionVariable.cs:52-68`; `docs/SMP_AUDIT.md` „Vorfrage“ |
| futex | eine Tabelle je Prozess unter einem Monitor; Wert lesen und Einreihen unter `_gate`, Wecken nimmt dieselbe Sperre; `PulseAll` weckt alle Wartenden des Prozesses; Schlaf in Scheiben von 50 ms | `ProjektZeh/Processes/LinuxThreads.cs:126-272` |
| Nach außen | `sched_getaffinity`, `/proc/stat`, `/proc/cpuinfo` melden eine CPU; kein `getcpu` | `Syscalls/LinuxSyscallsLimits.cs:157`, `Syscalls/LinuxProcFiles.cs:632-695` |
| Frames freigeben | `munmap`, `Release`, `mprotect` (neu abbilden statt umschreiben) rufen `IUserPages.Unmap`; der Frame wird erst nach erfolgreichem Unmap frei (`DropFrame`, geteilte Frames gezählt) | `LinuxUserProcess.cs:1126-1139`, `:1205-1215`, `:1446-1460`, `LinuxUserProcessShared.cs:180-195`, `Syscalls/LinuxMemFile.cs:786-793` |

Folge: Die Bindung hebt man nicht einfach auf. Ohne Abschuss lehnt die Tabelle jedes `munmap` und `mprotect` ab, sobald zwei Threads des Prozesses
auf zwei CPUs laufen; ein Abschuss, der den Frame vor der Bestätigung freigibt, ließe Ring 3 auf einer anderen CPU in einen fremden Frame schreiben.

## 2. Ziel und Abgrenzung

- Alle Threads eines Prozesses laufen auf der CPU-Menge des Laufs (Vorgabe: `KernelCpus.DefaultAffinity` beim Start, heute `WorkerCpus`, CPU 0 bleibt
  Interrupts und Cosmos-Diensten, `CosmosBootOptions.cs:54-70`). Verschiedene Prozesse eines Laufs ebenso.
- `munmap`, `mprotect`, `brk`-Schrumpfen, `madvise(DONTNEED)` und execve-Abbau wirken auf allen CPUs, bevor ein Frame frei wird.
- Nicht in N4-SMP: Copy-on-Write für fork (Bahn Prozesse; nutzt aber den Abschuss aus S1), PCID, Lastausgleich nach Cache-Nähe, `sched_setaffinity` für fremde Threads,
  NUMA. Die Threadgrenze 256 je Lauf (`CosmosLinuxRun.cs:88`) bleibt.

## 3. Pakete

Reihenfolge: S0 → S1 → S2 → (S3, S4 nebeneinander) → S5. S4 kann auf dem Host früher beginnen.

| Paket | Inhalt | Ort | Abhängigkeit | Aufwand (Schätzung) |
| --- | --- | --- | --- | --- |
| S0 Vorprobe | Monitor Wait/Pulse über CPUs im Gast belegen (Threading-Suite smp4, Paar Wartender/Weckender mit harter Affinität auf zwei CPUs, 10⁵ Runden ohne Hänger) | Cosmos-Testkern | – | 1 Tag |
| S1 Abschuss | Cosmos-Patch `tlb-shootdown`: CPU-Maske statt Zähler, IPI-Vektor, Epoche je CPU, `Unmap`/`TryProtect` ohne Ablehnung, Bereichsvarianten | Cosmos `UserPageTable.cs`, `PerCpuState.cs`, `X64InterruptController.cs` | S0 | 3-5 Tage |
| S2 Lauf freigeben | Bindung in `Execute` aufheben (zuerst hinter Schalter), `IUserPages` auf Bereiche, Ausgabe der CPU-Zahl, `getcpu` | ZehOS `CosmosLinuxRun.cs`, `LinuxUserProcess*.cs`, `Syscalls/` | S1 | 2-3 Tage |
| S3 Signale, Abbruch | Anstoß per IPI an die CPU des Zielthreads bei Signal, exit_group, Absturz; schmale Rennen aus Risiko-Review 2 schließen, die mit echter Parallelität breiter werden | Cosmos `UserMode.cs` (kleiner Patch `user-leave-kick`), ZehOS `LinuxSignals.cs`, `CosmosLinuxRun.cs` | S2 | 2 Tage |
| S4 futex | Wartende einzeln wecken, Tabelle in Eimer nach Adresse, Requeue über zwei Eimer | ZehOS `LinuxThreads.cs` | – (Host), Gast nach S2 | 2-3 Tage |
| S5 Prüfung, Tor | Testprogramme, Gegenproben, Messung smp1 gegen smp4, Schalter auf Vorgabe | `experiments/LinuxAbi/src/s3/`, Skripte | S2-S4 | 2-3 Tage plus Messung |

### S1 Cosmos-Patch `tlb-shootdown`

**Idee:** Jede CPU zählt eine TLB-Epoche, die bei jedem CR3-Schreiben und bei jedem Abschuss-IPI steigt. Wer Einträge ändert, merkt sich die Epochen der
CPUs, die die Tabelle halten, schickt ihnen das IPI und wartet, bis jede Epoche weitergezählt hat. Danach kann keine CPU den alten Eintrag mehr im TLB haben.
Kein Auftragspuffer, keine Allokation, mehrere Auslöser gleichzeitig (auch für dieselbe Ziel-CPU) brauchen keine Absprache: Das Ziel leert immer den ganzen
Benutzer-TLB (CR3 neu laden), das bedient jeden wartenden Auslöser.

Schnittstellen (Cosmos, `[Experimental(SchedulerSeamDiagId)]` wie bisher):

```csharp
public sealed unsafe class UserPageTable
{
    // Entfällt: int _active mit Bit Changing. Neu: CPUs mit der Tabelle in CR3, ein Bit je logischer CPU
    // (4 × ulong wie CpuSet). Interlocked.Or vor dem CR3-Schreiben dieser Tabelle; Interlocked.And erst nach dem CR3-Schreiben,
    // das auf dieser CPU eine andere Wurzel lädt. Ein Wechsel T -> T (gleiche Tabelle, anderer Thread) schreibt CR3 nicht
    // (SwitchTo kehrt früh zurück, UserPageTable.cs:412-416) und lässt das Bit stehen: Der TLB der CPU hält die Einträge weiter.
    private ActiveMask _active;

    /// Entfernt die Seite und kehrt erst zurück, wenn keine CPU sie mehr erreicht; der Frame gehört danach dem Aufrufer.
    /// Nur Threadkontext, Interrupts frei, kein SpinLock gehalten (eine Monitor-Sperre wie _memGate ist erlaubt).
    public ulong Unmap(ulong user);

    /// Wie Unmap für [start, start + count Seiten); schreibt die Frames (0 = nichts abgebildet) nach frames, ein Abschuss für alle.
    public int UnmapRange(ulong start, int count, Span<ulong> frames);

    /// Wie bisher, ohne Ablehnung wegen anderer CPUs.
    public bool TryProtect(ulong user, bool writable, bool executable);
    public bool TryProtectRange(ulong start, int count, bool writable, bool executable);
}

public sealed class PerCpuState
{
    // Steigt bei jedem CR3-Schreiben (SwitchTo) und in jedem Abschuss-Handler, immer NACH dem CR3-Schreiben (Interlocked,
    // volle Barriere): Eine Epoche, die vor dem Flush stiege, wäre eine Bestätigung vor dem Leeren des TLB.
    internal ulong _tlbEpoch;
}

public interface IInterruptController   // X64InterruptController
{
    void SendTlbFlush(ulong hardwareId);  // Vektor 0xF2
}
```

Ablauf `UnmapRange`:

1. Unter `_lock` (`AcquireIrqSafe`, wie heute; Interrupts maskiert, der Auslöser kann nicht umziehen): Einträge entfernen, Frames sammeln, auf der eigenen CPU
   `invlpg` je Seite.
2. Noch unter `_lock`: volle Barriere (`Interlocked`), dann `_active` lesen, die **eigene CPU-Nummer** festhalten und für jede andere CPU der Maske die Epoche lesen
   (Schnappschuss auf dem Stack: Maske, eigene CPU, Epochen; keine Allokation). Eine CPU, die danach einsteigt, setzt ihr Bit vor ihrem CR3-Schreiben (Interlocked)
   und lädt CR3 erst danach; ihre Seitenwanderung sieht die neuen Einträge. Eine, die vorher einstieg, steht in der Maske. Das ersetzt `Changing`.
3. `_lock` freigeben. Für jede CPU des Schnappschusses außer der in Schritt 2 festgehaltenen eigenen: IPI senden (Interrupts kurz maskiert um die zwei
   ICR-Schreiben, wie `SendIpi` verlangt). Der Ausschluss gilt der CPU, die in Schritt 1 `invlpg` ausgeführt hat, nicht der, auf der der Auslöser jetzt läuft:
   Zieht er zwischen Schritt 1 und 3 auf eine andere CPU der Maske um, bekommt diese ihr IPI trotzdem (ein IPI an die eigene CPU ist harmlos, der Handler läuft
   nach dem Freigeben der Interrupts).
4. Mit freien Interrupts warten, bis jede gemerkte Epoche weitergezählt hat (`Thread.SpinWait`). Grenze nicht 100 ms: Im Gast kann der Wirt eine vCPU für
   Zehntelsekunden entziehen (QEMU unter Last, VMware), ohne dass der Kernel fehlerhaft ist. Deshalb nach 100 ms das IPI an die säumigen CPUs **erneut** senden
   und weiter warten, nach der Grenze `zehos.tlbtimeout` (Bootoption, Vorgabe 5000 ms, 0 = nie) Panik mit CPU-Nummer: Ein Frame, der ohne Bestätigung frei würde,
   wäre Datenverlust; deshalb nie „aufgeben und freigeben“.
5. Rückkehr; erst jetzt darf der Aufrufer die Frames freigeben.

Handler `0xF2` (Interruptkontext): CR3 **aus dem Register** lesen und denselben Wert zurückschreiben (leert alle nicht globalen Einträge; ohne geladene
Benutzertabelle trifft es nur die Kernelwurzel und ist harmlos), **danach** `_tlbEpoch` erhöhen (Interlocked), EOI. Nicht `state._loadedTable` lesen: Der Handler
kann zwischen dem CR3-Schreiben in `SwitchTo` und dem Nachführen von `_loadedTable` kommen, das Register stimmt immer. Keine Allokation, kein Protokoll, keine Sperre.

**Kontext der Aufrufer (S2-Pflicht vor dem Patch):** `Unmap`, `UnmapRange`, `TryProtect` mit Abschuss und `Destroy` warten und dürfen nur im Threadkontext mit freien
Interrupts laufen. Zwei Auslöser, die mit IF=0 aufeinander warten, beantworten das IPI des anderen nie: Deadlock. Deshalb vor S1 alle Aufrufer auditieren und
im PR auflisten, besonders die Abbau- und Absturzpfade: `CosmosFaultIsolation.OnCpuException` (läuft mit IF=0, gibt heute nichts frei, nur `isolated.Stage`),
`CosmosLinuxRun.OnFault` (Ausnahmekontext; ruft nur `HandlePageFault`, das abbildet, nicht entfernt) und der Abbau nach dem Verlassen des Ring 3
(`CosmosLinuxRun.cs:1574`, Threadkontext). Der Patch prüft die Regel selbst: `Unmap` mit IF=0 oder auf einem Interruptstack ist ein Kernelfehler (Panik mit
Aufrufer-RIP), nicht ein stilles Warten.

**Regeln:** Gewartet wird nie unter einem SpinLock oder `CosmosIrqLock` und nie mit maskierten Interrupts: Zwei Auslöser, die mit maskierten Interrupts
aufeinander warten, hielten sich gegenseitig fest. `_lock` der Tabelle bleibt die innerste Sperre (darunter nur der `PageAllocator`). `TryMap` braucht keinen
Abschuss (x86 speichert keine nicht vorhandenen Einträge); der dann mögliche unechte Seitenfehler ist schon abgedeckt (`IUserPagesQuery.MapsTo`, V38).
`TryProtect` mit Abschuss, wenn Rechte wegfallen (RW → R, X → NX). Schreibrecht hinzufügen (R → RW) braucht keinen: Eine CPU mit altem Eintrag bekommt einen
unechten Schreibfehler auf vorhandener Seite, den `IsStaleWriteFault` (V38b, `LinuxUserProcess.cs:975-976`, `:1011`) schon als „Zugriff jetzt erlaubt, Befehl wiederholen“
statt SIGSEGV behandelt; diese Regel gehört in den Patchtext als Bedingung für die Abkürzung. **Ausführrecht hinzufügen (NX → X) nur mit Abschuss:** Ein unechter
Ausführfehler auf vorhandener Seite endet heute als SIGSEGV (die Tabelle meldet kein NX-Bit, `IsStaleWriteFault` nimmt Ausführfehler bewusst aus). Erst wenn
`IUserPagesQuery` das NX-Bit meldet und der Seitenfehler-Handler auch dafür „jetzt erlaubt → Retry“ prüft, darf auch dieser Fall ohne Abschuss laufen.

**Prüfung (Cosmos-Testkern, smp4):** Thread A liest auf CPU 1 in Schleife eine Seite, Thread B entfernt sie auf CPU 2 und bildet einen anderen Frame mit
Marke ab: A sieht die Marke nie über die alte Abbildung (Testsonde zählt Lesungen des alten Frames nach Rückkehr von `Unmap`: 0). Gegenprobe mit
`COSMOS_TEST_HOOKS`-Schalter, der das IPI auslässt: Zähler > 0. Dazu zwei Auslöser gleichzeitig auf zwei CPUs, je 10⁵ Abschüsse, kein Hänger.
Threading-, Interrupts-, Smp- und GarbageCollector-Suiten wie die Basis.

### S2 Lauf freigeben (ZehOS)

- `CosmosLinuxRun.Execute`: Bindung auf eine CPU nur noch, wenn der Schalter aus ist (Bootoption `zehos.linuxsmp=0|1`, Vorgabe 0 bis S5 bestanden; dann 1).
  Die Menge des Laufs wird beim Start festgehalten (`Running.Cpus`), damit `/proc` und `sched_getaffinity` sie melden.
- `IUserPages`: `Unmap(page)` behält die Bedeutung „danach erreicht keine CPU den Frame“; neu `UnmapRange(start, count, Span<ulong> frames)`.
  `munmap`, `Release` und `mprotect` sammeln zusammenhängende Seiten und schießen einmal ab statt je Seite. Der Zweig „Tabelle lehnt ab, Frame bleibt“
  (`LinuxUserProcess.cs:1446-1460`) bleibt als Schutz, sollte aber nicht mehr vorkommen; zählen und im Lauf melden.
- `mprotect` kann von „neu abbilden“ auf `TryProtectRange` umstellen (ein Abschuss, kein unechter Fehler je Seite); getrennter Commit, weil V48 darauf beruht.
- `sched_getaffinity`, `/proc/stat` (`cpuN`-Zeilen), `/proc/cpuinfo` (ein Block je CPU der Menge): Anzahl der Laufmenge. musl nimmt
  `sysconf(_SC_NPROCESSORS_ONLN)` aus `sched_getaffinity`; Ladybird und Skia richten ihre Threadpools danach.
- `getcpu` (309): CPU-Nummer aus `SchedulerManager.CurrentCpuState`, Knoten 0. `sched_setaffinity` nur für den eigenen Thread und nur Teilmengen der
  Laufmenge (`ThreadAffinity.Current`), sonst `-EINVAL`; im PR als Abweichung.
- Alle Stellen, die Frames freigeben, gegen die neue Regel prüfen: `DropFrame`, `LinuxMemFile.UnrefLocked` (geteilte Frames erst bei Zähler 0, also nach
  dem Unmap in jedem Prozess), execve (`ReplaceOwnImage` → `Destroy` erst nach dem Lösen aller Threads, `UserPageTable.Dispose` wirft sonst).
- `Destroy`/execve prüfen **die Maske**, nicht nur „alle Threads gelöst“: `RequestLeave` wirkt erst am Takt, und eine CPU löscht ihr Bit erst beim nächsten
  CR3-Schreiben einer anderen Wurzel (Wechsel T → T lässt es stehen). `Destroy` wartet im Threadkontext, bis `_active` leer ist (Grenze und Panik wie Schritt 4;
  eine CPU, die nur noch die Tabelle hält, ohne einen ihrer Threads zu fahren, bekommt ein `0xF0`, damit sie die Kernelwurzel lädt), und gibt erst dann
  Tabellenseiten und Frames frei.

### S3 Signale und Abbruch über CPUs

- Heute wirkt `RequestLeave` und jede Signalzustellung an einen Thread in Ring 3 erst am nächsten Timertakt seiner CPU. Auf einer CPU war das der Wechsel
  ohnehin; auf mehreren läuft ein Thread nach exit_group oder nach dem Absturz eines Geschwisters bis zu einem Takt weiter. Für die Richtigkeit reicht das
  (Speicher bleibt bis zum Lösen aller Threads), für die Latenz nicht.
- Cosmos-Patch `user-leave-kick`: `UserMode.RequestLeave` und eine neue `UserMode.Kick(SchedulerThread)` senden `0xF0` (Reschedule) an die CPU, auf der der
  Thread gerade läuft (`SchedulerThread` kennt seine CPU), sonst nichts.
- Abweichung (Befund S3, Entscheidung Lead 28. November: Variante A): ZehOS stellt Signale mit Handler nur beim Rücksprung aus einem Aufruf oder einer
  Ausnahme zu (`CosmosLinuxRun.DeliverSignals`), nie beim Rücksprung eines Interrupts in den Ring 3. Ein `Kick` nach `DeliverSignal` brächte das Signal
  einem rechnenden Thread daher nicht näher, auch nicht auf einer CPU; er entfällt in S3. SIGKILL und Signale mit Standardaktion „beenden“ gehen dagegen
  über `Exit`/`Kill` → `LeaveAll`/`LeaveProcess` → `RequestLeave` und erreichen den rechnenden Thread mit dem Patch sofort (Satz `n4smp`, `kill_spinner`).
  Die Zustellung bei IRQ-Rückkehr ist ein offenes Paket (Abschnitt 6).
- Mit echter Parallelität werden die schmalen Fenster aus [Risiko-Review 2](2026-11-27-ladybird-risk-2-smp-signale.md) breiter; vor der Vorgabe 1 schließen:
  P3-1 (`sigaction` unter `_threadGate`), P3-2 (Alternativstack je Thread, wie Linux), P3-9 (`TerminatedBySignal` unter derselben Sperre wie `ExitCode`).
- Absturz eines Threads (Ring-3-Ausnahme) auf CPU B, während Geschwister auf A und C laufen: `FaultIsolationPolicy` entscheidet mit `CpuExceptionInfo.ThreadId`
  (E5-D) unabhängig von CPU 0; im Gast mit smp4 prüfen (T3-Fälle aus dem Torplan, mit zwei laufenden Geschwistern).

### S4 futex über CPUs

Die Tabelle ist mit echter Sperre richtig (kein verlorenes Wecken); auf mehreren CPUs kostet sie:

- `Monitor.PulseAll(_gate)` weckt jeden Wartenden des Prozesses; jeder nimmt danach `_gate` und schläft wieder. Neu: ein Warteobjekt je Wartendem
  (vorab im Thread angelegt, `LinuxThread.FutexWait`), Wecken setzt `Woken` und weckt nur dieses.
- Eine Sperre je Prozess: alle Mutexe aller Threads streiten um sie. Neu: 16 Eimer nach `(address >> 2) % 16`, je eigene Sperre. `Requeue` nimmt beide Eimer
  in aufsteigender Nummer. Sperrreihenfolge: Eimer (aufsteigend) vor `_memGate`, an der Deklaration vermerken.
- `Abort` und `Sleep` (ohne Adresse) gehen über alle Eimer; die Scheiben von 50 ms bleiben als Netz gegen ein verlorenes Wecken.
- Geteilte futexe über Prozesse (V21, Abkürzung mit Neulesen je Scheibe) bleiben; eine laufweite Tabelle nach Frame und Versatz gehört zur Bahn IPC.
- Hosttests: bestehende futex-Tests, dazu Wecken genau eines von zwei Wartenden auf verschiedenen Adressen im selben Eimer, Requeue über zwei Eimer.

### S5 Prüfung und Tor

Neue Programme in `experiments/LinuxAbi/src/s3/` mit erwarteter Ausgabe von Linux (wie bisher `expected/s3/`), Ausgabe unabhängig von der CPU-Zahl:

| Programm | Prüft | Erfolg |
| --- | --- | --- |
| `smp_two_cpus.c` | Tor N4-Z: zwei Threads rechnen je 2 s; Prozess-CPU-Zeit (`CLOCK_PROCESS_CPUTIME_ID`) gegen Wandzeit, `getcpu` beider Threads | Verhältnis > 1,6 und zwei verschiedene CPUs; Ausgabe `two-cpus ok` (smp1: `one-cpu`, als eigener erwarteter Fall) |
| `tlb_munmap.c` | Thread A liest eine Seite in Schleife, B entfernt sie und bildet neu mit Marke ab | A endet mit SIGSEGV (Handler, `siglongjmp`), liest nie die Marke über die alte Adresse |
| `tlb_mprotect.c` | A schreibt in Schleife, B setzt `PROT_READ` und liest zweimal | beide Lesungen gleich, A bekommt SIGSEGV |
| `futex_stress.c` | 4 Threads, Mutex und Bedingung im Wechsel, 10⁵ Runden | Zähler genau, kein Hänger, Laufzeit im Protokoll |
| bestehend `threads.c`, `futex.c`, `fork_cow.c`, `exec.c`, `sigsegv_status.c` | keine Rückschritte | wie Linux |

Gastläufe smp1, smp2, smp4 (2 GiB), je 20 Durchläufe der Programme; Ladybird headless (Demo-Ablauf) smp4 zehnmal. Gegenprobe: ZehOS mit S1-Cosmos, aber
`zehos.linuxsmp=1` und Testschalter ohne IPI: `tlb_munmap` muss rot werden (beweist, dass der Test den Abschuss sieht). Messung nach `senior-engineer.md` 3:
Ladybird-Seitenaufbau smp1 gegen smp4, zwei ISOs im Wechsel, je fünf Läufe, Umgebung im PR. Erst danach Vorgabe `zehos.linuxsmp=1`.

**Bezug zum Tor N4-Z:** „ein `pthread`-Programm nutzt zwei CPUs“ belegt `smp_two_cpus.c`. Die übrigen Torpunkte (gleiche Ausgabe wie Linux, Deskriptor und geteilter
Speicher zwischen zwei Prozessen, Absturz des einen lässt den anderen laufen) laufen nach S2 auf mehreren CPUs und gehören deshalb in dieselbe Prüfmatrix:
geteilter Speicher (`memfd_seal.c`, `unix_fdpass.c`) mit beiden Prozessen auf verschiedenen CPUs, Absturz (`sigsegv_status.c`) mit laufendem Geschwister.

## 4. Sperrreihenfolge und Kontexte (neu oder geändert)

1. futex-Eimer (aufsteigend) → `_memGate` → `UserPageTable._lock` → `PageAllocator`.
2. Warten auf den Abschuss: unter `_memGate` erlaubt (Monitor, die Ziel-CPUs blockieren dort, nicht mit maskierten Interrupts); nie unter `_lock`,
   `CosmosIrqLock`, Queue-Sperre oder in einem Scheduler-Hook.
3. Handler `0xF2`: Interruptkontext, keine Sperre, keine Allokation, kein Protokoll; schreibt nur CR3 und die eigene Epoche.
4. `SwitchFor`/`SwitchTo` (unter Queue-Sperre): Maskenbit der neuen Tabelle setzen vor dem CR3-Schreiben, Bit der alten Tabelle löschen und Epoche erhöhen
   **nach** dem CR3-Schreiben; T → T schreibt nichts und ändert nichts; kein Warten mehr (entfällt mit `Changing`).
5. Statischer Zustand: keiner neu in ZehOS; in Cosmos nur der Vektor. `docs/SMP_AUDIT.md` bekommt eine Zeile für `UserPageTable._active` (Maske) und die Epoche.

## 5. Risiken

| Risiko | Wirkung | Gegenmittel |
| --- | --- | --- |
| Frame frei vor der Bestätigung | Ring 3 schreibt in Kernel- oder fremden Speicher | `Unmap` kehrt erst nach allen Epochen zurück; Test `tlb_munmap` mit Gegenprobe |
| Ziel-CPU lange mit maskierten Interrupts (lange Spinlocks, Plattenwarten im IRQ-Pfad) | `munmap` wartet, Bildzeit leidet | Messung in S5; nach 100 ms IPI erneut, Panik mit CPU-Nummer erst nach `zehos.tlbtimeout` (Vorgabe 5 s) statt stillem Hänger |
| vCPU-Entzug im Gast (QEMU unter Last, VMware) | Abschuss wartet ohne Kernelfehler lange | Grenze großzügig und einstellbar (Schritt 4); in S5 die längste Wartezeit je Lauf ausgeben |
| Auslöser oder `Destroy` mit IF=0 (Ausnahme-, Absturzpfad) | zwei Wartende beantworten ihre IPIs nie: Deadlock | Aufrufer-Audit vor S1 (Abschnitt S1, „Kontext der Aufrufer“), Panik bei IF=0 im Patch |
| Abschüsse häufig (Allokator mit `madvise`/`munmap`, Ladybird-GC) | IPI-Last auf allen CPUs des Laufs | Bereichsvarianten; Zahl der Abschüsse je Lauf zählen und in `/proc`-Diagnose oder Laufprotokoll ausgeben |
| Monitor im Gast über CPUs fehlerhaft | Hänger oder Datenrennen in allen Prozesssperren | S0 vor allem anderen |
| Schalter bleibt dauerhaft aus | kein Fortschritt zum Tor | Vorgabe 1 als Abschluss von S5, Gegenprobe smp1 bleibt im Plan |
| Threads eines Laufs verdrängen Desktop (CPU 0 bleibt frei, Worker geteilt) | Desktop ruckelt | Messung Bildzeit mit laufendem Ladybird smp4; notfalls Laufmenge ohne die CPU der Hauptschleife |

## 6. Offen

- **Offenes Paket: Signalzustellung bei IRQ-Rückkehr.** Ein Signal mit Handler erreicht einen Thread, der im Ring 3 rechnet und keinen Aufruf macht, erst
  bei seinem nächsten Aufruf (Grenze in `docs/LINUX_ABI.md`, V18). Linux stellt es beim nächsten Interrupt zu. Nötig wäre ein Zustellpunkt in Cosmos
  (Haken vor dem `iretq` in den Ring 3, etwa in `UserMode`/Prepare, eigener Patch) und danach `Kick` nach `DeliverSignal`. Betrifft Programme, die sich
  auf `SIGALRM`/`SIGPROF` oder `pthread_kill` mit Handler in Rechenschleifen verlassen; SIGKILL und fatale Standardsignale sind nicht betroffen.
- `SchedulerThread.CpuId` (Cosmos `SchedulerThread.cs:48`) und `IsOnCpu` (`:115`) gibt es; ob `CpuId` während des Laufs ohne Sperre stimmt (Umzug zwischen Lesen und IPI ist harmlos: ein überflüssiges Reschedule), in S3 am Code der Policy belegen.
- Ob das ZehOS-Platzierungs- und Ausgleichsverfahren (`Scheduling/`) Threads eines Laufs über die Menge verteilt oder auf der ersten CPU stapelt; in S2 im Gast
  mit `getcpu` messen, sonst Platzierung für neue Kindthreads anpassen.
- VMware und echte Hardware: keine Aussage; der Abschuss hängt am xAPIC (MMIO). x2APIC-Maschinen nur, soweit Cosmos sie im xAPIC-Modus fährt.
