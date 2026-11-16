using System;
using System.Collections.Generic;
using ProjektZeh.Apps;
using ProjektZeh.Apps.FileManager;
using ProjektZeh.Apps.Pong;
using ProjektZeh.Apps.Settings;
using ProjektZeh.Apps.TaskManager;
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
    public const int BarHeight = 56;
    private const int PinnedLeft = 152;
    private const int TaskLeft = 308;
    private const int PagerSlot = 36;
    private static readonly string[] DefaultShortcuts = ["Terminal", "Dateien", "Texteditor", "Einstellungen"];
    private readonly byte[] font;
    private readonly int glyphWidth;
    private readonly int glyphHeight;
    private readonly Func<ShellService> createShell;
    private readonly Func<FileService> createFiles;
    private readonly DisplaySettings? display;
    private readonly TaskManagerSection[] systemSections;
    private readonly UiPainter painter;
    private readonly UiPanel menu = new();
    private readonly UiTextBox search;
    private readonly UiTileGrid appList = new();
    private readonly UiTileGrid desktopIcons = new() { ColumnMajor = true, Transparent = true };
    private readonly List<ApplicationEntry> shortcuts = [];
    private readonly List<Window> tasks = [];
    private readonly UiButton launch;
    private readonly UiButton pin;
    private readonly UiButton diagnose;
    private readonly UiButton reboot;
    private readonly UiButton shutdown;
    private readonly List<ApplicationEntry> matches = [];
    private readonly ApplicationEntry[] applications;
    private readonly ApplicationEntry[] pinned;
    private readonly Func<ExternalApplication[]>? listExternal;
    private ApplicationEntry[] external = [];
    private int width;
    private int height;
    private UiRect menuBounds;
    public const int PointerWidth = 9;
    public const int PointerHeight = 16;
    private int[] wallpaper = [];
    private int[] scene = [];
    private PixelSurface? sceneSurface;
    private bool sceneDirty = true;
    private int pointerLeft;
    private int pointerTop;
    private int wallpaperRevision = -1;
    private long now;
    private long clockMinute = -1;
    private string clock = "";
    private string menuStatus = "";
    private readonly DesktopSession? session;
    private readonly LoginScreen? login;
    private int generation;
    private int sequence;
    private bool menuOpen;
    private bool alt;
    private bool control;
    private bool leftShift;
    private bool rightShift;
    private bool suppressSpace;
    public WindowManager Windows { get; }
    public DesktopMetrics Metrics { get; } = new();
    public SystemMetrics System { get; } = new();
    public DesktopPreferences Preferences { get; private set; }
    private bool restoreDisplay = true;
    public bool NeedsAnimation => !menuOpen && Windows.ActiveWindow?.Application.NeedsAnimation == true;

    private sealed record ApplicationEntry(string Name, string Description, AppIcon Icon, Func<bool> Launch);

    public Desktop(int width, int height, byte[] font, int glyphWidth, int glyphHeight,
        Func<ShellService> createShell, Func<FileService> createFiles,
        DisplaySettings? display = null, TaskManagerSection[]? systemSections = null, DesktopSession? session = null,
        Func<ExternalApplication[]>? listExternal = null)
    {
        this.listExternal = listExternal;
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
        this.display = display;
        this.systemSections = systemSections ?? [];
        Preferences = new DesktopPreferences(createFiles());
        painter = new(font, glyphWidth, glyphHeight);
        Windows = new WindowManager(width, height - BarHeight);
        applications =
        [
            new("Terminal", "Befehlszeile - Doppelklick/Enter", AppIcon.Terminal, OpenTerminal),
            new("Dateien", "Laufwerke, Ordner - Doppelklick/Enter", AppIcon.Files, OpenFiles),
            new("Texteditor", "Textdateien - Doppelklick/Enter", AppIcon.Editor, () => OpenEditor()),
            new("Einstellungen", "Anzeige, Aussehen - Doppelklick/Enter", AppIcon.Settings, OpenSettings),
            new("Taskmanager", "Fenster, System - Doppelklick/Enter", AppIcon.Tasks, OpenTaskManager),
            new("Pong", "Spiel - Doppelklick/Enter", AppIcon.Pong, OpenPong),
        ];
        pinned = [applications[1], applications[0], applications[3]];
        search = new(64, painter: painter);
        launch = new("Starten", LaunchSelected) { Primary = true };
        pin = new("Auf Desktop", TogglePinned);
        diagnose = new("Diagnose", () => RunFromMenu(OpenTaskManager));
        reboot = new("Neu starten", () => RunPower("reboot"));
        shutdown = new("Ausschalten", () => RunPower("shutdown"));
        appList.GetMilliseconds = desktopIcons.GetMilliseconds = () => now;
        appList.GlyphWidth = desktopIcons.GlyphWidth = glyphWidth;
        appList.GlyphHeight = desktopIcons.GlyphHeight = glyphHeight;
        desktopIcons.Activated = () => { if (desktopIcons.SelectedIndex >= 0) shortcuts[desktopIcons.SelectedIndex].Launch(); };
        if (session != null)
        {
            this.session = session;
            login = new(session, painter, width, height);
            generation = session.Identity.Generation;
            applications = [.. applications, new("Abmelden", "Benutzersitzung beenden", AppIcon.Terminal, () => { login.Logout(); return true; })];
        }
        search.Submitted = LaunchSelected;
        appList.Activated = LaunchSelected;
        // Reihenfolge bestimmt den Tab-Fokus: Suche, Kacheln, Starten.
        foreach (var control in new UiControl[] { search, appList, launch, pin, diagnose, reboot, shutdown }) menu.Add(control);
        LayoutMenu();
        LayoutIcons();
        RefreshExternal();
    }

    public void Resize(int newWidth, int newHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(newWidth, 640);
        ArgumentOutOfRangeException.ThrowIfLessThan(newHeight, 400 + BarHeight);
        CloseMenu();
        width = newWidth;
        height = newHeight;
        Windows.Resize(width, height - BarHeight);
        login?.Resize(width, height);
        LayoutMenu();
        LayoutIcons();
        wallpaperRevision = -1;
        sceneDirty = true;
    }

    public bool OpenTerminal()
    {
        if (Windows.Count == MaximumWindows) return false;
        Windows.Reset();
        sceneDirty = true;
        int offset = sequence % 4 * 40;
        // Anteilige Groesse, damit kaskadierte Terminals auch bei 1024 x 768 versetzt sichtbar bleiben.
        int windowWidth = Math.Clamp(width * 3 / 4, 640, 1100);
        int windowHeight = Math.Clamp((height - BarHeight) * 3 / 5, 400, 600);
        var terminal = new TerminalWorkspaceApp(createShell, font, glyphWidth, glyphHeight);
        Windows.Add(new Window($"Terminal {++sequence}", 72 + offset, 72 + offset, windowWidth, windowHeight, terminal, 320, 200)
            { Icon = AppIcon.Terminal });
        return true;
    }

    public bool OpenFiles() => Windows.Count < MaximumWindows && OpenApplication("Dateien", AppIcon.Files,
        new FileManagerApp(createFiles(), painter, path => OpenEditor(path)), 640, 440, 1100, 700);
    public bool OpenEditor(string path = "") => Windows.Count < MaximumWindows && OpenApplication("Texteditor", AppIcon.Editor,
        new TextEditorApp(createFiles(), painter, path), 640, 440);
    public bool OpenPong() => Windows.Count < MaximumWindows && OpenApplication("Pong", AppIcon.Pong, new PongApp(painter),
        680, Math.Max(480, Window.TitleHeight + 34 + 2 * glyphHeight + PongGame.Height));
    // Installierte Paket-Apps: ein offenes Fenster derselben App wird nach vorn geholt statt eine zweite Instanz zu starten.
    private bool OpenExternal(ExternalApplication app)
    {
        for (int index = 0; index < Windows.Count; index++)
        {
            Window window = Windows.GetWindow(index);
            if (window.Title != app.Name) continue;
            if (window != Windows.ActiveWindow || window.IsMinimized) Windows.ToggleTask(window);
            sceneDirty = true;
            return true;
        }
        if (Windows.Count == MaximumWindows) return false;
        var created = app.Create(painter);
        if (created == null)
        {
            menuStatus = "Es laeuft bereits eine Paket-App; zuerst beenden.";
            return false;
        }
        return OpenApplication(app.Name, AppIcon.Pong, created, 640, 400 + Window.TitleHeight, 680, 440 + Window.TitleHeight);
    }
    public bool OpenSettings() => ActivateExisting<SettingsApp>() || Windows.Count < MaximumWindows && OpenApplication("Einstellungen",
        AppIcon.Settings, new SettingsApp(painter, Preferences, display, () => now), 640, 440, 1200, 820);
    public bool OpenTaskManager() => ActivateExisting<TaskManagerApp>() || Windows.Count < MaximumWindows && OpenApplication("Taskmanager",
        AppIcon.Tasks, new TaskManagerApp(painter, Windows, System, Metrics, systemSections), 640, 440, 1200, 860);

    private bool ActivateExisting<T>() where T : IApplication
    {
        for (int index = 0; index < Windows.Count; index++)
        {
            Window window = Windows.GetWindow(index);
            if (window.Application is not T) continue;
            if (window != Windows.ActiveWindow || window.IsMinimized) Windows.ToggleTask(window);
            sceneDirty = true;
            return true;
        }
        return false;
    }

    private bool OpenApplication(string title, AppIcon icon, IApplication application, int minimumWidth, int minimumHeight,
        int preferredWidth = 800, int preferredHeight = 560)
    {
        int area = height - BarHeight;
        if (Windows.Count == MaximumWindows || width < 800 || area < 560) return false;
        Windows.Reset();
        sceneDirty = true;
        int windowWidth = Math.Clamp(preferredWidth, 800, width - 16);
        int windowHeight = Math.Clamp(preferredHeight, 560, area - 16);
        int offset = sequence++ % 3 * 28;
        int x = preferredWidth > 800 ? (width - windowWidth) / 2 : 32 + offset;
        int y = preferredHeight > 560 ? (area - windowHeight) / 2 : 80 + offset;
        Windows.Add(new Window(title, x, y, windowWidth, windowHeight, application, minimumWidth, minimumHeight) { Icon = icon });
        return true;
    }

    public bool CloseMenu()
    {
        if (!menuOpen) return false;
        menuOpen = false;
        sceneDirty = true;
        menu.Handle(new(InputEventType.Reset));
        Windows.Reset();
        return true;
    }

    public bool HandleEscape(InputEvent value)
    {
        // Ein gehaltenes Esc darf nach der Zeigerfreigabe keinen zweiten Wechsel ausloesen.
        if (value.IsRepeat) return true;
        if (CloseMenu()) return true;
        if (!Windows.HasAppPointerCapture) return false;
        Windows.Handle(value);
        return true;
    }

    public void Suspend()
    {
        display?.Revert();
        Handle(new(InputEventType.Reset));
    }

    private void ToggleMenu()
    {
        if (CloseMenu()) return;
        Windows.Reset();
        menu.Handle(new(InputEventType.Reset));
        menu.Focus(search);
        menuStatus = "";
        menuOpen = true;
        RefreshExternal();
    }

    // Beim Oeffnen des Menues neu lesen, damit pkg install/remove ohne Neustart sichtbar wird.
    private void RefreshExternal()
    {
        var apps = listExternal?.Invoke() ?? [];
        external = new ApplicationEntry[apps.Length];
        for (int index = 0; index < apps.Length; index++)
        {
            var app = apps[index];
            external[index] = new(app.Name, $"Paket {app.Id} - Doppelklick/Enter", AppIcon.Pong, () => OpenExternal(app));
        }
        FilterApplications();
        RebuildShortcuts();
    }

    private ApplicationEntry? FindApplication(string name)
    {
        foreach (var app in applications) if (string.Equals(app.Name, name, StringComparison.OrdinalIgnoreCase)) return app;
        foreach (var app in external) if (string.Equals(app.Name, name, StringComparison.OrdinalIgnoreCase)) return app;
        return null;
    }

    private string[] ShortcutNames => Preferences.Settings.Shortcuts ?? DefaultShortcuts;

    // Nicht mehr installierte Paket-Apps bleiben gespeichert, erscheinen aber erst nach erneuter Installation wieder.
    private void RebuildShortcuts()
    {
        shortcuts.Clear();
        foreach (string name in ShortcutNames)
            if (FindApplication(name) is { } app && !shortcuts.Contains(app)) shortcuts.Add(app);
        var names = new string[shortcuts.Count];
        var icons = new AppIcon[shortcuts.Count];
        for (int index = 0; index < shortcuts.Count; index++) (names[index], icons[index]) = (shortcuts[index].Name, shortcuts[index].Icon);
        desktopIcons.SetItems(names);
        desktopIcons.Icons = icons;
        UpdatePinLabel();
        sceneDirty = true;
    }

    private bool IsPinned(string name)
    {
        foreach (string item in ShortcutNames) if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void UpdatePinLabel() =>
        pin.Label = appList.SelectedIndex >= 0 && IsPinned(matches[appList.SelectedIndex].Name) ? "Vom Desktop" : "Auf Desktop";

    private void TogglePinned()
    {
        if (appList.SelectedIndex < 0) return;
        string name = matches[appList.SelectedIndex].Name;
        var names = new List<string>(ShortcutNames);
        int existing = names.FindIndex(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) names.RemoveAt(existing);
        // Das Trennzeichen der Einstellungsdatei darf nicht im Namen stehen; mehr Symbole als Plaetze waeren unsichtbar.
        else if (name.Contains('|', StringComparison.Ordinal)) { menuStatus = "Dieser Name kann nicht auf den Desktop gelegt werden."; return; }
        else if (shortcuts.Count >= Math.Min(DesktopSettings.MaximumShortcuts, desktopIcons.Capacity)) { menuStatus = "Kein freier Platz fuer weitere Desktopsymbole."; return; }
        else names.Add(name);
        // Apply speichert sofort; ohne Laufwerk gilt die Auswahl nur fuer die Sitzung und der Grund wird angezeigt.
        var result = Preferences.Apply(Preferences.Settings with { Shortcuts = [.. names] });
        menuStatus = result.IsOK() ? "" : Preferences.Status;
        RebuildShortcuts();
    }

    public void Handle(InputEvent value)
    {
        // Reine Zeigerbewegung ohne Ziehen veraendert die Szene nicht; nur der Zeiger wird neu gesetzt.
        if (value.Type != InputEventType.PointerMove || Windows.HasPointerCapture) sceneDirty = true;
        HandleInput(value);
    }

    private void HandleInput(InputEvent value)
    {
        if (value.Type == InputEventType.Reset)
        {
            alt = control = leftShift = rightShift = suppressSpace = false;
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
            if (value.KeyCode == (byte)PhysicalKey.Control) control = down;
            if (alt && control && down && WorkspaceShortcut(value.KeyCode)) return;
            if (value.KeyCode == (byte)PhysicalKey.LeftShift) leftShift = down;
            if (value.KeyCode == (byte)PhysicalKey.RightShift) rightShift = down;
            if (alt && down && value.KeyCode == (byte)PhysicalKey.F1 && !value.IsRepeat) { ToggleMenu(); return; }
            if (alt && down && value.KeyCode == (byte)PhysicalKey.Tab)
            {
                CloseMenu();
                desktopIcons.Focused = false;
                Windows.Cycle(leftShift || rightShift);
                return;
            }
            if (down && value.KeyCode == (byte)PhysicalKey.Escape && CloseMenu()) return;
        }
        if ((alt && value.Type is InputEventType.KeyDown or InputEventType.Text) ||
            (suppressSpace && value.Type == InputEventType.Text && value.Character == ' ')) return;
        if (Windows.HasPointerCapture && value.Type is InputEventType.PointerMove or InputEventType.PointerDown or InputEventType.PointerUp or InputEventType.PointerScroll)
        {
            Windows.Handle(value);
            return;
        }
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            if (MenuButtonBounds.Contains(value.X, value.Y)) { ToggleMenu(); return; }
            for (int index = 0; index < pinned.Length; index++)
                if (PinnedBounds(index).Contains(value.X, value.Y)) { CloseMenu(); pinned[index].Launch(); return; }
            for (int index = 0; index < WindowManager.WorkspaceCount; index++)
                if (PagerBounds(index).Contains(value.X, value.Y)) { CloseMenu(); Windows.SwitchWorkspace(index); return; }
        }
        if (menuOpen)
        {
            if (value.Type == InputEventType.PointerDown && !menuBounds.Contains(value.X, value.Y)) { CloseMenu(); return; }
            string previous = search.Text;
            bool space = value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Space && menu.Focused == launch;
            launch.Enabled = pin.Enabled = matches.Count > 0;
            menu.Handle(value);
            if (space && !menuOpen) suppressSpace = true;
            if (!string.Equals(previous, search.Text, StringComparison.Ordinal)) FilterApplications();
            UpdatePinLabel();
            return;
        }
        if (value.Type is InputEventType.PointerDown or InputEventType.PointerUp or InputEventType.PointerMove && value.Y >= height - BarHeight)
        {
            if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
            {
                TaskWindows();
                for (int index = 0; index < tasks.Count; index++)
                    if (TaskBounds(index, tasks.Count).Contains(value.X, value.Y)) { Windows.ToggleTask(tasks[index]); break; }
            }
            return;
        }
        if (value.Type == InputEventType.PointerDown)
        {
            // Freie Desktopflaeche: Verknuepfungen waehlen und per Doppelklick starten.
            bool onIcon = value.Button == PointerButtons.Left && Windows.HitTest(value.X, value.Y) == null &&
                desktopIcons.IndexAt(value.X, value.Y) >= 0;
            desktopIcons.Focused = onIcon;
            if (onIcon) { Windows.Handle(value); desktopIcons.Handle(value); return; }
        }
        if (desktopIcons.Focused && Windows.ActiveWindow == null && value.Type == InputEventType.KeyDown) { desktopIcons.Handle(value); return; }
        Windows.Handle(value);
    }

    // Strg+Alt+Pfeil wechselt den Desktop, mit Umschalt nimmt es das aktive Fenster mit; Strg+Alt+1..4 waehlt direkt.
    private bool WorkspaceShortcut(byte key)
    {
        int current = Windows.CurrentWorkspace, count = WindowManager.WorkspaceCount;
        int target = key switch
        {
            (byte)PhysicalKey.Left => (current + count - 1) % count,
            (byte)PhysicalKey.Right => (current + 1) % count,
            >= 2 and <= 5 => key - 2,
            _ => -1
        };
        if (target < 0) return false;
        CloseMenu();
        if (leftShift || rightShift) Windows.MoveActiveToWorkspace(target);
        else Windows.SwitchWorkspace(target);
        return true;
    }

    // Fuellt die wiederverwendete Liste, damit das Zeichnen der Leiste nichts allokiert.
    private List<Window> TaskWindows()
    {
        tasks.Clear();
        for (int index = 0; index < Windows.Count; index++)
            if (Windows.GetWindow(index).Workspace == Windows.CurrentWorkspace) tasks.Add(Windows.GetWindow(index));
        return tasks;
    }

    private void LaunchSelected()
    {
        if (appList.SelectedIndex >= 0 && matches[appList.SelectedIndex].Launch()) CloseMenu();
    }

    private void RunFromMenu(Func<bool> action)
    {
        if (action()) CloseMenu();
    }

    private void RunPower(string command)
    {
        // Die Shellbefehle haengen das Datenlaufwerk vorher aus und brechen bei Fehlern ab.
        var output = new TerminalBuffer(120);
        var shell = createShell();
        try
        {
            var result = shell.ExecuteLine(command, output);
            menuStatus = result.IsOK() ? "Energieaktion wurde nicht ausgefuehrt." : result.Message;
        }
        finally { shell.Close(); }
    }

    private void FilterApplications()
    {
        matches.Clear();
        foreach (var app in applications)
            if (app.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)) matches.Add(app);
        foreach (var app in external)
            if (app.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)) matches.Add(app);
        var names = new string[matches.Count];
        var icons = new AppIcon[matches.Count];
        for (int index = 0; index < matches.Count; index++) (names[index], icons[index]) = (matches[index].Name, matches[index].Icon);
        appList.SetItems(names);
        appList.Icons = icons;
        launch.Enabled = pin.Enabled = matches.Count > 0;
        UpdatePinLabel();
    }

    private void LayoutMenu()
    {
        int menuWidth = Math.Min(640, width - 16);
        int menuHeight = Math.Min(720, height - BarHeight - 16);
        menuBounds = new(8, height - BarHeight - 8 - menuHeight, menuWidth, menuHeight);
        int left = menuBounds.X + 16, inner = menuWidth - 32, bottom = menuBounds.Y + menuHeight - 16;
        search.Bounds = new(left, menuBounds.Y + 16, inner, Math.Max(44, glyphHeight + 12));
        int third = (inner - 16) / 3;
        diagnose.Bounds = new(left, bottom - 48, third, 48);
        reboot.Bounds = new(left + third + 8, bottom - 48, third, 48);
        shutdown.Bounds = new(left + 2 * (third + 8), bottom - 48, inner - 2 * (third + 8), 48);
        int half = (inner - 8) / 2;
        launch.Bounds = new(left, bottom - 104, half, 48);
        pin.Bounds = new(left + half + 8, bottom - 104, inner - half - 8, 48);
        int listTop = AppsHeaderTop + glyphHeight + 8;
        appList.Bounds = new(left, listTop, inner, Math.Max(48, HintTop - 8 - listTop));
    }

    private void LayoutIcons() => desktopIcons.Bounds = new(8, 8, Math.Max(1, width - 16), Math.Max(1, height - BarHeight - 16));

    private int AppsHeaderTop => search.Bounds.Y + search.Bounds.Height + 12;
    private int HintTop => launch.Bounds.Y - 8 - glyphHeight;
    private int ClockWidth => 5 * glyphWidth + 32;
    private UiRect MenuButtonBounds => new(8, height - 48, 136, 40);
    private UiRect PinnedBounds(int index) => new(PinnedLeft + index * 48, height - 48, 44, 40);
    private UiRect PagerBounds(int index) =>
        new(width - ClockWidth - (WindowManager.WorkspaceCount - index) * PagerSlot - 8, height - 46, PagerSlot - 4, 36);
    private UiRect TaskBounds(int index, int count)
    {
        int taskWidth = Math.Min(260, (PagerBounds(0).X - 12 - TaskLeft) / Math.Max(1, count));
        return new(TaskLeft + index * taskWidth, height - 48, taskWidth - 4, 40);
    }

    public void Update(long milliseconds)
    {
        SyncSession();
        if (IsLocked)
        {
            // Nur abgeschlossene Kennwortpruefungen veraendern die Anzeige; Eingaben invalidieren bereits Handle.
            if (login!.Update()) sceneDirty = true;
            SyncSession();
        }
        if (!IsLocked && restoreDisplay)
        {
            restoreDisplay = false;
            var saved = Preferences.Settings;
            var mode = new DisplayMode(saved.Width, saved.Height);
            if (display != null && saved.DisplayConfirmed && mode != display.Current && Array.IndexOf(display.Modes, mode) >= 0)
            {
                display.Apply(mode, milliseconds);
                if (display.Pending) OpenSettings();
            }
        }
        // Apps aktualisieren Uhr, Cursor, Messwerte und Spiele zeitgesteuert; dann die Szene neu zeichnen.
        if (milliseconds / 500 != now / 500 || NeedsAnimation) sceneDirty = true;
        now = milliseconds;
        int count = Windows.Count;
        Windows.Update(milliseconds);
        if (count != Windows.Count) sceneDirty = true;
    }

    private bool IsLocked => session != null && session.Identity.UserId == null;

    // Auch ein Identitaetswechsel aus der Textshell beendet Fenster und eigene Terminalsitzungen.
    private void SyncSession()
    {
        if (session == null || session.Identity.Generation == generation) return;
        generation = session.Identity.Generation;
        CloseMenu();
        Windows.CloseAll();
        desktopIcons.Focused = false;
        TerminalClipboard.Clear();
        login!.Reset();
        display?.Revert();
        Preferences = new DesktopPreferences(createFiles());
        RebuildShortcuts();
        wallpaperRevision = -1;
        restoreDisplay = true;
        sceneDirty = true;
    }

    /// <summary>
    /// Zeichnet das Bild und liefert die Position des wiederhergestellten alten Zeigers, wenn sich ausser beiden
    /// Zeigerflaechen nichts geaendert hat; <see langword="null"/> bedeutet ein vollstaendig neu gezeichnetes Bild.
    /// </summary>
    public (int X, int Y)? Render(PixelSurface surface, long milliseconds, int pointerX, int pointerY, PointerButtons buttons)
    {
        SyncSession();
        (int X, int Y)? previousPointer = (pointerLeft, pointerTop);
        if (sceneDirty || surface != sceneSurface || scene.Length != width * height)
        {
            surface.ResetClip();
            if (IsLocked)
            {
                surface.FillRectangle(0, 0, width, height, UiPainter.Background);
                login!.Render(surface);
            }
            else
            {
                PrepareWallpaper();
                surface.DrawPixels(wallpaper, 0, 0, width, height);
                desktopIcons.Draw(surface, painter, 0, 0, Windows.ActiveWindow == null);
                Windows.Render(surface, font, glyphWidth, glyphHeight, !menuOpen);
                RenderTaskbar(surface, milliseconds);
                if (menuOpen) RenderMenu(surface);
            }
            if (scene.Length != width * height) scene = new int[width * height];
            surface.CopyTo(scene);
            sceneSurface = surface;
            sceneDirty = false;
            previousPointer = null;
        }
        else
        {
            // Nur den Bereich unter dem zuletzt gezeichneten Zeiger aus der unveraenderten Szene herstellen.
            surface.SetClip(pointerLeft, pointerTop, PointerWidth, PointerHeight);
            surface.DrawPixels(scene, 0, 0, width, height);
            surface.ResetClip();
        }
        pointerLeft = pointerX;
        pointerTop = pointerY;
        DrawPointer(surface, pointerX, pointerY, buttons);
        return previousPointer;
    }

    internal static void DrawPointer(PixelSurface surface, int pointerX, int pointerY, PointerButtons buttons)
    {
        int color = buttons == PointerButtons.None ? -1 : UiPainter.Accent;
        for (int row = 0; row < 15; row++) surface.FillRectangle(pointerX, pointerY + row, row / 2 + 1, 1, unchecked((int)0xff000000));
        surface.DrawLine(pointerX, pointerY, pointerX, pointerY + 14, color);
        surface.DrawLine(pointerX, pointerY, pointerX + 7, pointerY + 14, color);
        surface.DrawLine(pointerX, pointerY + 14, pointerX + 7, pointerY + 14, color);
    }

    // Der Hintergrund wird nur bei Aenderungen berechnet; pro Bild bleibt eine Pufferkopie.
    private void PrepareWallpaper()
    {
        if (wallpaperRevision == Preferences.Revision && wallpaper.Length == width * height) return;
        if (wallpaper.Length != width * height) wallpaper = new int[width * height];
        var settings = Preferences.Settings;
        if (settings.Wallpaper == DesktopSettings.CustomWallpaper && Preferences.CustomImage is { } image)
            image.Fit(wallpaper, width, height);
        else
            WallpaperImage.Create(wallpaper, width, height, Math.Min(settings.Wallpaper, 3), settings.Dark, UiPainter.Accent);
        wallpaperRevision = Preferences.Revision;
    }

    private void RenderTaskbar(PixelSurface surface, long milliseconds)
    {
        int top = height - BarHeight;
        // Abgerundete Schaltflaechen auf ruhiger Leiste, angelehnt an KDE Plasma und Windows 11.
        surface.FillRectangle(0, top, width, BarHeight, UiPainter.Panel);
        surface.FillRectangle(0, top, width, 1, UiPainter.Border);
        var menuButton = MenuButtonBounds;
        UiPainter.RoundRect(surface, menuButton.X, menuButton.Y, menuButton.Width, menuButton.Height, 10,
            menuOpen ? UiPainter.Selection : UiPainter.Muted);
        UiPainter.RoundRect(surface, menuButton.X + 6, menuButton.Y + 6, 28, 28, 8, UiPainter.Accent);
        AppIcons.Draw(surface, AppIcon.Menu, menuButton.X + 10, menuButton.Y + 10, UiPainter.AccentText);
        painter.Text(surface, "Start", menuButton.X + 44, menuButton.Y + (40 - glyphHeight) / 2, menuButton.Width - 50,
            menuOpen ? UiPainter.Accent : UiPainter.Foreground);
        for (int index = 0; index < pinned.Length; index++)
        {
            var bounds = PinnedBounds(index);
            UiPainter.RoundRect(surface, bounds.X, bounds.Y, bounds.Width, bounds.Height, 10, UiPainter.Muted);
            AppIcons.Draw(surface, pinned[index].Icon, bounds.X + 12, bounds.Y + 10, UiPainter.Foreground);
        }
        surface.FillRectangle(TaskLeft - 10, top + 16, 1, 24, UiPainter.Border);
        TaskWindows();
        for (int index = 0; index < tasks.Count; index++)
        {
            Window window = tasks[index];
            UiRect bounds = TaskBounds(index, tasks.Count);
            bool active = window == Windows.ActiveWindow;
            if (active || !window.IsMinimized)
                UiPainter.RoundRect(surface, bounds.X, bounds.Y, bounds.Width, bounds.Height, 10, active ? UiPainter.Selection : UiPainter.Muted);
            int textColor = window.IsMinimized ? UiPainter.Secondary : UiPainter.Foreground;
            int left = bounds.X + 10;
            if (window.Icon is AppIcon icon)
            {
                AppIcons.Draw(surface, icon, left, bounds.Y + 10, active ? UiPainter.Accent : textColor);
                left += AppIcons.Size + 10;
            }
            painter.Text(surface, window.Title, left, bounds.Y + (bounds.Height - glyphHeight) / 2, bounds.X + bounds.Width - 8 - left, textColor);
            // Aktiv: langer Akzentbalken; offen: kurzer Balken; minimiert: nur gedaempfte Schrift.
            if (active) UiPainter.RoundRect(surface, bounds.X + bounds.Width / 2 - 20, bounds.Y + bounds.Height - 4, 40, 4, 2, UiPainter.Accent);
            else if (!window.IsMinimized) UiPainter.RoundRect(surface, bounds.X + bounds.Width / 2 - 8, bounds.Y + bounds.Height - 4, 16, 4, 2, UiPainter.Secondary);
        }
        for (int index = 0; index < WindowManager.WorkspaceCount; index++)
        {
            var bounds = PagerBounds(index);
            bool current = index == Windows.CurrentWorkspace;
            UiPainter.RoundRect(surface, bounds.X, bounds.Y, bounds.Width, bounds.Height, 8, current ? UiPainter.Accent : UiPainter.Muted);
            painter.Text(surface, "1234".AsSpan(index, 1), bounds.X + (bounds.Width - glyphWidth) / 2,
                bounds.Y + (bounds.Height - glyphHeight) / 2, glyphWidth, current ? UiPainter.AccentText : UiPainter.Secondary);
            // Punkt: auf diesem Desktop liegen Fenster.
            if (!current && Windows.CountOn(index) > 0) surface.FillRectangle(bounds.X + bounds.Width / 2 - 2, bounds.Y + bounds.Height - 5, 4, 3, UiPainter.Accent);
        }
        long minute = milliseconds / 60000;
        if (minute != clockMinute)
        {
            clockMinute = minute;
            clock = $"{minute / 60 % 100:00}:{minute % 60:00}";
        }
        painter.Text(surface, clock, width - 16 - clock.Length * glyphWidth, top + (BarHeight - glyphHeight) / 2, width);
    }

    private void RenderMenu(PixelSurface surface)
    {
        // Abgerundetes Panel mit Versatzschatten; Apps als Kacheln wie im Windows-11-Startmenue.
        UiPainter.RoundRect(surface, menuBounds.X + 4, menuBounds.Y + 8, menuBounds.Width, menuBounds.Height, 14, unchecked((int)0xff0c0f14));
        UiPainter.RoundFrame(surface, menuBounds.X, menuBounds.Y, menuBounds.Width, menuBounds.Height, 14, 1, UiPainter.Border, UiPainter.Background);
        int footer = launch.Bounds.Y - 12;
        UiPainter.RoundRect(surface, menuBounds.X + 1, footer, menuBounds.Width - 2, menuBounds.Y + menuBounds.Height - 1 - footer, 13, UiPainter.Panel);
        surface.FillRectangle(menuBounds.X + 1, footer, menuBounds.Width - 2, 14, UiPainter.Panel);
        surface.FillRectangle(menuBounds.X + 1, footer, menuBounds.Width - 2, 1, UiPainter.Border);
        int left = menuBounds.X + 16, inner = menuBounds.Width - 32;
        painter.Text(surface, search.Text.Length == 0 ? "Alle Apps" : "Suchergebnisse", left + 4, AppsHeaderTop, inner, UiPainter.Secondary);
        menu.Draw(surface, painter, 0, 0, true);
        if (matches.Count == 0) painter.Text(surface, "Keine Programme gefunden", left + 12, appList.Bounds.Y + 12, inner - 24, UiPainter.Secondary);
        if (menuStatus.Length > 0) painter.Text(surface, menuStatus, left, HintTop, inner, UiPainter.Danger);
        else if (appList.SelectedIndex >= 0)
            painter.Text(surface, matches[appList.SelectedIndex].Description, left, HintTop, inner, UiPainter.Secondary);
    }
}
