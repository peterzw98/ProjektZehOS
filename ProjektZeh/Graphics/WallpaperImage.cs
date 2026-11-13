using System;
using System.Buffers.Binary;
using System.Numerics;

namespace ProjektZeh.Graphics;

internal sealed record WallpaperImage(int Width, int Height, int[] Pixels)
{
    public static WallpaperImage Decode(byte[] data)
    {
        if (data.Length < 54 || data[0] != 'B' || data[1] != 'M') throw new ArgumentException("Erwartet wird ein unkomprimiertes 24-/32-Bit-BMP.");
        int Read(int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        int start = Read(10), header = Read(14), width = Read(18), rawHeight = Read(22);
        int depth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28, 2));
        int compression = Read(30);
        if (header < 40 || start < 14L + header || width is <= 0 or > 4096 || rawHeight is 0 or int.MinValue ||
            Math.Abs((long)rawHeight) > 4096 || (long)width * Math.Abs((long)rawHeight) > 4194304 ||
            data[26] != 1 || data[27] != 0 || depth is not (24 or 32) ||
            (compression != 0 && !(depth == 32 && compression is 3 or 6)))
            throw new ArgumentException("BMP-Format oder Bildgroesse nicht unterstuetzt (maximal 4 Megapixel).");
        int redShift = 16, greenShift = 8, blueShift = 0;
        if (compression is 3 or 6)
        {
            int maskEnd = compression == 6 ? 70 : 66;
            if (data.Length < maskEnd || start < maskEnd || (header != 40 && header < maskEnd - 14))
                throw new ArgumentException("Die BMP-Farbmasken fehlen oder sind unvollstaendig.");
            uint red = unchecked((uint)Read(54)), green = unchecked((uint)Read(58)), blue = unchecked((uint)Read(62));
            uint alpha = compression == 6 || header >= 56 ? unchecked((uint)Read(66)) : 0;
            redShift = BitOperations.TrailingZeroCount(red);
            greenShift = BitOperations.TrailingZeroCount(green);
            blueShift = BitOperations.TrailingZeroCount(blue);
            if (red == 0 || green == 0 || blue == 0 || (red >> redShift) != 255 || (green >> greenShift) != 255 ||
                (blue >> blueShift) != 255 || (red & green) != 0 || (red & blue) != 0 || (green & blue) != 0 ||
                (alpha & (red | green | blue)) != 0)
                throw new ArgumentException("BMP benoetigt getrennte 8-Bit-Farbkanaele ohne ueberlappende Masken.");
        }
        int height = Math.Abs(rawHeight), stride = (width * (depth / 8) + 3) & ~3;
        if (start < 0 || (long)start + (long)stride * height > data.Length) throw new ArgumentException("Die BMP-Datei ist unvollstaendig.");
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            int source = start + (rawHeight > 0 ? height - y - 1 : y) * stride;
            for (int x = 0; x < width; x++)
            {
                int offset = source + x * (depth / 8);
                uint pixel = depth == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4))
                    : (uint)(data[offset + 2] << 16 | data[offset + 1] << 8 | data[offset]);
                // Hintergrundbilder sind deckend; ein optionaler Alphakanal veraendert die Farbkanaele nicht.
                pixels[y * width + x] = unchecked((int)(0xff000000u | ((pixel >> redShift) & 255) << 16 |
                    ((pixel >> greenShift) & 255) << 8 | ((pixel >> blueShift) & 255)));
            }
        }
        return new(width, height, pixels);
    }

    public int[] Fit(int width, int height)
    {
        var result = new int[width * height];
        Fit(result, width, height);
        return result;
    }

    /// <summary>Fuellt einen vorhandenen Puffer bildfuellend, damit Wechsel keine neue Vollbildallokation brauchen.</summary>
    public void Fit(int[] target, int width, int height)
    {
        if ((long)width * height > target.Length) throw new ArgumentException("Der Zielpuffer ist zu klein.", nameof(target));
        double scale = Math.Max((double)width / Width, (double)height / Height);
        double left = (Width - width / scale) / 2, top = (Height - height / scale) / 2;
        for (int y = 0; y < height; y++)
        {
            int source = Math.Min(Height - 1, (int)(top + y / scale)) * Width;
            for (int x = 0; x < width; x++)
                target[y * width + x] = Pixels[source + Math.Min(Width - 1, (int)(left + x / scale))];
        }
    }

    public static readonly string[] BuiltInNames = ["Kanten", "Nacht", "Duenen", "Einfarbig"];

    /// <summary>
    /// Zeichnet ein eingebautes Motiv zeilenweise aus Flaechenspannen. Keine Trigonometrie pro Pixel,
    /// damit ein Wechsel auch bei Full HD im Kernel kurz bleibt.
    /// </summary>
    public static void Create(int[] target, int width, int height, int variant, bool dark, int accent)
    {
        if ((long)width * height > target.Length) throw new ArgumentException("Der Zielpuffer ist zu klein.", nameof(target));
        if (variant == 2) { Dunes(target, width, height, dark); return; }
        uint[] colors = variant switch
        {
            1 => [0xff0f131a, 0xff0f131a, 0xff121722, 0xff141c2a, 0xff182336, 0xff1d2c44],
            3 => dark ? [0xff2e3a48, 0xff2e3a48, 0xff2e3a48, 0xff2e3a48, 0xff2e3a48, 0xff2e3a48]
                      : [0xffdce3ec, 0xffdce3ec, 0xffdce3ec, 0xffdce3ec, 0xffdce3ec, 0xffdce3ec],
            _ => dark ? [0xff151a22, 0xff18202a, 0xff1a212c, 0xff1b2431, 0xff1f2a3a, 0xff24344a]
                      : [0xffdce3ec, 0xffd3dbe5, 0xffccd5e0, 0xffc9d4e1, 0xffbcc9d9, 0xffaebfd3]
        };
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            double v = (double)y / height;
            Array.Fill(target, unchecked((int)colors[0]), row, width);
            Span(target, row, width, v, false, false, 0.2917, 0.3704, colors[1]);
            Span(target, row, width, v, false, true, 0.4063, 0.4444, colors[2]);
            int edge = Span(target, row, width, v, true, false, 0.4896, 0.7037, colors[3]);
            Span(target, row, width, v, true, true, 0.3958, 0.5370, colors[4]);
            Span(target, row, width, v, true, true, 0.3021, 0.3519, colors[5]);
            if (variant != 3 && edge > 0 && edge < width - 1)
            {
                int line = UI.UiPainter.Mix(accent, unchecked((int)colors[3]), dark || variant == 1 ? 35 : 45);
                target[row + width - edge] = line;
                target[row + width - edge - 1] = line;
            }
        }
    }

    // Dreieck an einer Bildecke mit relativen Schenkellaengen; liefert die Spannenbreite der Zeile.
    private static int Span(int[] target, int row, int width, double v, bool right, bool bottom, double across, double down, uint color)
    {
        double distance = bottom ? 1 - v : v;
        if (distance >= down) return 0;
        int length = Math.Min(width, (int)(across * (1 - distance / down) * width));
        Array.Fill(target, unchecked((int)color), right ? row + width - length : row, length);
        return length;
    }

    private static void Dunes(int[] target, int width, int height, bool dark)
    {
        int sky = unchecked((int)(dark ? 0xff2a2420 : 0xffe9dcc9));
        int near = unchecked((int)(dark ? 0xff3a2f27 : 0xffd8c2a3));
        int far = unchecked((int)(dark ? 0xff4a3a2c : 0xffc4a57f));
        Array.Fill(target, sky, 0, width * height);
        for (int x = 0; x < width; x++)
        {
            double u = (double)x / width;
            int first = (int)(height * (0.62 + 0.05 * Math.Sin(u * 7.5)));
            int second = (int)(height * (0.80 + 0.04 * Math.Sin(u * 5.3 + 1.7)));
            for (int y = first; y < height; y++) target[y * width + x] = y < second ? near : far;
        }
    }
}
