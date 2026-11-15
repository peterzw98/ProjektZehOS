using System;
using ProjektZeh.Input;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Graphics;

namespace ProjektZeh.Runtime;

internal sealed class ConsoleLineView
{
    public const string ShellPrompt = "zehos> ";
    private long revision = -1;
    private int offset = -1;
    private int previousWidth;
    private int previousRows;
    public string Prompt { get; set; } = ShellPrompt;

    public void Invalidate() => revision = -1;

    public void Draw(LineEditor editor, TerminalBuffer buffer, int scrollOffset)
    {
        int rows = Math.Max(1, Console.WindowHeight - 2);
        int first = Math.Max(0, buffer.Count - rows - scrollOffset);
        int width = Math.Max(1, Console.WindowWidth - 1);
        // Reine Zeigerbewegung und die Statusuhr brauchen keinen neuen Ausgabetext.
        if (revision != buffer.Revision || offset != scrollOffset || previousWidth != width || previousRows != rows)
        {
            for (int row = 0; row < rows; row++)
            {
                Console.SetCursorPosition(0, row + 1);
                string output = first + row < buffer.Count ? buffer.GetLine(first + row) : "";
                Console.Write(GermanFont.ConsoleText(output[..Math.Min(width, output.Length)].PadRight(width)));
            }
            revision = buffer.Revision;
            offset = scrollOffset;
            previousWidth = width;
            previousRows = rows;
        }
        int inputRow = Math.Max(1, Console.WindowHeight - 1);
        int columns = Math.Max(1, Console.WindowWidth - Prompt.Length - 1);
        string text = editor.DisplayText;
        int start = Math.Max(0, editor.Cursor - columns + 1);
        string visible = text.Substring(start, Math.Min(columns, text.Length - start));
        Console.SetCursorPosition(0, inputRow);
        Console.Write(GermanFont.ConsoleText(FormatLine(visible, Console.WindowWidth, Prompt)));
        Console.SetCursorPosition(Prompt.Length + editor.Cursor - start, inputRow);
    }

    internal static string FormatLine(string text, int width, string prompt = ShellPrompt) => (prompt + text).PadRight(width - 1);

    public static void Status(InputService input, long milliseconds)
    {
        int left = Console.CursorLeft;
        int top = Console.CursorTop;
        string status = $"ZehOS | Laufzeit: {milliseconds / 1000} s | Tasten: {input.PressedKeyCount} | Verloren: {input.DroppedEvents} | F4: Desktop";
        int columns = Math.Max(1, Console.WindowWidth - 1);
        Console.SetCursorPosition(0, 0);
        Console.Write(status[..Math.Min(columns, status.Length)].PadRight(columns));
        Console.SetCursorPosition(left, top);
    }
}
