// ZehOS-eigener Code; ergaenzt Cosmos Ext2Superblock.Truncate (efcf20cc, 3.0.97), das Zeigerbloecke nicht freigibt.
// Aufrufer: CosmosExt2TruncatePlug (jede Verkuerzung, auch Unlink und Rmdir).
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

    /// <summary>Was nach dem Kuerzen an Zeigerbloecken frei wird und welche Zeiger dafuer zu nullen sind.</summary>
    /// <param name="Free">Freizugebende Zeigerbloecke, jeder genau einmal.</param>
    /// <param name="ClearRoots">Indizes in i_block (12..14), deren ganzer Baum frei wird.</param>
    /// <param name="ClearEntries">Eintraege in verbleibenden Zeigerbloecken (Block, Eintragsindex), die auf freie Teilbaeume zeigen.</param>
    internal sealed record TruncatePlan(List<uint> Free, List<int> ClearRoots, List<(uint Block, int Index)> ClearEntries);

    /// <summary>
    /// Plant die Freigabe der Zeigerbloecke beim Kuerzen auf <paramref name="newBlocks"/> logische Bloecke. Ein Zeigerblock
    /// wird frei, wenn sein ganzer Bereich hinter der neuen Laenge liegt; ein teilweise genutzter bleibt, verliert aber die
    /// Eintraege auf freie Teilbaeume. Die Datenbloecke und deren Zeiger in den untersten Bloecken gibt Cosmos selbst frei.
    /// </summary>
    public static TruncatePlan PlanTruncate(ReadOnlySpan<uint> block, ulong newBlocks, uint blockSize, uint blocksCount,
        Func<uint, byte[]> readBlock)
    {
        ArgumentNullException.ThrowIfNull(readBlock);
        var plan = new TruncatePlan([], [], []);
        var seen = new HashSet<uint>();
        ulong perBlock = blockSize / 4;
        ulong start = SingleIndirectIndex;
        ulong span = perBlock;
        for (int level = 1; level <= 3 && SingleIndirectIndex + level - 1 < block.Length; level++)
        {
            uint root = block[SingleIndirectIndex + level - 1];
            if (root != 0 && root < blocksCount)
            {
                if (newBlocks <= start)
                {
                    Walk(root, level, blockSize, blocksCount, readBlock, plan.Free, seen);
                    plan.ClearRoots.Add(SingleIndirectIndex + level - 1);
                }
                else if (newBlocks < start + span)
                {
                    Partial(root, level, start, newBlocks, perBlock, blockSize, blocksCount, readBlock, plan, seen);
                }
            }

            start += span;
            span = span > ulong.MaxValue / perBlock ? ulong.MaxValue : span * perBlock;
        }

        return plan;
    }

    // Teilweise genutzter Zeigerblock der Ebene depth ab logischem Block start: Kinder ganz hinter newBlocks freigeben.
    private static void Partial(uint node, int depth, ulong start, ulong newBlocks, ulong perBlock, uint blockSize,
        uint blocksCount, Func<uint, byte[]> readBlock, TruncatePlan plan, HashSet<uint> seen)
    {
        if (depth == 1) return;
        ulong childSpan = 1;
        for (int level = 1; level < depth; level++) childSpan *= perBlock;
        byte[] data = readBlock(node);
        int entries = (int)Math.Min(perBlock, (ulong)(data.Length / 4));
        for (int index = 0; index < entries; index++)
        {
            uint child = BitConverter.ToUInt32(data, index * 4);
            if (child == 0 || child >= blocksCount) continue;
            ulong childStart = start + (ulong)index * childSpan;
            if (newBlocks <= childStart)
            {
                Walk(child, depth - 1, blockSize, blocksCount, readBlock, plan.Free, seen);
                plan.ClearEntries.Add((node, index));
            }
            else if (newBlocks < childStart + childSpan)
            {
                Partial(child, depth - 1, childStart, newBlocks, perBlock, blockSize, blocksCount, readBlock, plan, seen);
            }
        }
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
