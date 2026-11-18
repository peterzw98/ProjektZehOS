// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-truncate, see PATCHES.md

using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// Planning logic of <see cref="Ext2IndirectBlocks"/> on a fake block map.
/// </summary>
[TestFixture]
public class Ext2IndirectBlocksTests
{
    private const uint BlockSize = 1024;
    private const uint BlocksCount = 100_000;

    // With 1 KiB blocks: 256 pointers per block; single 12..267, double 268..65803, triple after.
    private const ulong DoubleStart = 12 + 256;

    private Dictionary<uint, byte[]> _disk = null!;
    private List<uint> _reads = null!;

    [SetUp]
    public void SetUp()
    {
        _disk = [];
        _reads = [];
    }

    private byte[] Read(uint block)
    {
        _reads.Add(block);
        return _disk.TryGetValue(block, out byte[]? data) ? data : new byte[BlockSize];
    }

    private void PointerBlock(uint block, params uint[] pointers)
    {
        byte[] data = new byte[BlockSize];
        for (int index = 0; index < pointers.Length; index++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(index * 4, 4), pointers[index]);
        }

        _disk[block] = data;
    }

    private static uint[] Inode(uint single = 0, uint dbl = 0, uint triple = 0)
    {
        uint[] block = new uint[15];
        for (uint index = 0; index < 12; index++)
        {
            block[index] = 500 + index;
        }

        block[12] = single;
        block[13] = dbl;
        block[14] = triple;
        return block;
    }

    [Test]
    public void DirectOnly_NothingToFree()
    {
        Assert.That(Ext2IndirectBlocks.Collect(Inode(), BlockSize, BlocksCount, Read), Is.Empty);
        Assert.That(_reads, Is.Empty);
    }

    [Test]
    public void SingleIndirect_OnlyPointerBlockWithoutReading()
    {
        PointerBlock(900, 0, 0, 0);
        Assert.That(Ext2IndirectBlocks.Collect(Inode(single: 900), BlockSize, BlocksCount, Read), Is.EqualTo(new List<uint> { 900 }));
        Assert.That(_reads, Is.Empty, "data pointers of the first level are not read");
    }

    [Test]
    public void DoubleIndirect_FreesSecondLevelPointerBlocks()
    {
        PointerBlock(910, 911, 0, 912);
        List<uint> result = Ext2IndirectBlocks.Collect(Inode(single: 900, dbl: 910), BlockSize, BlocksCount, Read);
        Assert.That(result, Is.EquivalentTo(new List<uint> { 900, 910, 911, 912 }));
        Assert.That(_reads, Is.EqualTo(new List<uint> { 910 }));
    }

    [Test]
    public void TripleIndirect_WalksAllLevels()
    {
        PointerBlock(920, 921);
        PointerBlock(921, 922, 923);
        List<uint> result = Ext2IndirectBlocks.Collect(Inode(triple: 920), BlockSize, BlocksCount, Read);
        Assert.That(result, Is.EquivalentTo(new List<uint> { 920, 921, 922, 923 }));
    }

    [Test]
    public void PointersOutsideVolume_SkippedWithoutRead()
    {
        PointerBlock(910, BlocksCount, 911, uint.MaxValue);
        List<uint> result = Ext2IndirectBlocks.Collect(Inode(single: BlocksCount + 5, dbl: 910), BlockSize, BlocksCount, Read);
        Assert.That(result, Is.EquivalentTo(new List<uint> { 910, 911 }));
        Assert.That(_reads, Is.EqualTo(new List<uint> { 910 }));
    }

    [Test]
    public void DuplicatePointers_FreedOnce()
    {
        // A duplicate would make FreeBlock raise the free counters twice.
        PointerBlock(910, 911, 911, 910);
        List<uint> result = Ext2IndirectBlocks.Collect(Inode(single: 911, dbl: 910), BlockSize, BlocksCount, Read);
        Assert.That(result, Is.EquivalentTo(new List<uint> { 910, 911 }));
    }

    [TestCase(0UL)]
    [TestCase(12UL)]
    public void PlanTruncate_ToDirectRange_FreesEveryTree(ulong newBlocks)
    {
        PointerBlock(910, 911, 912);
        PointerBlock(920, 921);
        PointerBlock(921, 922);
        Ext2IndirectBlocks.TruncatePlan plan = Ext2IndirectBlocks.PlanTruncate(Inode(single: 900, dbl: 910, triple: 920), newBlocks, BlockSize, BlocksCount, Read);
        Assert.That(plan.Free, Is.EquivalentTo(new List<uint> { 900, 910, 911, 912, 920, 921, 922 }));
        Assert.That(plan.ClearRoots, Is.EquivalentTo(new List<int> { 12, 13, 14 }));
        Assert.That(plan.ClearEntries, Is.Empty);
    }

    [Test]
    public void PlanTruncate_InsideSingle_KeepsSingleFreesDouble()
    {
        PointerBlock(910, 911);
        Ext2IndirectBlocks.TruncatePlan plan = Ext2IndirectBlocks.PlanTruncate(Inode(single: 900, dbl: 910), 13, BlockSize, BlocksCount, Read);
        Assert.That(plan.Free, Is.EquivalentTo(new List<uint> { 910, 911 }));
        Assert.That(plan.ClearRoots, Is.EqualTo(new List<int> { 13 }));
        Assert.That(plan.ClearEntries, Is.Empty);
    }

    [Test]
    public void PlanTruncate_InsideDouble_FreesOnlyChildrenBehindNewEnd()
    {
        // Children 0..3 of the double block; the new end lies in child 2, so only child 3 is freed.
        PointerBlock(910, 911, 912, 913, 914);
        Ext2IndirectBlocks.TruncatePlan plan = Ext2IndirectBlocks.PlanTruncate(Inode(single: 900, dbl: 910), DoubleStart + (2 * 256) + 5, BlockSize, BlocksCount, Read);
        Assert.That(plan.Free, Is.EqualTo(new List<uint> { 914 }));
        Assert.That(plan.ClearRoots, Is.Empty);
        Assert.That(plan.ClearEntries, Is.EqualTo(new List<(uint, int)> { (910, 3) }));
    }

    [Test]
    public void PlanTruncate_AtChildBoundary_FreesThatChild()
    {
        PointerBlock(910, 911, 912);
        Ext2IndirectBlocks.TruncatePlan plan = Ext2IndirectBlocks.PlanTruncate(Inode(single: 900, dbl: 910), DoubleStart + 256, BlockSize, BlocksCount, Read);
        Assert.That(plan.Free, Is.EqualTo(new List<uint> { 912 }));
        Assert.That(plan.ClearEntries, Is.EqualTo(new List<(uint, int)> { (910, 1) }));
    }

    [Test]
    public void PlanTruncate_BeyondUsedRange_NothingToDo()
    {
        PointerBlock(910, 911);
        Ext2IndirectBlocks.TruncatePlan plan = Ext2IndirectBlocks.PlanTruncate(Inode(single: 900, dbl: 910), DoubleStart + (256 * 256), BlockSize, BlocksCount, Read);
        Assert.That(plan.Free.Count + plan.ClearRoots.Count + plan.ClearEntries.Count, Is.EqualTo(0));
    }

    [Test]
    public void ShortInodeArray_NoOutOfRange()
    {
        Assert.That(Ext2IndirectBlocks.Collect(new uint[12], BlockSize, BlocksCount, Read), Is.Empty);
        Assert.That(Ext2IndirectBlocks.Collect(new uint[13], BlockSize, BlocksCount, Read), Is.Empty);
    }
}

