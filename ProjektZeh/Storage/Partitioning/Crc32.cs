using System;

namespace ProjektZeh.Storage.Partitioning;

/// <summary>
/// CRC-32 nach IEEE 802.3 (reflektiert, Polynom 0xEDB88320), wie UEFI sie fuer GPT-Kopf und Eintragstabelle verlangt.
/// Eigenbau, weil System.IO.Hashing ein zusaetzliches Paket waere und Cosmos 3.0.97 selbst keine CRC rechnet
/// (Gpt.Create schreibt 0).
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint entry = i;
            for (int bit = 0; bit < 8; bit++) entry = (entry & 1) != 0 ? 0xEDB88320u ^ (entry >> 1) : entry >> 1;
            table[i] = entry;
        }
        return table;
    }
}
