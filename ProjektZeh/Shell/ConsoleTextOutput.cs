using System;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Input;
using ProjektZeh.Graphics;

namespace ProjektZeh.Shell;

/// <summary>
/// Schreibt auf die Cosmos-Konsole.
/// </summary>
internal sealed class ConsoleTextOutput : ITextOutput
{
    private bool interactive;
    private int visibleRows = 1;
    public TerminalBuffer Buffer { get; } = new(80);
    public int ScrollOffset { get; private set; }

    public void StartInteractive(int columns, int rows)
    {
        Buffer.Reflow(columns);
        visibleRows = Math.Max(1, rows);
        ScrollOffset = Math.Clamp(ScrollOffset, 0, Math.Max(0, Buffer.Count - visibleRows));
        interactive = true;
    }

    public bool HandleScroll(InputEvent value)
    {
        long delta;
        if (value.Type == InputEventType.PointerScroll) delta = (long)value.ScrollDelta * 3;
        else if (value.Type == InputEventType.KeyDown && !value.IsTextKey && value.KeyCode == (byte)PhysicalKey.PageUp) delta = visibleRows;
        else if (value.Type == InputEventType.KeyDown && !value.IsTextKey && value.KeyCode == (byte)PhysicalKey.PageDown) delta = -visibleRows;
        else return false;
        ScrollOffset = (int)Math.Clamp(ScrollOffset + delta, 0, Math.Max(0, Buffer.Count - visibleRows));
        return true;
    }

    public void FollowOutput() => ScrollOffset = 0;

    /// <summary>
    /// Schreibt eine Textzeile.
    /// </summary>
    /// <param name="text"></param>
    public void WriteLine(string text)
    {
        Buffer.WriteLine(text);
        if (!interactive) Console.WriteLine(GermanFont.ConsoleText(text));
    }

    /// <summary>
    /// Leert die Konsole.
    /// </summary>
    public void Clear()
    {
        Buffer.Clear();
        ScrollOffset = 0;
        if (!interactive) Console.Clear();
    }
}
