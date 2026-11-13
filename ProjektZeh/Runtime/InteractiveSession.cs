using System;
using System.Threading;
using Cosmos.Kernel.System.Graphics;
using ProjektZeh.Apps.TaskManager;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Security;
using ProjektZeh.Shell;
using ProjektZeh.SystemServices;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime;

internal sealed class InteractiveSession(ShellService shell, Func<ShellService> createShell,
    Func<FileService> createFiles, ITextOutput output,
    Func<long> getElapsedMilliseconds, SystemLog systemLog, AccountService accounts, UserSession identity,
    TaskManagerSection[] sections)
{
    private readonly CosmosInputService devices = new();
    private readonly LoginFlow login = new(accounts, shell, output);
    private int generation = identity.Generation;
    private LineEditor editor = new();
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
        BeginLine();
        if (startDesktop) ToggleGraphics(input);
        while (true)
        {
            if (!graphics && shell.Poll(output)) { BeginLine(); dirty = true; }
            SyncSession();
            editor.IsSecret = IsLocked ? login.IsSecret : shell.IsSecretInput;
            devices.Poll(input);
            for (var count = 0; count < InputService.QueueCapacity && input.TryRead(out var value); count++)
            {
                if (value.Type == InputEventType.KeyDown && !value.IsRepeat && value.KeyCode == (byte)PhysicalKey.F4)
                    ToggleGraphics(input);
                else if (graphics)
                {
                    if (value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Escape)
                    {
                        if (!display!.CloseMenu()) LeaveGraphics();
                    }
                    else
                    {
                        // Auch Treiberfehler beim Moduswechsel muessen zur bedienbaren Textshell fuehren.
                        try { display!.Handle(value); }
                        catch (Exception exception) { FailGraphics("Eingabe", exception); }
                    }
                }
                else if (!shell.IsBusy && editor.Handle(value, out string? command) && command != null)
                {
                    line.Submit(editor.IsSecret ? "********" : command);
                    var result = IsLocked ? login.Submit(command) : shell.ExecuteLine(command, output);
                    if (!result.IsOK()) output.WriteLine(result.Message);
                    SyncSession();
                    editor.IsSecret = IsLocked ? login.IsSecret : shell.IsSecretInput;
                    BeginLine();
                }
                dirty = true;
            }
            long now = getElapsedMilliseconds();
            if (graphics)
            {
                try
                {
                    display!.Update(now);
                    if (display.NeedsAnimation && now >= nextFrame) dirty = true;
                }
                catch (Exception exception) { FailGraphics("Aktualisieren", exception); }
            }
            if (now / 500 != previousTick) { previousTick = now / 500; dirty = true; }
            if (dirty && now >= nextFrame)
            {
                if (graphics)
                {
                    try { display!.Render(input, now); }
                    catch (Exception exception) { FailGraphics("Zeichnen", exception); }
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

    private void FailGraphics(string step, Exception exception)
    {
        LeaveGraphics();
        systemLog.Error("Desktop", $"{step} fehlgeschlagen: {exception.Message}");
        output.WriteLine($"Desktopfehler: {exception.Message}");
        BeginLine();
    }

    private void ToggleGraphics(InputService input)
    {
        if (graphics) { LeaveGraphics(); return; }
        try
        {
            display ??= new DisplayService(createShell, createFiles, input, sections, new DesktopSession(accounts, shell, identity));
            Console.CursorVisible = false;
            graphics = true;
            systemLog.Information("Desktop", "Desktop ist bereit.");
        }
        catch (Exception exception)
        {
            output.Clear();
            systemLog.Error("Desktop", $"Start fehlgeschlagen: {exception.Message}");
            output.WriteLine($"Desktop konnte nicht starten: {exception.Message}");
            BeginLine();
        }
    }

    private void LeaveGraphics()
    {
        string? suspendError = null;
        try { display?.Suspend(); }
        catch (Exception exception)
        {
            // Auch ein Treiberfehler bei der Ruecknahme darf den direkten F4-/Esc-Pfad nicht beenden.
            suspendError = exception.Message;
            systemLog.Error("Desktop", "Anzeige konnte nicht zurueckgenommen werden: " + suspendError);
        }
        graphics = false;
        output.Clear();
        Console.CursorVisible = true;
        output.WriteLine("ZehOS Textshell - F4 oeffnet den Desktop.");
        if (suspendError != null) output.WriteLine("Anzeigefehler beim Verlassen: " + suspendError);
        BeginLine();
    }

    /// <summary>Ohne angemeldete Konsolenidentitaet nimmt die Textshell nur die Anmeldung entgegen.</summary>
    private bool IsLocked => identity.UserId == null && login.IsActive;

    /// <summary>Verlauf und halbe Anmeldungen gehoeren nur zur jeweiligen Identitaet.</summary>
    private void SyncSession()
    {
        if (identity.Generation == generation) return;
        generation = identity.Generation;
        editor = new();
        login.Reset();
        shell.CancelSecretInput();
    }

    private void BeginLine()
    {
        bool locked = IsLocked;
        // Waehrend einer laufenden Kennwortpruefung erscheint der Titel erst mit dem naechsten Schritt erneut.
        if (locked && !login.IsSecret && !login.IsBusy) output.WriteLine($"ZehOS - {login.Title}");
        line.Prompt = !locked ? ConsoleLineView.ShellPrompt : login.IsBusy ? "" : login.Prompt;
        line.Begin();
    }
}
