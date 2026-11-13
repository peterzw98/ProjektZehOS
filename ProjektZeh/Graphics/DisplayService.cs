using System;
using System.Diagnostics;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Graphics.Fonts;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Graphics;

internal sealed class DisplayService
{
    private readonly Canvas canvas;
    private readonly PixelSurface surface;
    private readonly Desktop desktop;
    private long nextSample;

    public DisplayService(Func<ShellService> createShell, Func<FileService> createFiles)
    {
        canvas = Canvas.GetFullScreen();
        if (canvas.Width != 1024 || canvas.Height != 768 || canvas.Mode.ColorDepth != ColorDepth.ColorDepth32)
            throw new InvalidOperationException("Der Desktop benoetigt 1024x768 mit 32 Bit (Limine-Aufloesung).");
        surface = new PixelSurface(canvas.Width, canvas.Height,
            canvas.GetBuffer() ?? throw new InvalidOperationException("Kein Grafikpuffer vorhanden."));
        var font = PCScreenFont.DefaultFont;
        desktop = new Desktop(canvas.Width, canvas.Height, font.Data, font.Width, font.Height, createShell, createFiles);
    }

    public bool NeedsAnimation => desktop.NeedsAnimation;

    public void Handle(InputEvent value) => desktop.Handle(value);
    public bool CloseMenu() => desktop.CloseMenu();
    public void Update(long milliseconds)
    {
        desktop.Update(milliseconds);
        if (milliseconds >= nextSample)
        {
            desktop.Metrics.Sample(milliseconds, GC.GetGCMemoryInfo().HeapSizeBytes, GC.GetTotalAllocatedBytes(false));
            nextSample = milliseconds + 1000;
        }
    }
    public void Suspend() => desktop.Handle(new(InputEventType.Reset));

    public void Render(InputService input, long milliseconds)
    {
        long started = Stopwatch.GetTimestamp();
        desktop.Render(surface, milliseconds, input.PointerX, input.PointerY, input.Buttons);
        canvas.Display();
        desktop.Metrics.RecordFrame((Stopwatch.GetTimestamp() - started) * 1000000 / Stopwatch.Frequency);
    }
}
