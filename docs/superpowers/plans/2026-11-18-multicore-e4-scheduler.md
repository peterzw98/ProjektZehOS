# Etappe 4: Klassenscheduler und Topologie auf einer CPU

> **Für ausführende Agenten:** Zuerst die [Übersicht](2026-11-18-multicore.md) lesen. Empfohlene Arbeitsweise:
> superpowers:subagent-driven-development oder superpowers:executing-plans. Schritte haben Kästchen zum Abhaken.

**Ziel:** Der Round-Robin-Scheduler ist durch den Klassenscheduler ersetzt, die Topologie-Erkennung und die
Platzierungsregeln liegen hostgetestet bereit, und die Eingabepuffer sind mit einer echten Sperre geschützt. Alles
noch auf Cosmos 3.0.97 und einer CPU.

**Architektur:** Die Policy steckt in Klassen ohne Cosmos-Typen (`ClassRunQueues<T>`, `ClassPlacement`,
`CpuTopology`), die das Hosttestprojekt direkt einbindet. `CosmosClassScheduler` ist der dünne Adapter auf
`IScheduler` und die einzige Datei mit Cosmos-Signaturen. Etappe 5 tauscht nur diesen Adapter.

**Technik:** C#, MSTest. `ProjektZeh.Tests.csproj:44` bindet `ProjektZeh/Scheduling/*.cs` ein und schließt
`Cosmos*.cs` aus; neue plattformunabhängige Dateien dürfen deshalb nicht mit `Cosmos` beginnen, Adapter müssen es.

**Entwurf:** [Abschnitte 5.3, 5.4 und 6](../specs/2026-11-18-multicore-design.md).

## Grenze dieser Etappe

Auf 3.0.97 ist die Hauptschleife der Idle-Thread von Cosmos und blockiert nie (`Thread.Sleep` wartet dort per Halt).
Sie läuft in dieser Etappe deshalb in der Klasse Normal mit der bisherigen Idle-Regel (`AlwaysYields`). Das Verhalten
von Hauptschleife und Wasm-Worker ist damit dasselbe wie unter Round-Robin; neu wirkt nur die Klasse Hintergrund. Der
Vorrang der Oberfläche kommt in Etappe 5.

## Pakete

| Paket | Inhalt | Rolle | Nach | Neben |
| --- | --- | --- | --- | --- |
| E4-A | `CpuTopology`, `CpuTopologyDecoder` | Engineer | keine | B, D |
| E4-B | `ThreadClass`, `ClassThreadState`, `ClassRunQueues<T>`, `ClassPlacement` | Engineer senior | keine | A, D |
| E4-C | Adapter, Kernel-Verdrahtung, Klassen beim Threadstart, Round-Robin entfernen | Engineer senior | B | D |
| E4-D | `CosmosIrqLock`, Eingabepuffer umstellen | Engineer senior | keine | A, B, C |
| E4-E | Messung und Abnahme, Doku | Clerk und Engineer | C, D | keine |

E4-B verwendet aus E4-A nur die unten genannte Schnittstelle von `CpuTopology`. Wer E4-B vor dem Merge von E4-A
beginnt, legt `CpuTopology.cs` mit genau diesen Signaturen und `throw new NotImplementedException()` an und übernimmt
beim Rebase die Fassung von E4-A.

## Review-Schwerpunkte

Fälle, die der Entwurf nahelegt und die leicht übersehen werden. Jeder hat unten einen Test im genannten Paket.

1. Ein Thread wird geweckt, während er noch auf der CPU ist, und steht dann zugleich als laufend und in der Queue (E4-B, Test `Tick_CurrentAlsoQueued_...`).
2. Die Queue einer Klasse ist voll: `Ready` meldet das, statt einen Thread zu verlieren (E4-B, `Ready_FullClass_...`).
3. CPUID meldet Blatt `0x0B`, aber mit `EBX = 0` (ältere virtuelle CPUs): Rückfall statt falscher Topologie (E4-A, `ReadShifts_LeafBWithoutData_...`).
4. APIC-IDs mit Lücken (`0,1,4,5`): Kerne werden trotzdem dicht nummeriert (E4-A, `FromApicIds_SparseIds_...`).
5. Ein Thread wird unter Quarantäne gestellt, während er in einer Queue steht: Er wird nie wieder gewählt (E4-C, Gastprobe `test-isolation-probe.py`).

---

### Paket E4-A: Topologie

**Branch:** `task/mc-e4-a-topologie`

**Dateien:**
- Anlegen: `ProjektZeh/Scheduling/CpuTopology.cs`
- Anlegen: `ProjektZeh/Scheduling/CpuTopologyDecoder.cs`
- Test: `ProjektZeh.Tests/CpuTopologyTests.cs`

**Schnittstellen (liefert):**

```csharp
namespace ProjektZeh.Scheduling;

/// Unveränderliche Zuordnung CPU-Index -> Kern und Verbund. Kerne und Verbünde sind dicht ab 0 nummeriert,
/// in der Reihenfolge ihres ersten Auftretens über aufsteigende CPU-Indizes.
internal sealed class CpuTopology
{
    public static CpuTopology Flat(int cpuCount);                       // jede CPU eigener Kern, ein Verbund
    public static CpuTopology FromApicIds(ReadOnlySpan<uint> apicIdByCpu, int smtShift, int packageShift);
    public int CpuCount { get; }
    public int CoreCount { get; }
    public int CoreOf(int cpu);
    public int PackageOf(int cpu);
    public ReadOnlySpan<int> SiblingsOf(int cpu);                       // andere CPUs desselben Kerns, aufsteigend
}

internal static class CpuTopologyDecoder
{
    public const int SinglePackage = 32;                                // packageShift >= 32: alle CPUs ein Verbund
    /// cpuId(leaf, subleaf) wie in Security/HardwareRandom.cs:20.
    public static (int SmtShift, int PackageShift) ReadShifts(Func<int, int, (int Eax, int Ebx, int Ecx, int Edx)> cpuId);
}
```

