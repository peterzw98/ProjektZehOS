using System;
using ProjektZeh.Apps;
using ProjektZeh.Apps.Terminal;
using ProjektZeh.Apps.FileManager;
using ProjektZeh.Apps.TextEditor;
using ProjektZeh.Apps.Pong;
using ProjektZeh.Storage;
using ProjektZeh.UI;
using ProjektZeh.Input;
using ProjektZeh.Shell;

namespace ProjektZeh.Graphics;

internal sealed class Desktop
{
    public const int MaximumWindows = 8;
    private const int WindowWidth = 640;
    private const int WindowHeight = 400;
    private const int BarHeight = 56;
    private readonly int width;
    private readonly int height;
    private readonly byte[] font;
    private readonly int glyphWidth;
    private readonly int glyphHeight;
    private readonly Func<ShellService> createShell;
    private readonly Func<FileService> createFiles;
    private readonly UiPainter painter;
    private int sequence;
    public DesktopMetrics Metrics { get; } = new();
    public bool NeedsAnimation => Windows.ActiveWindow?.Application is PongApp { IsPaused: false };
    public WindowManager Windows { get; }

    public Desktop(int width, int height, byte[] font, int glyphWidth, int glyphHeight, Func<ShellService> createShell, Func<FileService>? createFiles = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, WindowWidth);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, WindowHeight + BarHeight);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(createShell);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphHeight);
        this.width = width;
        this.height = height;
        this.font = font;
        this.glyphWidth = glyphWidth;
        this.glyphHeight = glyphHeight;
        this.createShell = createShell;
        this.createFiles = createFiles ?? (() => new FileService("/data"));
        painter = new UiPainter(font, glyphWidth, glyphHeight);
        Windows = new WindowManager(width, height - BarHeight);
        OpenTerminal();
        OpenTerminal();
    }

    public bool OpenTerminal()
    {
        if (Windows.Count == MaximumWindows) return false;
        Windows.Reset();
        int offset = sequence % 4 * 60;
        var terminal = new TerminalApp(createShell(), font, glyphWidth, glyphHeight, (WindowWidth - 20) / glyphWidth);
        Windows.Add(new Window($"Terminal {++sequence}", 72 + offset, 72 + offset, WindowWidth, WindowHeight, terminal));
        return true;
    }

    public bool OpenFiles() => Windows.Count < MaximumWindows && OpenApplication("Dateien", new FileManagerApp(createFiles(), painter, path => OpenEditor(path)));
    public bool OpenEditor(string path = "") => Windows.Count < MaximumWindows && OpenApplication("Texteditor", new TextEditorApp(createFiles(), painter, path));
    public bool OpenPong() => Windows.Count < MaximumWindows && OpenApplication("Pong", new PongApp(painter));

    private bool OpenApplication(string title, IApplication application)
    {
        if (Windows.Count == MaximumWindows || width < 800 || height - BarHeight < 560) return false;
        Windows.Reset();
        int offset = sequence++ % 3 * 28;
        Windows.Add(new Window(title, 32 + offset, 80 + offset, 800, 560, application));
        return true;
    }

    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.PointerDown && value.Button == PointerButtons.Left &&
            value.Y >= height - 44 && value.Y < height - 12)
        {
            if (value.X >= 16 && value.X < 192) { OpenTerminal(); return; }
            if (value.X >= 200 && value.X < 360) { OpenFiles(); return; }
            if (value.X >= 368 && value.X < 512) { OpenEditor(); return; }
            if (value.X >= 520 && value.X < 632) { OpenPong(); return; }
        }
        Windows.Handle(value);
    }

    public void Update(long milliseconds) => Windows.Update(milliseconds);

    public void Render(PixelSurface surface, long milliseconds, int pointerX, int pointerY, PointerButtons buttons)
    {
        surface.ResetClip();
        surface.FillRectangle(0, 0, width, height, unchecked((int)0xff142236));
        Text(surface, "ZehOS", 24, 20, unchecked((int)0xffedf4fc));
        painter.Text(surface, Metrics.AllocationSummary, 200, 12, width - 224, UiPainter.Border);
        Text(surface, "Esc/F4: Textshell | Titelleiste ziehen | X: Schliessen", 24, 44, unchecked((int)0xff53d7bb));
        Windows.Render(surface, font, glyphWidth, glyphHeight);
        surface.FillRectangle(0, height - BarHeight, width, BarHeight, unchecked((int)0xff213b55));
        surface.FillRectangle(16, height - 44, 176, 32, Windows.Count < MaximumWindows ? unchecked((int)0xff285c7b) : unchecked((int)0xff344254));
        Text(surface, "+ Terminal", 28, height - 36, unchecked((int)0xffedf4fc));
        Starter(surface, "Dateien", 200, 160);
        Starter(surface, "Editor", 368, 144);
        Starter(surface, "Pong", 520, 112);
        painter.Text(surface, Metrics.Summary, 648, height - 36, width - 664);
        int color = buttons == PointerButtons.None ? -1 : unchecked((int)0xffffbf69);
        for (int row = 0; row < 15; row++) surface.FillRectangle(pointerX, pointerY + row, row / 2 + 1, 1, unchecked((int)0xff000000));
        surface.DrawLine(pointerX, pointerY, pointerX, pointerY + 14, color);
        surface.DrawLine(pointerX, pointerY, pointerX + 7, pointerY + 14, color);
        surface.DrawLine(pointerX, pointerY + 14, pointerX + 7, pointerY + 14, color);
    }

    private void Starter(PixelSurface surface, string label, int x, int buttonWidth)
    {
        surface.FillRectangle(x, height - 44, buttonWidth, 32,
            Windows.Count < MaximumWindows ? unchecked((int)0xff285c7b) : unchecked((int)0xff344254));
        painter.Text(surface, label, x + 8, height - 36, buttonWidth - 16);
    }

    private void Text(PixelSurface surface, string text, int x, int y, int color) =>
        surface.DrawText(text, x, y, color, font, glyphWidth, glyphHeight);
}