/// <summary>
/// <see cref="Ext2Superblock.Truncate"/> on the real driver: pointer blocks
/// are released together with the data blocks.
/// </summary>
[TestFixture]
public class Ext2TruncateTests
{
    private const ulong DeviceBlockSize = 512;
    private const ulong DeviceBlockCount = 32768; // 16 MiB
    private const uint FsBlockSize = 4096;

    // 4 KiB blocks: 12 direct, then 1024 single-indirect blocks.
    [TestCase(20u, Description = "single indirect")]
    [TestCase(12u + 1024u + 40u, Description = "double indirect")]
    public void TruncateToZero_FreesAllBlocksIncludingPointerBlocks(uint fileBlocks)
    {
        Ext2Superblock sb = Mount(out MemoryBlockDevice device);
        uint freeBefore = sb.FreeBlocksCount;

        Assert.That(sb.Root.InodeOperations.Create(sb.Root, "big.bin", VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out IVfsInode? created), Is.True);
        Ext2Inode inode = (Ext2Inode)created!;
        Assert.That(sb.FreeBlocksCount, Is.EqualTo(freeBefore), "creating an empty file must not allocate blocks");

        byte[] block = new byte[FsBlockSize];
        for (uint i = 0; i < fileBlocks; i++)
        {
            uint physical = sb.GetBlockPointer(inode, i, true, out _);
            Assert.That(physical, Is.Not.Zero, $"allocation of logical block {i}");
            block[0] = (byte)i;
            sb.WriteBlocks(physical, 1, block);
        }

        inode.Size = fileBlocks * FsBlockSize;
        sb.WriteInode(inode);
        Assert.That(sb.FreeBlocksCount, Is.LessThan(freeBefore - fileBlocks), "pointer blocks were allocated");

        sb.Truncate(inode, 0);

        Assert.That(sb.FreeBlocksCount, Is.EqualTo(freeBefore));
        Assert.That(inode.Blocks, Is.Zero);
        Assert.That(inode.Block[Ext2InodeLayout.SingleIndirectIndex], Is.Zero);
        Assert.That(inode.Block[Ext2InodeLayout.DoubleIndirectIndex], Is.Zero);

        // The cleared roots and the sector count must be on disk, not only in the cached object.
        Ext2Inode onDisk = Remount(device).ReadInode(inode.InodeNumber, "big.bin");
        Assert.That(onDisk.Blocks, Is.Zero);
        Assert.That(onDisk.Block[Ext2InodeLayout.SingleIndirectIndex], Is.Zero);
        Assert.That(onDisk.Block[Ext2InodeLayout.DoubleIndirectIndex], Is.Zero);
    }

