using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.UI;

internal readonly record struct UiRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && (long)x < (long)X + Width && (long)y < (long)Y + Height;
}

internal sealed class UiPainter(byte[] font, int glyphWidth, int glyphHeight)
{
    public const int Background = unchecked((int)0xff202a36);
    public const int Foreground = unchecked((int)0xffe8edf4);
    public const int Accent = unchecked((int)0xff63b3ed);
    public const int Border = unchecked((int)0xff465260);
    public const int Muted = unchecked((int)0xff303c4b);
    public int GlyphWidth { get; } = glyphWidth;
    public int GlyphHeight { get; } = glyphHeight;

    public void Text(PixelSurface surface, ReadOnlySpan<char> text, int x, int y, int width, int color = Foreground)
    {
        int newline = text.IndexOf('\n');
        if (newline >= 0) text = text[..newline];
        surface.DrawText(text[..Math.Min(text.Length, Math.Max(0, width / GlyphWidth))], x, y, color, font, GlyphWidth, GlyphHeight);
    }

    public static void Frame(PixelSurface surface, UiRect bounds, int x, int y, bool focused)
    {
        surface.FillRectangle(x + bounds.X, y + bounds.Y, bounds.Width, bounds.Height, focused ? Accent : Border);
        surface.FillRectangle(x + bounds.X + 2, y + bounds.Y + 2, Math.Max(0, bounds.Width - 4), Math.Max(0, bounds.Height - 4), Background);
    }
}

internal abstract class UiControl
{
    public UiRect Bounds { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Focused { get; internal set; }
    public abstract void Handle(InputEvent value);
    public void Draw(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        var previous = surface.Clip;
        surface.IntersectClip(x + Bounds.X, y + Bounds.Y, Bounds.Width, Bounds.Height);
        try { DrawContent(surface, painter, x, y, active); }
        finally { surface.SetClip(previous.X, previous.Y, previous.Width, previous.Height); }
    }
    protected abstract void DrawContent(PixelSurface surface, UiPainter painter, int x, int y, bool active);
}
