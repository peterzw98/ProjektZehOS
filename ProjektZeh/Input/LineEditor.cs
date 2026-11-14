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
    public Func<string, bool>? HistoryFilter { get; set; }
    public string DisplayText => IsSecret ? new string('*', text.Length) : Text;
    public int Cursor => text.Cursor;

    public bool Handle(InputEvent value, out string? command)
    {
        command = null;
        if (value.Type == InputEventType.Reset) { control = false; return false; }
        if (value.KeyCode == (byte)PhysicalKey.Control)
        {
            if (value.Type == InputEventType.KeyDown) control = true;
            if (value.Type == InputEventType.KeyUp) control = false;
            return false;
        }
        if (control && value.Type == InputEventType.KeyDown && value.KeyCode == 19) return SearchHistory();
        if (control && value.Type == InputEventType.Text) return false;
        if (value.Type is InputEventType.Text or InputEventType.KeyDown) searchQuery = null;
        if (value.Type == InputEventType.Text && IsSecret && text.Length >= 128) return false;
        if (value.Type != InputEventType.KeyDown || value.IsTextKey) return text.Handle(value);

        switch ((PhysicalKey)value.KeyCode)
        {
            case PhysicalKey.Enter:
                command = Text;
                if (!IsSecret && !string.IsNullOrWhiteSpace(command) && CanRemember(command) &&
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

    public void SetText(string value) { text.SetText(value); searchQuery = null; }
    private string? searchQuery;
    private int searchIndex = -1;
    private bool control;

    public void ResetHistory()
    {
        history.Clear(); historyIndex = 0; draft = ""; searchQuery = null; searchIndex = -1; text.SetText(""); control = false;
    }

    public bool SearchHistory()
    {
        if (IsSecret) return false;
        if (searchQuery == null) { searchQuery = Text; searchIndex = history.Count; }
        for (int i = searchIndex - 1; i >= 0; i--)
            if (history[i].Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
            {
                text.SetText(history[i]); searchIndex = i; return true;
            }
        return false;
    }
    public void Complete(ProjektZeh.Shell.ShellService shell)
    {
        if (IsSecret) return;
        string previous = Text;
        int previousCursor = Cursor;
        string completed = shell.Complete(previous, previousCursor);
        if (completed == previous) return;
        SetText(completed);
        text.MoveTo(previousCursor + completed.Length - previous.Length);
    }

    private bool CanRemember(string command)
    {
        if (HistoryFilter != null) return HistoryFilter(command);
        var parsed = ProjektZeh.Shell.CommandLineParser.Parse(command);
        if (!parsed.IsOK()) return false;
        foreach (string token in parsed.Value!)
            if (ProjektZeh.Shell.ShellService.IsSensitiveCommand(token) || token.Equals("alias", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void Replace(string value)
    {
        text.SetText(value);
    }
}
