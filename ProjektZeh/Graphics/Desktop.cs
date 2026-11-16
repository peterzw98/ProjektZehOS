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
using ProjektZeh.Network;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.SystemServices;
using ProjektZeh.UI;

namespace ProjektZeh.Graphics;

internal sealed class Desktop
{
    public const int MaximumWindows = 8;
    public const int BarHeight = 56;
    // Taskleiste wie Windows 11: Start, angeheftete und offene Apps als gleich breite Symbolfelder.
    private const int Slot = 48;
    private const int PagerSlot = 20;
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
    private readonly UiButton power;
    private readonly (string Label, Action Run)[] powerItems;
    // Kleines Auswahlmenue ueber dem Startmenue: Ein/Aus-Aktionen oder Kontextmenue einer Kachel.
    private (string Label, Action Run)[] popup = [];
    private UiRect popupBounds;
    private int popupIndex;
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
    private long clockNextRead = long.MinValue;
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
    private readonly Func<NetworkStatus>? getNetwork;
    private NetworkStatus networkStatus;
    private string? networkAddress;
    private long networkSample = -1;
    private bool networkFlyoutOpen;
    public WindowManager Windows { get; }
    public DesktopMetrics Metrics { get; } = new();
    public SystemMetrics System { get; } = new();
    public DesktopPreferences Preferences { get; private set; }
    private bool restoreDisplay = true;
    public bool NeedsAnimation => !menuOpen && Windows.ActiveWindow?.Application.NeedsAnimation == true;

    private sealed record ApplicationEntry(string Name, AppIcon Icon, Func<bool> Launch);

