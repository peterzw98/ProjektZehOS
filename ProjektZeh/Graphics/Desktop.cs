using System;
using System.Collections.Generic;
using ProjektZeh.Apps;
using ProjektZeh.Apps.FileManager;
using ProjektZeh.Apps.Pong;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Apps.TextEditor;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.UI;

namespace ProjektZeh.Graphics;

internal sealed class Desktop
{
    public const int MaximumWindows = 8;
    private const int BarHeight = 56;
    private const int PanelColor = unchecked((int)0xff18212c);
    private const int SecondaryText = unchecked((int)0xffa0afc1);
    private readonly int width;
    private readonly int height;
    private readonly byte[] font;
    private readonly int glyphWidth;
    private readonly int glyphHeight;
    private readonly Func<ShellService> createShell;
    private readonly Func<FileService> createFiles;
    private readonly UiPainter painter;
    private readonly UiPanel menu = new();
    private readonly UiTextBox search;
    private readonly UiList appList = new();
    private readonly UiButton launch;
    private readonly List<ApplicationEntry> matches = [];
    private readonly ApplicationEntry[] applications;
    private readonly UiRect menuBounds;
    private readonly DesktopSession? session;
    private readonly LoginScreen? login;
    private int generation;
    private int sequence;
    private bool menuOpen;
    private bool alt;
    private bool leftShift;
    private bool rightShift;
    private bool suppressSpace;
    public WindowManager Windows { get; }
    public DesktopMetrics Metrics { get; } = new();
    public bool NeedsAnimation => !menuOpen && Windows.ActiveWindow?.Application is PongApp { IsPaused: false };

    private sealed record ApplicationEntry(string Name, Func<bool> Launch);

