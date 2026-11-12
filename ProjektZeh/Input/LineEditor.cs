using System;
using System.Collections.Generic;
using System.Text;

namespace ProjektZeh.Input;

internal sealed class LineEditor
{
    public const int MaximumLength = 66560;
    public const int HistoryCapacity = 32;
    private readonly StringBuilder text = new();
    private readonly List<string> history = [];
    private int historyIndex;
    private string draft = "";
    private int draftCursor;

    public string Text => text.ToString();
    public int Cursor { get; private set; }

    public bool Handle(InputEvent value, out string? command)
    {
        command = null;
        if (value.Type == InputEventType.Text)
        {
            if (char.IsControl(value.Character) || text.Length == MaximumLength) return false;
            text.Insert(Cursor++, value.Character);
            return true;
        }
        if (value.Type != InputEventType.KeyDown || value.IsTextKey) return false;

        switch ((PhysicalKey)value.KeyCode)
        {
            case PhysicalKey.Enter:
                command = Text;
                if (!string.IsNullOrWhiteSpace(command) &&
                    (history.Count == 0 || history[^1] != command))
                {
                    if (history.Count == HistoryCapacity) history.RemoveAt(0);
                    history.Add(command);
                }
                text.Clear();
                Cursor = 0;
                historyIndex = history.Count;
                draft = "";
                return true;
            case PhysicalKey.Backspace when Cursor > 0:
                text.Remove(--Cursor, 1);
                return true;
            case PhysicalKey.Delete when Cursor < text.Length:
                text.Remove(Cursor, 1);
                return true;
            case PhysicalKey.Left when Cursor > 0:
                Cursor--;
                return true;
            case PhysicalKey.Right when Cursor < text.Length:
                Cursor++;
                return true;
            case PhysicalKey.Home:
                Cursor = 0;
                return true;
            case PhysicalKey.End:
                Cursor = text.Length;
                return true;
            case PhysicalKey.Up when historyIndex > 0:
                if (historyIndex == history.Count)
                {
                    draft = Text;
                    draftCursor = Cursor;
                }
                Replace(history[--historyIndex]);
                return true;
            case PhysicalKey.Down when historyIndex < history.Count:
                historyIndex++;
                Replace(historyIndex == history.Count ? draft : history[historyIndex]);
                if (historyIndex == history.Count) Cursor = draftCursor;
                return true;
            default:
                return false;
        }
    }

    private void Replace(string value)
    {
        text.Clear();
        text.Append(value);
        Cursor = text.Length;
    }
}
