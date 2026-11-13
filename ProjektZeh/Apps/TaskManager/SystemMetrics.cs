using System;

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

    public int GetHistory(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, HistoryCount);
        return history[(nextHistory - HistoryCount + index + history.Length) % history.Length];
    }
}
