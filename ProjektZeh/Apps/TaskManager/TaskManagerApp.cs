using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.UI;

namespace ProjektZeh.Apps.TaskManager;

internal sealed class TaskManagerApp : IApplication
{
    public const int OverviewSection = 0;
    public const int ApplicationsSection = 1;
    private const int SidebarWidth = 260;
    private readonly UiPainter painter;
    private readonly WindowManager windows;
    private readonly SystemMetrics metrics;
    private readonly DesktopMetrics desktopMetrics;
    private readonly TaskManagerSection[] sections;
    private readonly UiPanel panel = new();
    private readonly UiList sidebar = new() { RowPadding = 12 };
    private readonly UiList list = new();
    private readonly UiButton focus;
    private readonly UiButton close;
    private readonly Window?[] rows = new Window?[Desktop.MaximumWindows];
    private readonly string[] cards = new string[12];
    private string[] labels = [];
    private string[] lines = [];
    private string status = "Schliessen fragt die Anwendung; ungespeicherte Inhalte bleiben geschuetzt.";
    private long nextRefresh;
    private int shownSection = -1;
    private int width = 760;
    private int height = 590;

    public TaskManagerApp(UiPainter painter, WindowManager windows, SystemMetrics metrics, DesktopMetrics desktopMetrics,
        TaskManagerSection[] sections)
    {
        this.painter = painter;
        this.windows = windows;
        this.metrics = metrics;
        this.desktopMetrics = desktopMetrics;
        this.sections = sections;
        focus = new("Aktivieren", FocusSelected) { Primary = true };
        close = new("Schliessen...", CloseSelected);
        list.Activated = FocusSelected;
        var titles = new string[2 + sections.Length];
        titles[OverviewSection] = "Uebersicht";
        titles[ApplicationsSection] = "Anwendungen";
        for (int index = 0; index < sections.Length; index++) titles[2 + index] = sections[index].Title;
        sidebar.SetItems(titles);
        foreach (var control in new UiControl[] { sidebar, list, focus, close }) panel.Add(control);
        Layout(width, height);
    }

    public int Section => sidebar.SelectedIndex;
    public string[] Lines => lines;

    public void Start() => Refresh();
    public void Resize(int newWidth, int newHeight) => Layout(newWidth, newHeight);

    public void Handle(InputEvent value)
    {
        panel.Handle(value);
        if (Section != shownSection) Refresh();
    }

    public void Update(long milliseconds)
    {
        if (milliseconds < nextRefresh) return;
        nextRefresh = milliseconds + 1000;
        Refresh();
    }

    private void Refresh()
    {
        shownSection = Section;
        bool apps = Section == ApplicationsSection;
        list.Enabled = focus.Enabled = close.Enabled = apps;
        if (apps) RefreshApplications();
        else if (Section >= 2) lines = sections[Section - 2].ReadLines();
        else RefreshCards();
    }

    private void RefreshApplications()
    {
        int count = Math.Min(rows.Length, windows.Count);
        if (labels.Length != count)
        {
            labels = new string[count];
            list.SetItems(labels);
        }
        for (int index = 0; index < rows.Length; index++)
        {
            Window? window = index < count ? windows.GetWindow(index) : null;
            rows[index] = window;
            if (window == null) continue;
            string state = window.IsMinimized ? "minimiert" : windows.ActiveWindow == window ? "aktiv" : "geoeffnet";
            labels[index] = $"{window.Title,-24} {state,-10} {window.Width} x {window.Height}";
        }
        focus.Enabled = close.Enabled = count > 0;
    }

    private void RefreshCards()
    {
        cards[0] = "Desktop-Schleife";
        cards[1] = metrics.HasCpuSample ? $"{metrics.CpuPercent} %" : "Messung startet";
        cards[2] = "gemessene Arbeit, keine Gesamt-CPU";
        cards[3] = "Arbeitsspeicher";
        cards[4] = metrics.HasPhysicalMemory
            ? $"{metrics.FreeMemory / 1048576} / {metrics.TotalMemory / 1048576} MiB frei" : "physisch nicht verfuegbar";
        cards[5] = $"Heap {metrics.ManagedBytes / 1024} KiB";
        cards[6] = "Bildrate";
        cards[7] = desktopMetrics.HasSample ? $"{desktopMetrics.FramesPerSecond} FPS" : "Messung startet";
        cards[8] = desktopMetrics.HasSample ? $"laengstes Bild {desktopMetrics.MaximumFrameMilliseconds} ms" : "";
        long seconds = metrics.UptimeMilliseconds / 1000;
        cards[9] = "Betriebszeit";
        cards[10] = $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";
        cards[11] = "seit dem Start";
    }

    private Window? SelectedWindow => list.SelectedIndex >= 0 && list.SelectedIndex < labels.Length ? rows[list.SelectedIndex] : null;

    private void FocusSelected()
    {
        Window? window = SelectedWindow;
        if (window != null && window != windows.ActiveWindow) windows.ToggleTask(window);
    }

    private void CloseSelected()
    {
        Window? window = SelectedWindow;
        if (window == null) return;
        // Die normale Schliessanfrage erhaelt die Rueckfrage bei ungespeicherten Inhalten.
        windows.RequestClose(window);
        status = "Schliessanfrage gesendet; Rueckfragen in der Anwendung beantworten.";
        RefreshApplications();
    }

