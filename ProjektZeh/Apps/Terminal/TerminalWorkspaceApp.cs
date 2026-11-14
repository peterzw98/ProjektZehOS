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
    private int ToolbarHeight => glyphHeight + 12;
    private int StatusHeight => glyphHeight + 8;
    private int SearchHeight => glyphHeight + 12;
    private static readonly string[] ToolbarLabels = ["+ Tab", "Teil |", "Teil -", "Suche", "Profil", "Menue"];
    private static readonly string[] ProfileNames = ["Breeze Dunkel", "Breeze Hell", "Solarisiert"];
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
        int top = TabsHeight + ToolbarHeight + (searchOpen ? SearchHeight : 0);
        foreach (var tab in tabs) Place(tab.Root, new(0, top, width, Math.Max(1, height - top - StatusHeight)));
    }

    private static void Place(Node node, UiRect bounds)
    {
        node.Bounds = bounds;
        if (node.Terminal != null)
        {
            node.Terminal.Resize(Math.Max(1, bounds.Width - 4), Math.Max(1, bounds.Height - 4));
            return;
        }
        int half = Math.Max(0, ((node.Vertical ? bounds.Width : bounds.Height) - 4) / 2);
        if (node.Vertical)
        {
            Place(node.First!, bounds with { Width = half });
            Place(node.Second!, new(bounds.X + half + 4, bounds.Y, Math.Max(0, bounds.Width - half - 4), bounds.Height));
        }
        else
        {
            Place(node.First!, bounds with { Height = half });
            Place(node.Second!, new(bounds.X, bounds.Y + half + 4, bounds.Width, Math.Max(0, bounds.Height - half - 4)));
        }
    }

    public void SetProfile(int profile)
    {
        Profile = Math.Clamp(profile, 0, ProfileNames.Length - 1);
        foreach (var tab in tabs)
            foreach (var leaf in tab.Leaves) ApplyProfile(leaf.Terminal!);
        status = ProfileNames[Profile] + " (dieses Fenster)";
    }

    private void ApplyProfile(TerminalApp terminal)
    {
        var colors = Profile switch
        {
            1 => (0xfffafafa, 0xff20252b, 0xff1377a5),
            2 => (0xff002b36, 0xff93a1a1, 0xffb58900),
            _ => (0xff1b1e20, 0xffeff0f1, 0xff3daee9)
        };
        terminal.SetColors(unchecked((int)colors.Item1), unchecked((int)colors.Item2), unchecked((int)colors.Item3));
    }

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
                if (value.KeyCode is 13 or 78) { Zoom(1); return; }
                if (value.KeyCode is 12 or 74) { Zoom(-1); return; }
                if (value.KeyCode == 11) { ActiveTerminal.SetZoom(1); Layout(); return; }
            }
        }
        if (menuOpen) { HandleMenu(value); return; }
        if (value.Type == InputEventType.PointerDown)
        {
            if (value.Button == PointerButtons.Left && value.Y < TabsHeight)
            {
                int tabWidth = TabWidth;
                if (value.X >= width - NewTabWidth) NewTab();
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
            if (value.Button == PointerButtons.Left && value.Y < TabsHeight + ToolbarHeight)
            {
                switch (Math.Min(5, value.X / Math.Max(1, width / 6)))
                {
                    case 0: NewTab(); break;
                    case 1: Split(true); break;
                    case 2: Split(false); break;
                    case 3: OpenSearch(); break;
                    case 4: SetProfile((Profile + 1) % ProfileNames.Length); break;
                    case 5: OpenMenu(Math.Max(0, width - MenuWidth), TabsHeight + ToolbarHeight); break;
                }
                return;
            }
            if (searchOpen && value.Y < TabsHeight + ToolbarHeight + SearchHeight)
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
                leaf.Terminal!.Handle(value with { X = value.X - leaf.Bounds.X - 2, Y = value.Y - leaf.Bounds.Y - 2 });
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
            case 50: OpenMenu(Math.Max(0, width - MenuWidth), TabsHeight + ToolbarHeight); break;
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
    private int NewTabWidth => glyphWidth + 12;
    private int CloseControlWidth => glyphWidth + 12;
    private int SearchButtonWidth => glyphWidth + 14;
    private int TabWidth => Math.Max(1, Math.Min(180, Math.Max(1, width - NewTabWidth) / Math.Max(1, tabs.Count)));
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
        surface.FillRectangle(x, y, width, height, UiPainter.Panel);
        var tab = tabs[ActiveTabIndex];
        foreach (var leaf in tab.Leaves)
        {
            var b = leaf.Bounds;
            surface.FillRectangle(x + b.X, y + b.Y, b.Width, b.Height, leaf == tab.Active && active ? UiPainter.Accent : UiPainter.Border);
            surface.IntersectClip(x + b.X + 2, y + b.Y + 2, Math.Max(0, b.Width - 4), Math.Max(0, b.Height - 4));
            leaf.Terminal!.Draw(surface, x + b.X + 2, y + b.Y + 2, Math.Max(1, b.Width - 4), Math.Max(1, b.Height - 4), active && leaf == tab.Active && !searchOpen && !menuOpen);
            surface.SetClip(previous.X, previous.Y, previous.Width, previous.Height);
            surface.IntersectClip(x, y, width, height);
        }
        for (int index = 0; index < tabs.Count; index++)
        {
            int left = index * TabWidth;
            surface.FillRectangle(x + left, y, TabWidth - 1, TabsHeight, index == ActiveTabIndex ? UiPainter.Background : UiPainter.Panel);
            if (index == ActiveTabIndex) surface.FillRectangle(x + left, y + TabsHeight - 2, TabWidth - 1, 2, UiPainter.Accent);
            string directory = tabs[index].Active.Terminal!.Shell.CurrentDirectory;
            string label = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            if (label.Length == 0) label = "Shell";
            painter.Text(surface, $"{index + 1} {label}", x + left + 5, y + 6, TabWidth - MenuRowHeight);
            painter.Text(surface, "x", x + left + TabWidth - CloseControlWidth + 6, y + 6, glyphWidth, UiPainter.Secondary);
        }
        painter.Text(surface, "+", x + width - NewTabWidth + 6, y + 6, glyphWidth, UiPainter.Accent);
        string[] labels = ToolbarLabels;
        int cell = Math.Max(1, width / labels.Length);
        for (int index = 0; index < labels.Length; index++)
            painter.Text(surface, labels[index], x + index * cell + 5, y + TabsHeight + 7, cell - 8, UiPainter.Secondary);
        if (searchOpen)
        {
            int top = y + TabsHeight + ToolbarHeight;
            surface.FillRectangle(x, top, width, SearchHeight, UiPainter.Background);
            painter.Text(surface, "Suche: " + search.Text + "_", x + 6, top + 7, width - 3 * SearchButtonWidth - 12);
            painter.Text(surface, "<", x + width - 3 * SearchButtonWidth + 7, top + 7, glyphWidth, UiPainter.Accent);
            painter.Text(surface, ">", x + width - 2 * SearchButtonWidth + 7, top + 7, glyphWidth, UiPainter.Accent);
            painter.Text(surface, "x", x + width - SearchButtonWidth + 7, top + 7, glyphWidth, UiPainter.Accent);
        }
        surface.FillRectangle(x, y + height - StatusHeight, width, StatusHeight, UiPainter.Panel);
        string detail = $"{ActiveTerminal.Shell.CurrentDirectory} | {ViewCount} Ansicht(en) | {ActiveTerminal.Zoom * 100}% | {ProfileNames[Profile]}";
        painter.Text(surface, status.Length > 0 ? status : detail, x + 6, y + height - StatusHeight + 3, width - 12, searchOpen ? UiPainter.Accent : UiPainter.Secondary);
        if (menuOpen)
        {
            surface.FillRectangle(x + menuX, y + menuY, Math.Min(width, MenuWidth), MenuRows * MenuRowHeight, UiPainter.Border);
            for (int row = 0; row < MenuRows; row++)
            {
                int item = menuFirst + row;
                if (item >= MenuItems.Length) break;
                surface.FillRectangle(x + menuX + 1, y + menuY + row * MenuRowHeight + 1, MenuWidth - 2, MenuRowHeight - 1,
                    item == menuIndex ? UiPainter.Selection : UiPainter.Panel);
                painter.Text(surface, MenuItems[item], x + menuX + 5, y + menuY + row * MenuRowHeight + 4, Math.Min(width, MenuWidth) - 10);
            }
        }

        surface.SetClip(previous.X, previous.Y, previous.Width, previous.Height);
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
