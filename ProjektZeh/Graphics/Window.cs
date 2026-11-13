using System;
using ProjektZeh.Apps;

namespace ProjektZeh.Graphics;

internal sealed class Window
{
    public const int TitleHeight = 32;
    public string Title { get; }
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
    public int ContentX => X + 2;
    public int ContentY => Y + TitleHeight;
    public int ContentWidth => Width - 4;
    public int ContentHeight => Height - TitleHeight - 2;

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
        x < X + Width - 2 && y >= ContentY && y < Y + Height - 2;
    public bool ContainsClose(int x, int y) => x >= X + Width - 28 && x < X + Width - 4 &&
        y >= Y + 4 && y < Y + TitleHeight - 4;
    public bool ContainsMaximize(int x, int y) => x >= X + Width - 60 && x < X + Width - 36 &&
        y >= Y + 4 && y < Y + TitleHeight - 4;
    public bool ContainsMinimize(int x, int y) => x >= X + Width - 92 && x < X + Width - 68 &&
        y >= Y + 4 && y < Y + TitleHeight - 4;

    public void Render(PixelSurface surface, byte[] font, int glyphWidth, int glyphHeight, bool active)
    {
        surface.ResetClip();
        surface.FillRectangle(X + 4, Y + 5, Width, Height, unchecked((int)0xff10151c));
        surface.FillRectangle(X, Y, Width, Height, active ? unchecked((int)0xff63b3ed) : unchecked((int)0xff465260));
        surface.FillRectangle(X + 2, Y + 2, Width - 4, TitleHeight - 2,
            active ? unchecked((int)0xff303c4b) : unchecked((int)0xff28313d));
        surface.SetClip(X + 10, Y + 2, Math.Max(0, Width - 110), TitleHeight - 2);
        surface.DrawText(Title, X + 8, Y + (TitleHeight - glyphHeight) / 2,
            unchecked((int)0xffedf4fc), font, glyphWidth, glyphHeight);
        surface.ResetClip();
        surface.DrawLine(X + Width - 86, Y + 21, X + Width - 74, Y + 21, -1);
        surface.FillRectangle(X + Width - 54, Y + 10, 12, 12, -1);
        surface.FillRectangle(X + Width - 52, Y + 12, 8, 8, unchecked((int)0xff303c4b));
        surface.FillRectangle(X + Width - 28, Y + 4, 24, 24, unchecked((int)0xff9d4553));
        surface.DrawLine(X + Width - 22, Y + 10, X + Width - 10, Y + 22, -1);
        surface.DrawLine(X + Width - 10, Y + 10, X + Width - 22, Y + 22, -1);
        surface.SetClip(ContentX, ContentY, ContentWidth, ContentHeight);
        try { Application.Draw(surface, ContentX, ContentY, ContentWidth, ContentHeight, active); }
        finally { surface.ResetClip(); }
    }
}
