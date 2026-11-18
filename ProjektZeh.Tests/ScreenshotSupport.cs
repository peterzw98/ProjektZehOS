using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using ProjektZeh.UI;

namespace ProjektZeh.Tests;

/// <summary>Nur fuer Testbilder: echte Cosmos-Schrift laden und Bildpuffer als PNG speichern.</summary>
internal static class ScreenshotSupport
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>
    /// Liest die PSF2-Standardschrift aus TestData/DefaultFont.psf (byteweise die Ressource von Cosmos.Kernel.System, Herkunft
    /// in TestData/DefaultFont.ORIGIN.md). Null, wenn die Datei neben der Testassembly fehlt.
    /// </summary>
    public static (byte[] Data, int Width, int Height)? LoadCosmosFontData(string? baseDirectory = null)
    {
        string path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "TestData", "DefaultFont.psf");
        if (!File.Exists(path)) return null;
        byte[] file = File.ReadAllBytes(path);
        // PSF2: Magic 72 b5 4a 86, Version 0, Kopfgroesse 32; dahinter 256 Glyphen (Kopf: Glyphgroesse, Hoehe, Breite).
        ReadOnlySpan<byte> magic = [0x72, 0xb5, 0x4a, 0x86, 0, 0, 0, 0, 32, 0, 0, 0];
        if (file.Length < 32 || !file.AsSpan().StartsWith(magic)) return null;
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        int glyphBytes = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(20));
        int height = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(24));
        int width = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(28));
        if (file.Length < headerSize + 256 * glyphBytes) return null;
        return (file.AsSpan(headerSize, 256 * glyphBytes).ToArray(), width, height);
    }

    public static void WritePng(string path, int width, int height, int[] pixels)
    {
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(file, "IHDR", header);
        using var image = new MemoryStream();
        using (var zlib = new ZLibStream(image, CompressionLevel.Fastest, true))
        {
            var row = new byte[1 + width * 3];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int color = pixels[y * width + x];
                    row[1 + x * 3] = (byte)(color >> 16);
                    row[2 + x * 3] = (byte)(color >> 8);
                    row[3 + x * 3] = (byte)color;
                }
                zlib.Write(row);
            }
        }
        Chunk(file, "IDAT", image.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var buffer = new byte[12 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        for (int i = 0; i < 4; i++) buffer[4 + i] = (byte)type[i];
        data.CopyTo(buffer, 8);
        uint crc = 0xffffffff;
        for (int i = 4; i < 8 + data.Length; i++) crc = CrcTable[(crc ^ buffer[i]) & 0xff] ^ crc >> 8;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8 + data.Length), ~crc);
        stream.Write(buffer);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ c >> 1 : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
