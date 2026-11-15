using System;
using System.Text;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.Shell;

namespace ProjektZeh.Apps.Terminal;

internal sealed class TerminalApp(ShellService shell, byte[] font, int glyphWidth, int glyphHeight, int columns) : IApplication
{
    private const string Prompt = "zehos> ";
    private int sessionGeneration = shell.SessionGeneration;
    private int visibleRows = 1;
    private int width = 400;
    private int height = 300;
    private bool cursorVisible = true;
    private bool control;
    private bool shift;
    private bool selecting;
    private bool scrolling;
    private int scrollGrab;
    private int selectionStart = -1;
    private int selectionEnd = -1;
    private long selectedRevision = -1;
    private string search = "";
    private string? pagerText;
    private TerminalBuffer? pager;
    private int pagerOffset;
    private int background = unchecked((int)0xff101923);
    private int foreground = unchecked((int)0xffedf4fc);
    private int accent = unchecked((int)0xff53d7bb);
    public TerminalBuffer Buffer { get; } = new(columns);
    public LineEditor Editor { get; } = new() { HistoryFilter = shell.CanRemember };
    public ShellService Shell => shell;
    public int ScrollOffset { get; private set; }
    public int Zoom { get; private set; } = 1;
    public bool IsClosed { get; private set; }
    public bool IsCloseRequested => shell.ExitRequested;
    private int CellWidth => glyphWidth * Zoom;
    private int CellHeight => glyphHeight * Zoom;
    private TerminalBuffer Output => pager ?? Buffer;
    private int MaximumScroll => Math.Max(0, Output.Count - visibleRows);
    private int FirstLine => pager != null ? pagerOffset : Math.Max(0, Buffer.Count - visibleRows - ScrollOffset);

    public void Start()
    {
        Buffer.WriteLine("ZehOS Terminal - Hilfe: help");
        Buffer.WriteLine("PageUp/PageDown: Ausgabe scrollen.");
    }

    public void Handle(InputEvent value)
    {
        if (IsClosed) return;
        SyncSession();
        SyncPager();
        if (value.Type == InputEventType.Reset)
        {
            control = shift = selecting = scrolling = false;
            ClearSelection();
            return;
        }
        if (value.Type is InputEventType.KeyDown or InputEventType.KeyUp)
        {
            bool down = value.Type == InputEventType.KeyDown;
            if (value.KeyCode == (byte)PhysicalKey.Control) { control = down; return; }
            if (value.KeyCode is (byte)PhysicalKey.LeftShift or (byte)PhysicalKey.RightShift) { shift = down; return; }
        }
        if (HandlePointer(value)) return;
        if (value.Type == InputEventType.KeyDown && !value.IsTextKey)
        {
            if (value.KeyCode == (byte)PhysicalKey.PageUp) { Scroll(-visibleRows); return; }
            if (value.KeyCode == (byte)PhysicalKey.PageDown) { Scroll(visibleRows); return; }
            if (pager != null)
            {
                if (value.KeyCode == (byte)PhysicalKey.Up) Scroll(-1);
                if (value.KeyCode == (byte)PhysicalKey.Down || value.KeyCode == (byte)PhysicalKey.Enter) Scroll(1);
                if (value.KeyCode == (byte)PhysicalKey.Home) Scroll(-Output.Count);
                if (value.KeyCode == (byte)PhysicalKey.End) Scroll(Output.Count);
                if (value.KeyCode == (byte)PhysicalKey.Escape) { shell.DismissPager(); SyncPager(); }
                return;
            }
            if (control)
            {
                if (shift && value.KeyCode == 46) CopySelection();
                else if (shift && value.KeyCode == 47) PasteClipboard();
                else if (value.KeyCode == 38) { Buffer.Clear(); ClearSelection(); ScrollOffset = 0; }
                else if (value.KeyCode == 19 && !shell.IsSecretInput) Editor.SearchHistory();
                return;
            }
            if (value.KeyCode == (byte)PhysicalKey.Tab && !shell.IsSecretInput && !shell.IsBusy)
            {
                Editor.Complete(shell);
                return;
            }
        }
        if (pager != null)
        {
            if (value.Type == InputEventType.Text && value.Character == ' ') Scroll(visibleRows);
            if (value.Type == InputEventType.Text && value.Character is 'q' or 'Q') { shell.DismissPager(); SyncPager(); }
            return;
        }
        if (control) return;
        Editor.IsSecret = shell.IsSecretInput;
        if (shell.IsBusy || !Editor.Handle(value, out string? command)) return;
        ScrollOffset = 0;
        cursorVisible = true;
        if (command == null) return;
        Buffer.WriteLine(Prompt + (Editor.IsSecret ? "********" : command));
        var result = shell.ExecuteLine(command, Buffer);
        if (!result.IsOK()) Buffer.WriteLine(result.Message);
        SyncPager();
    }