Regeln für `FromApicIds`: Kern = `apicId >> smtShift`, Verbund = `packageShift >= 32 ? 0 : apicId >> packageShift`;
gleiche Werte bilden einen Kern beziehungsweise Verbund. `Flat(n)` entspricht `FromApicIds([0..n-1], 0, SinglePackage)`.
Ungültige Argumente (`cpuCount < 1`, negative Verschiebung, `smtShift > packageShift`) werfen `ArgumentOutOfRangeException`.

Regeln für `ReadShifts`, in dieser Reihenfolge, die erste zutreffende gilt:

1. `maxLeaf = cpuId(0, 0).Eax`. Für `leaf` in `0x1F`, dann `0x0B`: wenn `maxLeaf >= leaf` und `(cpuId(leaf, 0).Ebx & 0xFFFF) != 0`: Unterblätter `0, 1, 2, …` lesen, bis der Ebenentyp `(Ecx >> 8) & 0xFF` null ist (höchstens 8). Je Ebene `shift = Eax & 0x1F`. `SmtShift` ist die Verschiebung der Ebene mit Typ 1 (sonst 0), `PackageShift` die der letzten gelesenen Ebene.
2. Hersteller `AuthenticAMD` (Blatt 0: `Ebx = 0x68747541`, `Edx = 0x69746E65`, `Ecx = 0x444D4163`) und `cpuId(unchecked((int)0x80000000), 0).Eax >= 0x8000001E` (als vorzeichenlose Zahl verglichen): `threadsPerCore = ((cpuId(0x8000001E).Ebx >> 8) & 0xFF) + 1`, `SmtShift = CeilLog2(threadsPerCore)`. `idSize = (cpuId(0x80000008).Ecx >> 12) & 0xF`; `PackageShift = idSize != 0 ? idSize : CeilLog2((cpuId(0x80000008).Ecx & 0xFF) + 1)`.
3. `maxLeaf >= 4` und Bit 28 von `cpuId(1, 0).Edx` gesetzt: `logical = (cpuId(1, 0).Ebx >> 16) & 0xFF`, `cores = ((cpuId(4, 0).Eax >> 26) & 0x3F) + 1`, `PackageShift = CeilLog2(logical)`, `SmtShift = max(0, PackageShift - CeilLog2(cores))`.
4. Sonst `(0, SinglePackage)`.

`CeilLog2(1) = 0`, `CeilLog2(2) = 1`, `CeilLog2(3) = 2`, `CeilLog2(8) = 3`.

- [ ] **Schritt 1: Tests schreiben** (`CpuTopologyTests.cs`, MSTest, Klasse `CpuTopologyTests`). Die CPUID-Tabellen werden als Wörterbuch `(leaf, subleaf) -> (Eax, Ebx, Ecx, Edx)` gebaut; nicht eingetragene Blätter liefern vier Nullen.

| Test | Gegeben | Erwartet |
| --- | --- | --- |
| `Flat_FourCpus_EachItsOwnCore` | `Flat(4)` | `CoreCount == 4`; `SiblingsOf(2).Length == 0`; `PackageOf(3) == 0` |
| `FromApicIds_TwoCoresTwoThreads_PairsSiblings` | IDs `0,1,2,3`, `smtShift 1`, `packageShift 2` | `CoreOf` = `0,0,1,1`; `SiblingsOf(0)` = `[1]`; `SiblingsOf(3)` = `[2]`; `CoreCount == 2` |
| `FromApicIds_SparseIds_NumbersCoresDensely` | IDs `0,1,4,5`, `smtShift 1`, `packageShift 4` | `CoreOf` = `0,0,1,1`; `PackageOf` alle `0` |
| `FromApicIds_TwoPackages_SeparatesThem` | IDs `0,1,16,17`, `smtShift 1`, `packageShift 4` | `PackageOf` = `0,0,1,1`; `CoreOf` = `0,0,1,1` |
| `FromApicIds_InvalidShift_Throws` | `smtShift 3`, `packageShift 2` | `ArgumentOutOfRangeException` |
| `ReadShifts_LeafB_FourCoresEightThreads` | Blatt 0: `Eax 0x16`; `(0x0B,0)`: `Eax 1, Ebx 2, Ecx 0x100`; `(0x0B,1)`: `Eax 4, Ebx 8, Ecx 0x201` | `(1, 4)` |
| `ReadShifts_LeafB_QemuTwoCoresTwoThreads` | Blatt 0: `Eax 0x0D`; `(0x0B,0)`: `Eax 1, Ebx 2, Ecx 0x100`; `(0x0B,1)`: `Eax 2, Ebx 4, Ecx 0x201` | `(1, 2)` |
| `ReadShifts_LeafB_NoHyperthreading` | Blatt 0: `Eax 0x0D`; `(0x0B,0)`: `Eax 0, Ebx 1, Ecx 0x100`; `(0x0B,1)`: `Eax 2, Ebx 4, Ecx 0x201` | `(0, 2)` |
| `ReadShifts_Leaf1F_PreferredOverLeafB` | Blatt 0: `Eax 0x20`; `(0x1F,0)`: `Eax 1, Ebx 2, Ecx 0x100`; `(0x1F,1)`: `Eax 6, Ebx 24, Ecx 0x201`; `(0x0B,0)`: `Eax 1, Ebx 2, Ecx 0x100`; `(0x0B,1)`: `Eax 5, Ebx 16, Ecx 0x201` | `(1, 6)` |
| `ReadShifts_LeafBWithoutData_FallsBackToLegacy` | Blatt 0: `Eax 0x0D`; `(0x0B,0)`: alles 0; `(1,0)`: `Ebx 0x00040000, Edx 0x10000000`; `(4,0)`: `Eax 0x04000000` | `(1, 2)` (4 logische, 2 Kerne) |
| `ReadShifts_Amd_UsesExtendedLeaves` | Blatt 0: `Eax 0x0A, Ebx 0x68747541, Edx 0x69746E65, Ecx 0x444D4163`; `(0x80000000,0)`: `Eax 0x8000001E`; `(0x8000001E,0)`: `Ebx 0x100`; `(0x80000008,0)`: `Ecx 0x4000` | `(1, 4)` |
| `ReadShifts_NothingUsable_SinglePackageFlat` | Blatt 0: `Eax 1`; `(1,0)`: `Edx 0` | `(0, CpuTopologyDecoder.SinglePackage)` |