    public void Draw(PixelSurface surface, int x, int y, int newWidth, int newHeight, bool active)
    {
        Layout(newWidth, newHeight);
        surface.FillRectangle(x, y, SidebarWidth, height, UiPainter.Panel);
        surface.FillRectangle(x + SidebarWidth, y, 1, height, UiPainter.Border);
        surface.FillRectangle(x + SidebarWidth + 1, y, width - SidebarWidth - 1, height, UiPainter.Background);
        sidebar.Draw(surface, painter, x, y, active);
        int left = x + SidebarWidth + 24, inner = width - SidebarWidth - 48, top = y + 20;
        painter.Text(surface, sidebar.SelectedIndex >= 0 ? SectionTitle : "", left, top, inner, UiPainter.Accent);
        top += painter.GlyphHeight + 16;
        if (Section == OverviewSection) DrawOverview(surface, left, top, inner, y + height - 16 - top);
        else if (Section == ApplicationsSection)
        {
            list.Draw(surface, painter, x, y, active);
            focus.Draw(surface, painter, x, y, active);
            close.Draw(surface, painter, x, y, active);
            painter.Paragraph(surface, status, left, y + close.Bounds.Y + close.Bounds.Height + 12, inner, UiPainter.Secondary);
        }
        else
            for (int index = 0; index < lines.Length; index++)
                painter.Text(surface, lines[index], left, top + index * (painter.GlyphHeight + 8), inner);
    }

    private string SectionTitle => Section switch
    {
        OverviewSection => "Systemuebersicht",
        ApplicationsSection => "Laufende Anwendungen",
        _ => sections[Section - 2].Title
    };

    private void DrawOverview(PixelSurface surface, int x, int y, int width, int available)
    {
        int line = painter.GlyphHeight + 6;
        int cardWidth = (width - 16) / 2, cardHeight = 3 * line + 20;
        for (int card = 0; card < 4; card++)
        {
            int left = x + card % 2 * (cardWidth + 16), top = y + card / 2 * (cardHeight + 16);
            surface.FillRectangle(left, top, cardWidth, cardHeight, UiPainter.Border);
            surface.FillRectangle(left + 1, top + 1, cardWidth - 2, cardHeight - 2, UiPainter.Muted);
            painter.Text(surface, cards[card * 3] ?? "", left + 14, top + 10, cardWidth - 28, UiPainter.Secondary);
            painter.Text(surface, cards[card * 3 + 1] ?? "", left + 14, top + 10 + line, cardWidth - 28);
            painter.Text(surface, cards[card * 3 + 2] ?? "", left + 14, top + 10 + 2 * line, cardWidth - 28, UiPainter.Secondary);
        }
        if (metrics.HasPhysicalMemory)
        {
            int left = x + cardWidth + 16 + 14, barWidth = cardWidth - 28, used = (int)((ulong)barWidth * (metrics.TotalMemory - metrics.FreeMemory) / metrics.TotalMemory);
            surface.FillRectangle(left, y + cardHeight - 8, barWidth, 4, UiPainter.Background);
            surface.FillRectangle(left, y + cardHeight - 8, used, 4, UiPainter.Accent);
        }
        int graphTop = y + 2 * (cardHeight + 16);
        int graphHeight = available - (graphTop - y) - line;
        if (graphHeight < 48) return;
        painter.Text(surface, "Desktop-Schleife, letzte 60 Sekunden (0-100 %)", x, graphTop, width, UiPainter.Secondary);
        DrawGraph(surface, x, graphTop + line, width, graphHeight);
    }

    private void DrawGraph(PixelSurface surface, int x, int y, int width, int height)
    {
        surface.FillRectangle(x, y, width, height, UiPainter.Border);
        surface.FillRectangle(x + 1, y + 1, width - 2, height - 2, UiPainter.Muted);
        for (int row = 1; row < 4; row++)
            surface.FillRectangle(x + 1, y + row * height / 4, width - 2, 1, UiPainter.Border);
        int previousX = x;
        int previousY = y + height - 2;
        for (int index = 0; index < metrics.HistoryCount; index++)
        {
            int pointX = x + 1 + (width - 3) * index / 59;
            int pointY = y + height - 2 - (height - 3) * metrics.GetHistory(index) / 100;
            if (index > 0)
            {
                surface.DrawLine(previousX, previousY, pointX, pointY, UiPainter.Accent);
                surface.DrawLine(previousX, previousY - 1, pointX, pointY - 1, UiPainter.Accent);
            }
            previousX = pointX;
            previousY = pointY;
        }
    }

    private void Layout(int newWidth, int newHeight)
    {
        width = newWidth;
        height = newHeight;
        int line = painter.GlyphHeight;
        sidebar.Bounds = new(12, 12, SidebarWidth - 24, Math.Max(48, height - 24));
        int left = SidebarWidth + 24, inner = Math.Max(1, width - SidebarWidth - 48);
        int listTop = 20 + line + 16;
        int buttonHeight = line + 16;
        int buttonTop = height - 3 * line - buttonHeight - 32;
        list.Bounds = new(left, listTop, inner, Math.Max(48, buttonTop - listTop - 12));
        int half = Math.Max(1, (inner - 12) / 2);
        focus.Bounds = new(left, buttonTop, half, buttonHeight);
        close.Bounds = new(left + half + 12, buttonTop, inner - half - 12, buttonHeight);
    }

    public void Close() => panel.Handle(new(InputEventType.Reset));
}
