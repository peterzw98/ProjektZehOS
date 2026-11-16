using System;
using System.Collections.Generic;
using System.Text;
using ProjektZeh.Shell;

namespace ProjektZeh.Apps.Terminal;

internal sealed class TerminalBuffer : IProgressOutput
{
    private readonly string?[] lines;
    private readonly bool[] continuation;
    private int columns;
    private int first;
    private int progressRows;
    public int Count { get; private set; }
    public int Columns => columns;
    public long Revision { get; private set; }

    public TerminalBuffer(int columns, int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.columns = columns;
        lines = new string[capacity];
        continuation = new bool[capacity];
    }

    public string GetLine(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return lines[(first + index) % lines.Length]!;
    }

    public bool IsContinuation(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return continuation[(first + index) % lines.Length];
    }

    public void WriteLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        progressRows = 0;
        foreach (string part in text.Split('\n'))
        {
            string line = part.TrimEnd('\r').Replace("\t", "    ", StringComparison.Ordinal);
            if (line.Length == 0) Append("", false);
            for (int start = 0; start < line.Length; start += columns)
                Append(line.Substring(start, Math.Min(columns, line.Length - start)), start > 0);
        }
    }

    public void WriteProgress(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Count -= progressRows;
        long revision = Revision;
        WriteLine(text);
        progressRows = (int)Math.Min(Count, Revision - revision);
    }

    public void Clear()
    {
        Revision++;
        progressRows = 0;
        Array.Clear(lines);
        first = 0;
        Count = 0;
    }

    public void Reflow(int newColumns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newColumns);
        if (newColumns == columns) return;
        bool hasProgress = progressRows > 0;
        var logicalLines = new List<string>();
        var text = new StringBuilder();
        for (int index = 0; index < Count; index++)
        {
            if (index > 0 && !continuation[(first + index) % lines.Length])
            {
                logicalLines.Add(text.ToString());
                text.Clear();
            }
            text.Append(GetLine(index));
        }
        if (Count > 0) logicalLines.Add(text.ToString());
        Clear();
        columns = newColumns;
        for (int index = 0; index < logicalLines.Count; index++)
            if (hasProgress && index == logicalLines.Count - 1) WriteProgress(logicalLines[index]);
            else WriteLine(logicalLines[index]);
    }

    private void Append(string line, bool continuesPrevious)
    {
        Revision++;
        if (Count == lines.Length)
        {
            lines[first] = line;
            continuation[first] = continuesPrevious;
            first = (first + 1) % lines.Length;
        }
        else
        {
            int index = (first + Count++) % lines.Length;
            lines[index] = line;
            continuation[index] = continuesPrevious;
        }
    }
}