    public Desktop(int width, int height, byte[] font, int glyphWidth, int glyphHeight,
        Func<ShellService> createShell, Func<FileService> createFiles,
        DisplaySettings? display = null, TaskManagerSection[]? systemSections = null, DesktopSession? session = null,
        Func<ExternalApplication[]>? listExternal = null, Func<NetworkStatus>? getNetwork = null)
    {
        this.getNetwork = getNetwork;
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
            new("Terminal", AppIcon.Terminal, OpenTerminal),
            new("Dateien", AppIcon.Files, OpenFiles),
            new("Texteditor", AppIcon.Editor, () => OpenEditor()),
            new("Einstellungen", AppIcon.Settings, OpenSettings),
            new("Taskmanager", AppIcon.Tasks, OpenTaskManager),
            new("Pong", AppIcon.Pong, OpenPong),
        ];
        pinned = [applications[1], applications[0], applications[3]];
        search = new(64, painter: painter);
        power = new("Ein/Aus", OpenPowerMenu) { Icon = AppIcon.Power };
        appList.GetMilliseconds = desktopIcons.GetMilliseconds = () => now;
        appList.GlyphWidth = desktopIcons.GlyphWidth = glyphWidth;
        appList.GlyphHeight = desktopIcons.GlyphHeight = glyphHeight;
        desktopIcons.Activated = () => { if (desktopIcons.SelectedIndex >= 0) shortcuts[desktopIcons.SelectedIndex].Launch(); };
        if (session != null)
        {
            this.session = session;
            login = new(session, painter, width, height);
            generation = session.Identity.Generation;
        }
        // Energieaktionen wie in Windows 11 gesammelt hinter dem Ein/Aus-Symbol; Abmelden nur mit Benutzersitzung.
        powerItems = login == null
            ? [("Neu starten", () => RunPower("reboot")), ("Herunterfahren", () => RunPower("shutdown"))]
            : [("Abmelden", () => { CloseMenu(); login.Logout(); }), ("Neu starten", () => RunPower("reboot")),
                ("Herunterfahren", () => RunPower("shutdown"))];
        search.Submitted = LaunchSelected;
        appList.Activated = LaunchSelected;
        // Reihenfolge bestimmt den Tab-Fokus: Suche, Kacheln, Ein/Aus.
        foreach (var control in new UiControl[] { search, appList, power }) menu.Add(control);
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
        popup = [];
        sceneDirty = true;
        menu.Handle(new(InputEventType.Reset));
        Windows.Reset();
        return true;
    }

    public bool HandleEscape(InputEvent value)
    {
        // Ein gehaltenes Esc darf nach der Zeigerfreigabe keinen zweiten Wechsel ausloesen.
        if (value.IsRepeat) return true;
        if (CloseNetworkFlyout()) return true;
        if (ClosePopup() || CloseMenu()) return true;
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
        CloseNetworkFlyout();
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
            external[index] = new(app.Name, AppIcon.Pong, () => OpenExternal(app));
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
        sceneDirty = true;
    }

    private bool IsPinned(string name)
    {
        foreach (string item in ShortcutNames) if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Kontextmenue der gewaehlten Kachel (Rechtsklick oder Umschalt+F10) ersetzt die fruehere Schaltflaeche.
    private void OpenTileMenu(int x, int y)
    {
        if (appList.SelectedIndex < 0) return;
        string label = IsPinned(matches[appList.SelectedIndex].Name) ? "Vom Desktop entfernen" : "Auf Desktop legen";
        OpenPopup([(label, TogglePinned)], x, y);
    }

    private void OpenPowerMenu()
    {
        menuStatus = "";
        var rows = new UiRect(0, 0, PopupWidth(powerItems), powerItems.Length * PopupRowHeight + 8);
        OpenPopup(powerItems, power.Bounds.X + power.Bounds.Width - rows.Width, power.Bounds.Y - rows.Height - 8);
    }

    private int PopupRowHeight => glyphHeight + 20;

    private int PopupWidth((string Label, Action Run)[] items)
    {
        int longest = 0;
        foreach (var item in items) longest = Math.Max(longest, item.Label.Length);
        return Math.Max(200, longest * glyphWidth + 40);
    }

    private void OpenPopup((string Label, Action Run)[] items, int x, int y)
    {
        int popupWidth = PopupWidth(items), popupHeight = items.Length * PopupRowHeight + 8;
        popupBounds = new(Math.Clamp(x, 0, Math.Max(0, width - popupWidth)), Math.Clamp(y, 0, Math.Max(0, height - BarHeight - popupHeight)),
            popupWidth, popupHeight);
        popup = items;
        popupIndex = 0;
        sceneDirty = true;
    }

    private bool ClosePopup()
    {
        if (popup.Length == 0) return false;
        popup = [];
        sceneDirty = true;
        return true;
    }

    private void HandlePopup(InputEvent value)
    {
        if (value.Type == InputEventType.KeyDown)
        {
            if (value.KeyCode == (byte)PhysicalKey.Up) popupIndex = (popupIndex + popup.Length - 1) % popup.Length;
            else if (value.KeyCode == (byte)PhysicalKey.Down) popupIndex = (popupIndex + 1) % popup.Length;
            // Nur Enter: Leertaste liefert danach noch ein Textzeichen, das nach dem Abmelden im Anmeldefeld landen wuerde.
            else if (value.KeyCode == (byte)PhysicalKey.Enter && !value.IsRepeat) RunPopup(popupIndex);
            return;
        }
        if (value.Type != InputEventType.PointerDown) return;
        if (value.Button == PointerButtons.Left && popupBounds.Contains(value.X, value.Y))
        {
            // Der 4-px-Rand oben gehoert zu keiner Zeile; ohne Pruefung runde die Division ihn auf Zeile 0.
            int offset = value.Y - popupBounds.Y - 4;
            if (offset >= 0 && offset / PopupRowHeight < popup.Length) RunPopup(offset / PopupRowHeight);
            return;
        }
        ClosePopup();
    }

    private void RunPopup(int index)
    {
        var action = popup[index].Run;
        ClosePopup();
        action();
    }

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
            alt = control = leftShift = rightShift = false;
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
            if (down && value.KeyCode == (byte)PhysicalKey.Escape && (ClosePopup() || CloseMenu())) return;
        }
        if (alt && value.Type is InputEventType.KeyDown or InputEventType.Text) return;
        if (Windows.HasPointerCapture && value.Type is InputEventType.PointerMove or InputEventType.PointerDown or InputEventType.PointerUp or InputEventType.PointerScroll)
        {
            Windows.Handle(value);
            return;
        }
        if (value.Type == InputEventType.PointerDown)
        {
            if (getNetwork != null && value.Button == PointerButtons.Left && TrayBounds.Contains(value.X, value.Y))
            {
                CloseMenu();
                networkFlyoutOpen = !networkFlyoutOpen;
                sceneDirty = true;
                return;
            }
            CloseNetworkFlyout();
        }
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            TaskWindows();
            if (MenuButtonBounds.Contains(value.X, value.Y)) { ToggleMenu(); return; }
            for (int index = 0; index < pinned.Length; index++)
                if (PinnedBounds(index).Contains(value.X, value.Y)) { CloseMenu(); pinned[index].Launch(); return; }
            for (int index = 0; index < WindowManager.WorkspaceCount; index++)
                if (PagerBounds(index).Contains(value.X, value.Y)) { CloseMenu(); Windows.SwitchWorkspace(index); return; }
        }
        if (menuOpen)
        {
            if (popup.Length > 0) { HandlePopup(value); return; }
            if (value.Type == InputEventType.PointerDown && !menuBounds.Contains(value.X, value.Y)) { CloseMenu(); return; }
            if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Right)
            {
                int index = appList.IndexAt(value.X, value.Y);
                if (index >= 0) { menu.Focus(appList); appList.Select(index); OpenTileMenu(value.X, value.Y); }
                return;
            }
            if (value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.F10 && (leftShift || rightShift) &&
                menu.Focused == appList)
            {
                OpenTileMenu(appList.Bounds.X + appList.Bounds.Width / 2, appList.Bounds.Y + appList.Bounds.Height / 3);
                return;
            }
            string previous = search.Text;
            menu.Handle(value);
            if (!string.Equals(previous, search.Text, StringComparison.Ordinal)) FilterApplications();
            return;
        }
        if (value.Type is InputEventType.PointerDown or InputEventType.PointerUp or InputEventType.PointerMove && value.Y >= height - BarHeight)
        {
            if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
            {
                for (int index = 0; index < tasks.Count; index++)
                    if (TaskBounds(index).Contains(value.X, value.Y)) { Windows.ToggleTask(tasks[index]); break; }
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
        catch (Exception error)
        {
            menuStatus = "Energieaktion fehlgeschlagen: " + error.Message;
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
    }

    private const int FooterHeight = 64;

    private void LayoutMenu()
    {
        // Mittig ueber der Taskleiste wie in Windows 11; unten nur Benutzer und Ein/Aus.
        int menuWidth = Math.Min(600, width - 16);
        int menuHeight = Math.Min(640, height - BarHeight - 16);
        menuBounds = new((width - menuWidth) / 2, height - BarHeight - 8 - menuHeight, menuWidth, menuHeight);
        int left = menuBounds.X + 24, inner = menuWidth - 48, footerTop = menuBounds.Y + menuHeight - FooterHeight;
        search.Bounds = new(left, menuBounds.Y + 24, inner, Math.Max(40, glyphHeight + 12));
        power.Bounds = new(menuBounds.X + menuWidth - 24 - 44, footerTop + (FooterHeight - 44) / 2, 44, 44);
        int listTop = AppsHeaderTop + glyphHeight + 8;
        appList.Bounds = new(left, listTop, inner, Math.Max(48, HintTop - 8 - listTop));
    }

    private void LayoutIcons() => desktopIcons.Bounds = new(8, 8, Math.Max(1, width - 16), Math.Max(1, height - BarHeight - 16));

    private int AppsHeaderTop => search.Bounds.Y + search.Bounds.Height + 20;
    private int HintTop => menuBounds.Y + menuBounds.Height - FooterHeight - 12 - glyphHeight;
    private int ClockWidth => 5 * glyphWidth + 32;
    private int TrayWidth => getNetwork == null ? 0 : 40;
    internal UiRect TrayBounds => new(width - ClockWidth - TrayWidth, height - 48, TrayWidth - 4, 40);
    private int FixedSlots => (1 + pinned.Length) * Slot;
    // Fensterfelder werden schmaler, bevor die Gruppe Arbeitsflaechen, Netz und Uhr erreicht (640 px, acht Terminals).
    private int TaskSlot
    {
        get
        {
            int open = Windows.CountOn(Windows.CurrentWorkspace);
            return open == 0 ? Slot : Math.Clamp((PagerBounds(0).X - 16 - FixedSlots) / open, AppIcons.Size + 6, Slot);
        }
    }
    // Mittig wie in Windows 11, aber nie in Arbeitsflaechen, Netz und Uhr hinein.
    private int GroupLeft
    {
        get
        {
            int groupWidth = FixedSlots + Windows.CountOn(Windows.CurrentWorkspace) * TaskSlot - 4;
            return Math.Max(8, Math.Min((width - groupWidth) / 2, PagerBounds(0).X - 8 - groupWidth));
        }
    }
    internal UiRect MenuButtonBounds => new(GroupLeft, height - 48, Slot - 4, 40);
    internal UiRect PinnedBounds(int index) => new(GroupLeft + (1 + index) * Slot, height - 48, Slot - 4, 40);
    internal UiRect TaskBounds(int index) => new(GroupLeft + FixedSlots + index * TaskSlot, height - 48, TaskSlot - 4, 40);
    internal UiRect PagerBounds(int index) =>
        new(width - ClockWidth - TrayWidth - (WindowManager.WorkspaceCount - index) * PagerSlot - 8, height - 46, PagerSlot, 36);

    public void Update(long milliseconds)
    {
        if (getNetwork != null && milliseconds / 500 != networkSample)
        {
            networkSample = milliseconds / 500;
            var status = getNetwork();
            if (status != networkStatus)
            {
                networkStatus = status;
                networkAddress = status.Address == null ? null : "IPv4 " + status.Address;
            }
            sceneDirty = true;
        }
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
        CloseNetworkFlyout();
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
                if (networkFlyoutOpen) RenderNetworkFlyout(surface);
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
        TaskWindows();
        // Nur Symbole ohne Flaechen; hinterlegt sind allein das offene Menue und das aktive Fenster.
        var menuButton = MenuButtonBounds;
        if (menuOpen) UiPainter.RoundRect(surface, menuButton.X, menuButton.Y, menuButton.Width, menuButton.Height, 8, UiPainter.Muted);
        AppIcons.Draw(surface, AppIcon.Menu, menuButton.X + (menuButton.Width - AppIcons.Size) / 2, menuButton.Y + 10, UiPainter.Accent);
        for (int index = 0; index < pinned.Length; index++)
        {
            var bounds = PinnedBounds(index);
            AppIcons.Draw(surface, pinned[index].Icon, bounds.X + (bounds.Width - AppIcons.Size) / 2, bounds.Y + 10, UiPainter.Foreground);
        }
        for (int index = 0; index < tasks.Count; index++)
        {
            Window window = tasks[index];
            UiRect bounds = TaskBounds(index);
            bool active = window == Windows.ActiveWindow;
            if (active) UiPainter.RoundRect(surface, bounds.X, bounds.Y, bounds.Width, bounds.Height, 8, UiPainter.Muted);
            AppIcons.Draw(surface, window.Icon ?? AppIcon.Terminal, bounds.X + (bounds.Width - AppIcons.Size) / 2, bounds.Y + 9,
                window.IsMinimized ? UiPainter.Secondary : UiPainter.Foreground);
            // Aktiv: langer Akzentbalken; offen: kurzer Balken; minimiert: kein Balken, gedaempftes Symbol.
            if (active) UiPainter.RoundRect(surface, bounds.X + bounds.Width / 2 - 10, bounds.Y + bounds.Height - 4, 20, 3, 1, UiPainter.Accent);
            else if (!window.IsMinimized) UiPainter.RoundRect(surface, bounds.X + bounds.Width / 2 - 4, bounds.Y + bounds.Height - 4, 8, 3, 1, UiPainter.Secondary);
        }
        // Arbeitsflaechen als Punkte: aktuelle als Akzentpille, belegte hell, leere gedaempft.
        for (int index = 0; index < WindowManager.WorkspaceCount; index++)
        {
            var bounds = PagerBounds(index);
            bool current = index == Windows.CurrentWorkspace;
            int dotWidth = current ? 16 : 8, middle = bounds.Y + bounds.Height / 2;
            UiPainter.RoundRect(surface, bounds.X + (bounds.Width - dotWidth) / 2, middle - 4, dotWidth, 8, 4,
                current ? UiPainter.Accent : Windows.CountOn(index) > 0 ? UiPainter.Secondary : UiPainter.Border);
        }
        if (getNetwork != null)
        {
            var bounds = TrayBounds;
            if (networkFlyoutOpen) UiPainter.RoundRect(surface, bounds.X, bounds.Y, bounds.Width, bounds.Height, 8, UiPainter.Muted);
            int x = bounds.X + 8, y = bounds.Y + 10;
            bool connected = networkStatus.Link && networkStatus.Address != null;
            AppIcons.Draw(surface, AppIcon.Network, x, y, connected ? UiPainter.Foreground : UiPainter.Secondary);
            if (!networkStatus.Adapter || !networkStatus.Link)
            {
                surface.DrawLine(x + 12, y + 12, x + 19, y + 19, UiPainter.Secondary);
                surface.DrawLine(x + 19, y + 12, x + 12, y + 19, UiPainter.Secondary);
            }
            else if (!connected && milliseconds / 500 % 2 == 0)
                surface.FillRectangle(x + 16, y + 16, 4, 4, UiPainter.Accent);
        }
        // Die CMOS-Uhr nur zum erwarteten Minutenwechsel lesen, nicht in jedem Bild.
        if (milliseconds >= clockNextRead)
        {
            DateTime utc = DateTime.UtcNow;
            clockNextRead = milliseconds + (60 - utc.Second) * 1000L - utc.Millisecond;
            DateTime local = LocalTime.FromUtc(utc).Time;
            clock = $"{local.Hour:00}:{local.Minute:00}";
        }
        painter.Text(surface, clock, width - 16 - clock.Length * glyphWidth, top + (BarHeight - glyphHeight) / 2, width);
    }

    private void RenderMenu(PixelSurface surface)
    {
        // Abgerundetes Panel mit Versatzschatten; Apps als Kacheln wie im Windows-11-Startmenue.
        UiPainter.RoundRect(surface, menuBounds.X + 4, menuBounds.Y + 8, menuBounds.Width, menuBounds.Height, 14, unchecked((int)0xff0c0f14));
        UiPainter.RoundFrame(surface, menuBounds.X, menuBounds.Y, menuBounds.Width, menuBounds.Height, 14, 1, UiPainter.Border, UiPainter.Background);
        int footer = menuBounds.Y + menuBounds.Height - FooterHeight;
        UiPainter.RoundRect(surface, menuBounds.X + 1, footer, menuBounds.Width - 2, FooterHeight - 1, 13, UiPainter.Panel);
        surface.FillRectangle(menuBounds.X + 1, footer, menuBounds.Width - 2, 14, UiPainter.Panel);
        surface.FillRectangle(menuBounds.X + 1, footer, menuBounds.Width - 2, 1, UiPainter.Border);
        int left = menuBounds.X + 24, inner = menuBounds.Width - 48;
        painter.Text(surface, search.Text.Length == 0 ? "Alle Apps" : "Suchergebnisse", left + 4, AppsHeaderTop, inner, UiPainter.Foreground);
        // Benutzer links in der Fusszeile: Initiale im Kreis und Name.
        string user = session?.Identity.UserName ?? "Benutzer";
        int badgeTop = footer + (FooterHeight - 32) / 2;
        UiPainter.RoundRect(surface, left, badgeTop, 32, 32, 16, UiPainter.Muted);
        painter.Text(surface, user.AsSpan(0, Math.Min(1, user.Length)), left + (32 - glyphWidth) / 2, badgeTop + (32 - glyphHeight) / 2, glyphWidth);
        painter.Text(surface, user, left + 44, footer + (FooterHeight - glyphHeight) / 2, power.Bounds.X - left - 56);
        menu.Draw(surface, painter, 0, 0, popup.Length == 0);
        if (matches.Count == 0) painter.Text(surface, "Keine Programme gefunden", left + 12, appList.Bounds.Y + 12, inner - 24, UiPainter.Secondary);
        if (menuStatus.Length > 0) painter.Text(surface, menuStatus, left, HintTop, inner, UiPainter.Danger);
        else if (matches.Count > 0) painter.Text(surface, "Rechtsklick: auf Desktop legen", left, HintTop, inner, UiPainter.Secondary);
        if (popup.Length > 0) RenderPopup(surface);
    }

    private void RenderPopup(PixelSurface surface)
    {
        var b = popupBounds;
        UiPainter.RoundRect(surface, b.X + 3, b.Y + 6, b.Width, b.Height, 10, unchecked((int)0xff0c0f14));
        UiPainter.RoundFrame(surface, b.X, b.Y, b.Width, b.Height, 10, 1, UiPainter.Border, UiPainter.Background);
        for (int index = 0; index < popup.Length; index++)
        {
            int rowTop = b.Y + 4 + index * PopupRowHeight;
            if (index == popupIndex) UiPainter.RoundRect(surface, b.X + 4, rowTop, b.Width - 8, PopupRowHeight, 6, UiPainter.Selection);
            painter.Text(surface, popup[index].Label, b.X + 16, rowTop + (PopupRowHeight - glyphHeight) / 2, b.Width - 32);
        }
    }

    private bool CloseNetworkFlyout()
    {
        if (!networkFlyoutOpen) return false;
        networkFlyoutOpen = false;
        sceneDirty = true;
        return true;
    }

    private void RenderNetworkFlyout(PixelSurface surface)
    {
        int x = Math.Max(8, TrayBounds.X + TrayBounds.Width - 320), y = height - BarHeight - 120;
        UiPainter.RoundFrame(surface, x, y, 320, 112, 12, 1, UiPainter.Border, UiPainter.Background);
        painter.Text(surface, "Netzwerk", x + 16, y + 16, 288, UiPainter.Foreground);
        painter.Text(surface, networkStatus.Text, x + 16, y + 40, 288, UiPainter.Secondary);
        if (networkAddress != null) painter.Text(surface, networkAddress, x + 16, y + 64, 288, UiPainter.Foreground);
    }
}