    public void Update(long milliseconds)
    {
        SyncSession();
        shell.Poll(Buffer);
        Editor.IsSecret = shell.IsSecretInput;
        SyncPager();
        cursorVisible = milliseconds / 500 % 2 == 0;
    }

    public void Resize(int width, int height)
    {
        this.width = Math.Max(0, width);
        this.height = Math.Max(0, height);
        Buffer.Reflow(Math.Max(1, (width - 16) / CellWidth));
        pager?.Reflow(Math.Max(1, (width - 16) / CellWidth));
        visibleRows = Math.Max(1, (height - 16) / CellHeight - 1);
        ScrollOffset = Math.Clamp(ScrollOffset, 0, Math.Max(0, Buffer.Count - visibleRows));
        pagerOffset = Math.Clamp(pagerOffset, 0, MaximumScroll);
        ValidateSelection();
    }

    public void SetZoom(int zoom)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(zoom, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(zoom, 3);
        Zoom = zoom;
        Resize(width, height);
    }

    public void SetColors(int background, int foreground, int accent)
    {
        this.background = background;
        this.foreground = foreground;
        this.accent = accent;
    }

    public void SelectAll()
    {
        SyncSession();
        selectedRevision = Output.Revision;
        selectionStart = Output.Count == 0 ? -1 : 0;
        selectionEnd = Output.Count == 0 ? -1 : Position(Output.Count - 1, Output.GetLine(Output.Count - 1).Length);
    }

    public string CopySelection()
    {
        SyncSession();
        ValidateSelection();
        if (shell.IsSecretInput || selectionStart < 0 || selectionEnd < 0) return "";
        int start = Math.Min(selectionStart, selectionEnd);
        int end = Math.Max(selectionStart, selectionEnd);
        var text = new StringBuilder();
        int stride = Output.Columns + 1;
        for (int row = start / stride; row <= end / stride && row < Output.Count; row++)
        {
            string line = Output.GetLine(row);
            int from = row == start / stride ? Math.Min(line.Length, start % stride) : 0;
            int to = row == end / stride ? Math.Min(line.Length, end % stride) : line.Length;
            if (row > start / stride && !Output.IsContinuation(row) && text.Length < LineEditor.MaximumLength) text.Append('\n');
            int remaining = LineEditor.MaximumLength - text.Length;
            if (remaining <= 0) break;
            text.Append(line.AsSpan(from, Math.Min(to - from, remaining)));
        }
        string result = text.ToString();
        if (result.Length > 0) TerminalClipboard.Text = result;
        return result;
    }

    public void PasteClipboard()
    {
        SyncSession();
        if (IsClosed || shell.IsSecretInput || shell.IsBusy || pager != null) return;
        string text = TerminalClipboard.Text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        int available = LineEditor.MaximumLength - Editor.Text.Length;
        text = text[..Math.Min(text.Length, available)];
        foreach (char character in text)
            if (!char.IsControl(character)) Editor.Handle(new(InputEventType.Text, Character: character), out _);
        ScrollOffset = 0;
    }

