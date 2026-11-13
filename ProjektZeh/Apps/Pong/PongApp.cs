using System;
using ProjektZeh.Graphics;
using ProjektZeh.Input;
using ProjektZeh.UI;

namespace ProjektZeh.Apps.Pong;

internal sealed class PongApp(UiPainter painter) : IApplication
{
    private bool up;
    private bool down;
    private bool paused;
    private bool active;
    private long previous = -1;
    private readonly PongGame game = new();
    private int shownPlayer = -1;
    private int shownComputer = -1;
    private string score = "";
    public bool IsPaused => paused;
    public PongGame Game => game;

    public void Start() { }
    public void Handle(InputEvent value)
    {
        if (value.Type == InputEventType.Reset) { up = down = false; previous = -1; active = false; return; }
        if (value.Type is not (InputEventType.KeyDown or InputEventType.KeyUp)) return;
        bool pressed = value.Type == InputEventType.KeyDown;
        if (value.KeyCode == 17 || value.KeyCode == (byte)PhysicalKey.Up && !value.IsTextKey) up = pressed;
        if (value.KeyCode == 31 || value.KeyCode == (byte)PhysicalKey.Down && !value.IsTextKey) down = pressed;
        if (pressed && !value.IsRepeat && value.KeyCode == (byte)PhysicalKey.Space) paused = !paused;
    }

    public void Update(long milliseconds)
    {
        long elapsed = previous < 0 ? 0 : Math.Max(0, milliseconds - previous);
        previous = milliseconds;
        if (active && !paused) game.Step((int)Math.Min(elapsed, 100), (down ? 1 : 0) - (up ? 1 : 0));
    }

    public void Draw(PixelSurface surface, int x, int y, int width, int height, bool isActive)
    {
        active = isActive;
        surface.FillRectangle(x, y, width, height, UiPainter.Background);
        if (game.PlayerScore != shownPlayer || game.ComputerScore != shownComputer)
        {
            shownPlayer = game.PlayerScore;
            shownComputer = game.ComputerScore;
            score = $"Spieler {shownPlayer} : {shownComputer} Computer";
        }
        painter.Text(surface, score, x + 16, y + 8, width - 32, UiPainter.Accent);
        painter.Text(surface, "W/S oder Pfeile | Leertaste: Pause", x + 16, y + 12 + painter.GlyphHeight, width - 32);
        int left = x + (width - PongGame.Width) / 2;
        int top = y + 24 + 2 * painter.GlyphHeight;
        surface.FillRectangle(left, top, PongGame.Width, PongGame.Height, unchecked((int)0xff172c3e));
        surface.DrawLine(left + PongGame.Width / 2, top, left + PongGame.Width / 2, top + PongGame.Height - 1, UiPainter.Border);
        surface.FillRectangle(left + 24, top + (int)game.PlayerY, PongGame.PaddleWidth, PongGame.PaddleHeight, UiPainter.Accent);
        surface.FillRectangle(left + PongGame.Width - 24 - PongGame.PaddleWidth, top + (int)game.ComputerY,
            PongGame.PaddleWidth, PongGame.PaddleHeight, UiPainter.Foreground);
        surface.FillRectangle(left + (int)game.BallX, top + (int)game.BallY, PongGame.BallSize, PongGame.BallSize, UiPainter.Foreground);
        if (paused || !isActive)
            painter.Text(surface, paused ? "Pause" : "Fuer Spiel aktivieren", left + 120, top + 140, 400);
    }

    public void Close() { up = down = false; active = false; }
}
