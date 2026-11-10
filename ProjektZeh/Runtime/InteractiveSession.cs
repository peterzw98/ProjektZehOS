using System;
using System.Diagnostics;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Timers;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;

namespace ProjektZeh.Runtime;

internal sealed class InteractiveSession(ShellService shell, ITextOutput output)
{
    private readonly CosmosInputService devices = new();
    private readonly LineEditor editor = new();
    private readonly ConsoleLineView line = new();
    private DisplayService? display;
    private bool graphics;
    private string lastKey = "keine";

    public void Run()
    {
        var canvas = KernelConsole.Default?.Canvas;
        var input = new InputService(canvas?.Width ?? 1024, canvas?.Height ?? 768);
        CosmosInputService.SetScreenSize(canvas?.Width ?? 1024, canvas?.Height ?? 768);
        var clock = Stopwatch.StartNew();
        long nextFrame = 0;
        long previousSecond = -1;
        bool dirty = true;
        output.WriteLine("F4: Grafikprobe. Pfeiltasten: Cursor/Verlauf. Esc: zur Shell.");
        line.Begin();
        while (true)
        {
            devices.Poll(input);
            for (var count = 0; count < InputService.QueueCapacity && input.TryRead(out var value); count++)
            {
                if (value.Type is InputEventType.KeyDown or InputEventType.KeyUp)
                    lastKey = $"{value.KeyCode} {(value.Type == InputEventType.KeyDown ? "gedrueckt" : "losgelassen")}";
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
            long now = clock.ElapsedMilliseconds;
            if (now / 1000 != previousSecond) { previousSecond = now / 1000; dirty = true; }
            if (dirty && now >= nextFrame)
            {
                if (graphics) display!.Render(input, now, lastKey);
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
            display ??= new DisplayService();
            Console.CursorVisible = false;
            graphics = true;
        }
        catch (Exception exception)
        {
            output.Clear();
            output.WriteLine($"Grafikprobe konnte nicht starten: {exception.Message}");
            line.Begin();
        }
    }

    private void LeaveGraphics()
    {
        graphics = false;
        output.Clear();
        Console.CursorVisible = true;
        output.WriteLine("ZehOS Shell - F4 oeffnet die Grafikprobe.");
        line.Begin();
    }
}
