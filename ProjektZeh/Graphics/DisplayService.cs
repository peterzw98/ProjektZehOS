using System;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Graphics.Fonts;
using ProjektZeh.Input;
using ProjektZeh.Shell;

namespace ProjektZeh.Graphics;

internal sealed class DisplayService
{
    private readonly Canvas canvas;
    private readonly PixelSurface surface;
    private readonly Desktop desktop;

    public DisplayService(Func<ShellService> createShell)
    {
        canvas = Canvas.GetFullScreen();
        if (canvas.Width != 1024 || canvas.Height != 768 || canvas.Mode.ColorDepth != ColorDepth.ColorDepth32)
            throw new InvalidOperationException("Der Desktop benoetigt 1024x768 mit 32 Bit (Limine-Aufloesung).");
        surface = new PixelSurface(canvas.Width, canvas.Height,
            canvas.GetBuffer() ?? throw new InvalidOperationException("Kein Grafikpuffer vorhanden."));
        var font = PCScreenFont.DefaultFont;
        desktop = new Desktop(canvas.Width, canvas.Height, font.Data, font.Width, font.Height, createShell);
    }

    public void Handle(InputEvent value) => desktop.Handle(value);
    public void Update(long milliseconds) => desktop.Update(milliseconds);
    public void Suspend() => desktop.Handle(new(InputEventType.Reset));

    public void Render(InputService input, long milliseconds)
    {
        desktop.Render(surface, milliseconds, input.PointerX, input.PointerY, input.Buttons);
        canvas.Display();
    }
}
