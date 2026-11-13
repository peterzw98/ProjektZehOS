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
    public int SelectedIndex { get; private set; } = -1;
    public Action? Activated { get; set; }
    public int Count => items.Length;

    public void SetItems(string[] values)
    {
        items = values;
        first = 0;
        SelectedIndex = items.Length == 0 ? -1 : 0;
    }

    public override void Handle(InputEvent value)
    {
        if (items.Length == 0) return;
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            int row = (value.Y - Bounds.Y - 4) / rowHeight;
            if (value.Y >= Bounds.Y + 4 && row < visible && first + row < items.Length) SelectedIndex = first + row;
        }
        if (value.Type != InputEventType.KeyDown || value.IsTextKey) return;
        int delta = (PhysicalKey)value.KeyCode switch
        {
            PhysicalKey.Up => -1, PhysicalKey.Down => 1,
            PhysicalKey.PageUp => -visible, PhysicalKey.PageDown => visible,
            PhysicalKey.Home => -items.Length, PhysicalKey.End => items.Length,
            _ => 0
        };
        if (delta != 0) SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, items.Length - 1);
        if (value.KeyCode == (byte)PhysicalKey.Enter && !value.IsRepeat) Activated?.Invoke();
    }

    protected override void DrawContent(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        UiPainter.Frame(surface, Bounds, x, y, Focused && active);
        rowHeight = painter.GlyphHeight + 4;
        visible = Math.Max(1, (Bounds.Height - 8) / rowHeight);
        if (SelectedIndex >= 0) first = Math.Clamp(first, Math.Max(0, SelectedIndex - visible + 1), SelectedIndex);
        for (int row = 0; row < Math.Min(visible, items.Length - first); row++)
        {
            int top = y + Bounds.Y + 4 + row * rowHeight;
            if (first + row == SelectedIndex)
                surface.FillRectangle(x + Bounds.X + 4, top, Bounds.Width - 8, rowHeight, UiPainter.Muted);
            painter.Text(surface, items[first + row], x + Bounds.X + 8, top, Bounds.Width - 16);
        }
    }
}
