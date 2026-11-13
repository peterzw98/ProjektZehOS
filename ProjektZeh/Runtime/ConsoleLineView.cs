using System;
using ProjektZeh.Input;

namespace ProjektZeh.Runtime;

internal sealed class ConsoleLineView
{
    private const string Prompt = "zehos> ";
    private int row;

    public void Begin()
    {
        if (Console.CursorTop == 0) Console.WriteLine();
        row = Console.CursorTop;
    }

    public void Draw(LineEditor editor)
    {
        int columns = Math.Max(1, Console.WindowWidth - Prompt.Length - 1);
        string text = editor.Text;
        int start = Math.Max(0, editor.Cursor - columns + 1);
        string visible = text.Substring(start, Math.Min(columns, text.Length - start));
        Console.SetCursorPosition(0, row);
        Console.Write(FormatLine(visible, Console.WindowWidth));
        Console.SetCursorPosition(Prompt.Length + editor.Cursor - start, row);
    }

    public void Submit(string command)
    {
        Console.SetCursorPosition(0, row);
        Console.WriteLine(FormatLine(command, Console.WindowWidth));
    }

    internal static string FormatLine(string text, int width) => (Prompt + text).PadRight(width - 1);

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
