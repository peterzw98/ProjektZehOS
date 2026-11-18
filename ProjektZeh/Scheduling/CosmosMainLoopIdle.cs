using System.Runtime.CompilerServices;
using System.Threading;

namespace ProjektZeh.Scheduling;

/// <summary>Cosmos-Anbindung von <see cref="MainLoopIdle"/>: Thread.Yield plus sofortiger Reschedule-Interrupt.</summary>
internal static class CosmosMainLoopIdle
{
    // Internes Cosmos-Mitglied ueber UnsafeAccessor wie bei der Fehlerisolierung (Roadmap 12.4-I1); Signatur aus 3.0.97 gelesen.
    private const string InterruptManagerType = "Cosmos.Kernel.Core.CPU.InterruptManager, Cosmos.Kernel.Core";

    // Der HAL-Wartetimer tickt im Referenzgast nur mit 18 Hz; der Scheduler weckt feiner auf.
    public static void Wait() => MainLoopIdle.Wait(CosmosRoundRobinScheduler.WaitingThreads, YieldNow, () => Thread.Sleep(1));

    // Yield merkt nur den Wechsel vor; ohne Interrupt wartet er bis zum naechsten Tick oder Geraeteinterrupt. Ist der
    // Interruptcontroller nicht bereit, schlaeft die Schleife normal, statt bis dahin zu drehen.
    private static bool YieldNow()
    {
        Thread.Yield();
        return TryRaiseReschedule(null);
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "TryRaiseReschedule")]
    private static extern bool TryRaiseReschedule([UnsafeAccessorType(InterruptManagerType)] object? owner);
}
