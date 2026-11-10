using System;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Graphics.Fonts;
using ProjektZeh.Input;

namespace ProjektZeh.Graphics;

internal sealed class DisplayService
{
    private const int Background = unchecked((int)0xff142236);
    private const int Foreground = unchecked((int)0xffedf4fc);
    private const int Accent = unchecked((int)0xff53d7bb);
    private readonly Canvas canvas;
    private readonly PixelSurface surface;
    private readonly PCScreenFont font = PCScreenFont.DefaultFont;
    private readonly int[] sample = new int[160 * 100];
    private int clicks;
    private int clickX;
    private int clickY;

    public DisplayService()
    {
        canvas = Canvas.GetFullScreen();
        if (canvas.Width != 1024 || canvas.Height != 768 || canvas.Mode.ColorDepth != ColorDepth.ColorDepth32)
            throw new InvalidOperationException("Die Grafikprobe benoetigt 1024x768 mit 32 Bit (Limine-Aufloesung).");
        surface = new PixelSurface(canvas.Width, canvas.Height,
            canvas.GetBuffer() ?? throw new InvalidOperationException("Kein Grafikpuffer vorhanden."));
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 160; x++) sample[y * 160 + x] = unchecked((int)0xff000000) | x * 255 / 159 << 16 | y * 255 / 99 << 8 | 120;
        }
    }

    public void Handle(InputEvent value)
    {
        if (value.Type != InputEventType.PointerDown) return;
        clicks++;
        clickX = value.X;
        clickY = value.Y;
    }

    public void Render(InputService input, long milliseconds, string lastKey)
    {
        surface.ResetClip();
        surface.FillRectangle(0, 0, 1024, 768, Background);
        surface.FillRectangle(0, 0, 1024, 64, unchecked((int)0xff213b55));
        Text("ZehOS - Grafikprobe", 24, 24, Foreground);
        Text("Esc: zur Shell | F4: Ansicht wechseln", 24, 88, Accent);
        Text($"Laufzeit: {milliseconds / 1000} s | Tasten: {input.PressedKeyCount} | Verloren: {input.DroppedEvents}", 24, 116, Foreground);

        Text("Rechteck/Linie", 32, 164, Foreground);
        surface.FillRectangle(32, 200, 250, 130, unchecked((int)0xff315878));
        surface.FillRectangle(64, 224, 110, 80, Accent);
        surface.DrawLine(32, 200, 281, 329, Foreground);
        surface.DrawLine(32, 329, 281, 200, Foreground);

        Text("Beschneiden", 360, 164, Foreground);
        surface.SetClip(360, 200, 240, 130);
        surface.FillRectangle(320, 180, 200, 190, unchecked((int)0xffdfad5c));
        surface.DrawLine(-500, 100, 1400, 450, Foreground);
        surface.ResetClip();
        surface.DrawLine(360, 200, 599, 200, Accent);
        surface.DrawLine(599, 200, 599, 329, Accent);
        surface.DrawLine(599, 329, 360, 329, Accent);
        surface.DrawLine(360, 329, 360, 200, Accent);

        Text("Pixelpuffer", 680, 164, Foreground);
        surface.DrawPixels(sample, 680, 200, 160, 100);
        Text($"Letzte Taste: {lastKey}", 32, 394, Foreground);
        Text($"Maus: {input.PointerX}, {input.PointerY} | Tasten: {input.Buttons}", 32, 426, Foreground);
        Text($"Klicks: {clicks}", 32, 458, Accent);
        Text("Die Statusanzeige laeuft auch ohne Tastatureingabe weiter.", 32, 526, Foreground);
        Text("Fenster und Desktop folgen in Etappe 5 und 6.", 32, 558, Foreground);
        if (clicks != 0)
        {
            surface.DrawLine(clickX - 5, clickY, clickX + 5, clickY, Accent);
            surface.DrawLine(clickX, clickY - 5, clickX, clickY + 5, Accent);
        }
        DrawPointer(input.PointerX, input.PointerY, input.Buttons != PointerButtons.None ? unchecked((int)0xffffbf69) : Foreground);
        canvas.Display();
    }

    private void Text(string text, int x, int y, int color) =>
        surface.DrawText(text, x, y, color, font.Data, font.Width, font.Height);

    private void DrawPointer(int x, int y, int color)
    {
        for (var row = 0; row < 15; row++) surface.FillRectangle(x, y + row, row / 2 + 1, 1, unchecked((int)0xff000000));
        surface.DrawLine(x, y, x, y + 14, color);
        surface.DrawLine(x, y, x + 7, y + 14, color);
        surface.DrawLine(x, y + 14, x + 7, y + 14, color);
    }
}
