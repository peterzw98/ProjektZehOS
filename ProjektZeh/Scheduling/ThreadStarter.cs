using System;
using System.Threading;
using ProjektZeh.Shared;

namespace ProjektZeh.Scheduling;

internal enum ThreadPlacement { Inherit = 0, Workers = 1 }

/// <summary>
/// Einziger Weg fuer Thread-Starts im Kernel. Seit Cosmos 3.0.97-zehos.6 (Stapel smp-phase5b) prueft SchedulerThread.InitializeStack
/// den Threadstack selbst: bei Misserfolg Collect und zweiter Versuch, sonst false; CreateThread liefert 0 und Thread.Start wirft.
/// Eine eigene Stackprobe davor war nur ein Fenster (TOCTOU unter SMP) und entfaellt. Ein scheiternder Start kommt als
/// Fehler beim Aufrufer an.
/// </summary>
internal static class ThreadStarter
{
    /// <summary>
    /// Vom Kernel gesetzt: ordnet dem AUFRUFENDEN Thread eine Klasse zu (SetPriority des Klassenschedulers). Auf dem Host und
    /// vor dem Setzen geschieht nichts.
    /// </summary>
    internal static Action<ThreadClass> ApplyClass { get; set; } = _ => { };

    internal static Action<ThreadPlacement> ApplyPlacement { get; set; } = _ => { };

    /// <summary>
    /// Startet <paramref name="body"/> mit <paramref name="stackBytes"/> (0 = Cosmos-Standard) .
    /// <paramref name="background"/> markiert dauerhaft wartende Worker, damit sie ein Prozessende (Hosttests) nicht aufhalten.
    /// </summary>
    public static ResultObject<Thread> TryStart(ThreadStart body, int stackBytes = 0, bool background = false,
        ThreadClass threadClass = ThreadClass.Normal, ThreadPlacement placement = ThreadPlacement.Inherit)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(stackBytes);
        if (threadClass != ThreadClass.Normal || placement != ThreadPlacement.Inherit)
        {
            var inner = body;
            body = () =>
            {
                // Die Klasse noch auf der geerbten CPU setzen, bevor die Affinitaet den neuen Thread verschiebt.
                if (threadClass != ThreadClass.Normal) ApplyClass(threadClass);
                if (placement != ThreadPlacement.Inherit) ApplyPlacement(placement);
                inner();
            };
        }
        try
        {
            var thread = stackBytes == 0 ? new Thread(body) : new Thread(body, stackBytes);
            thread.IsBackground = background;
            thread.Start();
            return ResultObjectFactory.CreateSuccess(thread);
        }
        catch (Exception exception) when (exception is OutOfMemoryException or ThreadStartException or InvalidOperationException)
        {
            return ResultObjectFactory.CreateFromException<Thread>(exception);
        }
    }
}
