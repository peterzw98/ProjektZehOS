using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.UI;

internal sealed class UiButton(string label, Action activate) : UiControl
{
    private bool pressed;
    public string Label { get; set; } = label;
    /// <summary>Hauptaktion eines Bereichs in Akzentfarbe.</summary>
    public bool Primary { get; set; }
    /// <summary>Gewaehlter Zustand einer Auswahlgruppe.</summary>
    public bool Selected { get; set; }
    /// <summary>Farbfeld statt Beschriftung, etwa fuer die Akzentauswahl.</summary>
    public int? Swatch { get; set; }
    /// <summary>Flache Symbolschaltflaeche; die Beschriftung dient dann nur der Benennung.</summary>
    public AppIcon? Icon { get; set; }

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
        bool highlighted = active && Focused && Enabled || Selected;
        int inset = highlighted ? 2 : 1;
        if (Icon is AppIcon icon)
        {
            // Ohne Flaeche wie in Windows 11; Fokus und Druck bleiben sichtbar.
            if (highlighted || pressed)
                UiPainter.RoundFrame(surface, x + Bounds.X, y + Bounds.Y, Bounds.Width, Bounds.Height, 8, inset,
                    highlighted ? UiPainter.Accent : UiPainter.Border, pressed ? UiPainter.Selection : UiPainter.Muted);
            AppIcons.Draw(surface, icon, x + Bounds.X + (Bounds.Width - AppIcons.Size) / 2, y + Bounds.Y + (Bounds.Height - AppIcons.Size) / 2,
                Enabled ? UiPainter.Foreground : UiPainter.Secondary);
            return;
        }
        int fill = !Enabled ? UiPainter.Background
            : Primary ? (pressed ? UiPainter.Mix(UiPainter.Accent, UiPainter.Foreground, 80) : UiPainter.Accent)
            : pressed || Selected ? UiPainter.Selection : UiPainter.Muted;
        // Abgerundete Flaechen wie in aktuellen Desktops; der Rand zeigt weiterhin den Fokus.
        UiPainter.RoundFrame(surface, x + Bounds.X, y + Bounds.Y, Bounds.Width, Bounds.Height, 8, inset,
            highlighted ? UiPainter.Accent : UiPainter.Border, fill);
        if (Swatch is int color)
        {
            surface.FillRectangle(x + Bounds.X + 6, y + Bounds.Y + 6, Bounds.Width - 12, Bounds.Height - 12, color);
            return;
        }
        int textColor = !Enabled ? UiPainter.Secondary : Primary ? UiPainter.AccentText : Selected ? UiPainter.Accent : UiPainter.Foreground;
        int width = Math.Min(Bounds.Width - 16, Label.Length * painter.GlyphWidth);
        painter.Text(surface, Label, x + Bounds.X + (Bounds.Width - width) / 2,
            y + Bounds.Y + (Bounds.Height - painter.GlyphHeight) / 2, Bounds.Width - 16, textColor);
    }
}
