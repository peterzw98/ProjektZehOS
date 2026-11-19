using System;
using ProjektZeh.Scheduling;

namespace ProjektZeh.Apps.TaskManager;

internal sealed class SystemMetrics
{
    private readonly int[] history = new int[60];
    private long previousSample = -1;
    private long work;
    private int nextHistory;
    public long UptimeMilliseconds { get; private set; }
    public long ManagedBytes { get; private set; }
    public long AllocatedBytes { get; private set; }
    public ulong TotalMemory { get; private set; }
    public ulong FreeMemory { get; private set; }
    public bool HasPhysicalMemory => TotalMemory > 0 && FreeMemory <= TotalMemory;
    public bool HasCpuSample { get; private set; }
    public int CpuPercent { get; private set; }
    public int HistoryCount { get; private set; }
    /// <summary>Last je CPU seit der letzten Abfrage; leer, solange <see cref="SampleCpuLoads"/> nie aufgerufen wurde.</summary>
    public int[] CpuLoads { get; private set; } = [];
    private ulong[] previousBusy = [];
    private ulong previousAllTicks;

    public void RecordWork(long microseconds)
    {
        if (microseconds > 0) work += Math.Min(microseconds, long.MaxValue - work);
    }

    public void Sample(long milliseconds, long managedBytes, long allocatedBytes, ulong totalMemory = 0, ulong freeMemory = 0)
    {
        milliseconds = Math.Max(0, milliseconds);
        UptimeMilliseconds = milliseconds;
        ManagedBytes = Math.Max(0, managedBytes);
        AllocatedBytes = Math.Max(0, allocatedBytes);
        TotalMemory = totalMemory;
        FreeMemory = freeMemory;
        if (previousSample < 0 || milliseconds < previousSample)
        {
            previousSample = milliseconds;
            work = 0;
            HasCpuSample = false;
            return;
        }
        long elapsed = milliseconds - previousSample;
        if (elapsed < 1000) return;
        // Nur die gemessene Arbeit der Desktop-Schleife, keine Scheduler-CPU-Auslastung.
        CpuPercent = (int)Math.Clamp((double)work / elapsed / 10, 0, 100);
        HasCpuSample = true;
        history[nextHistory] = CpuPercent;
        nextHistory = (nextHistory + 1) % history.Length;
        HistoryCount = Math.Min(history.Length, HistoryCount + 1);
        previousSample = milliseconds;
        work = 0;
    }

    /// <summary>Aus kumulierten Zaehlern (belegte Ticks je CPU, alle Ticks) die Last seit dem vorigen Aufruf; der erste Aufruf misst seit Start.</summary>
    public void SampleCpuLoads(ReadOnlySpan<ulong> busyTicks, ulong allTicks)
    {
        if (previousBusy.Length != busyTicks.Length)
        {
            previousBusy = new ulong[busyTicks.Length];
            CpuLoads = new int[busyTicks.Length];
        }
        // Zaehler laufen nie rueckwaerts; ein Rueckgang (neu angelegte CPU-Zustaende) zaehlt als 0 statt als Ueberlauf.
        ulong elapsed = allTicks >= previousAllTicks ? allTicks - previousAllTicks : 0;
        for (int cpu = 0; cpu < busyTicks.Length; cpu++)
        {
            ulong busy = busyTicks[cpu] >= previousBusy[cpu] ? busyTicks[cpu] - previousBusy[cpu] : 0;
            CpuLoads[cpu] = CpuDiagnostics.LoadPercent(busy, elapsed);
            previousBusy[cpu] = busyTicks[cpu];
        }
        previousAllTicks = allTicks;
    }

    public int GetHistory(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, HistoryCount);
        return history[(nextHistory - HistoryCount + index + history.Length) % history.Length];
    }
}
