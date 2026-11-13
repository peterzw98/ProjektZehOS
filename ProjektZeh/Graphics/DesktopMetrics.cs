using System;

namespace ProjektZeh.Graphics;

internal sealed class DesktopMetrics
{
    private long nextSample;
    private long maximumFrame;
    private long previousSample;
    private int frames;
    public string Summary { get; private set; } = "Messung startet";
    public bool HasSample { get; private set; }
    public long FramesPerSecond { get; private set; }
    /// <summary>Laengste Zeichen- und Ausgabedauer im letzten Messintervall.</summary>
    public long MaximumFrameMilliseconds { get; private set; }
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
        FramesPerSecond = fps;
        MaximumFrameMilliseconds = maximumFrame / 1000;
        HasSample = true;
        Summary = $"{fps} FPS {maximumFrame / 1000}ms GC:{managedBytes / 1024}K";
        AllocationSummary = $"Allokiert: {allocatedBytes / 1024} KiB";
        maximumFrame = 0;
        frames = 0;
        previousSample = milliseconds;
        nextSample = milliseconds + 1000;
    }
}
