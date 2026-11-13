using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.UI;

internal readonly record struct UiBounds(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && (long)x < (long)X + Width && (long)y < (long)Y + Height;
}

internal sealed class UiPainter(byte[] font, int glyphWidth, int glyphHeight)
{
    public const int Background = unchecked((int)0xff202a36);
    public const int Panel = unchecked((int)0xff18212c);
    public const int TextColor = unchecked((int)0xffe8edf4);
    public const int Muted = unchecked((int)0xffa0afc1);
    public const int Accent = unchecked((int)0xff63b3ed);

    public void Text(PixelSurface surface, string text, int x, int y, int color = TextColor) =>
        surface.DrawText(text, x, y, color, font, glyphWidth, glyphHeight);

    public static void Box(PixelSurface surface, UiBounds bounds, bool focused, int fill = Background)
    {
        surface.FillRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height, focused ? Accent : unchecked((int)0xff465260));
        surface.FillRectangle(bounds.X + 1, bounds.Y + 1, bounds.Width - 2, bounds.Height - 2, fill);
    }

    public void Label(PixelSurface surface, UiBounds bounds, string text, int color = TextColor)
    {
        surface.SetClip(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        Text(surface, text, bounds.X + 10, bounds.Y + Math.Max(0, (bounds.Height - glyphHeight) / 2), color);
        surface.ResetClip();
    }
}

internal sealed class UiButton(string text)
{
    public UiBounds Bounds { get; set; }
    public bool Focused { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Handle(InputEvent value) => Enabled &&
        ((value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left && Bounds.Contains(value.X, value.Y)) ||
         (Focused && value.Type == InputEventType.KeyDown && !value.IsRepeat &&
          value.KeyCode is (byte)PhysicalKey.Enter or (byte)PhysicalKey.Space));

    public void Draw(PixelSurface surface, UiPainter painter)
    {
        UiPainter.Box(surface, Bounds, Focused);
        painter.Label(surface, Bounds, text, Enabled ? UiPainter.TextColor : UiPainter.Muted);
    }
}

internal sealed class UiTextField
{
    private readonly LineEditor editor = new();
    public UiBounds Bounds { get; set; }
    public bool Focused { get; set; }
    public string Text => editor.Text;

    public bool Handle(InputEvent value)
    {
        if (!Focused || (value.Type == InputEventType.Text && editor.Text.Length >= 64)) return false;
        if (value.Type == InputEventType.KeyDown && value.KeyCode is (byte)PhysicalKey.Enter or (byte)PhysicalKey.Up or (byte)PhysicalKey.Down)
            return false;
        return editor.Handle(value, out _);
    }

    public void Draw(PixelSurface surface, UiPainter painter)
    {
        UiPainter.Box(surface, Bounds, Focused, UiPainter.Panel);
        painter.Label(surface, Bounds, Text.Length == 0 && !Focused ? "Programme suchen" : Text + (Focused ? "|" : ""));
    }
}

internal sealed class UiList
{
    public const int RowHeight = 40;
    private string[] items = [];
    private int first;
    public UiBounds Bounds { get; set; }
    public bool Focused { get; set; }
    public int SelectedIndex { get; private set; }
    public int Count => items.Length;

    public void SetItems(string[] values)
    {
        items = values;
        SelectedIndex = 0;
        first = 0;
    }

    public void Handle(InputEvent value)
    {
        if (items.Length == 0) return;
        int rows = Math.Max(1, (Bounds.Height - 2) / RowHeight);
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left && Bounds.Contains(value.X, value.Y))
            SelectedIndex = Math.Min(items.Length - 1, first + (value.Y - Bounds.Y) / RowHeight);
        else if (Focused && value.Type == InputEventType.KeyDown && !value.IsTextKey)
        {
            int offset = value.KeyCode switch
            {
                (byte)PhysicalKey.Up => -1,
                (byte)PhysicalKey.Down => 1,
                (byte)PhysicalKey.PageUp => -rows,
                (byte)PhysicalKey.PageDown => rows,
                (byte)PhysicalKey.Home => -items.Length,
                (byte)PhysicalKey.End => items.Length,
                _ => 0
            };
            SelectedIndex = Math.Clamp(SelectedIndex + offset, 0, items.Length - 1);
        }
        first = Math.Clamp(first, Math.Max(0, SelectedIndex - rows + 1), SelectedIndex);
    }

    public void Draw(PixelSurface surface, UiPainter painter)
    {
        UiPainter.Box(surface, Bounds, Focused, UiPainter.Panel);
        int rows = Math.Max(0, (Bounds.Height - 2) / RowHeight);
        for (int row = 0; row < rows && first + row < items.Length; row++)
        {
            var bounds = new UiBounds(Bounds.X + 3, Bounds.Y + 2 + row * RowHeight, Bounds.Width - 6, RowHeight);
            if (first + row == SelectedIndex) surface.FillRectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height, unchecked((int)0xff315570));
            painter.Label(surface, bounds, items[first + row]);
        }
        if (items.Length == 0) painter.Label(surface, Bounds, "Keine Programme gefunden", UiPainter.Muted);
    }
}
