using System;
using ProjektZeh.Apps;
using ProjektZeh.UI;

namespace ProjektZeh.Graphics;

internal sealed class Window
{
    public const int TitleHeight = 40;
    public string Title { get; }
    public AppIcon? Icon { get; init; }
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public int MinimumWidth { get; }
    public int MinimumHeight { get; }
    public bool IsMinimized { get; internal set; }
    public bool IsMaximized { get; internal set; }
    internal (int X, int Y, int Width, int Height) RestoreBounds { get; set; }
    public IApplication Application { get; }
    public int ContentX => X + 1;
    public int ContentY => Y + TitleHeight;
    public int ContentWidth => Width - 2;
    public int ContentHeight => Height - TitleHeight - 1;

    public Window(string title, int x, int y, int width, int height, IApplication application,
        int minimumWidth = 96, int minimumHeight = 64)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 96);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumWidth, 96);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumHeight, 64);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumWidth, width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumHeight, height);
        Title = title;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        MinimumWidth = minimumWidth;
        MinimumHeight = minimumHeight;
        Application = application;
    }

    public bool Contains(int x, int y) => x >= X && y >= Y && (long)x < (long)X + Width && (long)y < (long)Y + Height;
    public bool ContainsContent(int x, int y) => Contains(x, y) && x >= ContentX &&
        x < X + Width - 1 && y >= ContentY && y < Y + Height - 1;
    public bool ContainsClose(int x, int y) => x >= X + Width - 38 && x < X + Width - 2 &&
        y >= Y + 4 && y < Y + TitleHeight - 4;
    public bool ContainsMaximize(int x, int y) => x >= X + Width - 74 && x < X + Width - 38 &&
        y >= Y + 4 && y < Y + TitleHeight - 4;
    public bool ContainsMinimize(int x, int y) => x >= X + Width - 110 && x < X + Width - 74 &&
        y >= Y + 4 && y < Y + TitleHeight - 4;

    public void Render(PixelSurface surface, byte[] font, int glyphWidth, int glyphHeight, bool active)
    {
        surface.ResetClip();
        // Fester Versatzschatten statt Unschaerfe; nur die sichtbaren Streifen fuellen, nicht die Fensterflaeche.
        int outer = UiPainter.Dark ? unchecked((int)0xff0c0f14) : unchecked((int)0xffaab4c1);
        int inner = UiPainter.Dark ? unchecked((int)0xff12161c) : unchecked((int)0xff98a3b1);
        surface.FillRectangle(X + Width, Y + 8, 6, Height, outer);
        surface.FillRectangle(X + 6, Y + Height, Width, 8, outer);
        surface.FillRectangle(X + Width, Y + 4, 3, Height, inner);
        surface.FillRectangle(X + 3, Y + Height, Width, 4, inner);
        int frame = active ? UiPainter.Mix(UiPainter.Accent, UiPainter.Border, 45) : UiPainter.Border;
        surface.FillRectangle(X, Y, Width, 1, frame);
        surface.FillRectangle(X, Y + Height - 1, Width, 1, frame);
        surface.FillRectangle(X, Y, 1, Height, frame);
        surface.FillRectangle(X + Width - 1, Y, 1, Height, frame);
        surface.FillRectangle(X + 1, Y + 1, Width - 2, TitleHeight - 1, UiPainter.Panel);
        surface.FillRectangle(X + 1, Y + TitleHeight - 1, Width - 2, 1, UiPainter.Border);
        int text = active ? UiPainter.Foreground : UiPainter.Secondary;
        int left = X + 12;
        if (Icon is AppIcon icon)
        {
            AppIcons.Draw(surface, icon, left, Y + (TitleHeight - AppIcons.Size) / 2, active ? UiPainter.Accent : UiPainter.Secondary);
            left += AppIcons.Size + 10;
        }
        surface.SetClip(left, Y + 1, Math.Max(0, X + Width - 120 - left), TitleHeight - 2);
        surface.DrawText(Title, left, Y + (TitleHeight - glyphHeight) / 2, text, font, glyphWidth, glyphHeight);
        surface.ResetClip();
        int middle = Y + TitleHeight / 2;
        surface.FillRectangle(X + Width - 98, middle + 4, 12, 2, text);
        surface.FillRectangle(X + Width - 62, middle - 6, 12, 2, text);
        surface.FillRectangle(X + Width - 62, middle + 4, 12, 2, text);
        surface.FillRectangle(X + Width - 62, middle - 6, 2, 12, text);
        surface.FillRectangle(X + Width - 52, middle - 6, 2, 12, text);
        surface.DrawLine(X + Width - 26, middle - 6, X + Width - 14, middle + 6, text);
        surface.DrawLine(X + Width - 25, middle - 6, X + Width - 13, middle + 6, text);
        surface.DrawLine(X + Width - 14, middle - 6, X + Width - 26, middle + 6, text);
        surface.DrawLine(X + Width - 13, middle - 6, X + Width - 25, middle + 6, text);
        surface.SetClip(ContentX, ContentY, ContentWidth, ContentHeight);
        try { Application.Draw(surface, ContentX, ContentY, ContentWidth, ContentHeight, active); }
        finally { surface.ResetClip(); }
    }
}
