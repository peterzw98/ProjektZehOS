using System;

namespace ProjektZeh.Graphics;

internal static class GermanFont
{
    public static int Glyph(char character) => character == '€' ? 128 : character <= 255 ? character : '?';

    public static string ConsoleText(string text) => text.Replace('€', '\u0080');

    public static byte[] Normalize(byte[] source, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int size = checked((width + 7) / 8 * height);
        if (source.Length / size < 256) throw new ArgumentException("Der Font benoetigt mindestens 256 Zeichen.", nameof(source));
        byte[] data = (byte[])source.Clone();
        // shortcut: Beide ausgelieferten Cosmos-3.0.94-Defaultfonts; fuer eigene Fonts die PSF-Unicodetabelle auswerten.
        ReadOnlySpan<char> characters = "äöüÄÖÜß€§°²³µ´";
        ReadOnlySpan<int> positions;
        if (width == 16 && height == 32 && source.Length / size == 256)
            positions = [228, 246, 252, 132, 150, 156, 159, 164, 167, 176, 178, 179, 181, 39];
        else if (width == 8 && height == 16 && source.Length / size == 512)
            positions = [132, 148, 129, 142, 153, 154, 225, 353, 21, 248, 253, 6, 230, 8];
        else throw new NotSupportedException("Die deutsche Zeichenzuordnung benoetigt einen Cosmos-3.0.94-Defaultfont.");
        for (int i = 0; i < characters.Length; i++)
        {
            int from = positions[i] < source.Length / size ? positions[i] : '?';
            source.AsSpan(from * size, size).CopyTo(data.AsSpan(Glyph(characters[i]) * size, size));
        }
        return data;
    }
}
