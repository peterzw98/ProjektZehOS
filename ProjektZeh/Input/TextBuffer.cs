using System;
using System.Text;

namespace ProjektZeh.Input;

internal sealed class TextBuffer(int maximumLength, bool multiline = false)
{
    private readonly StringBuilder text = new();
    private string? cached;
    public string Text => cached ??= text.ToString();
    public int Cursor { get; private set; }
    public int Length => text.Length;
    public int Revision { get; private set; }

    public void SetText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, maximumLength);
        text.Clear();
        text.Append(value);
        Cursor = text.Length;
        Changed();
    }

    public void MoveTo(int position) => Cursor = Math.Clamp(position, 0, text.Length);

    public bool Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Text && !char.IsControl(value.Character)) return Insert(value.Character);
        if (value.Type != InputEventType.KeyDown || value.IsTextKey) return false;
        switch ((PhysicalKey)value.KeyCode)
        {
            case PhysicalKey.Enter when multiline: return Insert('\n');
            case PhysicalKey.Backspace when Cursor > 0:
                text.Remove(--Cursor, 1); Changed(); return true;
            case PhysicalKey.Delete when Cursor < text.Length:
                text.Remove(Cursor, 1); Changed(); return true;
            case PhysicalKey.Left when Cursor > 0: Cursor--; return true;
            case PhysicalKey.Right when Cursor < text.Length: Cursor++; return true;
            case PhysicalKey.Home: Cursor = LineStart(Cursor); return true;
            case PhysicalKey.End: Cursor = LineEnd(Cursor); return true;
            case PhysicalKey.Up when multiline:
                int start = LineStart(Cursor);
                if (start == 0) return false;
                int previous = LineStart(start - 1);
                Cursor = Math.Min(start - 1, previous + Cursor - start);
                return true;
            case PhysicalKey.Down when multiline:
                int end = LineEnd(Cursor);
                if (end == text.Length) return false;
                Cursor = Math.Min(LineEnd(end + 1), end + 1 + Cursor - LineStart(Cursor));
                return true;
            default: return false;
        }
    }

    private bool Insert(char value)
    {
        if (text.Length == maximumLength) return false;
        text.Insert(Cursor++, value);
        Changed();
        return true;
    }

    private int LineStart(int position)
    {
        if (!multiline) return 0;
        while (position > 0 && text[position - 1] != '\n') position--;
        return position;
    }

    private int LineEnd(int position)
    {
        if (!multiline) return text.Length;
        while (position < text.Length && text[position] != '\n') position++;
        return position;
    }

    private void Changed() { cached = null; Revision++; }
}
