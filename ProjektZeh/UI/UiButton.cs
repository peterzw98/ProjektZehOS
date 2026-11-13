using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.UI;

internal sealed class UiButton(string label, Action activate) : UiControl
{
    private bool pressed;
    public override void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset) { pressed = false; return; }
        if (!Enabled) return;
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
            pressed = Bounds.Contains(value.X, value.Y);
        if (value.Type == InputEventType.PointerUp && value.Button == PointerButtons.Left)
        {
            bool invoke = pressed && Bounds.Contains(value.X, value.Y);
            pressed = false;
            if (invoke) activate();
        }
        if (value.Type == InputEventType.KeyDown && !value.IsRepeat &&
            value.KeyCode is (byte)PhysicalKey.Enter or (byte)PhysicalKey.Space) activate();
    }

    protected override void DrawContent(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        UiPainter.Frame(surface, Bounds, x, y, active && Focused && Enabled);
        surface.FillRectangle(x + Bounds.X + 2, y + Bounds.Y + 2, Bounds.Width - 4, Bounds.Height - 4,
            Enabled && pressed ? unchecked((int)0xff285c7b) : UiPainter.Muted);
        painter.Text(surface, label, x + Bounds.X + 8, y + Bounds.Y + (Bounds.Height - painter.GlyphHeight) / 2,
            Bounds.Width - 16, Enabled ? UiPainter.Foreground : UiPainter.Border);
    }
}
