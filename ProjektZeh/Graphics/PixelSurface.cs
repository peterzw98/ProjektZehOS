using System;

namespace ProjektZeh.Graphics;

internal sealed class PixelSurface
{
    private readonly int[] pixels;
    private int clipLeft;
    private int clipTop;
    private int clipRight;
    private int clipBottom;

    public (int X, int Y, int Width, int Height) Clip => (clipLeft, clipTop, clipRight - clipLeft, clipBottom - clipTop);
    public int Width { get; }
    public int Height { get; }

    public PixelSurface(int width, int height, int[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height > pixels.Length) throw new ArgumentException("Der Bildpuffer ist zu klein.", nameof(pixels));
        Width = width;
        Height = height;
        this.pixels = pixels;
        ResetClip();
    }

    public void SetClip(int x, int y, int width, int height)
    {
        clipLeft = (int)Math.Clamp((long)x, 0, Width);
        clipTop = (int)Math.Clamp((long)y, 0, Height);
        clipRight = (int)Math.Clamp((long)x + Math.Max(0, width), clipLeft, Width);
        clipBottom = (int)Math.Clamp((long)y + Math.Max(0, height), clipTop, Height);
    }

    public void IntersectClip(int x, int y, int width, int height)
    {
        Bounds(x, y, width, height, out int left, out int top, out int right, out int bottom);
        clipLeft = left; clipTop = top; clipRight = right; clipBottom = bottom;
    }

    public void ResetClip() => SetClip(0, 0, Width, Height);

    public void FillRectangle(int x, int y, int width, int height, int color)
    {
        Bounds(x, y, width, height, out int left, out int top, out int right, out int bottom);
        for (int row = top; row < bottom; row++) Array.Fill(pixels, color, row * Width + left, right - left);
    }

    public void DrawLine(int x1, int y1, int x2, int y2, int color)
    {
        if (clipLeft == clipRight || clipTop == clipBottom) return;
        double dx = (double)x2 - x1;
        double dy = (double)y2 - y1;
        double start = 0;
        double end = 1;
        if (!ClipLine(-dx, (double)x1 - clipLeft, ref start, ref end) ||
            !ClipLine(dx, (double)clipRight - 1 - x1, ref start, ref end) ||
            !ClipLine(-dy, (double)y1 - clipTop, ref start, ref end) ||
            !ClipLine(dy, (double)clipBottom - 1 - y1, ref start, ref end)) return;
        int endX = Math.Clamp((int)Math.Round(x1 + end * dx), clipLeft, clipRight - 1);
        int endY = Math.Clamp((int)Math.Round(y1 + end * dy), clipTop, clipBottom - 1);
        x1 = Math.Clamp((int)Math.Round(x1 + start * dx), clipLeft, clipRight - 1);
        y1 = Math.Clamp((int)Math.Round(y1 + start * dy), clipTop, clipBottom - 1);
        long distanceX = Math.Abs((long)endX - x1);
        long distanceY = -Math.Abs((long)endY - y1);
        int stepX = x1 < endX ? 1 : -1;
        int stepY = y1 < endY ? 1 : -1;
        long error = distanceX + distanceY;
        while (true)
        {
            pixels[y1 * Width + x1] = color;
            if (x1 == endX && y1 == endY) break;
            long twiceError = 2 * error;
            if (twiceError >= distanceY) { error += distanceY; x1 += stepX; }
            if (twiceError <= distanceX) { error += distanceX; y1 += stepY; }
        }
    }

    public void DrawPixels(int[] source, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height > source.Length) throw new ArgumentException("Der Quellpuffer ist zu klein.", nameof(source));
        Bounds(x, y, width, height, out int left, out int top, out int right, out int bottom);
        if (right == left) return;
        for (int row = top; row < bottom; row++)
        {
            int sourceIndex = (int)(((long)row - y) * width + left - x);
            Array.Copy(source, sourceIndex, pixels, row * Width + left, right - left);
        }
    }

    public void DrawText(string text, int x, int y, int color, byte[] font, int glyphWidth, int glyphHeight)
    {
        ArgumentNullException.ThrowIfNull(text);
        DrawText(text.AsSpan(), x, y, color, font, glyphWidth, glyphHeight);
    }

    public void DrawText(ReadOnlySpan<char> text, int x, int y, int color, byte[] font, int glyphWidth, int glyphHeight)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(glyphHeight);
        long glyphBytes = (glyphWidth + 7L) / 8 * glyphHeight;
        if (glyphBytes > font.Length / 256) throw new ArgumentException("Der Bitmapfont muss 256 Zeichen enthalten.", nameof(font));
        int stride = (int)((glyphWidth + 7L) / 8);
        long penX = x;
        long penY = y;
        foreach (char character in text)
        {
            if (character == '\n') { penX = x; penY += glyphHeight; continue; }
            Bounds(penX, penY, glyphWidth, glyphHeight, out int left, out int top, out int right, out int bottom);
            int glyph = character <= 255 ? character : '?';
            for (int row = top; row < bottom; row++)
            {
                for (int column = left; column < right; column++)
                {
                    int bit = (int)(column - penX);
                    int index = (int)(glyph * glyphBytes + (row - penY) * stride + bit / 8);
                    if ((font[index] & (0x80 >> bit % 8)) != 0) pixels[row * Width + column] = color;
                }
            }
            penX += glyphWidth;
        }
    }

    private void Bounds(long x, long y, int width, int height, out int left, out int top, out int right, out int bottom)
    {
        left = (int)Math.Clamp(x, clipLeft, clipRight);
        top = (int)Math.Clamp(y, clipTop, clipBottom);
        right = (int)Math.Clamp(x + Math.Max(0, width), left, clipRight);
        bottom = (int)Math.Clamp(y + Math.Max(0, height), top, clipBottom);
    }

    private static bool ClipLine(double direction, double distance, ref double start, ref double end)
    {
        if (direction == 0) return distance >= 0;
        double ratio = distance / direction;
        if (direction < 0) start = Math.Max(start, ratio);
        else end = Math.Min(end, ratio);
        return start <= end;
    }
}
