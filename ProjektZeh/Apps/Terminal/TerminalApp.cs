using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;

namespace ProjektZeh.Apps.Terminal;

internal sealed class TerminalApp(ShellService shell, byte[] font, int glyphWidth, int glyphHeight, int columns) : IApplication
{
    private const string Prompt = "zehos> ";
    private int visibleRows = 1;
    private bool cursorVisible = true;
    public TerminalBuffer Buffer { get; } = new(columns);
    public LineEditor Editor { get; } = new();
    public int ScrollOffset { get; private set; }
    public bool IsClosed { get; private set; }

    public void Start()
    {
        Buffer.WriteLine("ZehOS Terminal - Hilfe: help");
        Buffer.WriteLine("PageUp/PageDown: Ausgabe scrollen.");
    }

    public void Handle(InputEvent value)
    {
        if (IsClosed) return;
        if (value.Type == InputEventType.KeyDown && !value.IsTextKey && value.KeyCode == (byte)PhysicalKey.PageUp)
        {
            ScrollOffset = Math.Min(Math.Max(0, Buffer.Count - visibleRows), ScrollOffset + visibleRows);
            return;
        }
        if (value.Type == InputEventType.KeyDown && !value.IsTextKey && value.KeyCode == (byte)PhysicalKey.PageDown)
        {
            ScrollOffset = Math.Max(0, ScrollOffset - visibleRows);
            return;
        }
        if (!Editor.Handle(value, out string? command)) return;
        ScrollOffset = 0;
        cursorVisible = true;
        if (command == null) return;
        Buffer.WriteLine(Prompt + command);
        var result = shell.ExecuteLine(command, Buffer);
        if (!result.IsOK()) Buffer.WriteLine(result.Message);
    }

    public void Update(long milliseconds) => cursorVisible = milliseconds / 500 % 2 == 0;

    public void Resize(int width, int height)
    {
        Buffer.Reflow(Math.Max(1, (width - 16) / glyphWidth));
        visibleRows = Math.Max(1, (height - 16) / glyphHeight - 1);
        ScrollOffset = Math.Clamp(ScrollOffset, 0, Math.Max(0, Buffer.Count - visibleRows));
    }

    public void Draw(PixelSurface surface, int x, int y, int width, int height, bool active)
    {
        surface.FillRectangle(x, y, width, height, unchecked((int)0xff101923));
        visibleRows = Math.Max(1, (height - 16) / glyphHeight - 1);
        ScrollOffset = Math.Clamp(ScrollOffset, 0, Math.Max(0, Buffer.Count - visibleRows));
        int first = Math.Max(0, Buffer.Count - visibleRows - ScrollOffset);
        int end = Math.Min(Buffer.Count, first + visibleRows);
        for (int index = first; index < end; index++)
            Text(surface, Buffer.GetLine(index), x + 8, y + 8 + (index - first) * glyphHeight, unchecked((int)0xffedf4fc));

        int inputY = y + height - glyphHeight - 8;
        int inputColumns = Math.Max(1, (width - 16) / glyphWidth - Prompt.Length - 1);
        string text = Editor.Text;
        int start = Math.Max(0, Editor.Cursor - inputColumns + 1);
        string visible = text.Substring(start, Math.Min(inputColumns, text.Length - start));
        Text(surface, Prompt + visible, x + 8, inputY, unchecked((int)0xff53d7bb));
        if (active && cursorVisible)
            surface.FillRectangle(x + 8 + (Prompt.Length + Editor.Cursor - start) * glyphWidth,
                inputY + glyphHeight - 2, glyphWidth, 2, unchecked((int)0xff53d7bb));
    }

    public void Close()
    {
        Buffer.Clear();
        IsClosed = true;
    }

    private void Text(PixelSurface surface, string text, int x, int y, int color) =>
        surface.DrawText(text, x, y, color, font, glyphWidth, glyphHeight);
}
