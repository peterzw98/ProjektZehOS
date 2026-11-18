using System.Threading;

namespace ProjektZeh.Runtime;

/// <summary>Ergebnis der Pruefung, ob eine CPU-Ausnahme nur den Wasm-Worker beendet (Roadmap 12.4-I1).</summary>
internal enum IsolationDecision
{
    Isolate,
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
    public static IsolationDecision Decide(int currentThreadId, int workerThreadId, bool inNativeRun, int hostDepth,
        bool holdsStorageLock, bool holdsAppStorageGate)
    {
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
