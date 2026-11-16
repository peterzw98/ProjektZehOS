using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.UI;

/// <summary>Kachelraster mit Symbol und zweizeiligem Namen, etwa fuer Startmenue und Desktopverknuepfungen.</summary>
internal sealed class UiTileGrid : UiControl
{
    private string[] items = [];
    private int first;
    private int clicked = -1;
    private long clickedAt;
    private int columns = 1;
    private int rows = 1;
    public AppIcon[]? Icons { get; set; }
    /// <summary>Fuellt spaltenweise von oben nach unten wie Desktopsymbole; sonst zeilenweise wie ein Startmenue.</summary>
    public bool ColumnMajor { get; set; }
    /// <summary>Ohne eigenen Hintergrund, damit das Hintergrundbild sichtbar bleibt.</summary>
    public bool Transparent { get; set; }
    public bool DoubleClick { get; set; } = true;
    public Func<long> GetMilliseconds { get; set; } = () => Environment.TickCount64;
    public int SelectedIndex { get; private set; } = -1;
    public Action? Activated { get; set; }
    public int Count => items.Length;
    /// <summary>Sichtbare Plaetze; das spaltenweise Raster blaettert nicht, mehr Eintraege waeren unsichtbar.</summary>
    public int Capacity { get { Measure(); return columns * rows; } }
    public int GlyphWidth { get; set; } = 8;
    public int GlyphHeight { get; set; } = 16;
    public int TileWidth => Math.Max(96, 11 * GlyphWidth + 16);
    public int TileHeight => 60 + 2 * GlyphHeight;

    public void SetItems(string[] values)
    {
        items = values;
        first = 0;
        SelectedIndex = items.Length == 0 ? -1 : 0;
        clicked = -1;
    }

