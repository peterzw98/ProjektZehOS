using System;

namespace ProjektZeh.Apps.Pong;

internal sealed class PongGame
{
    public const int Width = 640;
    public const int Height = 360;
    public const int PaddleHeight = 72;
    public const int PaddleWidth = 12;
    public const int BallSize = 10;
    private double velocityX = 220;
    private double velocityY = 110;
    public double PlayerY { get; private set; } = (Height - PaddleHeight) / 2;
    public double ComputerY { get; private set; } = (Height - PaddleHeight) / 2;
    public double BallX { get; private set; } = Width / 2;
    public double BallY { get; private set; } = Height / 2;
    public int PlayerScore { get; private set; }
    public int ComputerScore { get; private set; }

    public void Step(int milliseconds, int direction)
    {
        // Kleine Schritte verhindern das Ueberspringen der Schlaeger auch nach einem langsamen Bild.
        int remaining = Math.Clamp(milliseconds, 0, 100);
        while (remaining > 0)
        {
            int step = Math.Min(remaining, 8);
            Advance(step / 1000.0, Math.Clamp(direction, -1, 1));
            remaining -= step;
        }
    }

    private void Advance(double seconds, int direction)
    {
        PlayerY = Math.Clamp(PlayerY + direction * 260 * seconds, 0, Height - PaddleHeight);
        double target = BallY + BallSize / 2.0 - PaddleHeight / 2.0;
        ComputerY = Math.Clamp(ComputerY + Math.Clamp(target - ComputerY, -150 * seconds, 150 * seconds), 0, Height - PaddleHeight);
        double previousX = BallX;
        BallX += velocityX * seconds;
        BallY += velocityY * seconds;
        if (BallY < 0) { BallY = -BallY; velocityY = Math.Abs(velocityY); }
        if (BallY > Height - BallSize) { BallY = 2 * (Height - BallSize) - BallY; velocityY = -Math.Abs(velocityY); }
        const int left = 24 + PaddleWidth;
        const int right = Width - 24 - PaddleWidth;
        if (velocityX < 0 && previousX >= left && BallX <= left && Overlaps(PlayerY))
        { BallX = left; velocityX = Math.Abs(velocityX); velocityY = (BallY - PlayerY - PaddleHeight / 2.0) * 5; }
        if (velocityX > 0 && previousX + BallSize <= right && BallX + BallSize >= right && Overlaps(ComputerY))
        { BallX = right - BallSize; velocityX = -Math.Abs(velocityX); velocityY = (BallY - ComputerY - PaddleHeight / 2.0) * 5; }
        if (BallX < -BallSize) { ComputerScore++; Serve(1); }
        if (BallX > Width) { PlayerScore++; Serve(-1); }
    }

    private bool Overlaps(double paddle) => BallY + BallSize >= paddle && BallY <= paddle + PaddleHeight;
    private void Serve(int direction)
    {
        BallX = (Width - BallSize) / 2.0;
        BallY = (Height - BallSize) / 2.0;
        velocityX = direction * 220;
        velocityY = 110;
    }
}
