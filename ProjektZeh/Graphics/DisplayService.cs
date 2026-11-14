using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cosmos.Kernel.HAL.Devices.Display;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Graphics.Fonts;
using ProjektZeh.Apps.TaskManager;
using ProjektZeh.Input;
using ProjektZeh.Runtime;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using FramebufferMode = Cosmos.Kernel.HAL.Devices.Display.DisplayMode;

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
        int pointerX = pointer.PointerX, pointerY = pointer.PointerY;
        var previous = desktop.Render(surface, milliseconds, pointerX, pointerY, pointer.Buttons);
        // Canvas.Display() kopiert immer das ganze Bild (Full HD rund 8 MB); bei reiner Zeigerbewegung genuegen beide Zeigerflaechen.
        if (previous is not { } old || !TryPresentPointers(old.X, old.Y, pointerX, pointerY)) canvas.Display();
        long elapsed = ElapsedMicroseconds(started);
        desktop.Metrics.RecordFrame(elapsed);
        desktop.System.RecordWork(elapsed);
    }

#pragma warning disable COSMOS0003 // IDisplay ist in Cosmos 3.0.94 experimentell; Canvas.Display() verwendet dieselben Mitglieder.
    // Liefert false, wenn nur die Vollausgabe von Cosmos sicher ist, etwa ohne Treiberanzeige oder bei anderer Farbtiefe.
    private bool TryPresentPointers(int previousX, int previousY, int pointerX, int pointerY)
    {
        // Abgeleitete Canvas-Typen koennen Display() mit eigener Ausgabelogik ueberschreiben.
        if (canvas.GetType() != typeof(Canvas)) return false;
        // Die zum Canvas gehoerende Anzeige; Primary koennte nach einem Treiberwechsel eine andere sein.
        if (CanvasDisplay(canvas) is not { } device || !device.TryGetFacet<IDisplay>(out var display)) return false;
        FramebufferMode mode = display.Mode;
        var framebuffer = display.Framebuffer;
        if (mode.IsEmpty || mode.BitsPerPixel != 32 || framebuffer == null ||
            framebuffer.Length < (ulong)((long)mode.Height * mode.Pitch)) return false;
        try
        {
            Span<int> target = framebuffer.As<int>();
            PresentPointer(device, display, target, mode, previousX, previousY);
            PresentPointer(device, display, target, mode, pointerX, pointerY);
            return true;
        }
        // Ein zwischenzeitlich entzogener Framebuffer wird ungueltig; die Vollausgabe behandelt das wie Cosmos selbst.
        catch (InvalidOperationException) { return false; }
    }

    private void PresentPointer(DisplayDevice device, IDisplay display, Span<int> target, FramebufferMode mode, int x, int y)
    {
        var (left, top, width, height) = FramebufferCopy.CopyRectangle(canvas.GetBuffer(), canvas.Width, canvas.Height,
            target, mode.Pitch / 4, mode.Width, mode.Height, x, y, Desktop.PointerWidth, Desktop.PointerHeight);
        if (width > 0 && height > 0 && !device.IsWithdrawn) display.Flush(left, top, width, height);
    }
#pragma warning restore COSMOS0003

    // In Cosmos 3.0.94 privat; gelesen wird nur die Anzeige, aus der Canvas.Display() selbst kopiert.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_display")]
    private static extern ref DisplayDevice? CanvasDisplay(Canvas canvas);

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
