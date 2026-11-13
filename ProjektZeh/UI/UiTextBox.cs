using System;
using System.Collections.Generic;
using ProjektZeh.Graphics;
using ProjektZeh.Input;

namespace ProjektZeh.UI;

internal sealed class UiTextBox(int maximumLength, bool multiline = false, bool readOnly = false, UiPainter? painter = null) : UiControl
{
    private readonly TextBuffer buffer = new(maximumLength, multiline);
    private readonly List<int> rows = [0];
    private int revision = -1;
    private int columns;
    private int visibleRows = 1;
    private int glyphWidth = painter?.GlyphWidth ?? 8;
    private int glyphHeight = painter?.GlyphHeight ?? 16;
    private bool selectedAll;
    private bool followCursor = true;
    public string Text => buffer.Text;
    public int Cursor => buffer.Cursor;
    public int FirstRow { get; private set; }
    public int Revision => buffer.Revision;
    public Action? Submitted { get; set; }
    /// <summary>Zeigt Sternchen statt des Inhalts, etwa fuer Kennwoerter.</summary>
    public bool Masked { get; set; }

    public void SetText(string value)
    {
        buffer.SetText(value);
        buffer.MoveTo(0);
        selectedAll = false;
        FirstRow = 0;
        rows.Clear();
        rows.Add(0);
        revision = -1;
        followCursor = true;
    }

    public void SelectAll()
    {
        if (readOnly) return;
        selectedAll = true;
        buffer.MoveTo(buffer.Length);
        followCursor = true;
    }

    public override void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset) { selectedAll = false; return; }
        PrepareRows();
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            int row = Math.Clamp(FirstRow + (value.Y - Bounds.Y - 6) / glyphHeight, 0, rows.Count - 1);
            int end = RowEnd(row);
            buffer.MoveTo(Math.Min(end, rows[row] + Math.Max(0, value.X - Bounds.X - 6) / glyphWidth));
            selectedAll = false;
            followCursor = true;
            return;
        }
        if (value.Type == InputEventType.KeyDown && !value.IsTextKey)
        {
            if (multiline && !readOnly && value.KeyCode is (byte)PhysicalKey.Up or (byte)PhysicalKey.Down)
            {
                int row = CursorRow();
                int target = Math.Clamp(row + (value.KeyCode == (byte)PhysicalKey.Up ? -1 : 1), 0, rows.Count - 1);
                buffer.MoveTo(Math.Min(RowEnd(target), rows[target] + buffer.Cursor - rows[row]));
                selectedAll = false;
                followCursor = true;
                return;
            }
            int delta = (PhysicalKey)value.KeyCode switch
            {
                PhysicalKey.PageUp => -visibleRows,
                PhysicalKey.PageDown => visibleRows,
                PhysicalKey.Up when readOnly => -1,
                PhysicalKey.Down when readOnly => 1,
                _ => 0
            };
            if (delta != 0)
            {
                FirstRow = Math.Clamp(FirstRow + delta, 0, Math.Max(0, rows.Count - visibleRows));
                followCursor = false;
                return;
            }
            if (!multiline && value.KeyCode == (byte)PhysicalKey.Enter) { Submitted?.Invoke(); return; }
        }
        if (readOnly) return;
        if (selectedAll && value.Type == InputEventType.KeyDown && !value.IsTextKey &&
            value.KeyCode is (byte)PhysicalKey.Left or (byte)PhysicalKey.Home or (byte)PhysicalKey.Right or (byte)PhysicalKey.End)
        {
            buffer.MoveTo(value.KeyCode is (byte)PhysicalKey.Left or (byte)PhysicalKey.Home ? 0 : buffer.Length);
            selectedAll = false;
            followCursor = true;
            return;
        }
        bool editing = value.Type == InputEventType.Text && !char.IsControl(value.Character) ||
            value.Type == InputEventType.KeyDown && !value.IsTextKey &&
            value.KeyCode is (byte)PhysicalKey.Backspace or (byte)PhysicalKey.Delete ||
            multiline && value.Type == InputEventType.KeyDown && value.KeyCode == (byte)PhysicalKey.Enter;
        if (selectedAll && editing) buffer.SetText("");
        if (buffer.Handle(value)) { selectedAll = false; followCursor = true; }
    }

    protected override void DrawContent(PixelSurface surface, UiPainter painter, int x, int y, bool active)
    {
        UiPainter.Frame(surface, Bounds, x, y, Focused && active);
        glyphWidth = painter.GlyphWidth;
        glyphHeight = painter.GlyphHeight;
        visibleRows = Math.Max(1, (Bounds.Height - 12) / glyphHeight);
        PrepareRows();
        int cursorRow = CursorRow();
        if (followCursor && !readOnly)
            FirstRow = Math.Clamp(FirstRow, Math.Max(0, cursorRow - visibleRows + 1), cursorRow);
        FirstRow = Math.Clamp(FirstRow, 0, Math.Max(0, rows.Count - visibleRows));
        int firstColumn = multiline ? 0 : Math.Max(0, buffer.Cursor - columns + 1);
        for (int row = FirstRow; row < Math.Min(rows.Count, FirstRow + visibleRows); row++)
        {
            int start = Math.Min(RowEnd(row), rows[row] + firstColumn);
            int length = Math.Min(columns, RowEnd(row) - start);
            int top = y + Bounds.Y + 6 + (row - FirstRow) * glyphHeight;
            if (selectedAll && active && Focused)
                surface.FillRectangle(x + Bounds.X + 6, top, length * glyphWidth, glyphHeight, UiPainter.Muted);
            painter.Text(surface, Masked ? new string('*', length) : Text.AsSpan(start, length), x + Bounds.X + 6, top, Bounds.Width - 12);
        }
        if (!readOnly && Focused && active && cursorRow >= FirstRow && cursorRow < FirstRow + visibleRows)
        {
            int column = buffer.Cursor - rows[cursorRow] - firstColumn;
            if (column < columns)
                surface.FillRectangle(x + Bounds.X + 6 + column * glyphWidth,
                    y + Bounds.Y + 6 + (cursorRow - FirstRow + 1) * glyphHeight - 2, glyphWidth, 2, UiPainter.Accent);
        }
    }

    private void PrepareRows()
    {
        int newColumns = Math.Max(1, (Bounds.Width - 12) / glyphWidth);
        if (revision == buffer.Revision && columns == newColumns) return;
        columns = newColumns;
        Reflow();
        revision = buffer.Revision;
    }

    private void Reflow()
    {
        rows.Clear();
        rows.Add(0);
        if (!multiline) return;
        string text = Text;
        int column = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n') { rows.Add(index + 1); column = 0; }
            else if (++column == columns && (index + 1 == text.Length || text[index + 1] != '\n'))
            { rows.Add(index + 1); column = 0; }
        }
    }

    private int CursorRow()
    {
        int row = rows.BinarySearch(buffer.Cursor);
        return row >= 0 ? row : ~row - 1;
    }

    private int RowEnd(int row)
    {
        int end = row + 1 < rows.Count ? rows[row + 1] : Text.Length;
        if (end > rows[row] && Text[end - 1] == '\n') end--;
        return end;
    }
}
