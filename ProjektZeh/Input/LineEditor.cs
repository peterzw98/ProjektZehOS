using System;
using System.Collections.Generic;

namespace ProjektZeh.Input;

internal sealed class LineEditor
{
    public const int MaximumLength = 66560;
    public const int HistoryCapacity = 32;
    private readonly TextBuffer text = new(MaximumLength);
    private readonly List<string> history = [];
    private int historyIndex;
    private string draft = "";
    private int draftCursor;

    public string Text => text.Text;
    public bool IsSecret { get; set; }
    public string DisplayText => IsSecret ? new string('*', text.Length) : Text;
    public int Cursor => text.Cursor;

    public bool Handle(InputEvent value, out string? command)
    {
        command = null;
        if (value.Type == InputEventType.Text && IsSecret && text.Length >= 128) return false;
        if (value.Type != InputEventType.KeyDown || value.IsTextKey) return text.Handle(value);

        switch ((PhysicalKey)value.KeyCode)
        {
            case PhysicalKey.Enter:
                command = Text;
                if (!IsSecret && !string.IsNullOrWhiteSpace(command) &&
                    (history.Count == 0 || history[^1] != command))
                {
                    if (history.Count == HistoryCapacity) history.RemoveAt(0);
                    history.Add(command);
                }
                text.SetText("");
                historyIndex = history.Count;
                draft = "";
                return true;
            case PhysicalKey.Up when !IsSecret && historyIndex > 0:
                if (historyIndex == history.Count)
                {
                    draft = Text;
                    draftCursor = Cursor;
                }
                Replace(history[--historyIndex]);
                return true;
            case PhysicalKey.Down when !IsSecret && historyIndex < history.Count:
                historyIndex++;
                Replace(historyIndex == history.Count ? draft : history[historyIndex]);
                if (historyIndex == history.Count) text.MoveTo(draftCursor);
                return true;
            default:
                return text.Handle(value);
        }
    }

    private void Replace(string value)
    {
        text.SetText(value);
    }
}
