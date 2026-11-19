using System.Threading;

namespace ProjektZeh.Runtime;

/// <summary>Ergebnis der Pruefung, ob eine CPU-Ausnahme nur den Wasm-Worker beendet (Roadmap 12.4-I1).</summary>
internal enum IsolationDecision
{
    Isolate,
    NotProgramFault,
    NotWorker,
    InterruptsMasked,
    OutsideGuest,
    InHostFunction,
    HoldsStorageLock,
    HoldsAppStorageGate,
}

/// <summary>
/// Reine Entscheidung fuer den Ausnahmepfad: Isoliert wird nur Gastcode des Wasm-Workers, weil nur dort sicher keine
/// Kernelsperre gehalten wird. Alles andere haelt das System wie bisher an (Entscheidung 3 von Peter, 18.11.).
/// </summary>
internal static class FaultIsolationPolicy
{
    /// <summary>Kein gueltiger Vektor: Der Halt kam nicht ueber Panic.CpuException oder ohne frischen Kontext.</summary>
    public const long UnknownVector = -1;

    /// <summary>
    /// Nur Fehler, die der ausgefuehrte Code selbst ausloest: #DE (0), #UD (6), #GP (13) und #PF (14). NMI, Machine
    /// Check, Double Fault und alle uebrigen Vektoren melden einen Zustand der Maschine statt einen App-Fehler und halten
    /// deshalb an, auch wenn sie zufaellig waehrend Gastcode eintreffen (Review pink P2-1).
    /// </summary>
    public static bool IsProgramFault(long vector) => vector is 0 or 6 or 13 or 14;

    /// <param name="interruptsEnabled">
    /// Interruptflag des fehlerhaften Codes (Cosmos <c>CpuExceptionInfo.InterruptsEnabled</c>, Patch ausnahme-interrupts).
    /// Gastcode laeuft immer mit freigegebenen Interrupts. Ein Interrupt-Handler laeuft maskiert auf dem Stack des Threads,
    /// den er unterbrochen hat; trifft er den Worker im Gastcode, meldet Cosmos den Worker, <paramref name="inNativeRun"/>
    /// ist 1 und die Hosttiefe 0. Eine Ausnahme im Handler ist aber Kernelcode, der Sperren halten kann: Halt (E6-C.10, #390).
    /// </param>
    public static IsolationDecision Decide(long vector, int currentThreadId, int workerThreadId, bool inNativeRun,
        bool interruptsEnabled, int hostDepth, bool holdsStorageLock, bool holdsAppStorageGate)
    {
        if (!IsProgramFault(vector)) return IsolationDecision.NotProgramFault;
        if (workerThreadId < 0 || currentThreadId != workerThreadId) return IsolationDecision.NotWorker;
        if (!interruptsEnabled) return IsolationDecision.InterruptsMasked;
        if (!inNativeRun) return IsolationDecision.OutsideGuest;
        if (hostDepth != 0) return IsolationDecision.InHostFunction;
        if (holdsStorageLock) return IsolationDecision.HoldsStorageLock;
        if (holdsAppStorageGate) return IsolationDecision.HoldsAppStorageGate;
        return IsolationDecision.Isolate;
    }
}

/// <summary>
/// Threads, die der Scheduler nie wieder einreihen darf. Feste Tabelle je Thread-Id, weil der Eintrag im Ausnahmepfad
/// ohne Allokation und ohne Sperre geschrieben wird.
/// </summary>
internal sealed class ThreadQuarantine(int capacity)
{
    private readonly int[] entries = new int[capacity];

    /// <summary>Liefert false fuer eine Id ausserhalb der Tabelle; dann darf der Thread nicht aufgegeben werden.</summary>
    public bool Add(int threadId)
    {
        if ((uint)threadId >= (uint)entries.Length) return false;
        Volatile.Write(ref entries[threadId], 1);
        return true;
    }

    public bool Contains(uint threadId) => threadId < (uint)entries.Length && Volatile.Read(ref entries[threadId]) != 0;
}

/// <summary>
/// Uebergabe eines aufgegebenen Workers vom Ausnahmepfad an die Hauptschleife (E6-C.9, #390). Der Ausnahme-Hook meldet den
/// Thread an, bevor Cosmos den Wechsel weg von ihm anlegt (<c>TryAbandonCurrentFromInterrupt</c>); die Hauptschleife
/// uebernimmt ihn erst, wenn die CPU der Ausnahme ihn nicht mehr als laufenden Thread fuehrt. Vorher konnte die
/// Hauptschleife auf einer anderen CPU schon aufraeumen und einen neuen Worker starten, waehrend Cosmos noch entschied,
/// ob es den Wechsel ueberhaupt anlegen kann, und bei einem Fehlschlag das System anhielt (Datentraeger mitten im
/// Schreiben von <c>CloseAll</c>).
/// </summary>
/// <remarks>
/// Es gibt nur einen Wasm-Worker und der Hook isoliert nur ihn; zwei gleichzeitige Anmeldungen gibt es deshalb nicht.
/// <see cref="Stage"/> laeuft im Ausnahmepfad: keine Allokation, keine Sperre.
/// </remarks>
internal sealed class IsolationHandoff
{
    /// <summary>Wert fuer "keine CPU fuehrt einen Thread" (Cosmos <c>CpuExceptionInfo.NoThread</c>).</summary>
    public const long NoThread = uint.MaxValue;

    private int threadId = -1;
    private int cpu = -1;

    /// <summary>Vom Ausnahme-Hook: Thread <paramref name="faultingThreadId"/> auf <paramref name="faultingCpu"/> wird aufgegeben.</summary>
    public void Stage(int faultingThreadId, int faultingCpu)
    {
        // CPU zuerst: Wer die Id liest (Volatile, Erwerb), sieht danach die passende CPU.
        Volatile.Write(ref cpu, faultingCpu);
        Volatile.Write(ref threadId, faultingThreadId);
    }

    /// <summary>CPU der angemeldeten Ausnahme oder -1; fuer die Abfrage des laufenden Threads dort.</summary>
    public int PendingCpu => Volatile.Read(ref threadId) < 0 ? -1 : Volatile.Read(ref cpu);

    /// <summary>
    /// Von der Hauptschleife: liefert die Id des aufgegebenen Workers und loescht die Anmeldung, sobald
    /// <paramref name="runningOnCpu"/> (laufender Thread der CPU aus <see cref="PendingCpu"/>, <see cref="NoThread"/> ohne)
    /// ein anderer Thread ist; sonst -1, und die naechste Runde fragt erneut.
    /// </summary>
    /// <remarks>
    /// Den laufenden Thread einer CPU aendert nur diese CPU selbst; nach der Ausnahme nur noch der Wechsel aus dem
    /// Ausnahmepfad, und den legt Cosmos nur an, wenn es den Thread wirklich aufgibt. Haelt Cosmos stattdessen an, bleibt
    /// der Worker der laufende Thread, und die Hauptschleife raeumt nichts ab. Ein veralteter Lesewert verzoegert nur.
    /// </remarks>
    public int TryTake(long runningOnCpu)
    {
        int pending = Volatile.Read(ref threadId);
        if (pending < 0 || runningOnCpu == pending) return -1;
        return Interlocked.CompareExchange(ref threadId, -1, pending) == pending ? pending : -1;
    }
}
