using System;
using System.Diagnostics;
using System.Threading;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Graphics.Fonts;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Installer;
using ProjektZeh.Shared;
using ProjektZeh.UI;

namespace ProjektZeh.Runtime;

/// <summary>
/// Ereignisschleife des Installer-Builds; zeigt ausschliesslich den Einrichtungsassistenten ohne Textshell oder Desktop.
/// </summary>
internal sealed class InstallerSession(IInstallService service)
{
    public void Run()
    {
        var canvas = Canvas.GetFullScreen();
        if (canvas.Mode.ColorDepth != ColorDepth.ColorDepth32)
            throw new InvalidOperationException("Setup benoetigt eine Anzeige mit 32 Bit Farbtiefe.");
        int width = canvas.Width;
        int height = canvas.Height;
        var surface = new PixelSurface(width, height,
            canvas.GetBuffer() ?? throw new InvalidOperationException("Kein Grafikpuffer vorhanden."));
        var font = PCScreenFont.DefaultFont;
        var wizard = new SetupWizard(service, new UiPainter(font.Data, font.Width, font.Height), width, height);
        var devices = new CosmosInputService();
        var input = new InputService(width, height);
        CosmosInputService.SetScreenSize(width, height);
        var clock = Stopwatch.StartNew();
        long nextFrame = 0;
        while (true)
        {
            devices.Poll(input);
            // Ausnahmen des Installationsdienstes beenden nicht die Schleife, sondern erscheinen auf der Fehlerseite.
            try
            {
                for (var count = 0; count < InputService.QueueCapacity && input.TryRead(out var value); count++)
                    wizard.Handle(value);
                wizard.Update();
            }
            catch (Exception exception) { wizard.Fail(ResultErr.CreateFromException(exception)); }
            long now = clock.ElapsedMilliseconds;
            if (wizard.IsDirty && now >= nextFrame)
            {
                wizard.Render(surface, input.PointerX, input.PointerY, input.Buttons);
                canvas.Display();
                nextFrame = FrameTiming.NextDeadline(now);
            }
            // Der HAL-Wartetimer tickt im Referenzgast nur mit 18 Hz; der Scheduler weckt feiner auf.
            Thread.Sleep(1);
        }
    }
}