    public Desktop(int width, int height, byte[] font, int glyphWidth, int glyphHeight,
        Func<ShellService> createShell, Func<FileService> createFiles, DesktopSession? session = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 640);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 400 + BarHeight);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(createShell);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphHeight);
        this.width = width;
        this.height = height;
        this.font = font;
        this.glyphWidth = glyphWidth;
        this.glyphHeight = glyphHeight;
        this.createShell = createShell;
        ArgumentNullException.ThrowIfNull(createFiles);
        this.createFiles = createFiles;
        painter = new(font, glyphWidth, glyphHeight);
        Windows = new WindowManager(width, height - BarHeight);
        applications = [new("Terminal", OpenTerminal), new("Dateien", OpenFiles),
            new("Texteditor", () => OpenEditor()), new("Pong", OpenPong)];
        if (session != null)
        {
            this.session = session;
            login = new(session, painter, width, height);
            generation = session.Identity.Generation;
            applications = [.. applications, new("Abmelden", () => { login.Logout(); return true; })];
        }
        menuBounds = new(12, height - 440, 400, 378);
        search = new(64, painter: painter) { Bounds = new(28, menuBounds.Y + 56, 368, 44) };
        appList.Bounds = new(28, menuBounds.Y + 112, 368, 160);
        launch = new("Programm starten", LaunchSelected) { Bounds = new(28, menuBounds.Y + 306, 368, 52) };
        search.Submitted = LaunchSelected;
        appList.Activated = LaunchSelected;
        menu.Add(search);
        menu.Add(appList);
        menu.Add(launch);
        FilterApplications();
    }

    public bool OpenTerminal()
    {
        if (Windows.Count == MaximumWindows) return false;
        Windows.Reset();
        int offset = sequence % 4 * 40;
        var terminal = new TerminalApp(createShell(), font, glyphWidth, glyphHeight, (640 - 20) / glyphWidth);
        Windows.Add(new Window($"Terminal {++sequence}", 72 + offset, 72 + offset, 640, 400, terminal, 320, 200));
        return true;
    }

    public bool OpenFiles() => Windows.Count < MaximumWindows && OpenApplication("Dateien",
        new FileManagerApp(createFiles(), painter, path => OpenEditor(path)), 640, 440);
    public bool OpenEditor(string path = "") => Windows.Count < MaximumWindows && OpenApplication("Texteditor",
        new TextEditorApp(createFiles(), painter, path), 640, 440);
    public bool OpenPong() => Windows.Count < MaximumWindows && OpenApplication("Pong", new PongApp(painter),
        680, Math.Max(480, Window.TitleHeight + 34 + 2 * glyphHeight + PongGame.Height));

    private bool OpenApplication(string title, IApplication application, int minimumWidth, int minimumHeight)
    {
        if (Windows.Count == MaximumWindows || width < 800 || height - BarHeight < 560) return false;
        Windows.Reset();
        int offset = sequence++ % 3 * 28;
        Windows.Add(new Window(title, 32 + offset, 80 + offset, 800, 560, application, minimumWidth, minimumHeight));
        return true;
    }

    public bool CloseMenu()
    {
        if (!menuOpen) return false;
        menuOpen = false;
        menu.Handle(new(InputEventType.Reset));
        Windows.Reset();
        return true;
    }

    private void ToggleMenu()
    {
        if (CloseMenu()) return;
        Windows.Reset();
        menu.Handle(new(InputEventType.Reset));
        menu.Focus(search);
        menuOpen = true;
    }

    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset)
        {
            alt = leftShift = rightShift = suppressSpace = false;
            CloseMenu();
            Windows.Reset();
            return;
        }
        SyncSession();
        if (IsLocked)
        {
            login!.Handle(value);
            return;
        }
        if (value.Type is InputEventType.KeyDown or InputEventType.KeyUp)
        {
            bool down = value.Type == InputEventType.KeyDown;
            if (value.KeyCode == (byte)PhysicalKey.Space && !down) suppressSpace = false;
            if (value.KeyCode == (byte)PhysicalKey.Alt) { alt = down; return; }
            if (value.KeyCode == (byte)PhysicalKey.LeftShift) leftShift = down;
            if (value.KeyCode == (byte)PhysicalKey.RightShift) rightShift = down;
            if (alt && down && value.KeyCode == (byte)PhysicalKey.F1 && !value.IsRepeat) { ToggleMenu(); return; }
            if (alt && down && value.KeyCode == (byte)PhysicalKey.Tab)
            {
                CloseMenu();
                Windows.Cycle(leftShift || rightShift);
                return;
            }
            if (down && value.KeyCode == (byte)PhysicalKey.Escape && CloseMenu()) return;
        }
        if ((alt && value.Type is InputEventType.KeyDown or InputEventType.Text) ||
            (suppressSpace && value.Type == InputEventType.Text && value.Character == ' ')) return;
        if (Windows.HasPointerCapture && value.Type is InputEventType.PointerMove or InputEventType.PointerUp)
        {
            Windows.Handle(value);
            return;
        }
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            if (MenuButtonBounds.Contains(value.X, value.Y)) { ToggleMenu(); return; }
            if (TerminalButtonBounds.Contains(value.X, value.Y)) { CloseMenu(); OpenTerminal(); return; }
        }
        if (menuOpen)
        {
            if (value.Type == InputEventType.PointerDown && !menuBounds.Contains(value.X, value.Y)) { CloseMenu(); return; }
            string previous = search.Text;
            bool space = value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Space && menu.Focused == launch;
            launch.Enabled = matches.Count > 0 && Windows.Count < MaximumWindows;
            menu.Handle(value);
            if (space && !menuOpen) suppressSpace = true;
            if (!string.Equals(previous, search.Text, StringComparison.Ordinal)) FilterApplications();
            return;
        }
        if (value.Type is InputEventType.PointerDown or InputEventType.PointerUp or InputEventType.PointerMove && value.Y >= height - BarHeight)
        {
            if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
                for (int index = 0; index < Windows.Count; index++)
                    if (TaskBounds(index).Contains(value.X, value.Y)) { Windows.ToggleTask(Windows.GetWindow(index)); break; }
            return;
        }
        Windows.Handle(value);
    }

    private void LaunchSelected()
    {
        if (appList.SelectedIndex >= 0 && Windows.Count < MaximumWindows && matches[appList.SelectedIndex].Launch()) CloseMenu();
    }

    private void FilterApplications()
    {
        matches.Clear();
        foreach (var app in applications)
            if (app.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)) matches.Add(app);
        var names = new string[matches.Count];
        for (int index = 0; index < matches.Count; index++) names[index] = matches[index].Name;
        appList.SetItems(names);
        launch.Enabled = matches.Count > 0 && Windows.Count < MaximumWindows;
    }

    private UiRect MenuButtonBounds => new(12, height - 44, 208, 32);
    private UiRect TerminalButtonBounds => new(232, height - 44, 64, 32);
    private UiRect TaskBounds(int index)
    {
        int taskWidth = Math.Min(224, (width - 444) / Math.Max(1, Windows.Count));
        return new(312 + index * taskWidth, height - 44, taskWidth - 4, 32);
    }

    public void Update(long milliseconds)
    {
        SyncSession();
        if (IsLocked) login!.Update();
        Windows.Update(milliseconds);
    }

    private bool IsLocked => session != null && session.Identity.UserId == null;

    /// <summary>Schliesst Fenster und Menue nach jedem Identitaetswechsel, auch wenn er in der Textshell geschah.</summary>
    private void SyncSession()
    {
        if (session == null || session.Identity.Generation == generation) return;
        generation = session.Identity.Generation;
        CloseMenu();
        Windows.CloseAll();
        login!.Reset();
    }

    public void Render(PixelSurface surface, long milliseconds, int pointerX, int pointerY, PointerButtons buttons)
    {
        surface.ResetClip();
        SyncSession();
        surface.FillRectangle(0, 0, width, height, unchecked((int)0xff142236));
        if (IsLocked)
        {
            login!.Render(surface);
            DrawPointer(surface, pointerX, pointerY, buttons);
            return;
        }
        Text(surface, "ZehOS", 32, 28);
        Text(surface, "DEIN ARBEITSPLATZ", 32, 64, SecondaryText);
        if (session != null) Text(surface, $"Angemeldet: {session.Identity.UserName}", 32, 100, SecondaryText);
        painter.Text(surface, Metrics.Summary, width - 300, 20, 276, SecondaryText);
        painter.Text(surface, Metrics.AllocationSummary, width - 300, 56, 276, SecondaryText);
        if (Windows.Count == 0)
        {
            Text(surface, "Alles beginnt hier.", 72, height / 2 - 24);
            Text(surface, "Programme unten links oeffnen.", 72, height / 2 + 8, SecondaryText);
        }
        Windows.Render(surface, font, glyphWidth, glyphHeight, !menuOpen);
        surface.FillRectangle(0, height - BarHeight, width, BarHeight, PanelColor);
        surface.FillRectangle(0, height - BarHeight, width, 1, UiPainter.Border);
        Button(surface, MenuButtonBounds, "Anwendungen", menuOpen);
        Button(surface, TerminalButtonBounds, ">_", false);
        for (int index = 0; index < Windows.Count; index++)
        {
            Window window = Windows.GetWindow(index);
            UiRect bounds = TaskBounds(index);
            string label = window.Title;
            if (label.Length * glyphWidth > bounds.Width - 20)
                label = window.Application switch
                {
                    TerminalApp => $"T{index + 1}", FileManagerApp => $"D{index + 1}",
                    TextEditorApp => $"E{index + 1}", PongApp => $"P{index + 1}", _ => $"{index + 1}"
                };
            Button(surface, bounds, label, window == Windows.ActiveWindow, window.IsMinimized);
        }
        Text(surface, $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}", width - 120, height - 38);
        if (menuOpen)
        {
            UiPainter.Frame(surface, menuBounds, 0, 0, false);
            Text(surface, "Anwendungen", menuBounds.X + 16, menuBounds.Y + 20);
            menu.Draw(surface, painter, 0, 0, true);
            if (matches.Count == 0) Text(surface, "Keine Programme gefunden", 36, menuBounds.Y + 120, SecondaryText);
        }
        DrawPointer(surface, pointerX, pointerY, buttons);
    }

    private static void DrawPointer(PixelSurface surface, int pointerX, int pointerY, PointerButtons buttons)
    {
        int color = buttons == PointerButtons.None ? -1 : UiPainter.Accent;
        for (int row = 0; row < 15; row++) surface.FillRectangle(pointerX, pointerY + row, row / 2 + 1, 1, unchecked((int)0xff000000));
        surface.DrawLine(pointerX, pointerY, pointerX, pointerY + 14, color);
        surface.DrawLine(pointerX, pointerY, pointerX + 7, pointerY + 14, color);
        surface.DrawLine(pointerX, pointerY + 14, pointerX + 7, pointerY + 14, color);
    }

    private void Button(PixelSurface surface, UiRect bounds, string label, bool active, bool minimized = false)
    {
        UiPainter.Frame(surface, bounds, 0, 0, active);
        painter.Text(surface, label, bounds.X + 10, bounds.Y + (bounds.Height - glyphHeight) / 2,
            bounds.Width - 20, minimized ? SecondaryText : UiPainter.Foreground);
        if (active) surface.FillRectangle(bounds.X + 8, bounds.Y + bounds.Height - 3, bounds.Width - 16, 2, UiPainter.Accent);
    }

    private void Text(PixelSurface surface, string text, int x, int y, int color = UiPainter.Foreground) =>
        painter.Text(surface, text, x, y, width - x, color);
}
