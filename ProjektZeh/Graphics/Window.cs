using System;
using ProjektZeh.Apps;

namespace ProjektZeh.Graphics;

internal sealed class Window
{
    public const int TitleHeight = 32;
    public string Title { get; }
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public int Width { get; }
    public int Height { get; }
    public IApplication Application { get; }
    public int ContentX => X + 2;
    public int ContentY => Y + TitleHeight;
    public int ContentWidth => Width - 4;
    public int ContentHeight => Height - TitleHeight - 2;

    public Window(string title, int x, int y, int width, int height, IApplication application)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 96);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 64);
        Title = title;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Application = application;
    }

    public bool Contains(int x, int y) => x >= X && y >= Y && (long)x < (long)X + Width && (long)y < (long)Y + Height;
    public bool ContainsContent(int x, int y) => Contains(x, y) && x >= ContentX &&
        x < X + Width - 2 && y >= ContentY && y < Y + Height - 2;
    public bool ContainsClose(int x, int y) => x >= X + Width - 28 && x < X + Width - 4 &&
        y >= Y + 4 && y < Y + TitleHeight - 4;

    public void Render(PixelSurface surface, byte[] font, int glyphWidth, int glyphHeight, bool active)
    {
        surface.ResetClip();
        surface.FillRectangle(X, Y, Width, Height, unchecked((int)0xff8294aa));
        surface.FillRectangle(X + 2, Y + 2, Width - 4, TitleHeight - 2,
            active ? unchecked((int)0xff285c7b) : unchecked((int)0xff344254));
        surface.SetClip(X + 8, Y + 2, Width - 44, TitleHeight - 2);
        surface.DrawText(Title, X + 8, Y + (TitleHeight - glyphHeight) / 2,
            unchecked((int)0xffedf4fc), font, glyphWidth, glyphHeight);
        surface.ResetClip();
        surface.FillRectangle(X + Width - 28, Y + 4, 24, 24, unchecked((int)0xff9d4553));
        surface.DrawLine(X + Width - 22, Y + 10, X + Width - 10, Y + 22, -1);
        surface.DrawLine(X + Width - 10, Y + 10, X + Width - 22, Y + 22, -1);
        surface.SetClip(ContentX, ContentY, ContentWidth, ContentHeight);
        try { Application.Draw(surface, ContentX, ContentY, ContentWidth, ContentHeight, active); }
        finally { surface.ResetClip(); }
    }
}
