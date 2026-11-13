using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Graphics.Fonts;
using ProjektZeh.Apps.TaskManager;
using ProjektZeh.Input;
using ProjektZeh.Runtime;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Graphics;

internal sealed class DisplayService
{
    private readonly Desktop desktop;
    private readonly InputService input;
    private Canvas canvas;
    private PixelSurface surface;
    private long nextSample;

    public DisplaySettings Display { get; }

    public DisplayService(Func<ShellService> createShell, Func<FileService> createFiles, InputService input,
        TaskManagerSection[] sections, DesktopSession session)
    {
        this.input = input;
        canvas = Canvas.GetFullScreen();
        if (canvas.Mode.ColorDepth != ColorDepth.ColorDepth32 || !DisplayMode.IsSupportedSize(canvas.Width, canvas.Height))
            throw new InvalidOperationException(
                $"Der Desktop benoetigt 1024x720 bis 1920x1080 mit 32 Bit, gemeldet: {canvas.Width}x{canvas.Height}. Im Bootmenue 1024x768 waehlen.");
        surface = CreateSurface();
        var current = new DisplayMode(canvas.Width, canvas.Height);
        Display = new DisplaySettings(current, ReadModes(current), SetMode, () => new(canvas.Width, canvas.Height));
        var font = PCScreenFont.DefaultFont;
        desktop = new Desktop(canvas.Width, canvas.Height, font.Data, font.Width, font.Height, createShell, createFiles,
            Display, sections, session);
        SyncInput();
    }

    public bool NeedsAnimation => desktop.NeedsAnimation;

    public void Handle(InputEvent value) => desktop.Handle(value);
    public bool CloseMenu() => desktop.CloseMenu();

    public void Update(long milliseconds)
    {
        long started = Stopwatch.GetTimestamp();
        desktop.Update(milliseconds);
        Display.Update(milliseconds);
        if (milliseconds >= nextSample)
        {
            var (total, free) = CosmosSystemInfo.PhysicalMemory();
            long heap = GC.GetGCMemoryInfo().HeapSizeBytes, allocated = GC.GetTotalAllocatedBytes(false);
            desktop.Metrics.Sample(milliseconds, heap, allocated);
            desktop.System.Sample(milliseconds, heap, allocated, total, free);
            nextSample = milliseconds + 1000;
        }
        desktop.System.RecordWork(ElapsedMicroseconds(started));
    }

    public void Suspend() => desktop.Suspend();

    public void Render(InputService pointer, long milliseconds)
    {
        long started = Stopwatch.GetTimestamp();
        desktop.Render(surface, milliseconds, pointer.PointerX, pointer.PointerY, pointer.Buttons);
        canvas.Display();
        long elapsed = ElapsedMicroseconds(started);
        desktop.Metrics.RecordFrame(elapsed);
        desktop.System.RecordWork(elapsed);
    }

    private static long ElapsedMicroseconds(long started) => (Stopwatch.GetTimestamp() - started) * 1000000 / Stopwatch.Frequency;

    private PixelSurface CreateSurface() => new(canvas.Width, canvas.Height,
        canvas.GetBuffer() ?? throw new InvalidOperationException("Kein Grafikpuffer vorhanden."));

    private DisplayMode[] ReadModes(DisplayMode current)
    {
        var modes = new List<DisplayMode> { current };
        foreach (var mode in canvas.AvailableModes)
        {
            var candidate = new DisplayMode(mode.Width, mode.Height);
            if (mode.ColorDepth == ColorDepth.ColorDepth32 && DisplayMode.IsSupportedSize(mode.Width, mode.Height) && !modes.Contains(candidate))
                modes.Add(candidate);
        }
        modes.Sort((first, second) => (second.Width * second.Height).CompareTo(first.Width * first.Height));
        return [.. modes];
    }

    private ResultErr SetMode(DisplayMode mode)
    {
        var previous = new DisplayMode(canvas.Width, canvas.Height);
        try
        {
            canvas = Canvas.GetFullScreen(new Mode(mode.Width, mode.Height, ColorDepth.ColorDepth32));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Der Treiber lehnt die Aufloesung ab: " + exception.Message);
        }
        // Erfolg gilt nur, wenn der Treiber die angeforderte Groesse tatsaechlich meldet.
        bool matches = canvas.Width == mode.Width && canvas.Height == mode.Height;
        if (!matches)
        {
            try { canvas = Canvas.GetFullScreen(new Mode(previous.Width, previous.Height, ColorDepth.ColorDepth32)); }
            catch (ArgumentOutOfRangeException) { }
        }
        surface = CreateSurface();
        desktop.Resize(canvas.Width, canvas.Height);
        SyncInput();
        // Cosmos berechnet dabei Textzellen, Zeilen und Spalten fuer den neu dimensionierten Canvas erneut.
        if (KernelConsole.Default is { } console) console.Font = console.Font;
        return matches ? ResultErr.CreateSuccess()
            : ResultErr.CreateFailure(ResultCode.Failure, $"Der Treiber meldet {canvas.Width} x {canvas.Height} statt {mode}.");
    }

    private void SyncInput()
    {
        input.Resize(canvas.Width, canvas.Height);
        CosmosInputService.SetScreenSize(canvas.Width, canvas.Height);
    }
}
