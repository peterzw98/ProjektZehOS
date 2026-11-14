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

    /// <summary>Liest die im Cosmos-Paket eingebettete PSF2-Standardschrift, ohne die Kernelassembly zu laden.</summary>
    public static UiPainter? LoadCosmosFont()
    {
        string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        string path = Path.Combine(packages, "cosmos.kernel.system", "3.0.94", "runtimes", "linux-x64", "lib", "net10.0", "Cosmos.Kernel.System.dll");
        if (!File.Exists(path)) return null;
        byte[] assembly = File.ReadAllBytes(path);
        // Das Magic steht auch im Ladecode; die Ressource erkennt man am folgenden Version-0-/32-Byte-Kopf.
        ReadOnlySpan<byte> magic = [0x72, 0xb5, 0x4a, 0x86, 0, 0, 0, 0, 32, 0, 0, 0];
        int start = assembly.AsSpan().IndexOf(magic);
        if (start < 0) return null;
        var header = assembly.AsSpan(start);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int glyphBytes = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
        return new UiPainter(header.Slice(headerSize, 256 * glyphBytes).ToArray(), width, height);
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
