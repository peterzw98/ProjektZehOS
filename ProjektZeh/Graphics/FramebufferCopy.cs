using System;

namespace ProjektZeh.Graphics;

/// <summary>Uebertraegt Rechtecke aus dem zusammenhaengenden Bildpuffer in einen Framebuffer mit eigener Zeilenlaenge.</summary>
internal static class FramebufferCopy
{
    /// <summary>
    /// Kopiert den sichtbaren Teil des Rechtecks zeilenweise und liefert ihn zurueck; Breite oder Hoehe 0 heisst, dass
    /// nichts kopiert wurde. Der Zuschnitt entspricht <c>Canvas.Display()</c> aus Cosmos 3.0.94, damit Teil- und
    /// Vollausgabe dieselben Pixel erzeugen.
    /// </summary>
    public static (int X, int Y, int Width, int Height) CopyRectangle(ReadOnlySpan<int> source, int sourceWidth, int sourceHeight,
        Span<int> target, int targetStride, int targetWidth, int targetHeight, int x, int y, int width, int height)
    {
        int columns = Math.Min(Math.Min(sourceWidth, targetWidth), targetStride);
        int rows = Math.Min(sourceHeight, targetHeight);
        int left = (int)Math.Clamp((long)x, 0, Math.Max(0, columns));
        int top = (int)Math.Clamp((long)y, 0, Math.Max(0, rows));
        int right = (int)Math.Clamp((long)x + Math.Max(0, width), left, Math.Max(left, columns));
        int bottom = (int)Math.Clamp((long)y + Math.Max(0, height), top, Math.Max(top, rows));
        for (int row = top; row < bottom; row++)
            source.Slice(row * sourceWidth + left, right - left).CopyTo(target.Slice(row * targetStride + left, right - left));
        return (left, top, right - left, bottom - top);
    }
}
