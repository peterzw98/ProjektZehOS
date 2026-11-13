using System;

namespace ProjektZeh.Graphics;

internal sealed class DesktopMetrics
{
    private long nextSample;
    private long maximumFrame;
    private long previousSample;
    private int frames;
    public string Summary { get; private set; } = "Messung startet";
    public void RecordFrame(long microseconds)
    {
        frames++;
        maximumFrame = Math.Max(maximumFrame, microseconds);
    }

    public string AllocationSummary { get; private set; } = "";

    public void Sample(long milliseconds, long managedBytes, long allocatedBytes)
    {
        if (milliseconds < nextSample) return;
        long elapsed = Math.Max(1, milliseconds - previousSample);
        long fps = frames * 1000L / elapsed;
        Summary = $"{fps} FPS {maximumFrame / 1000}ms GC:{managedBytes / 1024}K";
        AllocationSummary = $"Allokiert: {allocatedBytes / 1024} KiB";
        maximumFrame = 0;
        frames = 0;
        previousSample = milliseconds;
        nextSample = milliseconds + 1000;
    }
}