    public bool Find(string query, bool backwards = false)
    {
        SyncSession();
        ArgumentNullException.ThrowIfNull(query);
        ValidateSelection();
        if (query.Length == 0 || Output.Count == 0) return false;
        int stride = Output.Columns + 1;
        int limit = Output.Count * stride;
        int origin = search.Equals(query, StringComparison.OrdinalIgnoreCase) && selectionStart >= 0
            ? (backwards ? Math.Min(selectionStart, selectionEnd) - 1 : Math.Max(selectionStart, selectionEnd))
            : (backwards ? limit - 1 : 0);
        search = query;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int n = 0; n < Output.Count; n++)
            {
                int row = backwards ? Output.Count - n - 1 : n;
                if (row > 0 && Output.IsContinuation(row)) continue;
                var logicalLine = new StringBuilder(Output.GetLine(row));
                for (int next = row + 1; next < Output.Count && Output.IsContinuation(next); next++)
                    logicalLine.Append(Output.GetLine(next));
                string line = logicalLine.ToString();
                int index = backwards ? line.LastIndexOf(query, StringComparison.OrdinalIgnoreCase) : line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    int matchRow = row + index / Output.Columns;
                    int position = Position(matchRow, index % Output.Columns);
                    if (backwards ? position <= origin : position >= origin)
                    {
                        selectedRevision = Output.Revision;
                        selectionStart = position;
                        int last = index + query.Length - 1;
                        selectionEnd = Position(row + last / Output.Columns, last % Output.Columns + 1);
                        if (pager != null) pagerOffset = Math.Clamp(matchRow, 0, MaximumScroll);
                        else ScrollOffset = Math.Clamp(Buffer.Count - visibleRows - matchRow, 0, MaximumScroll);
                        return true;
                    }
                    if (backwards) index = index == 0 ? -1 : line.LastIndexOf(query, index - 1, StringComparison.OrdinalIgnoreCase);
                    else index = line.IndexOf(query, index + 1, StringComparison.OrdinalIgnoreCase);
                }
            }
            origin = backwards ? limit - 1 : 0;
        }
        ClearSelection();
        return false;
    }

    public void Draw(PixelSurface surface, int x, int y, int width, int height, bool active)
    {
        SyncSession();
        Resize(width, height);
        SyncPager();
        var clip = surface.Clip;
        surface.IntersectClip(x, y, width, height);
        surface.FillRectangle(x, y, width, height, background);
        int first = FirstLine;
        int end = Math.Min(Output.Count, first + visibleRows);
        for (int index = first; index < end; index++)
        {
            string line = Output.GetLine(index);
            for (int column = 0; column < line.Length; column++)
            {
                int position = Position(index, column);
                if (selectionStart >= 0 && position >= Math.Min(selectionStart, selectionEnd) && position < Math.Max(selectionStart, selectionEnd))
                    surface.FillRectangle(x + 8 + column * CellWidth, y + 8 + (index - first) * CellHeight, CellWidth, CellHeight, accent);
            }
            Text(surface, line, x + 8, y + 8 + (index - first) * CellHeight, foreground);
        }
        int inputY = y + height - CellHeight - 8;
        if (pager != null) Text(surface, "less: Pfeile, Bild, q", x + 8, inputY, accent);
        else
        {
            int inputColumns = Math.Max(1, (width - 16) / CellWidth - Prompt.Length - 1);
            string text = Editor.DisplayText;
            int start = Math.Max(0, Editor.Cursor - inputColumns + 1);
            string visible = text.Substring(start, Math.Min(inputColumns, text.Length - start));
            Text(surface, Prompt + visible, x + 8, inputY, accent);
            if (active && cursorVisible) surface.FillRectangle(x + 8 + (Prompt.Length + Editor.Cursor - start) * CellWidth,
                inputY + CellHeight - 2, CellWidth, 2, accent);
        }
        if (MaximumScroll > 0)
        {
            var (top, thumbHeight) = ScrollThumb();
            surface.FillRectangle(x + width - 6, y + 8, 4, TrackHeight, background ^ 0x00181818);
            surface.FillRectangle(x + width - 6, y + top, 4, thumbHeight, accent);
        }
        surface.SetClip(clip.X, clip.Y, clip.Width, clip.Height);
    }

    public void Close()
    {
        if (IsClosed) return;
        Buffer.Clear();
        Editor.ResetHistory();
        pager = null;
        shell.Close();
        IsClosed = true;
        ClearSelection();
    }

    private int TrackHeight => Math.Max(1, height - CellHeight - 24);
    private (int Top, int Height) ScrollThumb()
    {
        int thumbHeight = Math.Min(TrackHeight, Math.Max(8, TrackHeight * visibleRows / Math.Max(visibleRows, Output.Count)));
        return (8 + (TrackHeight - thumbHeight) * FirstLine / Math.Max(1, MaximumScroll), thumbHeight);
    }

    private bool HandlePointer(InputEvent value)
    {
        if (value.Type == InputEventType.PointerUp)
        {
            ValidateSelection();
            if (selecting) selectionEnd = PointerPosition(value);
            selecting = scrolling = false;
            return true;
        }
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left)
        {
            if (value.X >= width - 8 && value.X < width && value.Y >= 8 && value.Y < 8 + TrackHeight && MaximumScroll > 0)
            {
                var (top, thumbHeight) = ScrollThumb();
                if (value.Y < top) Scroll(-visibleRows);
                else if (value.Y >= top + thumbHeight) Scroll(visibleRows);
                else { scrolling = true; scrollGrab = value.Y - top; }
                return true;
            }
            if (value.X >= 8 && value.X < width - 8 && value.Y >= 8 && value.Y < 8 + visibleRows * CellHeight && Output.Count > 0)
            {
                selectedRevision = Output.Revision;
                selectionStart = selectionEnd = PointerPosition(value);
                selecting = true;
            }
            return true;
        }
        if (value.Type != InputEventType.PointerMove) return false;
        if (selecting) { ValidateSelection(); if (selecting) selectionEnd = PointerPosition(value); }
        if (scrolling)
        {
            int travel = TrackHeight - ScrollThumb().Height;
            int first = travel == 0 ? 0 : Math.Clamp(value.Y - 8 - scrollGrab, 0, travel) * MaximumScroll / travel;
            if (pager != null) pagerOffset = first;
            else ScrollOffset = MaximumScroll - first;
        }
        return true;
    }

    private int PointerPosition(InputEvent value)
    {
        int row = Math.Clamp(FirstLine + (value.Y - 8) / CellHeight, 0, Output.Count - 1);
        int column = Math.Clamp((value.X - 8) / CellWidth, 0, Output.GetLine(row).Length);
        return Position(row, column);
    }
    private int Position(int row, int column) => row * (Output.Columns + 1) + column;
    private void Scroll(int rows)
    {
        if (pager != null) pagerOffset = Math.Clamp(pagerOffset + rows, 0, MaximumScroll);
        else ScrollOffset = Math.Clamp(ScrollOffset - rows, 0, MaximumScroll);
    }
    private void ClearSelection() { selectionStart = selectionEnd = -1; selecting = false; }
    private void ValidateSelection() { if (selectedRevision != Output.Revision) ClearSelection(); }
    private void SyncSession()
    {
        int current = shell.SessionGeneration;
        if (current == sessionGeneration) return;
        sessionGeneration = current;
        Buffer.Clear();
        Editor.ResetHistory();
        TerminalClipboard.Clear();
        search = "";
        pagerText = null;
        pager = null;
        ScrollOffset = pagerOffset = 0;
        control = shift = scrolling = false;
        ClearSelection();
    }
    private void SyncPager()
    {
        string? text = shell.PagerText;
        if (ReferenceEquals(pagerText, text)) return;
        pagerText = text;
        pager = text == null ? null : new TerminalBuffer(Math.Max(1, (width - 16) / CellWidth), Math.Min(LineEditor.MaximumLength, text.Length) + 1);
        if (text != null) pager!.WriteLine(text[..Math.Min(LineEditor.MaximumLength, text.Length)]);
        pagerOffset = 0;
        ClearSelection();
    }
    private void Text(PixelSurface surface, string text, int x, int y, int color)
    {
        if (Zoom == 1) { surface.DrawText(text, x, y, color, font, glyphWidth, glyphHeight); return; }
        int stride = (glyphWidth + 7) / 8;
        foreach (char character in text)
        {
            int glyph = character <= 255 ? character : '?';
            for (int row = 0; row < glyphHeight; row++)
                for (int column = 0; column < glyphWidth; column++)
                    if ((font[glyph * stride * glyphHeight + row * stride + column / 8] & (0x80 >> column % 8)) != 0)
                        surface.FillRectangle(x + column * Zoom, y + row * Zoom, Zoom, Zoom, color);
            x += CellWidth;
            if (x >= surface.Clip.X + surface.Clip.Width) break;
        }
    }
}
