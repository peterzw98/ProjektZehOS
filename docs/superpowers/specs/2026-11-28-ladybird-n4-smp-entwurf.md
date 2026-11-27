# Entwurf N4-SMP: Threads und Prozesse eines Linux-Laufs auf mehreren CPUs

> Entwurf zu T5 im [Plan nach dem Vorzeigestand](../plans/2026-11-28-ladybird-n2z-tor.md); gehört zur Bahn „Threads“ in [N4](../plans/2026-11-24-ladybird-n4-posix.md)
> („danach mehrere CPUs je Prozess mit TLB-Abgleich per IPI“) und zum Tor N4-Z aus dem [Gesamtplan](../plans/2026-11-24-ladybird-nativ.md) („ein `pthread`-Programm nutzt zwei CPUs“).
> Stand der Befunde: ZehOS `origin/story/ladybird` 4519757a, Cosmos `f2b64df6` (`deploy/cosmos/COMMIT`). Nur gelesen: kein Bau, kein Gastlauf, keine Hosttests.

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
    // (4 × ulong wie CpuSet), Interlocked.Or vor dem CR3-Schreiben, Interlocked.And nach dem nächsten.
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
    internal ulong _tlbEpoch;   // steigt bei jedem CR3-Schreiben (SwitchTo) und in jedem Abschuss-Handler, Interlocked
}

public interface IInterruptController   // X64InterruptController
{
    void SendTlbFlush(ulong hardwareId);  // Vektor 0xF2
}
```

Ablauf `UnmapRange`:

1. Unter `_lock` (`AcquireIrqSafe`, wie heute): Einträge entfernen, Frames sammeln, auf der eigenen CPU `invlpg` je Seite.
2. Volle Barriere (`Interlocked`), dann `_active` lesen. Eine CPU, die danach einsteigt, setzt ihr Bit vor ihrem CR3-Schreiben (Interlocked) und lädt CR3 erst
   danach; ihre Seitenwanderung sieht die neuen Einträge. Eine, die vorher einstieg, steht in der Maske. Das ersetzt `Changing`.
3. `_lock` freigeben. Für jede andere CPU in der Maske: Epoche lesen, IPI senden (Interrupts kurz maskiert um die zwei ICR-Schreiben, wie `SendIpi` verlangt).
4. Mit freien Interrupts warten, bis jede gemerkte Epoche weitergezählt hat (`Thread.SpinWait`; nach 100 ms Panik mit CPU-Nummer: ein Abschuss ohne Antwort ist
   ein Kernelfehler, ein freigegebener Frame wäre Datenverlust).
5. Rückkehr; erst jetzt darf der Aufrufer die Frames freigeben.

Handler `0xF2` (Interruptkontext): Ist auf dieser CPU eine Benutzertabelle geladen, CR3 mit derselben Wurzel neu schreiben; `_tlbEpoch` erhöhen; EOI.
Keine Allokation, kein Protokoll, keine Sperre. Ohne geladene Tabelle nur die Epoche.

**Regeln:** Gewartet wird nie unter einem SpinLock oder `CosmosIrqLock` und nie mit maskierten Interrupts: Zwei Auslöser, die mit maskierten Interrupts
aufeinander warten, hielten sich gegenseitig fest. `_lock` der Tabelle bleibt die innerste Sperre (darunter nur der `PageAllocator`). `TryMap` braucht keinen
Abschuss (x86 speichert keine nicht vorhandenen Einträge); der dann mögliche unechte Seitenfehler ist schon abgedeckt (`IUserPagesQuery.MapsTo`, V38).
`TryProtect` nur mit Abschuss, wenn Rechte wegfallen (RW → R, X → NX); Rechte hinzufügen braucht keinen (unechter Fehler, wie bei `TryMap`).

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

### S3 Signale und Abbruch über CPUs

- Heute wirkt `RequestLeave` und jede Signalzustellung an einen Thread in Ring 3 erst am nächsten Timertakt seiner CPU. Auf einer CPU war das der Wechsel
  ohnehin; auf mehreren läuft ein Thread nach exit_group oder nach dem Absturz eines Geschwisters bis zu einem Takt weiter. Für die Richtigkeit reicht das
  (Speicher bleibt bis zum Lösen aller Threads), für die Latenz nicht.
- Cosmos-Patch `user-leave-kick`: `UserMode.RequestLeave` und eine neue `UserMode.Kick(SchedulerThread)` senden `0xF0` (Reschedule) an die CPU, auf der der
  Thread gerade läuft (`SchedulerThread` kennt seine CPU), sonst nichts. ZehOS ruft `Kick` nach `DeliverSignal` an einen laufenden Thread.
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
4. `SwitchFor`/`SwitchTo` (unter Queue-Sperre): Maskenbit setzen vor, löschen nach dem CR3-Schreiben, Epoche erhöhen; kein Warten mehr (entfällt mit `Changing`).
5. Statischer Zustand: keiner neu in ZehOS; in Cosmos nur der Vektor. `docs/SMP_AUDIT.md` bekommt eine Zeile für `UserPageTable._active` (Maske) und die Epoche.

## 5. Risiken

| Risiko | Wirkung | Gegenmittel |
| --- | --- | --- |
| Frame frei vor der Bestätigung | Ring 3 schreibt in Kernel- oder fremden Speicher | `Unmap` kehrt erst nach allen Epochen zurück; Test `tlb_munmap` mit Gegenprobe |
| Ziel-CPU lange mit maskierten Interrupts (lange Spinlocks, Plattenwarten im IRQ-Pfad) | `munmap` wartet, Bildzeit leidet | Messung in S5; Grenze 100 ms → Panik mit CPU-Nummer statt stillem Hänger |
| Abschüsse häufig (Allokator mit `madvise`/`munmap`, Ladybird-GC) | IPI-Last auf allen CPUs des Laufs | Bereichsvarianten; Zahl der Abschüsse je Lauf zählen und in `/proc`-Diagnose oder Laufprotokoll ausgeben |
| Monitor im Gast über CPUs fehlerhaft | Hänger oder Datenrennen in allen Prozesssperren | S0 vor allem anderen |
| Schalter bleibt dauerhaft aus | kein Fortschritt zum Tor | Vorgabe 1 als Abschluss von S5, Gegenprobe smp1 bleibt im Plan |
| Threads eines Laufs verdrängen Desktop (CPU 0 bleibt frei, Worker geteilt) | Desktop ruckelt | Messung Bildzeit mit laufendem Ladybird smp4; notfalls Laufmenge ohne die CPU der Hauptschleife |

## 6. Offen

- `SchedulerThread.CpuId` (Cosmos `SchedulerThread.cs:48`) und `IsOnCpu` (`:115`) gibt es; ob `CpuId` während des Laufs ohne Sperre stimmt (Umzug zwischen Lesen und IPI ist harmlos: ein überflüssiges Reschedule), in S3 am Code der Policy belegen.
- Ob das ZehOS-Platzierungs- und Ausgleichsverfahren (`Scheduling/`) Threads eines Laufs über die Menge verteilt oder auf der ersten CPU stapelt; in S2 im Gast
  mit `getcpu` messen, sonst Platzierung für neue Kindthreads anpassen.
- VMware und echte Hardware: keine Aussage; der Abschuss hängt am xAPIC (MMIO). x2APIC-Maschinen nur, soweit Cosmos sie im xAPIC-Modus fährt.
