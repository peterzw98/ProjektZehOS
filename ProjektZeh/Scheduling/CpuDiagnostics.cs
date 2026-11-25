using System;

namespace ProjektZeh.Scheduling;

/// <summary>Plattformunabhaengige Aufbereitung der Scheduler-Diagnose je CPU (die Zahlen liefert <c>CosmosClassScheduler.Snapshot(cpu)</c>).</summary>
internal static class CpuDiagnostics
{
    /// <summary>Anteil belegter Zeit in Prozent, auf 0 bis 100 begrenzt; 0 ohne Gesamtzeit (keine Division durch 0).</summary>
    public static int LoadPercent(ulong busyNs, ulong allNs) =>
        allNs == 0 ? 0 : (int)Math.Min(100UL, busyNs * 100 / allNs);

    public static string ClassName(ThreadClass threadClass) => threadClass switch
    {
        ThreadClass.Interactive => "Interaktiv",
        ThreadClass.Normal => "Normal",
        _ => "Hintergrund",
    };

    /// <summary>Eine Zeile von <c>tasks scheduler</c>; <paramref name="runningId"/> null heisst: es laeuft nur der Idle-Thread.</summary>
    public static string FormatLine(uint cpu, uint? runningId, ThreadClass runningClass, ReadOnlySpan<int> waitingByClass, ulong busyNs, ulong allNs)
    {
        string running = runningId is { } id ? $"{id} ({ClassName(runningClass)})" : "Leerlauf";
        return $"CPU {cpu}: l\u00e4uft {running}, wartend Interaktiv={waitingByClass[0]} Normal={waitingByClass[1]} Hintergrund={waitingByClass[2]}, Last {LoadPercent(busyNs, allNs)} %";
    }
}
