using System;
using System.Threading;
using ProjektZeh.Shared;

namespace ProjektZeh.Scheduling;

/// <summary>
/// Einziger Weg fuer Thread-Starts im Kernel. Cosmos 3.0.97 holt den Threadstack in SchedulerThread.InitializeStack ueber
/// MemoryOp.Alloc aus dem nativen Heap und prueft das Ergebnis nicht: bei fragmentiertem Heap schreibt es den Threadkontext
/// auf null+0x138 (Page Fault, E4.0 1G Schritt j). Diese Stelle liegt hinter einer UnmanagedCallersOnly-Grenze, keine
/// Ausnahme erreicht den Aufrufer. Deshalb fordert TryStart vorher einen Block in Stackgroesse ueber denselben Heap an und
/// gibt ihn sofort frei; scheitert das, startet kein Thread und der Aufrufer erhaelt einen Fehler.
/// Grenze: Zwischen Probe-Free und InitializeStack kann der Platz wieder vergeben werden, durch einen anderen Thread oder durch
/// Thread.Start selbst: dessen verwaltete Objekte (SchedulerThread, GCHandle) koennen ein neues GC-Segment aus demselben
/// Seitenspeicher holen und die gerade freigegebenen Seiten belegen. Das Fenster wird kleiner, nicht null.
/// </summary>
internal static class ThreadStarter
{
    /// <summary>Stack ohne ausdrueckliche Groesse; Cosmos meldet seriell "[libSystemNative] Thread … stack 262144 bytes".</summary>
    public const int DefaultStackBytes = 256 * 1024;
    // Cosmos rundet in SystemNative_CreateThread auf 4-KiB-Seiten auf und nimmt mindestens 64 KiB.
    private const int PageBytes = 4096;
    private const int MinimumStackBytes = 64 * 1024;

    /// <summary>
    /// Prueft, ob der native Heap einen Block der Groesse liefert (und gibt ihn wieder frei). Der Kernel setzt beim Start die
    /// Cosmos-Probe; auf dem Host und vor dem Setzen gilt jeder Start als moeglich.
    /// </summary>
    internal static Func<int, bool> StackProbe { get; set; } = _ => true;

    /// <summary>Startet <paramref name="body"/> mit <paramref name="stackBytes"/> (0 = Cosmos-Standard) nach der Stackprobe.</summary>
    public static ResultObject<Thread> TryStart(ThreadStart body, int stackBytes = 0) => TryStart(body, stackBytes, StackProbe);

    internal static ResultObject<Thread> TryStart(ThreadStart body, int stackBytes, Func<int, bool> probe)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(stackBytes);
        ArgumentNullException.ThrowIfNull(probe);
        int effective = EffectiveStackBytes(stackBytes);
        if (!probe(effective))
            return ResultObjectFactory.CreateFailure<Thread>(ResultCode.Failure,
                $"cause='OutOfMemory' message='Kein Speicher fuer einen Threadstack ({effective / 1024} KiB).'");
        try
        {
            var thread = stackBytes == 0 ? new Thread(body) : new Thread(body, stackBytes);
            thread.Start();
            return ResultObjectFactory.CreateSuccess(thread);
        }
        catch (Exception exception) when (exception is OutOfMemoryException or ThreadStartException or InvalidOperationException)
        {
            return ResultObjectFactory.CreateFromException<Thread>(exception);
        }
    }

    /// <summary>Groesse, die Cosmos tatsaechlich anfordert: Standard bzw. auf Seiten gerundet, mindestens 64 KiB.</summary>
    internal static int EffectiveStackBytes(int stackBytes)
    {
        if (stackBytes == 0) return DefaultStackBytes;
        long rounded = ((long)stackBytes + PageBytes - 1) / PageBytes * PageBytes;
        return (int)Math.Min(int.MaxValue, Math.Max(MinimumStackBytes, rounded));
    }
}
