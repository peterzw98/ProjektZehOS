using System;

namespace ProjektZeh.Graphics;

internal sealed class DesktopMetrics
{
    private long nextSample;
    private long maximumFrame;
    public string Summary { get; private set; } = "Messung startet";
    public void RecordFrame(long microseconds) => maximumFrame = Math.Max(maximumFrame, microseconds);

    public string AllocationSummary { get; private set; } = "";

    public void Sample(long milliseconds, long managedBytes, long allocatedBytes)
    {
        if (milliseconds < nextSample) return;
        Summary = $"{maximumFrame / 1000}ms GC:{managedBytes / 1024}K";
        AllocationSummary = $"Allokiert: {allocatedBytes / 1024} KiB";
        maximumFrame = 0;
        nextSample = milliseconds + 1000;
    }
}