    public void Select(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, items.Length);
        SelectedIndex = index;
        clicked = -1;
    }

    /// <summary>Kachel an einer Position, -1 fuer freie Flaeche.</summary>
    public int IndexAt(int x, int y)
    {
        Measure();
        if (!Bounds.Contains(x, y)) return -1;
        int column = (x - Bounds.X - 4) / TileWidth, row = (y - Bounds.Y - 4) / TileHeight;
        if (x < Bounds.X + 4 || y < Bounds.Y + 4 || column >= columns || row >= rows) return -1;
        int index = first + (ColumnMajor ? column * rows + row : row * columns + column);
        return index < items.Length ? index : -1;
    }

    public override void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset) { clicked = -1; return; }
        if (items.Length == 0) return;
        Measure();
        if (value.Type == InputEventType.PointerScroll && !ColumnMajor)
        {
            first = (int)Math.Clamp(first - (long)value.ScrollDelta * columns, 0, MaxFirst);
            first -= first % columns;
            clicked = -1;
            return;
        }
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            int index = IndexAt(value.X, value.Y);
            if (index < 0) { clicked = -1; return; }
            SelectedIndex = index;
            long now = GetMilliseconds();
            bool activate = !DoubleClick || clicked == index && now >= clickedAt && now - clickedAt <= 450;
            clicked = activate ? -1 : index;
            clickedAt = now;
            if (activate) Activated?.Invoke();
            return;
        }
        if (value.Type != InputEventType.KeyDown || value.IsTextKey) return;
        // Pfeile folgen der sichtbaren Anordnung: im spaltenweisen Raster springt links/rechts um eine Spalte.
        int across = ColumnMajor ? rows : 1, down = ColumnMajor ? 1 : columns;
        int delta = (PhysicalKey)value.KeyCode switch
        {
            PhysicalKey.Left => -across, PhysicalKey.Right => across,
            PhysicalKey.Up => -down, PhysicalKey.Down => down,
            PhysicalKey.Home => -items.Length, PhysicalKey.End => items.Length,
            _ => 0
        };
        if (delta != 0)
        {
            int last = ColumnMajor ? Math.Min(items.Length, columns * rows) - 1 : items.Length - 1;
            SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, last);
            clicked = -1;
            if (!ColumnMajor) first = Math.Clamp(first, Math.Max(0, (SelectedIndex / columns - rows + 1) * columns), SelectedIndex / columns * columns);
        }
        // Nach einer kleineren Aufloesung koennen gespeicherte Symbole ausserhalb des Rasters liegen; diese nicht unsichtbar starten.
        bool visible = !ColumnMajor || SelectedIndex < columns * rows;
        if (value.KeyCode == (byte)PhysicalKey.Enter && !value.IsRepeat && SelectedIndex >= 0 && visible) Activated?.Invoke();
    }

    private int MaxFirst => Math.Max(0, ((items.Length + columns - 1) / columns - rows) * columns);

    private void Measure()
    {
        columns = Math.Max(1, (Bounds.Width - 8) / TileWidth);
        rows = Math.Max(1, (Bounds.Height - 8) / TileHeight);
        if (ColumnMajor) first = 0;
        else first = Math.Clamp(first, 0, MaxFirst);
    }

    protected override void DrawContent(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        GlyphWidth = painter.GlyphWidth;
        GlyphHeight = painter.GlyphHeight;
        Measure();
        if (!Transparent) surface.FillRectangle(x + Bounds.X, y + Bounds.Y, Bounds.Width, Bounds.Height, UiPainter.Background);
        int labelColumns = Math.Max(1, (TileWidth - 12) / GlyphWidth);
        for (int slot = 0; slot < columns * rows && first + slot < items.Length; slot++)
        {
            int index = first + slot;
            int column = ColumnMajor ? slot / rows : slot % columns, row = ColumnMajor ? slot % rows : slot / columns;
            int left = x + Bounds.X + 4 + column * TileWidth, top = y + Bounds.Y + 4 + row * TileHeight;
            if (index == SelectedIndex && (Focused && active || !Transparent))
            {
                if (Focused && active) UiPainter.RoundRect(surface, left + 2, top + 2, TileWidth - 4, TileHeight - 4, 10, UiPainter.Accent);
                UiPainter.RoundRect(surface, left + 4, top + 4, TileWidth - 8, TileHeight - 8, 8, UiPainter.Selection);
            }
            int badge = 44, badgeLeft = left + (TileWidth - badge) / 2;
            int iconLeft = badgeLeft + (badge - AppIcons.Size) / 2, iconTop = top + 8 + (badge - AppIcons.Size) / 2;
            // Desktopsymbole ohne Kachelflaeche: weisses Symbol mit dunklem Versatz wie die Beschriftung.
            if (!Transparent) UiPainter.RoundRect(surface, badgeLeft, top + 8, badge, badge, 12, UiPainter.Mix(UiPainter.Accent, UiPainter.Panel, 28));
            if (Icons != null && index < Icons.Length)
            {
                if (Transparent) AppIcons.Draw(surface, Icons[index], iconLeft + 1, iconTop + 1, unchecked((int)0xff000000));
                AppIcons.Draw(surface, Icons[index], iconLeft, iconTop, Transparent ? unchecked((int)0xffffffff) : UiPainter.Accent);
            }
            (string line1, string line2) = Wrap(items[index], labelColumns);
            int textTop = top + 8 + badge + 4;
            int textColor = Transparent ? unchecked((int)0xffffffff) : UiPainter.Foreground;
            if (Transparent) Shadowed(surface, painter, line1, left, textTop, labelColumns);
            painter.Text(surface, line1, left + (TileWidth - line1.Length * GlyphWidth) / 2, textTop, TileWidth, textColor);
            if (line2.Length == 0) continue;
            if (Transparent) Shadowed(surface, painter, line2, left, textTop + GlyphHeight, labelColumns);
            painter.Text(surface, line2, left + (TileWidth - line2.Length * GlyphWidth) / 2, textTop + GlyphHeight, TileWidth, textColor);
        }
    }

    // Dunkler Versatz macht weisse Beschriftungen auf hellen Hintergruenden lesbar.
    private void Shadowed(PixelSurface surface, UiPainter painter, string text, int left, int top, int labelColumns) =>
        painter.Text(surface, text, left + (TileWidth - text.Length * GlyphWidth) / 2 + 1, top + 1, labelColumns * GlyphWidth, unchecked((int)0xff000000));

    /// <summary>Teilt einen Namen an einem Leerzeichen oder hart auf hoechstens zwei Zeilen; Rest wird mit ~ gekuerzt.</summary>
    internal static (string, string) Wrap(string text, int columns)
    {
        if (text.Length <= columns) return (text, "");
        int split = text.LastIndexOf(' ', Math.Min(columns, text.Length - 1));
        string first = split > 0 ? text[..split] : text[..columns];
        string rest = text[(split > 0 ? split + 1 : columns)..];
        if (rest.Length > columns) rest = rest[..(columns - 1)] + "~";
        return (first, rest);
    }
}
