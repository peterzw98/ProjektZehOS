// ZehOS-eigener Code; ergaenzt Cosmos Ext2Superblock.Truncate (efcf20cc, 3.0.97), das Zeigerbloecke nicht freigibt.
using System;
using System.Collections.Generic;

namespace ProjektZeh.Storage.Ext2;

/// <summary>
/// Sammelt die Zeigerbloecke (einfach, doppelt, dreifach indirekt) eines Inodes ohne Cosmos-Abhaengigkeit. Cosmos Truncate
/// gibt nur Datenbloecke frei und laesst die Zeigerbloecke belegt; beim Entfernen eines Verzeichnisses mit mehr als zwoelf
/// Bloecken (E4.0 Lauf 9, 8190 Eintraege) blieben sie sonst als Leck in der Blockbitmap.
/// </summary>
internal static class Ext2IndirectBlocks
{
    /// <summary>Index des einfach indirekten Zeigers in i_block; danach folgen doppelt und dreifach indirekt.</summary>
    public const int SingleIndirectIndex = 12;

    /// <summary>
    /// Liefert jeden Zeigerblock genau einmal. Zeiger ausserhalb des Volumes (0 oder ab <paramref name="blocksCount"/>) werden
    /// uebersprungen statt gelesen, damit ein beschaedigtes Inode keinen fremden Block freigibt oder ausserhalb liest.
    /// </summary>
    public static List<uint> Collect(ReadOnlySpan<uint> block, uint blockSize, uint blocksCount, Func<uint, byte[]> readBlock)
    {
        ArgumentNullException.ThrowIfNull(readBlock);
        var result = new List<uint>();
        var seen = new HashSet<uint>();
        for (int level = 1; level <= 3 && SingleIndirectIndex + level - 1 < block.Length; level++)
            Walk(block[SingleIndirectIndex + level - 1], level, blockSize, blocksCount, readBlock, result, seen);
        return result;
    }

    // depth: Anzahl der Zeigerebenen ab diesem Block; bei 1 zeigt er nur noch auf Datenbloecke, die Truncate freigibt.
    private static void Walk(uint pointer, int depth, uint blockSize, uint blocksCount, Func<uint, byte[]> readBlock,
        List<uint> result, HashSet<uint> seen)
    {
        if (pointer == 0 || pointer >= blocksCount || !seen.Add(pointer)) return;
        result.Add(pointer);
        if (depth == 1) return;
        byte[] data = readBlock(pointer);
        int length = (int)Math.Min(blockSize, (uint)data.Length) & ~3;
        for (int offset = 0; offset < length; offset += 4)
            Walk(BitConverter.ToUInt32(data, offset), depth - 1, blockSize, blocksCount, readBlock, result, seen);
    }
}
