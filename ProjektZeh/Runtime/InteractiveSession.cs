using System;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Timers;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Runtime;

internal sealed class InteractiveSession(Func<ShellService> createShell, ITextOutput output,
    Func<long> getElapsedMilliseconds, SystemLog systemLog)
{
    private readonly ShellService shell = createShell();
    private readonly CosmosInputService devices = new();
    private readonly LineEditor editor = new();
    private readonly ConsoleLineView line = new();
    private DisplayService? display;
    private bool graphics;

    public void Run(bool startDesktop)
    {
        var canvas = KernelConsole.Default?.Canvas;
        var input = new InputService(canvas?.Width ?? 1024, canvas?.Height ?? 768);
        CosmosInputService.SetScreenSize(canvas?.Width ?? 1024, canvas?.Height ?? 768);
        long nextFrame = 0;
        long previousTick = -1;
        bool dirty = true;
        output.WriteLine("F4: Desktop. Pfeiltasten: Cursor/Verlauf. Esc: zur Textshell.");
        line.Begin();
        if (startDesktop) ToggleGraphics();
        while (true)
        {
            devices.Poll(input);
            for (var count = 0; count < InputService.QueueCapacity && input.TryRead(out var value); count++)
            {
                if (value.Type == InputEventType.KeyDown && !value.IsRepeat && value.KeyCode == (byte)PhysicalKey.F4)
                    ToggleGraphics();
                else if (graphics)
                {
                    if (value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Escape) LeaveGraphics();
                    else display!.Handle(value);
                }
                else if (editor.Handle(value, out string? command) && command != null)
                {
                    line.Submit(command);
                    var result = shell.ExecuteLine(command, output);
                    if (!result.IsOK()) output.WriteLine(result.Message);
                    line.Begin();
                }
                dirty = true;
            }
            long now = getElapsedMilliseconds();
            if (graphics) display!.Update(now);
            if (now / 500 != previousTick) { previousTick = now / 500; dirty = true; }
            if (dirty && now >= nextFrame)
            {
                if (graphics)
                {
                    try { display!.Render(input, now); }
                    catch (Exception exception)
                    {
                        LeaveGraphics();
                        systemLog.Error("Desktop", $"Zeichnen fehlgeschlagen: {exception.Message}");
                        output.WriteLine($"Desktopfehler: {exception.Message}");
                        line.Begin();
                    }
                }
                else
                {
                    ConsoleLineView.Status(input, now);
                    line.Draw(editor);
                }
                dirty = false;
                nextFrame = now + 16;
            }
            TimerManager.Wait(1);
        }
    }

    private void ToggleGraphics()
    {
        if (graphics) { LeaveGraphics(); return; }
        try
        {
            display ??= new DisplayService(createShell);
            Console.CursorVisible = false;
            graphics = true;
            systemLog.Information("Desktop", "Desktop ist bereit.");
        }
        catch (Exception exception)
        {
            output.Clear();
            systemLog.Error("Desktop", $"Start fehlgeschlagen: {exception.Message}");
            output.WriteLine($"Desktop konnte nicht starten: {exception.Message}");
            line.Begin();
        }
    }

    private void LeaveGraphics()
    {
        display?.Suspend();
        graphics = false;
        output.Clear();
        Console.CursorVisible = true;
        output.WriteLine("ZehOS Textshell - F4 oeffnet den Desktop.");
        line.Begin();
    }
}
