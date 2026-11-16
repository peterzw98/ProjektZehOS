using System;
using System.Collections.Generic;
using System.IO;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;
using ProjektZeh.UI;

namespace ProjektZeh.Apps.Terminal;

/// <summary>Verwaltet getrennte Sitzungen; Fensterwechsel und Recovery lassen deren Zustand bestehen.</summary>
internal sealed class TerminalWorkspaceApp : IApplication
{
    public const int MaximumTabs = 8;
    public const int MaximumViews = 4;
    private int TabsHeight => glyphHeight + 12;
    private int StatusHeight => glyphHeight + 8;
    private int SearchHeight => glyphHeight + 12;
    private static readonly string[] ProfileNames = ["Catppuccin", "Tokyo Night", "Breeze Dunkel", "Breeze Hell", "Solarisiert"];
    // Hintergrund, Schrift, Akzent je Profil; die Fensterflaechen leiten sich daraus ab.
    private static readonly uint[] ProfileColors =
    [
        0xff1e1e2e, 0xffcdd6f4, 0xff89b4fa,
        0xff1a1b26, 0xffc0caf5, 0xff7aa2f7,
        0xff1b1e20, 0xffeff0f1, 0xff3daee9,
        0xfffafafa, 0xff20252b, 0xff1377a5,
        0xff002b36, 0xff93a1a1, 0xffb58900,
    ];
    // Abstaende zwischen Ansichten und zum Rand sowie Rahmenstaerke wie bei gekachelten Wayland-Desktops.
    private const int Gap = 8;
    private const int PaneInset = 3;
    private static readonly string[] MenuItems = ["Neuer Tab          Ctrl+Shift+T", "Nebeneinander      Ctrl+Shift+E",
        "Uebereinander      Ctrl+Shift+H", "Naechste Ansicht   Ctrl+Shift+N", "Ansicht schliessen Ctrl+Shift+W",
        "Kopieren           Ctrl+Shift+C", "Einfuegen          Ctrl+Shift+V", "Alles auswaehlen", "Ausgabe suchen     Ctrl+Shift+F",
        "Profil wechseln", "Schrift groesser   Ctrl++", "Schrift kleiner    Ctrl+-", "Schrift normal     Ctrl+0"];
    private readonly Func<ShellService> createShell;
    private readonly byte[] font;
    private readonly int glyphWidth;
    private readonly int glyphHeight;
    private readonly UiPainter painter;
    private readonly List<Tab> tabs = [];
    private readonly TextBuffer search = new(128);
    private int width = 640;
    private int height = 400;
    private bool started;
    private bool closed;
    private bool control;
    private bool leftShift;
    private bool rightShift;
    private bool searchOpen;
    private bool menuOpen;
    private int menuX;
    private int menuY;
    private int menuIndex;
    private int menuFirst;
    private Node? capture;
    private string status = "";

    private sealed class Node(TerminalApp? terminal)
    {
        internal TerminalApp? Terminal = terminal;
        internal Node? First;
        internal Node? Second;
        internal bool Vertical;
        internal UiRect Bounds;
    }

    private sealed class Tab(Node root)
    {
        internal Node Root = root;
        internal Node Active = root;
        internal readonly List<Node> Leaves = [root];
    }