- [ ] **Schritt 2: Tests laufen lassen, sie schlagen fehl.** `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror --filter "FullyQualifiedName~CpuTopologyTests"`. Erwartet: Übersetzungsfehler, weil die Typen fehlen.
- [ ] **Schritt 3: `CpuTopology` und `CpuTopologyDecoder` nach den Regeln oben umsetzen.** `CpuTopology` hält drei `int[]` (Kern, Verbund) und je CPU ein `int[]` der Geschwister; nichts wird nach dem Konstruktor verändert.
- [ ] **Schritt 4: Tests bestehen**, derselbe Befehl. Danach die ganze Suite: `dotnet test ProjektZeh.Tests/ProjektZeh.Tests.csproj --configuration Release -warnaserror`.
- [ ] **Schritt 5: Commit** (`feat(scheduling): CPU-Topologie aus APIC-IDs und CPUID`), Review, PR.

---

### Paket E4-B: Klassen, Queues, Platzierung

**Branch:** `task/mc-e4-b-klassen`

**Dateien:**
- Anlegen: `ProjektZeh/Scheduling/ThreadClass.cs` (Enum und `ClassThreadState`)
- Anlegen: `ProjektZeh/Scheduling/ClassRunQueues.cs`
- Anlegen: `ProjektZeh/Scheduling/ClassPlacement.cs`
- Test: `ProjektZeh.Tests/ClassRunQueuesTests.cs`, `ProjektZeh.Tests/ClassPlacementTests.cs`

**Schnittstellen (verwendet):** `CpuTopology` aus E4-A (`CpuCount`, `SiblingsOf`, `PackageOf`).

**Schnittstellen (liefert):**

```csharp
namespace ProjektZeh.Scheduling;

/// Kleinere Zahl = höherer Vorrang.
internal enum ThreadClass : byte { Interactive = 0, Normal = 1, Background = 2 }

/// Zustand eines Threads für die Policy; der Adapter legt ihn in SchedulerThread.SchedulerData.
internal sealed class ClassThreadState
{
    public ThreadClass Class = ThreadClass.Normal;
    public ulong RemainingNs;      // Rest der laufenden Zeitscheibe
    public int LastCpu = -1;       // CPU der letzten Auswahl, -1 = noch nie
    public bool AlwaysYields;      // nur bis Etappe 5: Idle-Thread von Cosmos 3.0.97 (Hauptschleife)
}

/// Die Run-Queues EINER CPU. Feste Größe, keine Allokation nach dem Konstruktor.
internal sealed class ClassRunQueues<T>(int capacityPerClass, Func<T, ClassThreadState?> stateOf) where T : class
{
    public const ulong TickNs = 10_000_000;
    public const int StarvationLimit = 8;
    public static ulong SliceNs(ThreadClass threadClass);   // 1, 2, 4 Ticks für Interactive, Normal, Background

    public int Count { get; }                               // wartende Threads aller Klassen
    public int Waiting { get; }                             // wie Count, aber ohne Sperre lesbar (Hinweis)
    public int CountOf(ThreadClass threadClass);
    public T? At(int index);                                // Diagnose: Klassen aufsteigend, je Klasse Queue-Reihenfolge
    public bool Contains(T thread);
    public bool Ready(T thread);                            // ans Ende seiner Klasse; false, wenn die Klasse voll ist
    public void Blocked(T thread);                          // aus der Queue, falls enthalten
    public void PickFailed(T thread);                       // zurück an den Anfang seiner Klasse
    public void Yielded(T thread);                          // nach Verdrängung; vorn oder hinten, siehe Regeln
    public T? Pick(T? running);                             // liefert running zurück = weiterlaufen lassen
    public bool Tick(T current, ulong elapsedNs);           // true = neu auswählen
    public void SetClass(T thread, ThreadClass threadClass);// zieht einen wartenden Thread in die neue Queue um
    public T? PeekStealable(Func<T, bool> mayMove);         // vorderster verschiebbarer Thread der höchsten Klasse
}

internal static class ClassPlacement
{
    /// running[cpu]: Klasse des dort laufenden Threads, null = CPU frei. queued[cpu]: wartende Threads dort.
    public static int SelectCpu(CpuTopology topology, ThreadClass threadClass, int lastCpu,
        ReadOnlySpan<bool> allowed, ReadOnlySpan<ThreadClass?> running, ReadOnlySpan<int> queued);
    /// Füllt order mit allen anderen CPUs: erst Geschwister, dann derselbe Verbund, dann der Rest, je aufsteigend.
    public static int StealOrder(CpuTopology topology, int cpu, Span<int> order);
}
```

**Regeln `ClassRunQueues<T>`** (ein Thread ohne `ClassThreadState` gilt als Normal ohne Zeitscheibe):

