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
    // Was zuletzt in welcher Zeile steht. Die Cosmos-Textkonsole zeichnet jedes Zeichen einzeln mit Hintergrund, Glyphe und
    // Cursor; unter QEMU TCG kostet das etwa 1,6 ms je Zeichen. Status- und Eingabezeile komplett alle 500 ms neu zu schreiben
    // belegte die CPU zu rund 86 % und bremste den Kennwort-Worker auf ein Drittel (#608). Deshalb nur geaenderte Spalten schreiben.
    private string?[] shownRows = [];
    private string? shownStatus;
    public string Prompt { get; set; } = ShellPrompt;

    /// <summary>Nach jedem fremden Schreiben auf die Konsole (Console.Clear, Pager, Moduswechsel) aufrufen: Alles wird neu geschrieben.</summary>
    public void Invalidate()
    {
        revision = -1;
        shownStatus = null;
        Array.Clear(shownRows);
    }

    public void Draw(LineEditor editor, TerminalBuffer buffer, int scrollOffset)
    {
        int rows = Math.Max(1, Console.WindowHeight - 2);
        int first = Math.Max(0, buffer.Count - rows - scrollOffset);
        int width = Math.Max(1, Console.WindowWidth - 1);
        // Reine Zeigerbewegung und die Statusuhr brauchen keinen neuen Ausgabetext.
        if (revision != buffer.Revision || offset != scrollOffset || previousWidth != width || previousRows != rows)
        {
            // Zeile 0 ist die Statuszeile, danach die Ausgabe, zuletzt die Eingabezeile.
            if (shownRows.Length != rows + 2 || previousWidth != width) shownRows = new string?[rows + 2];
            for (int row = 0; row < rows; row++)
            {
                string output = first + row < buffer.Count ? buffer.GetLine(first + row) : "";
                WriteRow(row + 1, GermanFont.ConsoleText(output[..Math.Min(width, output.Length)].PadRight(width)));
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
        WriteRow(inputRow, GermanFont.ConsoleText(FormatLine(visible, Console.WindowWidth, Prompt)));
        Console.SetCursorPosition(Prompt.Length + editor.Cursor - start, inputRow);
    }

    private void WriteRow(int row, string text)
    {
        if (row >= shownRows.Length) Array.Resize(ref shownRows, row + 1);
        WriteChanged(row, text, ref shownRows[row]);
    }

    private static void WriteChanged(int row, string text, ref string? shown)
    {
        if (!TryGetChangedSpan(shown, text, out int start, out int length)) return;
        Console.SetCursorPosition(start, row);
        Console.Write(length == text.Length ? text : text.Substring(start, length));
        shown = text;
    }

    /// <summary>
    /// Spalten, die neu geschrieben werden muessen, damit aus <paramref name="shown"/> <paramref name="text"/> wird: von der ersten bis
    /// zur letzten abweichenden Spalte. Ohne bekannten Stand oder bei anderer Laenge die ganze Zeile; false, wenn nichts zu tun ist.
    /// </summary>
    internal static bool TryGetChangedSpan(string? shown, string text, out int start, out int length)
    {
        start = 0;
        length = text.Length;
        if (shown == null || shown.Length != text.Length) return true;
        int first = 0;
        while (first < text.Length && shown[first] == text[first]) first++;
        if (first == text.Length) { length = 0; return false; }
        int last = text.Length - 1;
        while (shown[last] == text[last]) last--;
        start = first;
        length = last - first + 1;
        return true;
    }

    internal static string FormatLine(string text, int width, string prompt = ShellPrompt) => (prompt + text).PadRight(width - 1);

    public void Status(InputService input, long milliseconds)
    {
        string status = $"ZehOS | Laufzeit: {milliseconds / 1000} s | Tasten: {input.PressedKeyCount} | Verloren: {input.DroppedEvents} | F4: Desktop";
        int columns = Math.Max(1, Console.WindowWidth - 1);
        string text = status[..Math.Min(columns, status.Length)].PadRight(columns);
        if (!TryGetChangedSpan(shownStatus, text, out _, out _)) return;
        int left = Console.CursorLeft;
        int top = Console.CursorTop;
        WriteChanged(0, text, ref shownStatus);
        Console.SetCursorPosition(left, top);
    }
}