    public TerminalWorkspaceApp(Func<ShellService> createShell, byte[] font, int glyphWidth, int glyphHeight)
    {
        ArgumentNullException.ThrowIfNull(createShell);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphHeight);
        this.createShell = createShell;
        this.font = font;
        this.glyphWidth = glyphWidth;
        this.glyphHeight = glyphHeight;
        painter = new(font, glyphWidth, glyphHeight);
    }

    public int TabCount => tabs.Count;
    public int ActiveTabIndex { get; private set; }
    public int ViewCount => tabs.Count == 0 ? 0 : tabs[ActiveTabIndex].Leaves.Count;
    public TerminalApp ActiveTerminal => tabs[ActiveTabIndex].Active.Terminal!;
    public int Profile { get; private set; }
    public bool IsCloseRequested => closed;

    public void Start()
    {
        if (started || closed) return;
        started = true;
        NewTab();
    }

    private Node CreateView()
    {
        var terminal = new TerminalApp(createShell(), font, glyphWidth, glyphHeight, Math.Max(1, width / glyphWidth));
        ApplyProfile(terminal);
        terminal.Start();
        return new(terminal);
    }

    public bool NewTab()
    {
        if (closed || tabs.Count == MaximumTabs) { status = "Maximal acht Tabs pro Fenster."; return false; }
        ResetViews();
        tabs.Add(new(CreateView()));
        ActiveTabIndex = tabs.Count - 1;
        searchOpen = menuOpen = false;
        Layout();
        return true;
    }

    public void SelectTab(int index)
    {
        if ((uint)index >= (uint)tabs.Count || index == ActiveTabIndex) return;
        ResetViews();
        ActiveTabIndex = index;
        searchOpen = menuOpen = false;
        Layout();
    }

    /// <param name="vertical">Eine senkrechte Trennlinie erzeugt zwei nebeneinanderliegende Ansichten.</param>
    public bool Split(bool vertical)
    {
        if (closed || tabs.Count == 0) return false;
        var tab = tabs[ActiveTabIndex];
        var leaf = tab.Active;
        if (tab.Leaves.Count >= MaximumViews ||
            (vertical ? leaf.Bounds.Width < 324 : leaf.Bounds.Height < 196))
        {
            status = "Zu wenig Platz oder bereits vier Ansichten. Fenster vergroessern.";
            return false;
        }
        leaf.Terminal!.Handle(new(InputEventType.Reset));
        leaf.First = new(leaf.Terminal);
        leaf.Second = CreateView();
        leaf.Terminal = null;
        leaf.Vertical = vertical;
        tab.Active = leaf.Second;
        RebuildLeaves(tab);
        Layout();
        return true;
    }

    public void FocusNextPane()
    {
        if (tabs.Count == 0) return;
        var tab = tabs[ActiveTabIndex];
        tab.Active.Terminal!.Handle(new(InputEventType.Reset));
        tab.Active = tab.Leaves[(tab.Leaves.IndexOf(tab.Active) + 1) % tab.Leaves.Count];
        capture = null;
    }

    public void CloseActiveView()
    {
        if (tabs.Count == 0) return;
        var tab = tabs[ActiveTabIndex];
        tab.Active.Terminal!.Close();
        if (tab.Leaves.Count == 1)
        {
            tabs.RemoveAt(ActiveTabIndex);
            ActiveTabIndex = Math.Max(0, Math.Min(ActiveTabIndex, tabs.Count - 1));
            if (tabs.Count == 0) closed = true;
        }
        else
        {
            tab.Root = Remove(tab.Root, tab.Active)!;
            RebuildLeaves(tab);
            tab.Active = tab.Leaves[0];
        }
        capture = null;
        searchOpen = menuOpen = false;
        ResetViews();
        Layout();
    }

    private static Node? Remove(Node node, Node target)
    {
        if (node == target) return null;
        if (node.Terminal != null) return node;
        node.First = Remove(node.First!, target);
        node.Second = Remove(node.Second!, target);
        return node.First == null ? node.Second : node.Second == null ? node.First : node;
    }

    private static void RebuildLeaves(Tab tab)
    {
        tab.Leaves.Clear();
        Add(tab.Root);
        void Add(Node node)
        {
            if (node.Terminal != null) tab.Leaves.Add(node);
            else { Add(node.First!); Add(node.Second!); }
        }
    }

    public void Resize(int newWidth, int newHeight)
    {
        width = Math.Max(1, newWidth);
        height = Math.Max(1, newHeight);
        capture = null;
        Layout();
    }

    private void Layout()
    {
        int top = TabsHeight + (searchOpen ? SearchHeight : 0);
        foreach (var tab in tabs) Place(tab.Root, new(Gap, top + Gap / 2, Math.Max(1, width - 2 * Gap), Math.Max(1, height - top - StatusHeight - Gap)));
    }

    private static void Place(Node node, UiRect bounds)
    {
        node.Bounds = bounds;
        if (node.Terminal != null)
        {
            node.Terminal.Resize(Math.Max(1, bounds.Width - 2 * PaneInset), Math.Max(1, bounds.Height - 2 * PaneInset));
            return;
        }
        int half = Math.Max(0, ((node.Vertical ? bounds.Width : bounds.Height) - Gap) / 2);
        if (node.Vertical)
        {
            Place(node.First!, bounds with { Width = half });
            Place(node.Second!, new(bounds.X + half + Gap, bounds.Y, Math.Max(0, bounds.Width - half - Gap), bounds.Height));
        }
        else
        {
            Place(node.First!, bounds with { Height = half });
            Place(node.Second!, new(bounds.X, bounds.Y + half + Gap, bounds.Width, Math.Max(0, bounds.Height - half - Gap)));
        }
    }

    public void SetProfile(int profile)
    {
        Profile = Math.Clamp(profile, 0, ProfileNames.Length - 1);
        foreach (var tab in tabs)
            foreach (var leaf in tab.Leaves) ApplyProfile(leaf.Terminal!);
        status = ProfileNames[Profile] + " (dieses Fenster)";
    }

    private void ApplyProfile(TerminalApp terminal) =>
        terminal.SetColors(ProfileColor(0), ProfileColor(1), ProfileColor(2));

    private int ProfileColor(int part) => unchecked((int)ProfileColors[Profile * 3 + part]);

    private void ResetViews()
    {
        capture = null;
        foreach (var tab in tabs)
            foreach (var leaf in tab.Leaves) leaf.Terminal!.Handle(new(InputEventType.Reset));
    }

    public void Handle(InputEvent value)
    {
        if (closed || tabs.Count == 0) return;
        if (value.Type == InputEventType.Reset)
        {
            control = leftShift = rightShift = false;
            menuOpen = false;
            ResetViews();
            return;
        }
        bool shift = leftShift || rightShift;
        if (value.Type is InputEventType.KeyDown or InputEventType.KeyUp)
        {
            bool down = value.Type == InputEventType.KeyDown;
            if (value.KeyCode == (byte)PhysicalKey.Control) control = down;
            if (value.KeyCode == (byte)PhysicalKey.LeftShift) leftShift = down;
            if (value.KeyCode == (byte)PhysicalKey.RightShift) rightShift = down;
            if (down && control && !value.IsRepeat && !value.IsTextKey)
            {
                if (shift && Shortcut(value.KeyCode)) return;
                if (value.KeyCode == (byte)PhysicalKey.Tab) { SelectTab((ActiveTabIndex + (shift ? tabs.Count - 1 : 1)) % tabs.Count); return; }
                if (value.KeyCode is 27 or 78) { Zoom(1); return; }
                if (value.KeyCode is 53 or 74) { Zoom(-1); return; }
                if (value.KeyCode == 11) { ActiveTerminal.SetZoom(1); Layout(); return; }
            }
        }
        if (menuOpen) { HandleMenu(value); return; }
        if (value.Type == InputEventType.PointerScroll)
        {
            foreach (var leaf in tabs[ActiveTabIndex].Leaves)
                if (leaf.Bounds.Contains(value.X, value.Y))
                {
                    leaf.Terminal!.Handle(value with { X = value.X - leaf.Bounds.X - PaneInset, Y = value.Y - leaf.Bounds.Y - PaneInset });
                    break;
                }
            return;
        }
        if (value.Type == InputEventType.PointerDown)
        {
            if (value.Button == PointerButtons.Left && value.Y < TabsHeight)
            {
                int tabWidth = TabWidth;
                // Rechts in der Tabzeile: Menue ganz aussen, davor neuer Tab; die fruehere Symbolleiste entfaellt.
                if (value.X >= width - TabButtonWidth) OpenMenu(Math.Max(0, width - MenuWidth), TabsHeight);
                else if (value.X >= width - 2 * TabButtonWidth) NewTab();
                else
                {
                    int index = value.X / tabWidth;
                    if (index < tabs.Count)
                    {
                        SelectTab(index);
                        if (value.X % tabWidth >= tabWidth - CloseControlWidth) CloseTab(index);
                    }
                }
                return;
            }
            if (searchOpen && value.Y < TabsHeight + SearchHeight)
            {
                if (value.X >= width - SearchButtonWidth) { searchOpen = false; Layout(); }
                else if (value.X >= width - 3 * SearchButtonWidth) Search(value.X < width - 2 * SearchButtonWidth);
                return;
            }
            foreach (var leaf in tabs[ActiveTabIndex].Leaves)
            {
                if (!leaf.Bounds.Contains(value.X, value.Y)) continue;
                if (tabs[ActiveTabIndex].Active != leaf) ActiveTerminal.Handle(new(InputEventType.Reset));
                tabs[ActiveTabIndex].Active = leaf;
                if (value.Button == PointerButtons.Right) { OpenMenu(value.X, value.Y); return; }
                if (value.Button == PointerButtons.Left) capture = leaf;
                break;
            }
        }
        if (searchOpen && value.Type is InputEventType.Text or InputEventType.KeyDown)
        {
            if (value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Enter) Search(shift);
            else if (!control) search.Handle(value);
            return;
        }
        if (value.Type is InputEventType.PointerMove or InputEventType.PointerDown or InputEventType.PointerUp)
        {
            var leaf = capture ?? tabs[ActiveTabIndex].Active;
            if (capture != null || leaf.Bounds.Contains(value.X, value.Y))
                leaf.Terminal!.Handle(value with { X = value.X - leaf.Bounds.X - PaneInset, Y = value.Y - leaf.Bounds.Y - PaneInset });
            if (value.Type == InputEventType.PointerUp && value.Button == PointerButtons.Left) capture = null;
            return;
        }
        // Steuerkombinationen erzeugen je nach Tastaturlayout zusaetzlich Text; dieser ist keine Eingabe.
        if (control && value.Type == InputEventType.Text) return;
        if (value.Type == InputEventType.Text) status = "";
        ActiveTerminal.Handle(value);
    }

    private bool Shortcut(byte key)
    {
        switch (key)
        {
            case 20: NewTab(); break;
            case 18: Split(true); break;
            case 35: Split(false); break;
            case 49: FocusNextPane(); break;
            case 17: CloseActiveView(); break;
            case 33: OpenSearch(); break;
            case 46: ActiveTerminal.CopySelection(); break;
            case 47: ActiveTerminal.PasteClipboard(); break;
            case 50: OpenMenu(Math.Max(0, width - MenuWidth), TabsHeight); break;
            default: return false;
        }
        return true;
    }

    private void CloseTab(int index)
    {
        foreach (var leaf in tabs[index].Leaves) leaf.Terminal!.Close();
        tabs.RemoveAt(index);
        ActiveTabIndex = Math.Max(0, Math.Min(ActiveTabIndex, tabs.Count - 1));
        if (tabs.Count == 0) closed = true;
        ResetViews();
        Layout();
    }

    private void OpenSearch() { searchOpen = true; menuOpen = false; capture = null; Layout(); }
    private void Search(bool backwards) => status = ActiveTerminal.Find(search.Text, backwards) ? "Treffer: " + search.Text : "Kein Treffer: " + search.Text;
    private void Zoom(int delta) { ActiveTerminal.SetZoom(Math.Clamp(ActiveTerminal.Zoom + delta, 1, 3)); Layout(); }
    private int TabButtonWidth => glyphWidth + 20;
    private int CloseControlWidth => glyphWidth + 12;
    private int SearchButtonWidth => glyphWidth + 14;
    private int TabWidth => Math.Max(1, Math.Min(200, Math.Max(1, width - 2 * TabButtonWidth) / Math.Max(1, tabs.Count)));
    private int MenuWidth => Math.Min(width, 34 * glyphWidth + 12);
    private int MenuRowHeight => glyphHeight + 10;
    private int MenuRows => Math.Max(1, Math.Min(MenuItems.Length, (height - 4) / MenuRowHeight));

    private void OpenMenu(int x, int y)
    {
        menuOpen = true;
        menuX = Math.Clamp(x, 0, Math.Max(0, width - MenuWidth));
        menuY = Math.Clamp(y, 0, Math.Max(0, height - MenuRows * MenuRowHeight));
        menuIndex = menuFirst = 0;
        capture = null;
    }

    private void HandleMenu(InputEvent value)
    {
        if (value.Type == InputEventType.PointerScroll)
        {
            var area = new UiRect(menuX, menuY, Math.Min(width, MenuWidth), MenuRows * MenuRowHeight);
            if (area.Contains(value.X, value.Y))
                menuFirst = (int)Math.Clamp(menuFirst - (long)value.ScrollDelta * 3, 0, MenuItems.Length - MenuRows);
            return;
        }
        if (value.Type == InputEventType.KeyDown)
        {
            if (value.KeyCode == (byte)PhysicalKey.Up) menuIndex = (menuIndex + MenuItems.Length - 1) % MenuItems.Length;
            else if (value.KeyCode == (byte)PhysicalKey.Down) menuIndex = (menuIndex + 1) % MenuItems.Length;
            else if (value.KeyCode == (byte)PhysicalKey.Enter) { RunMenu(menuIndex); return; }
            else if (value.KeyCode == (byte)PhysicalKey.Escape) menuOpen = false;
            menuFirst = Math.Clamp(menuFirst, Math.Max(0, menuIndex - MenuRows + 1), menuIndex);
        }
        if (value.Type != InputEventType.PointerDown) return;
        var bounds = new UiRect(menuX, menuY, Math.Min(width, MenuWidth), MenuRows * MenuRowHeight);
        if (value.Button == PointerButtons.Left && bounds.Contains(value.X, value.Y)) RunMenu(menuFirst + (value.Y - menuY) / MenuRowHeight);
        else menuOpen = false;
    }

    private void RunMenu(int index)
    {
        menuOpen = false;
        switch (index)
        {
            case 0: NewTab(); break;
            case 1: Split(true); break;
            case 2: Split(false); break;
            case 3: FocusNextPane(); break;
            case 4: CloseActiveView(); break;
            case 5: ActiveTerminal.CopySelection(); break;
            case 6: ActiveTerminal.PasteClipboard(); break;
            case 7: ActiveTerminal.SelectAll(); break;
            case 8: OpenSearch(); break;
            case 9: SetProfile((Profile + 1) % ProfileNames.Length); break;
            case 10: Zoom(1); break;
            case 11: Zoom(-1); break;
            case 12: ActiveTerminal.SetZoom(1); Layout(); break;
        }
    }

    public void Update(long milliseconds)
    {
        for (int index = tabs.Count - 1; index >= 0; index--)
        {
            var tab = tabs[index];
            for (int pane = tab.Leaves.Count - 1; pane >= 0; pane--)
            {
                var leaf = tab.Leaves[pane];
                leaf.Terminal!.Update(milliseconds);
                if (!leaf.Terminal.IsCloseRequested) continue;
                if (tab.Leaves.Count == 1) { CloseTab(index); break; }
                leaf.Terminal.Close();
                tab.Root = Remove(tab.Root, leaf)!;
                RebuildLeaves(tab);
                if (tab.Active == leaf) tab.Active = tab.Leaves[0];
                capture = null;
                Layout();
            }
        }
    }

    public void Draw(PixelSurface surface, int x, int y, int drawWidth, int drawHeight, bool active)
    {
        if (closed || tabs.Count == 0) return;
        if (drawWidth != width || drawHeight != height) Resize(drawWidth, drawHeight);
        var previous = surface.Clip;
        surface.IntersectClip(x, y, width, height);
        // Alle Flaechen folgen dem Profil: dunkler Grund zwischen abgerundeten Ansichten, Pillen fuer Tabs und Status.
        int back = ProfileColor(0), text = ProfileColor(1), accent = ProfileColor(2);
        int chrome = UiPainter.Mix(back, unchecked((int)0xff000000), 72);
        int pill = UiPainter.Mix(text, chrome, 10);
        int dim = UiPainter.Mix(text, chrome, 55);
        surface.FillRectangle(x, y, width, height, chrome);
        var tab = tabs[ActiveTabIndex];
        foreach (var leaf in tab.Leaves)
        {
            var b = leaf.Bounds;
            bool focused = leaf == tab.Active && active;
            UiPainter.RoundFrame(surface, x + b.X, y + b.Y, b.Width, b.Height, 10, 2,
                focused ? accent : UiPainter.Mix(text, chrome, 18), back);
            surface.IntersectClip(x + b.X + PaneInset, y + b.Y + PaneInset, Math.Max(0, b.Width - 2 * PaneInset), Math.Max(0, b.Height - 2 * PaneInset));
            leaf.Terminal!.Draw(surface, x + b.X + PaneInset, y + b.Y + PaneInset, Math.Max(1, b.Width - 2 * PaneInset),
                Math.Max(1, b.Height - 2 * PaneInset), focused && !searchOpen && !menuOpen);
            surface.SetClip(previous.X, previous.Y, previous.Width, previous.Height);
            surface.IntersectClip(x, y, width, height);
        }
        int labelTop = (TabsHeight - glyphHeight) / 2;
        for (int index = 0; index < tabs.Count; index++)
        {
            int left = index * TabWidth;
            bool current = index == ActiveTabIndex;
            if (current) UiPainter.RoundRect(surface, x + left + 3, y + 3, TabWidth - 6, TabsHeight - 6, 8, UiPainter.Mix(accent, chrome, 28));
            string directory = tabs[index].Active.Terminal!.Shell.CurrentDirectory;
            string label = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            if (label.Length == 0) label = "Shell";
            painter.Text(surface, $"{index + 1} {label}", x + left + 10, y + labelTop, TabWidth - MenuRowHeight - 6, current ? text : dim);
            painter.Text(surface, "x", x + left + TabWidth - CloseControlWidth + 4, y + labelTop, glyphWidth, dim);
        }
        int buttons = x + width - 2 * TabButtonWidth, middle = y + TabsHeight / 2;
        if (menuOpen) UiPainter.RoundRect(surface, buttons + TabButtonWidth + 3, y + 3, TabButtonWidth - 6, TabsHeight - 6, 8, pill);
        painter.Text(surface, "+", buttons + (TabButtonWidth - glyphWidth) / 2, y + labelTop, glyphWidth, text);
        // Pfeil nach unten fuer das Menue, aus zwei Linien statt eines Schriftzeichens.
        int arrow = buttons + TabButtonWidth + TabButtonWidth / 2, size = Math.Max(4, glyphWidth / 2);
        for (int line = 0; line < 2; line++)
        {
            surface.DrawLine(arrow - size, middle - size / 2 + line, arrow, middle + size / 2 + line, text);
            surface.DrawLine(arrow, middle + size / 2 + line, arrow + size, middle - size / 2 + line, text);
        }
        if (searchOpen)
        {
            int top = y + TabsHeight;
            UiPainter.RoundRect(surface, x + Gap, top + 2, width - 2 * Gap, SearchHeight - 4, 8, pill);
            int searchText = top + (SearchHeight - glyphHeight) / 2;
            painter.Text(surface, "Suche: " + search.Text + "_", x + Gap + 8, searchText, width - 3 * SearchButtonWidth - 24, text);
            painter.Text(surface, "<", x + width - 3 * SearchButtonWidth + 7, searchText, glyphWidth, accent);
            painter.Text(surface, ">", x + width - 2 * SearchButtonWidth + 7, searchText, glyphWidth, accent);
            painter.Text(surface, "x", x + width - SearchButtonWidth + 7, searchText, glyphWidth, accent);
        }
        DrawStatus(surface, x, y + height - StatusHeight, chrome, pill, text, accent, back);
        if (menuOpen)
        {
            int menuWidth = Math.Min(width, MenuWidth);
            UiPainter.RoundFrame(surface, x + menuX, y + menuY, menuWidth, MenuRows * MenuRowHeight, 8, 1, accent, chrome);
            for (int row = 0; row < MenuRows; row++)
            {
                int item = menuFirst + row;
                if (item >= MenuItems.Length) break;
                if (item == menuIndex)
                    UiPainter.RoundRect(surface, x + menuX + 3, y + menuY + row * MenuRowHeight + 2, menuWidth - 6, MenuRowHeight - 4, 6, pill);
                painter.Text(surface, MenuItems[item], x + menuX + 8, y + menuY + row * MenuRowHeight + (MenuRowHeight - glyphHeight) / 2,
                    menuWidth - 16, item == menuIndex ? text : UiPainter.Mix(text, chrome, 75));
            }
        }

        surface.SetClip(previous.X, previous.Y, previous.Width, previous.Height);
    }

    // Statuszeile als Segmente: Arbeitsverzeichnis hervorgehoben, danach Ansichten, Zoom und Profil.
    private void DrawStatus(PixelSurface surface, int x, int top, int chrome, int pill, int text, int accent, int back)
    {
        surface.FillRectangle(x, top, width, StatusHeight, chrome);
        int textTop = top + (StatusHeight - glyphHeight) / 2;
        if (status.Length > 0)
        {
            painter.Text(surface, status, x + Gap + 4, textTop, width - 2 * Gap, searchOpen ? accent : text);
            return;
        }
        int left = x + Gap;
        left = Segment(surface, ActiveTerminal.Shell.CurrentDirectory, left, top, textTop, accent, back, x + width - Gap);
        left = Segment(surface, $"{ViewCount} Ansicht(en)", left, top, textTop, pill, text, x + width - Gap);
        left = Segment(surface, $"{ActiveTerminal.Zoom * 100}%", left, top, textTop, pill, text, x + width - Gap);
        Segment(surface, ProfileNames[Profile], left, top, textTop, pill, text, x + width - Gap);
    }

    private int Segment(PixelSurface surface, string label, int left, int top, int textTop, int fill, int color, int right)
    {
        int segmentWidth = Math.Min(label.Length * glyphWidth + 16, right - left);
        if (segmentWidth <= 16) return right;
        UiPainter.RoundRect(surface, left, top + 2, segmentWidth, StatusHeight - 4, (StatusHeight - 4) / 2, fill);
        painter.Text(surface, label, left + 8, textTop, segmentWidth - 16, color);
        return left + segmentWidth + 6;
    }

    public void Close()
    {
        if (closed && tabs.Count == 0) return;
        foreach (var tab in tabs)
            foreach (var leaf in tab.Leaves) leaf.Terminal!.Close();
        tabs.Clear();
        capture = null;
        closed = true;
    }
}
