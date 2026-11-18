using System.Threading;

namespace ProjektZeh.Runtime;

/// <summary>Ergebnis der Pruefung, ob eine CPU-Ausnahme nur den Wasm-Worker beendet (Roadmap 12.4-I1).</summary>
internal enum IsolationDecision
{
    Isolate,
    NotProgramFault,
    NotWorker,
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

    public static IsolationDecision Decide(long vector, int currentThreadId, int workerThreadId, bool inNativeRun,
        int hostDepth, bool holdsStorageLock, bool holdsAppStorageGate)
    {
        if (!IsProgramFault(vector)) return IsolationDecision.NotProgramFault;
        if (workerThreadId < 0 || currentThreadId != workerThreadId) return IsolationDecision.NotWorker;
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