- `Ready`: schon enthalten → `true` ohne Änderung. Klasse voll → `false`. `RemainingNs` bleibt unberührt: Ein Thread,
  der sich schlafen legt und geweckt wird, ohne die CPU zu verlassen, bekommt dadurch keine neue Zeitscheibe (das war
  die Ursache der Bildzeit-Ausreißer aus #254).
- `Pick(running)`:
  1. Ausgehungerte Klasse bestimmen: die Klasse mit dem höchsten Vorrang unter Normal und Hintergrund, die nicht leer ist und deren Zähler `StarvationLimit` erreicht hat.
  2. Zu bedienende Klasse `c`: die ausgehungerte, sonst die nicht leere Klasse mit dem höchsten Vorrang. Gibt es keine: `null`.
  3. Weiterlaufen: Ist `running` nicht `null`, nicht `AlwaysYields`, gibt es keine ausgehungerte Klasse, hat `c` niedrigeren Vorrang als `running` und ist `running.RemainingNs > 0`, dann `running` zurückgeben, ohne etwas zu ändern.
  4. Sonst den Kopf von `c` entnehmen, den Zähler von `c` auf 0 setzen, den Zähler jeder anderen nicht leeren Klasse mit niedrigerem Vorrang als `c` um 1 erhöhen, bei `RemainingNs == 0` auf `SliceNs(Klasse)` setzen und ihn zurückgeben. Dabei merken, ob `running` vorn wieder einzureihen ist: genau dann, wenn `running` nicht `null` ist, `c` höheren Vorrang als `running` hat und `running.RemainingNs > 0`.
- `Yielded(thread)`: Hat das letzte `Pick` für genau diesen Thread „vorn einreihen“ gemerkt, kommt er an den Anfang seiner Klasse und behält `RemainingNs`. Sonst kommt er ans Ende, und `RemainingNs` wird 0 (neue Scheibe bei der nächsten Auswahl). Der Merker gilt einmal.
- `Tick(current, elapsedNs)`: „Andere“ sind die wartenden Threads ohne `current` selbst, falls er auch in der Queue steht.
  1. `current.AlwaysYields`: Gibt es keine anderen → `false`. Sonst `current`, falls er in der Queue steht, ans Ende seiner Klasse setzen und `true`.
  2. `RemainingNs` um `elapsedNs` verringern, nicht unter 0. Ist danach `RemainingNs > 0` → `false`.
  3. Wartet ein anderer mit gleichem oder höherem Vorrang → `current`, falls in der Queue, ans Ende; `true`.
  4. Sonst für jede nicht leere Klasse mit niedrigerem Vorrang den Zähler um 1 erhöhen. Erreicht einer `StarvationLimit` → `true`. Sonst `RemainingNs = SliceNs(Klasse)` und `false`.
- `Waiting`: ein Feld, gelesen mit `Volatile.Read`, geschrieben mit `Volatile.Write(ref waiting, Count)` am Ende jeder
  Methode, die eine Queue ändert (`Ready`, `Blocked`, `PickFailed`, `Yielded`, `Pick`, `Tick`, `SetClass`). Die
  Hauptschleife liest den Wert ohne Sperre, um vor dem Schlafen abzugeben, wenn jemand bereit ist; fehlt er, schläft
  sie wieder bis zum nächsten Tick (Befund aus #254). Vorlage: `RoundRobinCore.Waiting` im Branch
  `task/main-loop-yield-on-ready` (`0e10ff7`). Der Wert ist nur ein Hinweis; keine Scheduler-Entscheidung hängt daran.
- `PeekStealable`: geht die Klassen nach Vorrang durch und liefert den ersten Thread, für den `mayMove` `true` gibt; entnimmt nichts.

**Regeln `ClassPlacement.SelectCpu`**, die erste zutreffende gilt; „frei“ heißt `running[cpu] == null` und `queued[cpu] == 0`; bei mehreren Treffern gewinnt der kleinste Index:

1. `lastCpu` ist erlaubt und frei, und kein Geschwister von `lastCpu` führt eine Klasse mit höherem Vorrang als `threadClass` aus → `lastCpu`.
2. Eine erlaubte freie CPU, deren Geschwister alle frei sind.
3. Eine erlaubte freie CPU, deren Geschwister keine Klasse mit höherem Vorrang als `threadClass` ausführen.
4. Eine erlaubte freie CPU.
5. Die erlaubte CPU mit der kleinsten Last `queued[cpu] + (running[cpu] != null ? 1 : 0)`.

Ist keine CPU erlaubt: `ArgumentException`.

- [ ] **Schritt 1: Tests für `ClassRunQueues<T>` schreiben** (`ClassRunQueuesTests.cs`). Hilfstyp wie in `SchedulingTests.cs:41`: `private sealed class FakeThread(string name) { public ClassThreadState State { get; } = new(); }`, Queue mit `new ClassRunQueues<FakeThread>(8, t => t.State)`.

| Test | Gegeben | Erwartet |
| --- | --- | --- |
| `Ready_SameThreadTwice_QueuedOnce` | `Ready(a)` zweimal | `Count == 1` |
| `Ready_FullClass_ReturnsFalseAndKeepsOrder` | Kapazität 2, drei Normal-Threads | drittes `Ready` ist `false`; `Pick(null)` liefert `a`, dann `b` |
| `Pick_PrefersHigherClass` | wartend: Normal `n`, Hintergrund `b`, Interaktiv `i` | `Pick(null)` liefert `i`, dann `n`, dann `b` |
| `Pick_SetsSliceForClass` | Interaktiv `i`, Hintergrund `b` | nach `Pick`: `i.RemainingNs == 10_000_000`, später `b.RemainingNs == 40_000_000` |
| `Pick_LowerClassWaiting_KeepsRunning` | `n` läuft mit Rest 5 ms, `b` wartet | `Pick(n)` ist `n`; `Count == 1` |
| `Pick_SameClassWaiting_Switches` | `n1` läuft mit Rest 5 ms, `n2` wartet | `Pick(n1)` ist `n2` |
| `Yielded_PreemptedByHigherClass_ReturnsToFrontWithRemainder` | `n1` läuft mit Rest 5 ms, `n2` wartet, `i` wird bereit | `Pick(n1)` ist `i`; nach `Yielded(n1)` liefert `Pick(null)` den Thread `n1` mit `RemainingNs == 5_000_000`, erst danach `n2` |
| `Yielded_AfterSliceEnd_GoesToBackWithFreshSlice` | `n1` verbraucht 20 ms, `n2` und `n3` warten | `Tick` ist `true`; `Pick(n1)` ist `n2`; nach `Yielded(n1)`: `At(0)` ist `n3`, `At(1)` ist `n1`, `n1.RemainingNs == 0` |
| `Pick_ThreadWokenWhileRunning_DoesNotRefillSlice` | `n1` hat 10 ms verbraucht, steht durch `Ready(n1)` in der Queue; `Pick(null)` liefert `n1` | `n1.RemainingNs == 10_000_000` (nicht 20 ms); nach einem weiteren Tick mit wartendem `n2`: `Tick` ist `true` |
| `Tick_SliceNotExpired_ReturnsFalse` | Normal, ein Tick von 10 ms | `false`, Rest 10 ms |
| `Tick_AloneAfterSlice_RefillsAndKeepsCpu` | Normal allein, zwei Ticks | zweites `Tick` ist `false`, Rest 20 ms |
| `Tick_OnlyLowerClassWaiting_KeepsCpuUntilStarvationLimit` | `n` rechnet dauernd, `b` wartet | die ersten 7 abgelaufenen Scheiben: `Tick` `false`; die 8.: `true`; `Pick(n)` ist `b`; danach beginnt die Zählung neu |
| `Starvation_NormalBeforeBackground` | `i` rechnet dauernd, `n` und `b` warten | nach 8 Scheiben von `i` läuft `n`, nach dessen Scheibe `b`, danach wieder `i` |
| `Tick_CurrentAlsoQueued_NotCountedAsWaiterAndMovedToBack` | `n1` läuft und steht in der Queue, `n2` wartet, Scheibe abgelaufen | `Tick` `true`; `At(0)` ist `n2`, `At(1)` ist `n1` |
| `AlwaysYields_PreemptedAtFirstTickWhenAnyoneWaits` | Thread mit `AlwaysYields`, `n` wartet | `Tick` nach 10 ms ist `true`; ohne Wartende `false` |
| `AlwaysYields_NeverKeptByPick` | Thread mit `AlwaysYields` und Rest 20 ms läuft, `b` wartet | `Pick(running)` ist `b` |
| `MainLoopAndBusyWorker_SameShareAsRoundRobin` | Hauptschleife (`AlwaysYields`, Normal) und rechnender Worker, 300 Ticks nach dem Muster von `SchedulingTests.cs:110` | Hauptschleife bekommt 100 Ticks |
| `SetClass_QueuedThread_MovesBetweenQueues` | `n` wartet, `SetClass(n, Background)` | `CountOf(Normal) == 0`, `CountOf(Background) == 1` |
| `Blocked_RemovesFromAnyClass` | `i`, `n`, `b` warten; `Blocked(n)` | `Count == 2`, `Pick` liefert `i`, dann `b` |
| `PickFailed_ReturnsToFront` | `n1`, `n2` warten; `Pick` liefert `n1`; `PickFailed(n1)` | nächstes `Pick` liefert `n1` |
| `PeekStealable_SkipsThreadsThatMayNotMove` | `i` (nicht verschiebbar), `n` | liefert `n`, `Count` bleibt 2 |
| `Waiting_PublishedAfterEveryQueueChange` | nacheinander `Ready(a)`, `Ready(b)`, `Pick(null)`, `Yielded(a)`, `Blocked(b)`, `SetClass(a, Background)`, `Pick(null)`, `PickFailed(a)`, `Tick` mit Umsetzen ans Ende | nach jedem Aufruf `Waiting == Count`; Werte der ersten fünf: 1, 2, 1, 2, 1 |

- [ ] **Schritt 2: Tests für `ClassPlacement` schreiben** (`ClassPlacementTests.cs`). Topologien: `T4x1 = CpuTopology.Flat(4)`, `T2x2 = FromApicIds([0,1,2,3], 1, 2)`, `T4x2 = FromApicIds([0..7], 1, 3)`.

| Test | Gegeben | Erwartet |
| --- | --- | --- |
| `SelectCpu_LastCpuFree_StaysForCacheWarmth` | `T4x1`, alles frei, `lastCpu 2` | `2` |
| `SelectCpu_PrefersWholeFreeCore` | `T2x2`, CPU 0 führt Normal aus, Rest frei, `lastCpu -1` | `2` (nicht das Geschwister 1) |
| `SelectCpu_AvoidsSiblingOfInteractive` | `T2x2`, CPU 0 Interaktiv, CPU 2 Normal, CPUs 1 und 3 frei, Klasse Normal | `3` |
| `SelectCpu_InteractiveMayShareWithLowerClass` | `T2x2`, CPU 0 Hintergrund, CPU 2 Hintergrund, 1 und 3 frei, Klasse Interaktiv | `1` |
| `SelectCpu_LastCpuNextToInteractive_Moves` | `T2x2`, `lastCpu 1` frei, CPU 0 Interaktiv, CPU 2 und 3 frei, Klasse Normal | `2` |
| `SelectCpu_NothingFree_LeastLoaded` | `T4x1`, alle laufen, `queued` = `2,0,1,3` | `1` |
| `SelectCpu_RespectsAllowed` | `T4x1`, alles frei, erlaubt nur CPU 3 | `3` |
| `SelectCpu_NoneAllowed_Throws` | `allowed` alle `false` | `ArgumentException` |
| `StealOrder_SiblingThenPackageThenRest` | `FromApicIds([0,1,2,3,16,17], 1, 4)`, CPU 0 | Rückgabe 5; `order` = `1,2,3,4,5` |
| `StealOrder_FromSecondThread` | `T4x2`, CPU 5 | `order` beginnt mit `4`, dann `0,1,2,3,6,7` |

- [ ] **Schritt 3: Tests laufen lassen, sie schlagen fehl** (Typen fehlen). Filter: `--filter "FullyQualifiedName~ClassRunQueuesTests|FullyQualifiedName~ClassPlacementTests"`.
- [ ] **Schritt 4: `ThreadClass.cs`, `ClassRunQueues.cs`, `ClassPlacement.cs` umsetzen.** `ClassRunQueues<T>` hält drei Felder `T?[capacityPerClass]` mit je einer Länge, wie `RoundRobinQueue<T>`; Einfügen und Entfernen verschieben wie dort. Zwei Zähler (Normal, Hintergrund) für die Aushungerungsregel.
- [ ] **Schritt 5: Tests bestehen**, danach die ganze Suite.
- [ ] **Schritt 6: Commit** (`feat(scheduling): Klassen-Queues und Platzierungsregeln`), Review, PR.

---

### Paket E4-C: Adapter und Umstellung

**Branch:** `task/mc-e4-c-adapter`. **Nach:** E4-B gemergt.

**Dateien:**
- Anlegen: `ProjektZeh/Scheduling/CosmosClassScheduler.cs`
- Ändern: `ProjektZeh/Scheduling/ThreadStarter.cs` (Parameter `threadClass`)
- Ändern: `ProjektZeh/Scheduling/BackgroundTaskService.cs:47` (Klasse Hintergrund)
- Ändern: `ProjektZeh/Kernel.cs:74` (Feld), `:385-386` (Verdrahtung), `:526-537` (`InstallScheduler`, `DescribeScheduler`)
- Ändern: `ProjektZeh/Shell/TaskShellCommands.cs:16` (Hilfetext „Scheduler-Diagnose“)
- Löschen: `ProjektZeh/Scheduling/CosmosRoundRobinScheduler.cs`, `RoundRobinCore.cs`, `RoundRobinQueue.cs`
- Ändern: `ProjektZeh.Tests/SchedulingTests.cs` (Tests `:13` bis `:201` entfallen; die Aufgaben-Tests ab `:203` bleiben)
- Test: `ProjektZeh.Tests/ThreadStarterTests.cs` (vorhandene Datei, sonst anlegen)
- Doku: `docs/COSMOS_SUPPORT.md` (neuer Abschnitt oben, der Abschnitt „Round-Robin: Idle-Thread und Zeitscheibe“ wird historisch)

**Schnittstellen (verwendet):** `ClassRunQueues<T>`, `ClassThreadState`, `ThreadClass` aus E4-B; `IScheduler`, `PerCpuState`, `SchedulerThread`, `SchedulerManager` aus Cosmos 3.0.97.

**Schnittstellen (liefert):**

```csharp
internal sealed class CosmosClassScheduler : IScheduler
{
    public string Name => "ZehOS Klassen";
    public (ulong Preemptions, uint[] ThreadIds, int[] WaitingByClass) Snapshot();   // WaitingByClass: 3 Einträge
    internal static int WaitingThreads { get; }   // ClassRunQueues.Waiting der CPU 0, vor InitializeCpu 0
}

internal static class ThreadStarter
{
    /// Vom Kernel gesetzt; ordnet dem AUFRUFENDEN Thread eine Klasse zu. Auf dem Host und vorher: nichts.
    internal static Action<ThreadClass> ApplyClass { get; set; } = _ => { };
    public static ResultObject<Thread> TryStart(ThreadStart body, int stackBytes = 0, bool background = false,
        ThreadClass threadClass = ThreadClass.Normal);
}
```

**Abbildung der Hooks** (Cosmos 3.0.97; `queues` ist `state.SchedulerData as ClassRunQueues<SchedulerThread>`):

| Hook | Umsetzung |
| --- | --- |
| `InitializeCpu` | `state.SchedulerData = new ClassRunQueues<SchedulerThread>(SchedulerThread.MaxThreadCount, t => t.SchedulerData as ClassThreadState)` |
| `ShutdownCpu` | `state.SchedulerData = null` |
| `OnThreadCreate` | `thread.SchedulerData = new ClassThreadState { AlwaysYields = thread.IsIdle }` |
| `OnThreadReady` | wie heute zuerst `if (Runtime.FaultIsolation.Quarantine.Contains(thread.Id)) return;`, dann `queues.Ready(thread)` |
| `OnThreadBlocked` | `queues.Blocked(thread)` |
| `OnThreadYield` | `queues.Yielded(thread)`, davor dieselbe Quarantäne-Prüfung |
| `OnThreadExit` | `queues.Blocked(thread)`; `thread.SchedulerData = null` |
| `PickNext` | `running = state.CurrentThread` nur, wenn dessen `State == SchedulerThreadState.Running`, sonst `null`; `queues.Pick(running)`; Auswahlspur wie heute (128 Einträge) |
| `OnPickFailed` | `queues.PickFailed(thread)` |
| `OnTick` | `current.TotalRuntime += elapsedNs`; `queues.Tick(current, elapsedNs)`; bei `true` Zähler `preemptions` erhöhen |
| `SelectCpu` | `currentCpu` |
| `OnThreadMigrate` | `Blocked` auf der Quelle, `Ready` auf dem Ziel |
| `Balance` | leer |
| `SetPriority` | `priority` 0, 1, 2 → `queues.SetClass(thread, (ThreadClass)priority)`; andere Werte ignorieren |
| `GetPriority` | Klasse als Zahl, ohne Zustand `1` |
| `GetRunQueueCount`, `GetRunQueueThread` | unter `SchedulerManager.MaskInterrupts()` wie heute: `queues.Count`, `queues.At(index)` |

Hinweis zum Mechanismus von 3.0.97 (`SchedulerManager.cs:1213-1280`): `PickNext` wird aufgerufen, bevor der bisherige
Thread zurückgegeben wird. Liefert es den laufenden Thread, wechselt Cosmos nicht. Liefert es `null`, läuft der
Idle-Thread. Ein Thread, der sich schlafen gelegt hat und noch vor dem Wechsel geweckt wird, steht in der Queue,
obwohl er noch `CurrentThread` ist; dann ist sein `State` nicht `Running`, und `Pick` bekommt `null`.

**`ThreadStarter`:** Ist `threadClass != ThreadClass.Normal`, wird `body` in `() => { ApplyClass(threadClass); body(); }`
verpackt. `Kernel.cs` setzt vor `InstallScheduler()`:

```csharp
ThreadStarter.ApplyClass = threadClass =>
{
    var state = SchedulerManager.GetCpuState(0);
    if (state?.CurrentThread is { } current) SchedulerManager.SetPriority(0, current, (long)threadClass);
};
```

`BackgroundTaskService` startet seine Worker mit `ThreadClass.Background`. Alle anderen Startstellen bleiben Normal.

**Waiting-Hinweis:** `WaitingThreads` liest die Queues der CPU 0 (statisches Feld, in `InitializeCpu` für CPU 0
gesetzt, in `ShutdownCpu` gelöscht), weil die Hauptschleife dort läuft und das ab Etappe 5 so bleibt. Ist
`ProjektZeh/Scheduling/CosmosMainLoopIdle.cs` bei Beginn dieses Pakets in `master` (Branch
`task/main-loop-yield-on-ready`, Peters Entscheidung steht aus), wird dort `CosmosRoundRobinScheduler.WaitingThreads`
durch `CosmosClassScheduler.WaitingThreads` ersetzt und der zugehörige Hosttest in `SchedulingTests.cs` bleibt. Ist
die Datei nicht in `master`, liefert dieses Paket nur die Eigenschaft.

- [ ] **Schritt 1: Test schreiben** (`ThreadStarterTests`): `TryStart_WithBackgroundClass_AppliesClassOnTheNewThread`: `ApplyClass` auf eine Lambda setzen, die Klasse und `Environment.CurrentManagedThreadId` merkt; `TryStart(body, threadClass: ThreadClass.Background)`, `Join`; erwartet: gemerkte Klasse `Background`, gemerkte Thread-Id ungleich der des Tests, `body` lief danach. Zweiter Test `TryStart_NormalClass_DoesNotCallApplyClass`. `ApplyClass` im `finally` zurücksetzen.
- [ ] **Schritt 2: Tests schlagen fehl** (Parameter fehlt).
- [ ] **Schritt 3: `ThreadStarter` erweitern, `BackgroundTaskService` anpassen.** Tests bestehen.
- [ ] **Schritt 4: `CosmosClassScheduler.cs` nach der Tabelle schreiben.** Kommentar am Dateikopf: einzige Datei mit `IScheduler`-Signaturen, Etappe 5 passt sie an die neue Schnittstelle an.
- [ ] **Schritt 5: `Kernel.cs` umstellen.** Feldtyp `CosmosClassScheduler`; Protokollzeile `$"{scheduler.Name}; Tick={ClassRunQueues<SchedulerThread>.TickNs}ns"`; `DescribeScheduler` liefert drei Zeilen: Name mit Verdrängungen, wartende Threads je Klasse (`Interaktiv=…, Normal=…, Hintergrund=…`), Auswahlspur wie bisher.
- [ ] **Schritt 6: Round-Robin entfernen.** Die drei Dateien löschen, die Round-Robin-Tests aus `SchedulingTests.cs` entfernen. `grep -rn "RoundRobin" ProjektZeh ProjektZeh.Tests scripts` liefert keine Treffer mehr.
- [ ] **Schritt 7: Hosttests und nativer Build.** Beide Befehle aus der Übersicht; erwartet: 0 Warnungen, 0 Fehler. Zusätzlich Recovery: `dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:ZehOSRecovery=true`.
- [ ] **Schritt 8: Gastläufe (Clerk).** Probe-ISOs entstehen mit `dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -p:<Schalter>=true -warnaserror` unter `ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso`; das normale ISO unter `ProjektZeh/output-x64/ProjektZeh.iso`.

| Lauf | ISO | Befehl | Erwartet |
| --- | --- | --- | --- |
| Threads und Nachrichten | `-p:ZehOSThreadProbe=true` | `./scripts/test-thread-probe.ps1 -IsoPath <ISO>` | `THREAD_PROBE PASS`, `MESSAGES_PROBE PASS`, Zeile `Scheduler=ZehOS Klassen` |
| Isolation positiv | `-p:ZehOSIsolationProbe=true` | `python3 scripts/test-isolation-probe.py --iso <ISO> --accel kvm --runs 5` | PASS in allen Läufen |
| Isolation negativ | `-p:ZehOSIsolationProbeNegative=true` | `python3 scripts/test-isolation-probe.py --iso <ISO> --accel kvm --negative` | Halt wie erwartet |
| Desktop | normal | `python3 scripts/test-desktop-guest.py --iso <ISO> --accel tcg` | Skript endet mit Erfolg |
| Doom | normal | `python3 scripts/test-wasm-doom-desktop.py --iso <ISO> --scenario quick` | Skript endet mit Erfolg |
| Hintergrundklasse | normal, von Hand | im Terminal `tasks start busy`, dann `tasks scheduler` | Zeile mit `Hintergrund=1`; Fenster bleiben bedienbar |
- [ ] **Schritt 9: Doku und Commit** (`feat(scheduling): Klassenscheduler ersetzt Round-Robin`), Review, PR. Im PR steht, dass der Vorrang der Oberfläche erst mit Etappe 5 wirkt.

---

### Paket E4-D: Sperre für die Eingabepuffer

**Branch:** `task/mc-e4-d-irq-sperre`

**Dateien:**
- Anlegen: `ProjektZeh/Input/CosmosIrqLock.cs`
- Ändern: `ProjektZeh/Input/CosmosKeyboardPlug.cs:34`, `ProjektZeh/Input/CosmosInputService.cs:59`, `ProjektZeh/Input/CosmosUsbTabletDriver.cs:53`, `ProjektZeh/Input/CosmosMousePlug.cs` (Stelle, an der die Rohdeltas geschrieben werden)

**Schnittstelle (liefert):**

```csharp
namespace ProjektZeh.Input;

/// Sperre für Daten, die ein Interrupt und ein Thread teilen. Maskiert Interrupts und dreht dann, bis sie frei ist.
/// In ihr gilt ohne Ausnahme: keine Allokation, kein Protokoll, keine zweite Sperre, nur wenige Zeilen.
internal struct CosmosIrqLock
{
    [UnscopedRef] public Scope Enter();
    internal ref struct Scope { public void Dispose(); }   // gibt erst die Sperre frei, stellt dann die Maske wieder her
}
```

`Enter`: `SchedulerManager.MaskInterrupts()` nehmen, dann `while (Interlocked.CompareExchange(ref taken, 1, 0) != 0) X86Base.Pause();`.
Auf einer CPU ist das Verhalten gleich der heutigen Maske; die Sperre kann dort nie belegt sein.

Eine Sperre je geteiltem Puffer, als `static` Feld neben dem Puffer: eine für die Tastatur (Plug schreibt,
`CosmosInputService` liest), eine für Maus und Tablet (Plug und Treiber schreiben, `CosmosInputService` liest).
Schreiber und Leser desselben Puffers nehmen dieselbe Sperre.

- [ ] **Schritt 1: `CosmosIrqLock.cs` schreiben** (kein Hosttest möglich: `SchedulerManager` gibt es auf dem Host nicht; die Datei beginnt deshalb mit `Cosmos`).
- [ ] **Schritt 2: Die vier Stellen umstellen.** Je Stelle `var mask = …MaskInterrupts(); try { … } finally { mask.Dispose(); }` durch `using (lockField.Enter()) { … }` ersetzen; der Inhalt des Blocks bleibt unverändert. Prüfen, dass im Block nichts allokiert oder protokolliert.
- [ ] **Schritt 3: `grep -rn "MaskInterrupts" ProjektZeh/Input`** liefert nur noch `CosmosIrqLock.cs`.
- [ ] **Schritt 4: Hosttests, nativer Build, Gastlauf (Clerk):** `python3 scripts/test-desktop-guest.py --iso ProjektZeh/output-x64/ProjektZeh.iso --accel tcg` und derselbe Lauf mit `--accel kvm` (Tastatur, Maus, Tablet im Desktop), dazu von Hand die Eingabeprüfung aus `docs/COSMOS_SUPPORT.md`, Abschnitt „Deutsche Eingabe und Scrollen“.
- [ ] **Schritt 5: Commit** (`fix(input): Eingabepuffer mit Sperre statt Interruptmaske`), Review, PR.

---

### Paket E4-E: Messung und Abnahme

**Branch:** `task/mc-e4-e-messung`. **Nach:** E4-C und E4-D gemergt. **Rolle:** Clerk misst, Engineer wertet aus.

**Dateien:** `docs/COSMOS_SUPPORT.md` (Messwerte), `ROADMAP.md` (Eintrag zur Etappe), `docs/DESKTOP_PERFORMANCE.md` falls dort die Bildraten geführt werden.

- [ ] **Schritt 1: Zwei ISOs bauen:** `master` vor E4-C (letzter Commit mit Round-Robin) und `master` danach. Commit-Hashes notieren.
- [ ] **Schritt 2: Bildraten messen**, je ISO fünf Läufe im Wechsel, unter KVM, 1 GiB, eine vCPU, CPU-Modell wie in `docs/WASM_FEASIBILITY.md`, Abschnitt „Leistung und Langzeitstabilität“: `python3 scripts/measure-wasm-frametimes.py --iso <ISO> --accel kvm --runs 5 -m 1G` für Doom 1.1.0 (Standardwerte des Skripts) und mit `--id`, `--version`, `--name`, `--entry` des veröffentlichten ClassiCube-Pakets (Sichtweite 512, dieselben Seeds und Startbedingungen wie in `ROADMAP.md` zu 13.2). Je Lauf mittlere Bildrate und Lastmittel des Hosts notieren.
- [ ] **Schritt 3: Auswerten.** Abnahme: Mittel des Klassenschedulers nicht mehr als 3 % unter Round-Robin für beide Apps; sonst zurück an E4-C mit den Rohwerten.
- [ ] **Schritt 4: Hintergrundlast.** Mit `tasks start busy` (eine rechnende Hintergrundaufgabe) dieselbe Doom-Messung auf beiden ISOs. Erwartet: Unter dem Klassenscheduler fällt die Bildrate weniger als unter Round-Robin. Wert berichten; kein Abnahmekriterium.
- [ ] **Schritt 5: VMware.** `scripts/start-vmware.ps1` mit dem normalen ISO: Boot bis Desktop, `tasks scheduler`; danach mit dem Thread-Probe-ISO seriell `THREAD_PROBE PASS`. Kann niemand VMware prüfen: als „offen“ in den PR.
- [ ] **Schritt 6: Doku.** Messwerte mit Umgebung in `docs/COSMOS_SUPPORT.md`; in `ROADMAP.md` unter 6a einen datierten Absatz „Round-Robin durch Klassenscheduler ersetzt“ mit Verweis auf diesen Plan. Commit, PR (reine Doku).