    [Test]
    public void Unlink_FastSymlink_FreesNoBlock()
    {
        Ext2Superblock sb = Mount();

        // Inline target whose first bytes read as block pointer 0x0201 = 513, inside the volume.
        Assert.That(sb.Root.InodeOperations.Symlink(sb.Root, "fast", "\u0001\u0002", out IVfsInode? link), Is.True);
        Assert.That(((Ext2Inode)link!).Blocks, Is.Zero);
        uint freeBefore = sb.FreeBlocksCount;

        Assert.That(sb.Root.InodeOperations.Unlink(sb.Root, "fast"), Is.True);

        Assert.That(sb.FreeBlocksCount, Is.EqualTo(freeBefore));
    }

    [Test]
    public void Unlink_SlowSymlink_FreesItsBlock()
    {
        Ext2Superblock sb = Mount();
        uint freeBefore = sb.FreeBlocksCount;
        string target = new('t', Ext2InodeLayout.InlineSymlinkMax + 1);

        Assert.That(sb.Root.InodeOperations.Symlink(sb.Root, "slow", target, out IVfsInode? link), Is.True);
        Assert.That(((Ext2Inode)link!).Blocks, Is.Not.Zero);
        Assert.That(sb.Root.InodeOperations.Unlink(sb.Root, "slow"), Is.True);

        Assert.That(sb.FreeBlocksCount, Is.EqualTo(freeBefore));
    }

    [Test]
    public void Unlink_FastSymlinkWithAttributeBlock_FreesNoBlock()
    {
        Ext2Superblock sb = Mount();
        Assert.That(sb.Root.InodeOperations.Symlink(sb.Root, "fast", "\u0001\u0002", out IVfsInode? created), Is.True);

        // As created by Linux with ext_attr: the only allocated block is the xattr block.
        Ext2Inode link = (Ext2Inode)created!;
        link.FileAcl = 600;
        link.Blocks = FsBlockSize / 512;
        uint freeBefore = sb.FreeBlocksCount;

        Assert.That(sb.Root.InodeOperations.Unlink(sb.Root, "fast"), Is.True);

        Assert.That(sb.FreeBlocksCount, Is.EqualTo(freeBefore));
    }

    private static Ext2Superblock Mount() => Mount(out _);

    private static Ext2Superblock Mount(out MemoryBlockDevice device)
    {
        device = new("ram0", DeviceBlockSize, DeviceBlockCount);
        Ext2FileSystemType fs = new(device);
        Assert.That(fs.TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = FsBlockSize }), Is.True);
        return Remount(device);
    }

    // A fresh mount has an empty inode cache, so ReadInode returns what is on disk.
    private static Ext2Superblock Remount(MemoryBlockDevice device)
    {
        Ext2FileSystemType fs = new(device);
        Assert.That(fs.TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        return (Ext2Superblock)vsb!;
    }

    [Test]
    public void TruncateInsideDoubleRange_KeepsLivingBlocksAndFreesTheRest()
    {
        Ext2Superblock sb = Mount(out MemoryBlockDevice device);
        Assert.That(sb.Root.InodeOperations.Create(sb.Root, "big.bin", VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out IVfsInode? created), Is.True);
        Ext2Inode inode = (Ext2Inode)created!;

        const uint DoubleStart = 12 + 1024;
        const uint FileBlocks = DoubleStart + 1024 + 10; // two second-level blocks
        byte[] block = new byte[FsBlockSize];
        for (uint i = 0; i < FileBlocks; i++)
        {
            Assert.That(sb.GetBlockPointer(inode, i, true, out _), Is.Not.Zero);
        }

        inode.Size = FileBlocks * FsBlockSize;
        sb.WriteInode(inode);
        uint freeFull = sb.FreeBlocksCount;
        uint blocksFull = inode.Blocks;

        // Keep 5 blocks of the double range: frees 1024 + 10 - 5 data blocks and the second second-level block.
        sb.Truncate(inode, (ulong)(DoubleStart + 5) * FsBlockSize);

        const uint Freed = 1024 + 10 - 5 + 1;
        Assert.That(sb.FreeBlocksCount, Is.EqualTo(freeFull + Freed));
        Assert.That(inode.Blocks, Is.EqualTo(blocksFull - (Freed * (FsBlockSize / 512))));
        Assert.That(Remount(device).ReadInode(inode.InodeNumber, "big.bin").Blocks, Is.EqualTo(inode.Blocks));
        Assert.That(inode.Block[Ext2InodeLayout.DoubleIndirectIndex], Is.Not.Zero);
        Assert.That(sb.GetBlockPointer(inode, DoubleStart + 4, false, out _), Is.Not.Zero);
        Assert.That(sb.GetBlockPointer(inode, DoubleStart + 5, false, out _), Is.Zero);
        Assert.That(sb.GetBlockPointer(inode, DoubleStart + 1024, false, out _), Is.Zero);
    }
}
