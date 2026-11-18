// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-truncate, see PATCHES.md

namespace Cosmos.Kernel.System.FileSystem.Ext2;

/// <summary>
/// Walks the indirect pointer tree of an inode (single, double, triple) so
/// that <see cref="Ext2Superblock.Truncate"/> can release pointer blocks, not
/// only data blocks. Takes a block reader instead of the superblock so the
/// planning logic is testable without a device.
/// </summary>
internal static class Ext2IndirectBlocks
{
    /// <summary>
    /// What a truncate frees in the pointer tree and which pointers it clears.
    /// </summary>
    /// <param name="Free">Pointer blocks to free, each listed once.</param>
    /// <param name="ClearRoots">Indices into i_block (12..14) whose whole tree is freed.</param>
    /// <param name="ClearEntries">Entries in surviving pointer blocks (block, entry index) that point to freed subtrees.</param>
    internal sealed record TruncatePlan(List<uint> Free, List<int> ClearRoots, List<(uint Block, int Index)> ClearEntries);

    /// <summary>
    /// Lists every pointer block of an inode exactly once. Pointers of 0 or at
    /// or beyond <paramref name="blocksCount"/> are skipped without reading,
    /// so a corrupt inode never frees or reads a block outside the volume.
    /// </summary>
    public static List<uint> Collect(ReadOnlySpan<uint> block, uint blockSize, uint blocksCount, Func<uint, byte[]> readBlock)
    {
        ArgumentNullException.ThrowIfNull(readBlock);
        List<uint> result = [];
        HashSet<uint> seen = [];
        for (int level = 1; level <= 3 && Ext2InodeLayout.SingleIndirectIndex + level - 1 < block.Length; level++)
        {
            Walk(block[Ext2InodeLayout.SingleIndirectIndex + level - 1], level, blockSize, blocksCount, readBlock, result, seen);
        }

        return result;
    }

    /// <summary>
    /// Plans the release of pointer blocks when truncating to
    /// <paramref name="newBlocks"/> logical blocks. A pointer block is freed
    /// when its whole range lies past the new end; a partly used one stays but
    /// loses its entries to freed subtrees. Data blocks and the data pointers
    /// in the lowest level are handled by the caller.
    /// </summary>
    public static TruncatePlan PlanTruncate(ReadOnlySpan<uint> block, ulong newBlocks, uint blockSize, uint blocksCount,
        Func<uint, byte[]> readBlock)
    {
        ArgumentNullException.ThrowIfNull(readBlock);
        TruncatePlan plan = new([], [], []);
        HashSet<uint> seen = [];
        ulong perBlock = blockSize / 4;
        ulong start = Ext2InodeLayout.DirectBlockCount;
        ulong span = perBlock;
        for (int level = 1; level <= 3 && Ext2InodeLayout.SingleIndirectIndex + level - 1 < block.Length; level++)
        {
            uint root = block[Ext2InodeLayout.SingleIndirectIndex + level - 1];
            if (root != 0 && root < blocksCount)
            {
                if (newBlocks <= start)
                {
                    Walk(root, level, blockSize, blocksCount, readBlock, plan.Free, seen);
                    plan.ClearRoots.Add(Ext2InodeLayout.SingleIndirectIndex + level - 1);
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

    // Partly used pointer block of the given depth starting at logical block
    // start: free the children that lie entirely past newBlocks.
    private static void Partial(uint node, int depth, ulong start, ulong newBlocks, ulong perBlock, uint blockSize,
        uint blocksCount, Func<uint, byte[]> readBlock, TruncatePlan plan, HashSet<uint> seen)
    {
        if (depth == 1)
        {
            return;
        }

        ulong childSpan = 1;
        for (int level = 1; level < depth; level++)
        {
            childSpan *= perBlock;
        }

        byte[] data = readBlock(node);
        int entries = (int)Math.Min(perBlock, (ulong)(data.Length / 4));
        for (int index = 0; index < entries; index++)
        {
            uint child = BitConverter.ToUInt32(data, index * 4);
            if (child == 0 || child >= blocksCount)
            {
                continue;
            }

            ulong childStart = start + ((ulong)index * childSpan);
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

    // depth: pointer levels below this block; at 1 it only points to data
    // blocks, which the caller frees, so it is not read.
    private static void Walk(uint pointer, int depth, uint blockSize, uint blocksCount, Func<uint, byte[]> readBlock,
        List<uint> result, HashSet<uint> seen)
    {
        if (pointer == 0 || pointer >= blocksCount || !seen.Add(pointer))
        {
            return;
        }

        result.Add(pointer);
        if (depth == 1)
        {
            return;
        }

        byte[] data = readBlock(pointer);
        int length = (int)Math.Min(blockSize, (uint)data.Length) & ~3;
        for (int offset = 0; offset < length; offset += 4)
        {
            Walk(BitConverter.ToUInt32(data, offset), depth - 1, blockSize, blocksCount, readBlock, result, seen);
        }
    }
}
