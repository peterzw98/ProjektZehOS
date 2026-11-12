using System;
using ProjektZeh.Shell;

namespace ProjektZeh.Apps.Terminal;

internal sealed class TerminalBuffer : ITextOutput
{
    private readonly string?[] lines;
    private readonly int columns;
    private int first;
    public int Count { get; private set; }

    public TerminalBuffer(int columns, int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.columns = columns;
        lines = new string[capacity];
    }

    public string GetLine(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return lines[(first + index) % lines.Length]!;
    }

    public void WriteLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string part in text.Split('\n'))
        {
            string line = part.TrimEnd('\r').Replace("\t", "    ", StringComparison.Ordinal);
            if (line.Length == 0) Append("");
            for (int start = 0; start < line.Length; start += columns)
                Append(line.Substring(start, Math.Min(columns, line.Length - start)));
        }
    }

    public void Clear()
    {
        Array.Clear(lines);
        first = 0;
        Count = 0;
    }

    private void Append(string line)
    {
        if (Count == lines.Length)
        {
            lines[first] = line;
            first = (first + 1) % lines.Length;
        }
        else lines[(first + Count++) % lines.Length] = line;
    }
}
