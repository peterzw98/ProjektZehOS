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
    public static readonly string[] AccentNames = ["Blau", "Tuerkis", "Violett", "Orange", "Magenta", "Graphit"];
    // Helle Akzente fuer dunkle Flaechen, dunklere fuer helle Flaechen; beide mit lesbarem Kontrast.
    private static readonly uint[] DarkAccents = [0xff3daee9, 0xff3cc8b4, 0xffa98ae6, 0xfff08a4b, 0xffe46aa6, 0xffb5bdc8];
    private static readonly uint[] LightAccents = [0xff1a73b5, 0xff0f7f72, 0xff6d4fc2, 0xffc2410c, 0xffb0306f, 0xff4b5563];
    public static int Background { get; private set; }
    public static int Panel { get; private set; }
    public static int Foreground { get; private set; }
    public static int Secondary { get; private set; }
    public static int Border { get; private set; }
    public static int Muted { get; private set; }
    public static int Accent { get; private set; }
    public static int AccentText { get; private set; }
    public static int Selection { get; private set; }
    public static int Danger { get; private set; }
    public static int Success { get; private set; }
    public static int Warning { get; private set; }
    public static bool Dark { get; private set; }

    static UiPainter() => SetTheme(true, 0);

    public static void SetTheme(bool dark, int accent)
    {
        accent = Math.Clamp(accent, 0, DarkAccents.Length - 1);
        Dark = dark;
        Background = Color(dark ? 0xff22272f : 0xffffffff);
        Panel = Color(dark ? 0xff1c2027 : 0xfff0f2f5);
        Foreground = Color(dark ? 0xffe6e8eb : 0xff1e2329);
        Secondary = Color(dark ? 0xffa3abb6 : 0xff5d6672);
        Border = Color(dark ? 0xff3a414c : 0xffc9cfd7);
        Muted = Color(dark ? 0xff2c323c : 0xffe9ecf0);
        Accent = Color(dark ? DarkAccents[accent] : LightAccents[accent]);
        AccentText = Color(dark ? 0xff0b1a24 : 0xffffffff);
        Selection = Mix(Accent, Background, 24);
        Danger = Color(dark ? 0xffef6b6b : 0xffb42318);
        Success = Color(dark ? 0xff5fd08a : 0xff1f6f43);
        Warning = Color(dark ? 0xffe8b04a : 0xff9a5b00);
    }

    public static int AccentPreview(int accent) =>
        Color((Dark ? DarkAccents : LightAccents)[Math.Clamp(accent, 0, DarkAccents.Length - 1)]);

    /// <summary>Mischt <paramref name="percent"/> Prozent von <paramref name="first"/> in <paramref name="second"/>.</summary>
    public static int Mix(int first, int second, int percent)
    {
        int Channel(int shift) => (((first >> shift) & 0xff) * percent + ((second >> shift) & 0xff) * (100 - percent)) / 100;
        return unchecked((int)0xff000000) | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    private static int Color(uint value) => unchecked((int)value);

    public int GlyphWidth { get; } = glyphWidth;
    public int GlyphHeight { get; } = glyphHeight;

    public void Text(PixelSurface surface, ReadOnlySpan<char> text, int x, int y, int width, int? color = null)
    {
        int newline = text.IndexOf('\n');
        if (newline >= 0) text = text[..newline];
        surface.DrawText(text[..Math.Min(text.Length, Math.Max(0, width / GlyphWidth))], x, y, color ?? Foreground, font, GlyphWidth, GlyphHeight);
    }

    /// <summary>Bricht an Leerzeichen um und liefert die belegte Hoehe.</summary>
    public int Paragraph(PixelSurface surface, string text, int x, int y, int width, int? color = null)
    {
        int columns = Math.Max(1, width / GlyphWidth);
        int top = y;
        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(text.Length, start + columns);
            if (end < text.Length)
            {
                int space = text.LastIndexOf(' ', end, end - start);
                if (space > start) end = space;
            }
            Text(surface, text.AsSpan(start, end - start), x, top, width, color);
            top += GlyphHeight + 4;
            start = end;
            while (start < text.Length && text[start] == ' ') start++;
        }
        return top - y;
    }

    public static void Frame(PixelSurface surface, UiRect bounds, int x, int y, bool focused)
    {
        int inset = focused ? 2 : 1;
        surface.FillRectangle(x + bounds.X, y + bounds.Y, bounds.Width, bounds.Height, focused ? Accent : Border);
        surface.FillRectangle(x + bounds.X + inset, y + bounds.Y + inset, Math.Max(0, bounds.Width - 2 * inset),
            Math.Max(0, bounds.Height - 2 * inset), Background);
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
