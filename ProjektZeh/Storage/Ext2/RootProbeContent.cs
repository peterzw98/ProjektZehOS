#if ZEHOS_STORAGE_ROOT_PROBE || ZEHOS_HOST_TESTS
using System;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;

namespace ProjektZeh.Storage.Ext2;

/// <summary>
/// Deterministischer Inhalt der E4.0-Integritaetsdateien: je 4-KiB-Abschnitt ein SHA-256 ueber Saat, Pfad und
/// Abschnittsnummer, im Abschnitt mit der Byteposition verknuepft. Ein Hash je 4 KiB haelt die 3 GiB plus 2 GiB je Lauf
/// im Gast bezahlbar; vertauschte oder doppelt vergebene Bloecke fallen trotzdem auf, weil Pfad und Abschnitt eingehen.
/// </summary>
internal static class RootProbeContent
{
    internal const int ChunkSize = 4096;

    /// <summary>Fuellt <paramref name="buffer"/> mit dem Inhalt ab Byte <paramref name="offset"/>; offset ist ein Vielfaches von 4 KiB.</summary>
    public static void Fill(string seed, string path, long offset, Span<byte> buffer)
    {
        if (offset % ChunkSize != 0) throw new ArgumentOutOfRangeException(nameof(offset), "Nur ganze 4-KiB-Abschnitte.");
        byte[] prefix = Encoding.UTF8.GetBytes(seed + "\n" + path + "\n");
        byte[] input = new byte[prefix.Length + sizeof(long)];
        prefix.CopyTo(input, 0);
        byte[] hash = new byte[32];
        var digest = new Sha256Digest();
        for (int start = 0; start < buffer.Length; start += ChunkSize)
        {
            BitConverter.TryWriteBytes(input.AsSpan(prefix.Length), (offset + start) / ChunkSize);
            digest.BlockUpdate(input, 0, input.Length);
            digest.DoFinal(hash, 0);
            Span<byte> chunk = buffer.Slice(start, Math.Min(ChunkSize, buffer.Length - start));
            for (int index = 0; index < chunk.Length; index++) chunk[index] = (byte)(hash[index % 32] ^ (index >> 5));
        }
    }

    public static byte[] Create(string seed, string path, int length)
    {
        byte[] content = new byte[length];
        Fill(seed, path, 0, content);
        return content;
    }
}
#endif
