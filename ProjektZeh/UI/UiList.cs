using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.UI;

internal sealed class UiList : UiControl
{
    private string[] items = [];
    private int first;
    private int visible = 1;
    private int rowHeight = 20;
    private int clicked = -1;
    private long clickedAt;
    public bool DoubleClick { get; set; }
    /// <summary>Optionales Symbol je Eintrag, parallel zu den Eintraegen.</summary>
    public AppIcon[]? Icons { get; set; }
    public int RowPadding { get; set; } = 4;
    public Func<long> GetMilliseconds { get; set; } = () => Environment.TickCount64;
    public int SelectedIndex { get; private set; } = -1;
    public Action? Activated { get; set; }
    public int Count => items.Length;

    public void SetItems(string[] values)
    {
        items = values;
        first = 0;
        SelectedIndex = items.Length == 0 ? -1 : 0;
        clicked = -1;
    }

    public override void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset) { clicked = -1; return; }
        if (items.Length == 0) return;
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            int row = (value.Y - Bounds.Y - 4) / rowHeight;
            if (Bounds.Contains(value.X, value.Y) && value.Y >= Bounds.Y + 4 && row < visible && first + row < items.Length)
            {
                SelectedIndex = first + row;
                long now = GetMilliseconds();
                bool activate = DoubleClick && clicked == SelectedIndex && now >= clickedAt && now - clickedAt <= 450;
                clicked = activate ? -1 : SelectedIndex;
                clickedAt = now;
                if (activate) Activated?.Invoke();
            }
            else clicked = -1;
        }
        if (value.Type != InputEventType.KeyDown || value.IsTextKey) return;
        int delta = (PhysicalKey)value.KeyCode switch
        {
            PhysicalKey.Up => -1, PhysicalKey.Down => 1,
            PhysicalKey.PageUp => -visible, PhysicalKey.PageDown => visible,
            PhysicalKey.Home => -items.Length, PhysicalKey.End => items.Length,
            _ => 0
        };
        if (delta != 0) { SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, items.Length - 1); clicked = -1; }
        if (value.KeyCode == (byte)PhysicalKey.Enter && !value.IsRepeat) Activated?.Invoke();
    }

    protected override void DrawContent(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        UiPainter.Frame(surface, Bounds, x, y, Focused && active);
        rowHeight = painter.GlyphHeight + RowPadding;
        visible = Math.Max(1, (Bounds.Height - 8) / rowHeight);
        if (SelectedIndex >= 0) first = Math.Clamp(first, Math.Max(0, SelectedIndex - visible + 1), SelectedIndex);
        int indent = Icons == null ? 12 : 44;
        for (int row = 0; row < Math.Min(visible, items.Length - first); row++)
        {
            int top = y + Bounds.Y + 4 + row * rowHeight;
            bool selected = first + row == SelectedIndex;
            if (selected)
                surface.FillRectangle(x + Bounds.X + 4, top, Bounds.Width - 8, rowHeight, UiPainter.Selection);
            if (Icons != null && first + row < Icons.Length)
                AppIcons.Draw(surface, Icons[first + row], x + Bounds.X + 14, top + (rowHeight - AppIcons.Size) / 2, UiPainter.Accent);
            painter.Text(surface, items[first + row], x + Bounds.X + indent, top + RowPadding / 2, Bounds.Width - indent - 8,
                selected ? UiPainter.Foreground : null);
        }
    }
}
