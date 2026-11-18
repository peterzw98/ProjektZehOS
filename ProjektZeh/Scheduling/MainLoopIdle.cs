using System;

namespace ProjektZeh.Scheduling;

/// <summary>
/// Leerlauf der Hauptschleife. Sie ist in Cosmos der Idle-Thread: Thread.Sleep endet in WaitPolling, sie bleibt Running und
/// haelt bis zum naechsten Tick, auch wenn ein Wasm-Worker bereit ist (ClassiCube: der Worker bekam unter Dauerlast hoechstens
/// 2 von 3 Ticks). Ist jemand bereit, gibt sie deshalb sofort ab, statt zu warten.
/// </summary>
internal static class MainLoopIdle
{
    /// <returns><see langword="true"/>, wenn abgegeben wurde; <see langword="false"/> nach dem normalen Schlafen.</returns>
    public static bool Wait(int waitingThreads, Action yieldNow, Action sleep)
    {
        if (waitingThreads > 0)
        {
            yieldNow();
            return true;
        }
        sleep();
        return false;
    }
}
