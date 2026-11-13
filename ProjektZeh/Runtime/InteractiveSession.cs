using System;
using System.Threading;
using Cosmos.Kernel.System.Graphics;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.SystemServices;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime;

internal sealed class InteractiveSession(ShellService shell, Func<ShellService> createShell,
    Func<FileService> createFiles, ITextOutput output,
    Func<long> getElapsedMilliseconds, SystemLog systemLog)
{
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
            if (!graphics && shell.Poll(output)) { line.Begin(); dirty = true; }
            editor.IsSecret = shell.IsSecretInput;
            devices.Poll(input);
            for (var count = 0; count < InputService.QueueCapacity && input.TryRead(out var value); count++)
            {
                if (value.Type == InputEventType.KeyDown && !value.IsRepeat && value.KeyCode == (byte)PhysicalKey.F4)
                    ToggleGraphics();
                else if (graphics)
                {
                    if (value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Escape)
                    {
                        if (!display!.CloseMenu()) LeaveGraphics();
                    }
                    else display!.Handle(value);
                }
                else if (!shell.IsBusy && editor.Handle(value, out string? command) && command != null)
                {
                    line.Submit(editor.IsSecret ? "********" : command);
                    var result = shell.ExecuteLine(command, output);
                    if (!result.IsOK()) output.WriteLine(result.Message);
                    editor.IsSecret = shell.IsSecretInput;
                    line.Begin();
                }
                dirty = true;
            }
            long now = getElapsedMilliseconds();
            if (graphics)
            {
                display!.Update(now);
                if (display.NeedsAnimation && now >= nextFrame) dirty = true;
            }
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
                nextFrame = FrameTiming.NextDeadline(now);
            }
            // Der HAL-Wartetimer tickt im Referenzgast nur mit 18 Hz; der Scheduler weckt feiner auf.
            Thread.Sleep(1);
        }
    }

    private void ToggleGraphics()
    {
        if (graphics) { LeaveGraphics(); return; }
        try
        {
            display ??= new DisplayService(createShell, createFiles);
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
