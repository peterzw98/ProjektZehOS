using System;
using System.Threading;
using Cosmos.Kernel.System.Graphics;
using ProjektZeh.Apps.TaskManager;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Network;
using ProjektZeh.Security;
using ProjektZeh.Shell;
using ProjektZeh.SystemServices;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime;

internal sealed class InteractiveSession(ShellService shell, Func<ShellService> createShell,
    Func<FileService> createFiles, ConsoleTextOutput output,
    Func<long> getElapsedMilliseconds, SystemLog systemLog, AccountService accounts, UserSession identity,
    TaskManagerSection[] sections, Func<Apps.ExternalApplication[]>? listExternal = null, Func<NetworkStatus>? getNetwork = null)
{
    private readonly CosmosInputService devices = new();
    private readonly LoginFlow login = new(accounts, shell, output);
    private int generation = identity.Generation;
    private LineEditor editor = new();
    private readonly ConsoleLineView line = new();
    private DisplayService? display;
    private bool graphics;
    private int pagerOffset;

    public void Run(bool startDesktop)
    {
        var canvas = KernelConsole.Default?.Canvas;
        var input = new InputService(canvas?.Width ?? 1024, canvas?.Height ?? 768);
        CosmosInputService.SetScreenSize(canvas?.Width ?? 1024, canvas?.Height ?? 768);
        long nextFrame = 0;
        long previousTick = -1;
        bool dirty = true;
        output.StartInteractive(Math.Max(1, Console.WindowWidth - 1), Math.Max(1, Console.WindowHeight - 2));
        output.WriteLine("F4: Desktop. Pfeiltasten: Cursor/Verlauf. Esc: zur Textshell.");
        editor.HistoryFilter = shell.CanRemember;
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
                        try { if (!display!.HandleEscape(value)) LeaveGraphics(); }
                        catch (Exception exception) { FailGraphics("Eingabe", exception); }
                    }
                    else
                    {
                        // Auch Treiberfehler beim Moduswechsel muessen zur bedienbaren Textshell fuehren.
                        try { display!.Handle(value); }
                        catch (Exception exception) { FailGraphics("Eingabe", exception); }
                    }
                }
                else if (!IsLocked && shell.PagerText != null)
                {
                    HandlePager(value);
                }
                else if (output.HandleScroll(value)) { }
                else if (!IsLocked && !shell.IsBusy && value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Tab)
                {
                    var alternatives = editor.Complete(shell);
                    // Wie im Terminalfenster: mehrdeutige Treffer unter der aktuellen Eingabe auflisten.
                    if (alternatives.Count > 0)
                    {
                        output.WriteLine(line.Prompt + editor.Text);
                        foreach (string row in ShellService.FormatAlternatives(alternatives, Math.Max(1, Console.WindowWidth - 1))) output.WriteLine(row);
                    }
                    output.FollowOutput();
                }
                else if (!shell.IsBusy && editor.Handle(value, out string? command))
                {
                    output.FollowOutput();
                    if (command == null) { dirty = true; continue; }
                    output.WriteLine(line.Prompt + (editor.IsSecret ? "********" : command));
                    var result = IsLocked ? login.Submit(command) : shell.ExecuteLine(command, output);
                    if (!result.IsOK()) output.WriteLine(result.Message);
                    if (shell.ExitRequested)
                    {
                        shell.ResetExitRequest();
                        output.WriteLine("Die Textshell bleibt als Wiederherstellungskonsole geoeffnet. F4: Desktop.");
                    }
                    SyncSession();
                    editor.IsSecret = IsLocked ? login.IsSecret : shell.IsSecretInput;
                    if (shell.PagerText != null) { pagerOffset = 0; DrawPager(); }
                    else BeginLine();
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
                    output.StartInteractive(Math.Max(1, Console.WindowWidth - 1), Math.Max(1, Console.WindowHeight - 2));
                    ConsoleLineView.Status(input, now);
                    if (shell.PagerText == null || IsLocked) line.Draw(editor, output.Buffer, output.ScrollOffset);
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
            display ??= new DisplayService(createShell, createFiles, input, sections, new DesktopSession(accounts, shell, identity), listExternal, getNetwork);
            Console.CursorVisible = false;
            graphics = true;
            systemLog.Information("Desktop", "Desktop ist bereit.");
            // Zeigt seriell, ob der Treiber einen Laufzeitwechsel der Aufloesung erlaubt (nur bei mehreren Modi).
            systemLog.Information("Desktop", $"Anzeige {display.Display.Current}, Modi: {string.Join(", ", display.Display.Modes)}");
        }
        catch (Exception exception)
        {
            line.Invalidate();
            Console.Clear();
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
        line.Invalidate();
        Console.Clear();
        Console.CursorVisible = true;
        output.WriteLine("ZehOS Textshell - F4 oeffnet den Desktop.");
        // Gegenstueck zu "Desktop ist bereit."; Gasttests warten darauf statt auf eine feste Zeit.
        systemLog.Information("Desktop", "Textshell aktiv.");
        if (suspendError != null) output.WriteLine("Anzeigefehler beim Verlassen: " + suspendError);
        if (shell.PagerText != null && !IsLocked) DrawPager(); else BeginLine();
    }

    /// <summary>Ohne angemeldete Konsolenidentitaet nimmt die Textshell nur die Anmeldung entgegen.</summary>
    private bool IsLocked => identity.UserId == null && login.IsActive;

    /// <summary>Verlauf und halbe Anmeldungen gehoeren nur zur jeweiligen Identitaet.</summary>
    private void SyncSession()
    {
        if (identity.Generation == generation) return;
        generation = identity.Generation;
        // Sichtbar im seriellen Protokoll; automatisierte Gasttests warten darauf statt auf feste Zeiten.
        systemLog.Information("Session", identity.UserId == null ? "Benutzer abgemeldet." : "Benutzer angemeldet.");
        output.Clear();
        editor = new() { HistoryFilter = shell.CanRemember };
        login.Reset();
        shell.CancelSecretInput();
    }

    private void HandlePager(InputEvent value)
    {
        if ((value.Type == InputEventType.Text && value.Character == 'q') ||
            (value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Escape))
        {
            shell.DismissPager(); line.Invalidate(); Console.Clear(); output.FollowOutput(); BeginLine(); return;
        }
        if (value.Type == InputEventType.PointerScroll)
        {
            int pageRows = Math.Max(1, Console.WindowHeight - 3);
            int count = (shell.PagerText ?? "").Split('\n').Length;
            pagerOffset = (int)Math.Clamp(pagerOffset - (long)value.ScrollDelta * 3, 0, Math.Max(0, count - pageRows));
            DrawPager();
            return;
        }
        if (value.Type != InputEventType.KeyDown && !(value.Type == InputEventType.Text && value.Character == ' ')) return;
        if (value.Type == InputEventType.KeyDown && (value.IsTextKey || value.KeyCode is not ((byte)PhysicalKey.Down or (byte)PhysicalKey.Up or (byte)PhysicalKey.PageDown or (byte)PhysicalKey.PageUp or (byte)PhysicalKey.Home or (byte)PhysicalKey.End))) return;
        int page = Math.Max(1, Console.WindowHeight - 3);
        if (value.Type == InputEventType.Text && value.Character == ' ') pagerOffset += page;
        if (value.Type == InputEventType.KeyDown && !value.IsTextKey)
            pagerOffset += (PhysicalKey)value.KeyCode switch
            {
                PhysicalKey.Down => 1, PhysicalKey.Up => -1, PhysicalKey.PageDown => page,
                PhysicalKey.PageUp => -page, PhysicalKey.Home => -pagerOffset,
                PhysicalKey.End => 65536, _ => 0
            };
        DrawPager();
    }

    private void DrawPager()
    {
        string[] lines = (shell.PagerText ?? "").Split('\n');
        int page = Math.Max(1, Console.WindowHeight - 3);
        pagerOffset = Math.Clamp(pagerOffset, 0, Math.Max(0, lines.Length - page));
        Console.Clear();
        int width = Math.Max(1, Console.WindowWidth - 1);
        Console.SetCursorPosition(0, 1);
        Console.Write("less - Scrollrad/Pfeile/Bildtasten/Leertaste, q/Esc schliesst");
        for (int i = pagerOffset; i < Math.Min(lines.Length, pagerOffset + page); i++)
        {
            // Explizite Zeilen verhindern automatisches Konsolenscrollen beim Neuzeichnen des Pagers.
            Console.SetCursorPosition(0, 2 + i - pagerOffset);
            Console.Write(GermanFont.ConsoleText(lines[i][..Math.Min(width, lines[i].Length)]));
        }
        Console.SetCursorPosition(0, Console.WindowHeight - 1);
        Console.Write($"Zeile {pagerOffset + 1}/{lines.Length}");
    }

    private void BeginLine()
    {
        bool locked = IsLocked;
        // Waehrend einer laufenden Kennwortpruefung erscheint der Titel erst mit dem naechsten Schritt erneut.
        if (locked && !login.IsSecret && !login.IsBusy) output.WriteLine($"ZehOS - {login.Title}");
        line.Prompt = !locked ? ConsoleLineView.ShellPrompt : login.IsBusy ? "" : login.Prompt;
    }
}
