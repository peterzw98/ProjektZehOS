using System;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Input;
using ProjektZeh.Shell;

namespace ProjektZeh.Graphics;

internal sealed class Desktop
{
    public const int MaximumWindows = 8;
    private const int WindowWidth = 640;
    private const int WindowHeight = 400;
    private const int BarHeight = 56;
    private readonly int width;
    private readonly int height;
    private readonly byte[] font;
    private readonly int glyphWidth;
    private readonly int glyphHeight;
    private readonly Func<ShellService> createShell;
    private int sequence;
    public WindowManager Windows { get; }

    public Desktop(int width, int height, byte[] font, int glyphWidth, int glyphHeight, Func<ShellService> createShell)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, WindowWidth);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, WindowHeight + BarHeight);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(createShell);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphHeight);
        this.width = width;
        this.height = height;
        this.font = font;
        this.glyphWidth = glyphWidth;
        this.glyphHeight = glyphHeight;
        this.createShell = createShell;
        Windows = new WindowManager(width, height - BarHeight);
        OpenTerminal();
        OpenTerminal();
    }

    public bool OpenTerminal()
    {
        if (Windows.Count == MaximumWindows) return false;
        Windows.Reset();
        int offset = sequence % 4 * 60;
        var terminal = new TerminalApp(createShell(), font, glyphWidth, glyphHeight, (WindowWidth - 20) / glyphWidth);
        Windows.Add(new Window($"Terminal {++sequence}", 72 + offset, 72 + offset, WindowWidth, WindowHeight, terminal));
        return true;
    }

    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left &&
            value.X >= 16 && value.X < 192 && value.Y >= height - 44 && value.Y < height - 12)
        {
            OpenTerminal();
            return;
        }
        Windows.Handle(value);
    }

    public void Update(long milliseconds) => Windows.Update(milliseconds);

    public void Render(PixelSurface surface, long milliseconds, int pointerX, int pointerY, PointerButtons buttons)
    {
        surface.ResetClip();
        surface.FillRectangle(0, 0, width, height, unchecked((int)0xff142236));
        Text(surface, "ZehOS", 24, 20, unchecked((int)0xffedf4fc));
        Text(surface, "Esc/F4: Textshell | Titelleiste ziehen | X: Schliessen", 24, 44, unchecked((int)0xff53d7bb));
        Windows.Render(surface, font, glyphWidth, glyphHeight);
        surface.FillRectangle(0, height - BarHeight, width, BarHeight, unchecked((int)0xff213b55));
        surface.FillRectangle(16, height - 44, 176, 32, Windows.Count < MaximumWindows ? unchecked((int)0xff285c7b) : unchecked((int)0xff344254));
        Text(surface, "+ Terminal", 28, height - 36, unchecked((int)0xffedf4fc));
        Text(surface, $"Fenster: {Windows.Count}/{MaximumWindows} | Laufzeit: {milliseconds / 1000} s", 216, height - 36, unchecked((int)0xffedf4fc));
        int color = buttons == PointerButtons.None ? -1 : unchecked((int)0xffffbf69);
        for (int row = 0; row < 15; row++) surface.FillRectangle(pointerX, pointerY + row, row / 2 + 1, 1, unchecked((int)0xff000000));
        surface.DrawLine(pointerX, pointerY, pointerX, pointerY + 14, color);
        surface.DrawLine(pointerX, pointerY, pointerX + 7, pointerY + 14, color);
        surface.DrawLine(pointerX, pointerY + 14, pointerX + 7, pointerY + 14, color);
    }

    private void Text(PixelSurface surface, string text, int x, int y, int color) =>
        surface.DrawText(text, x, y, color, font, glyphWidth, glyphHeight);
}
