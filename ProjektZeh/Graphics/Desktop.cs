using System;
using System.Collections.Generic;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.UI;

namespace ProjektZeh.Graphics;

internal sealed class Desktop
{
    public const int MaximumWindows = 8;
    private const int WindowWidth = 640;
    private const int WindowHeight = 400;
    private const int BarHeight = 56;
    private readonly int width;
    private readonly int height;
    private readonly byte[] font;
    private readonly int glyphWidth;
    private readonly int glyphHeight;
    private readonly Func<ShellService> createShell;
    private readonly UiPainter painter;
    private readonly UiButton menuButton = new("Anwendungen");
    private readonly UiButton terminalButton = new(" >_ ");
    private readonly UiButton launchButton = new("Programm starten");
    private readonly UiTextField search = new();
    private readonly UiList appList = new();
    private readonly List<ApplicationEntry> matches = [];
    private readonly ApplicationEntry[] applications;
    private readonly UiBounds menuBounds;
    private int sequence;
    private int menuFocus;
    private bool menuOpen;
    private bool alt;
    private bool leftShift;
    private bool rightShift;
    private bool suppressSpace;
    public WindowManager Windows { get; }

    private sealed record ApplicationEntry(string Name, Func<bool> Launch);

    public Desktop(int width, int height, byte[] font, int glyphWidth, int glyphHeight, Func<ShellService> createShell)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, WindowWidth);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, WindowHeight + BarHeight);
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
        painter = new(font, glyphWidth, glyphHeight);
        Windows = new WindowManager(width, height - BarHeight);
        applications = [new("Terminal", OpenTerminal)];
        menuButton.Bounds = new(12, height - 44, 156, 32);
        terminalButton.Bounds = new(180, height - 44, 68, 32);
        menuBounds = new(12, height - 390, 360, 328);
        search.Bounds = new(28, menuBounds.Y + 56, 328, 36);
        appList.Bounds = new(28, menuBounds.Y + 108, 328, 144);
        launchButton.Bounds = new(28, menuBounds.Y + 272, 328, 36);
        FilterApplications();
    }

    public bool OpenTerminal()
    {
        if (Windows.Count == MaximumWindows) return false;
        Windows.Reset();
        int offset = sequence % 4 * 40;
        var terminal = new TerminalApp(createShell(), font, glyphWidth, glyphHeight, (WindowWidth - 20) / glyphWidth);
        Windows.Add(new Window($"Terminal {++sequence}", 72 + offset, 72 + offset, WindowWidth, WindowHeight, terminal, 320, 200));
        return true;
    }

    public bool CloseMenu()
    {
        if (!menuOpen) return false;
        menuOpen = false;
        Windows.Reset();
        return true;
    }

    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset)
        {
            alt = leftShift = rightShift = suppressSpace = false;
            menuOpen = false;
            Windows.Reset();
            return;
        }
        if (value.Type is InputEventType.KeyDown or InputEventType.KeyUp)
        {
            bool down = value.Type == InputEventType.KeyDown;
            if (value.KeyCode == (byte)PhysicalKey.Space && !down) suppressSpace = false;
            if (value.KeyCode == (byte)PhysicalKey.Alt) { alt = down; return; }
            if (value.KeyCode == (byte)PhysicalKey.LeftShift) leftShift = down;
            if (value.KeyCode == (byte)PhysicalKey.RightShift) rightShift = down;
            if (alt && down && value.KeyCode == (byte)PhysicalKey.F1 && !value.IsRepeat)
            {
                Windows.Reset();
                menuOpen = !menuOpen;
                menuFocus = 0;
                return;
            }
            if (alt && down && value.KeyCode == (byte)PhysicalKey.Tab)
            {
                CloseMenu();
                Windows.Cycle(leftShift || rightShift);
                return;
            }
            if (down && value.KeyCode == (byte)PhysicalKey.Escape && CloseMenu()) return;
        }
        if (alt || (suppressSpace && value.Type == InputEventType.Text && value.Character == ' ')) return;
        if (Windows.HasPointerCapture && value.Type is InputEventType.PointerMove or InputEventType.PointerUp)
        {
            Windows.Handle(value);
            return;
        }
        if (menuButton.Handle(value))
        {
            Windows.Reset();
            menuOpen = !menuOpen;
            menuFocus = 0;
            return;
        }
        if (terminalButton.Handle(value)) { CloseMenu(); OpenTerminal(); return; }
        if (menuOpen)
        {
            if (value.Type == InputEventType.PointerDown && !menuBounds.Contains(value.X, value.Y))
            {
                CloseMenu();
                return;
            }
            HandleMenu(value);
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

    private void HandleMenu(InputEvent value)
    {
        if (value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Tab)
        {
            menuFocus = (menuFocus + (leftShift || rightShift ? 2 : 1)) % 3;
            return;
        }
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            if (search.Bounds.Contains(value.X, value.Y)) menuFocus = 0;
            if (appList.Bounds.Contains(value.X, value.Y)) menuFocus = 1;
            if (launchButton.Bounds.Contains(value.X, value.Y)) menuFocus = 2;
        }
        search.Focused = menuFocus == 0;
        appList.Focused = menuFocus == 1;
        launchButton.Focused = menuFocus == 2;
        if (search.Handle(value)) FilterApplications();
        appList.Handle(value);
        launchButton.Enabled = matches.Count > 0 && Windows.Count < MaximumWindows;
        bool enter = value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Enter && !value.IsRepeat;
        if ((enter || launchButton.Handle(value)) && launchButton.Enabled && matches[appList.SelectedIndex].Launch())
        {
            suppressSpace = value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Space;
            CloseMenu();
        }
    }

    private void FilterApplications()
    {
        matches.Clear();
        foreach (var app in applications)
            if (app.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)) matches.Add(app);
        var names = new string[matches.Count];
        for (int index = 0; index < matches.Count; index++) names[index] = matches[index].Name;
        appList.SetItems(names);
    }

    private UiBounds TaskBounds(int index)
    {
        int available = width - 284 - 148;
        int taskWidth = Math.Min(168, available / Math.Max(1, Windows.Count));
        return new(284 + index * taskWidth, height - 44, taskWidth - 4, 32);
    }

    public void Update(long milliseconds) => Windows.Update(milliseconds);

    public void Render(PixelSurface surface, long milliseconds, int pointerX, int pointerY, PointerButtons buttons)
    {
        surface.ResetClip();
        surface.FillRectangle(0, 0, width, height, unchecked((int)0xff142236));
        painter.Text(surface, "ZehOS", 32, 28);
        painter.Text(surface, "DEIN ARBEITSPLATZ", 32, 52, UiPainter.Muted);
        if (Windows.Count == 0)
        {
            painter.Text(surface, "Alles beginnt hier.", 72, height / 2 - 24);
            painter.Text(surface, "Programme unten links oeffnen.", 72, height / 2 + 8, UiPainter.Muted);
        }
        Windows.Render(surface, font, glyphWidth, glyphHeight);
        surface.FillRectangle(0, height - BarHeight, width, BarHeight, UiPainter.Panel);
        surface.FillRectangle(0, height - BarHeight, width, 1, unchecked((int)0xff465260));
        menuButton.Focused = menuOpen;
        menuButton.Draw(surface, painter);
        terminalButton.Enabled = Windows.Count < MaximumWindows;
        terminalButton.Draw(surface, painter);
        for (int index = 0; index < Windows.Count; index++)
        {
            Window window = Windows.GetWindow(index);
            UiBounds bounds = TaskBounds(index);
            UiPainter.Box(surface, bounds, window == Windows.ActiveWindow);
            painter.Label(surface, bounds, window.Title, window.IsMinimized ? UiPainter.Muted : UiPainter.TextColor);
            if (window == Windows.ActiveWindow) surface.FillRectangle(bounds.X + 8, bounds.Y + bounds.Height - 3, bounds.Width - 16, 2, UiPainter.Accent);
        }
        painter.Text(surface, $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}", width - 120, height - 38);
        painter.Text(surface, "Laufzeit", width - 120, height - 21, UiPainter.Muted);
        if (menuOpen)
        {
            UiPainter.Box(surface, menuBounds, false);
            painter.Text(surface, "Anwendungen", menuBounds.X + 16, menuBounds.Y + 20);
            search.Focused = menuFocus == 0;
            appList.Focused = menuFocus == 1;
            launchButton.Focused = menuFocus == 2;
            launchButton.Enabled = matches.Count > 0 && Windows.Count < MaximumWindows;
            search.Draw(surface, painter);
            appList.Draw(surface, painter);
            launchButton.Draw(surface, painter);
        }
        int color = buttons == PointerButtons.None ? -1 : UiPainter.Accent;
        for (int row = 0; row < 15; row++) surface.FillRectangle(pointerX, pointerY + row, row / 2 + 1, 1, unchecked((int)0xff000000));
        surface.DrawLine(pointerX, pointerY, pointerX, pointerY + 14, color);
        surface.DrawLine(pointerX, pointerY, pointerX + 7, pointerY + 14, color);
        surface.DrawLine(pointerX, pointerY + 14, pointerX + 7, pointerY + 14, color);
    }
}
