// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-direct-runs, see PATCHES.md

using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// Whole-block writes go out as runs of consecutive physical blocks: a large
/// call costs a handful of device writes, a pointer block never reaches the
/// disk before the data it points at, and a failed run leaves holes instead
/// of stale disk contents.
/// </summary>
[TestFixture]
public class Ext2DirectRunTests
{
    private const ulong SectorSize = 512;
    private const ulong Sectors = 16384; // 8 MiB, one block group
    private const uint FsBlockSize = 4096;
    private const VfsMode FileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;

    /// <summary>
    /// RAM device that logs every write (first sector, length, copy of the
    /// bytes) and can fail the next write longer than one fs block, after
    /// letting a number of such runs through, and optionally every write
    /// after that.
    /// </summary>
    private sealed class LoggingDevice(MemoryBlockDevice inner) : IBlockDevice
    {
        public List<(ulong Lba, byte[] Data)> Log { get; } = new();

        public bool FailNextRun { get; set; }

        /// <summary>Runs that still succeed before <see cref="FailNextRun"/> strikes.</summary>
        public int RunsToPass { get; set; }

        /// <summary>After the failed run every further write fails too (dead device).</summary>
        public bool FailAllAfterRun { get; set; }

        private bool _dead;

        public string Name => inner.Name;

        public ulong BlockSize => inner.BlockSize;

        public ulong BlockCount => inner.BlockCount;

        public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data) => inner.ReadBlock(blockNo, blockCount, data);

        public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
        {
            if (_dead)
            {
                throw new IOException("device gone");
            }

            if (FailNextRun && blockCount * SectorSize > FsBlockSize && RunsToPass-- <= 0)
            {
                FailNextRun = false;
                _dead = FailAllAfterRun;
                // Part of the run reaches the disk before the error.
                inner.WriteBlock(blockNo, FsBlockSize / SectorSize, data[..(int)FsBlockSize]);
                throw new IOException("injected run failure");
            }

            Log.Add((blockNo, data[..(int)(blockCount * SectorSize)].ToArray()));
            inner.WriteBlock(blockNo, blockCount, data);
        }

        public void Flush() => inner.Flush();
    }

    private sealed class OpenFile(IVfsInode inode) : IVfsOpenFile
    {
        public IVfsInode Inode { get; } = inode;

        public IFileOperations Operations => Inode.FileOperations!;

        public long Position { get; set; }
    }

    private static MemoryBlockDevice Formatted(byte fill = 0)
    {
        MemoryBlockDevice device = new("ram0", SectorSize, Sectors);
        if (fill != 0)
        {
            byte[] junk = new byte[SectorSize * Sectors];
            junk.AsSpan().Fill(fill);
            device.WriteBlock(0, Sectors, junk);
        }

        Assert.That(new Ext2FileSystemType(device).TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = FsBlockSize }), Is.True);
        return device;
    }

    private static Ext2Superblock Mount(IBlockDevice device, MountFlags flags = MountFlags.None)
    {
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, flags, out IVfsSuperblock? vsb), Is.True);
        return (Ext2Superblock)vsb!;
    }

    private static Ext2Superblock Inspect(IBlockDevice device) => Mount(device, MountFlags.ReadOnly);

    private static OpenFile Create(Ext2Superblock sb, string name)
    {
        Assert.That(sb.Root.InodeOperations.Create(sb.Root, name, FileMode, out IVfsInode? inode), Is.True);
        return new OpenFile(inode!);
    }

    private static byte[] Pattern(int length, int seed = 1)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 7) + (i >> 12) + seed);
        }

        return data;
    }

    private static byte[] ReadBack(IBlockDevice device, string name, long size)
    {
        Ext2Superblock again = Inspect(device);
        Assert.That(again.Root.InodeOperations.Lookup(again.Root, name, out IVfsInode? inode), Is.True);
        OpenFile reader = new(inode!);
        byte[] back = new byte[size];
        Assert.That(reader.Operations.Read(reader, back), Is.EqualTo(size));
        return back;
    }

    private static uint FreeByBitmap(Ext2Superblock sb)
    {
        byte[] bmp = new byte[sb.BlockSize];
        sb.ReadBlocks(sb.GetGroup(0).BlockBitmap, 1, bmp);
        uint free = 0;
        for (uint i = 0; i < sb.BlocksCount - sb.FirstDataBlock; i++)
        {
            if ((bmp[i / 8] & (1 << (int)(i % 8))) == 0)
            {
                free++;
            }
        }

        return free;
    }

    private static void AssertCountersOnDisk(IBlockDevice device, uint freeInMemory)
    {
        Ext2Superblock again = Inspect(device);
        Assert.That(FreeByBitmap(again), Is.EqualTo(freeInMemory), "bitmap on disk");
        Assert.That((uint)again.GetGroup(0).FreeBlocksCount, Is.EqualTo(freeInMemory), "descriptor on disk");
        Assert.That(again.FreeBlocksCount, Is.EqualTo(freeInMemory), "superblock on disk");
    }

    [TestCase(0u, 12u)]
    [TestCase(11u, 12u)]
    [TestCase(12u, 1036u)]
    [TestCase(1035u, 1036u)]
    [TestCase(1036u, 2060u)]
    [TestCase(2059u, 2060u)]
    [TestCase(2060u, 3084u)]
    public void NextPointerBoundary_EndsAtPointerBlock(uint logical, uint boundary)
    {
        Assert.That(Ext2FileOperations.NextPointerBoundary(logical, 1024), Is.EqualTo(boundary));
    }

    [Test]
    public void OneMiBWrite_InOneCall_CostsAFewDeviceWrites()
    {
        LoggingDevice device = new(Formatted());
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "w");
        byte[] data = Pattern(1024 * 1024);
        device.Log.Clear();

        Assert.That(file.Operations.Write(file, data), Is.EqualTo(data.Length));

        // Runs for the direct and the indirect blocks, the zeroed indirect
        // block, then bitmap, held-back indirect block, descriptor,
        // superblock and inode. One write per block would be 256 + 8.
        Assert.That(device.Log.Count, Is.LessThanOrEqualTo(20));
        file.Operations.Release(file);
        Assert.That(ReadBack(device, "w", data.Length), Is.EqualTo(data));
    }

    [Test]
    public void PointerBlock_ReachesDiskOnlyAfterTheDataItPointsAt()
    {
        LoggingDevice device = new(Formatted());
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "order");
        // Direct, the whole single indirect range and 400 blocks behind the
        // first second-level block: the single indirect block is written
        // back in the middle of the call, when the window moves on.
        byte[] data = Pattern((12 + 1024 + 400) * (int)FsBlockSize);
        device.Log.Clear();
        Assert.That(file.Operations.Write(file, data), Is.EqualTo(data.Length));

        Ext2Inode inode = (Ext2Inode)file.Inode;
        uint indirect = inode.Block[Ext2InodeLayout.SingleIndirectIndex];
        uint doubleIndirect = inode.Block[Ext2InodeLayout.DoubleIndirectIndex];
        byte[] top = new byte[FsBlockSize];
        sb.ReadBlocks(doubleIndirect, 1, top);
        uint second = BitConverter.ToUInt32(top, 0);
        Assert.That(indirect, Is.Not.Zero);
        Assert.That(second, Is.Not.Zero);

        const ulong perFsBlock = FsBlockSize / SectorSize;
        HashSet<uint> dataWritten = new();
        int pointerWrites = 0;
        foreach ((ulong lba, byte[] bytes) in device.Log)
        {
            uint first = (uint)(lba / perFsBlock);
            uint count = (uint)(bytes.Length / (int)FsBlockSize);
            if (count == 1 && (first == indirect || first == second))
            {
                for (int off = 0; off < bytes.Length; off += 4)
                {
                    uint target = BitConverter.ToUInt32(bytes, off);
                    if (target != 0)
                    {
                        Assert.That(dataWritten, Does.Contain(target), $"pointer block {first} names block {target} before its data");
                    }
                }

                pointerWrites++;
                continue;
            }

            for (uint i = 0; i < count; i++)
            {
                dataWritten.Add(first + i);
            }
        }

        Assert.That(pointerWrites, Is.GreaterThanOrEqualTo(2), "both pointer blocks written with entries");
        file.Operations.Release(file);
        Assert.That(ReadBack(device, "order", data.Length), Is.EqualTo(data));
    }

    [Test]
    public void FragmentedVolume_RunsBreakAtUsedBlocks_ContentSurvivesRemount()
    {
        LoggingDevice device = new(Formatted());
        Ext2Superblock sb = Mount(device);
        // Every second free block of a stretch stays used: new files get
        // single free blocks, then one long run behind the stretch.
        List<uint> taken = new();
        for (int i = 0; i < 200; i++)
        {
            Assert.That(sb.TryAllocateBlock(0, out uint b), Is.True);
            taken.Add(b);
        }

        for (int i = 0; i < taken.Count; i += 2)
        {
            sb.FreeBlock(taken[i]);
        }

        OpenFile file = Create(sb, "frag");
        byte[] data = Pattern(1024 * 1024, seed: 3);
        device.Log.Clear();
        Assert.That(file.Operations.Write(file, data), Is.EqualTo(data.Length));
        Assert.That(device.Log.Count, Is.GreaterThan(100), "runs break at the used blocks");
        uint freeInMemory = sb.FreeBlocksCount;
        file.Operations.Release(file);

        Assert.That(ReadBack(device, "frag", data.Length), Is.EqualTo(data));
        AssertCountersOnDisk(device, freeInMemory);
    }

    [Test]
    public void Overwrite_UnalignedHeadAndTail_KeepsNeighbourBytes()
    {
        MemoryBlockDevice device = Formatted(0xAA);
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "ow");
        byte[] data = Pattern(2 * 1024 * 1024);
        Assert.That(file.Operations.Write(file, data), Is.EqualTo(data.Length));

        // Starts and ends inside a block, crosses the direct/indirect border.
        byte[] patch = Pattern(300 * 1024 + 123, seed: 9);
        file.Position = 5 * FsBlockSize + 100;
        Assert.That(file.Operations.Write(file, patch), Is.EqualTo(patch.Length));
        patch.CopyTo(data, 5 * (int)FsBlockSize + 100);

        // A new tail past the old end, unaligned at both ends.
        byte[] tail = Pattern(5 * (int)FsBlockSize + 17, seed: 5);
        long tailAt = data.Length + 3 * FsBlockSize + 9;
        file.Position = tailAt;
        Assert.That(file.Operations.Write(file, tail), Is.EqualTo(tail.Length));
        file.Operations.Release(file);

        byte[] expected = new byte[tailAt + tail.Length];
        data.CopyTo(expected, 0);
        tail.CopyTo(expected, tailAt);
        Assert.That(ReadBack(device, "ow", expected.Length), Is.EqualTo(expected));
    }

    [Test]
    public void FailedRun_LeavesHolesForItsNewBlocks_AndConsistentCounters()
    {
        LoggingDevice device = new(Formatted(0xAA));
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "fail");
        // Block 13 exists (and with it the indirect block); block 12 and
        // everything from 14 on are new in the failing call.
        file.Position = 13 * FsBlockSize;
        Assert.That(file.Operations.Write(file, "abcdefghij"u8), Is.EqualTo(10));
        uint freeBefore = sb.FreeBlocksCount;

        byte[] big = Pattern(256 * (int)FsBlockSize, seed: 7);
        device.FailNextRun = true;
        file.Position = 12 * FsBlockSize;
        Assert.Throws<IOException>(() => file.Operations.Write(file, big));

        // Blocks 12 and 13 went out as single-block runs before the failing one.
        Assert.That(sb.FreeBlocksCount, Is.EqualTo(freeBefore - 1), "only block 12 stays allocated");

        // A later successful write persists the inode.
        file.Position = 300 * FsBlockSize;
        Assert.That(file.Operations.Write(file, "ABCDEFGHIJ"u8), Is.EqualTo(10));
        uint freeInMemory = sb.FreeBlocksCount;
        file.Operations.Release(file);

        long size = 300 * FsBlockSize + 10;
        byte[] expected = new byte[size];
        big.AsSpan(0, 2 * (int)FsBlockSize).CopyTo(expected.AsSpan(12 * (int)FsBlockSize));
        "ABCDEFGHIJ"u8.CopyTo(expected.AsSpan(300 * (int)FsBlockSize));
        Assert.That(ReadBack(device, "fail", size), Is.EqualTo(expected), "blocks 14.. read zeros, not 0xAA");
        AssertCountersOnDisk(device, freeInMemory);
    }

    /// <summary>
    /// Every block the file names on disk (inode and single indirect block)
    /// is used in the bitmap and holds written data, never the 0xAA the
    /// device was filled with; counters agree. Returns the remounted inode
    /// and the number of data pointers.
    /// </summary>
    private static (Ext2Inode Inode, int Pointers) AssertDiskPointersSafe(IBlockDevice device, string name)
    {
        Ext2Superblock again = Inspect(device);
        Assert.That(again.Root.InodeOperations.Lookup(again.Root, name, out IVfsInode? found), Is.True);
        Ext2Inode inode = (Ext2Inode)found!;
        Assert.That(inode.Block[Ext2InodeLayout.DoubleIndirectIndex], Is.Zero, "test stays below the double indirect range");

        byte[] bmp = new byte[FsBlockSize];
        again.ReadBlocks(again.GetGroup(0).BlockBitmap, 1, bmp);
        bool Used(uint blk) => (bmp[(blk - again.FirstDataBlock) / 8] & (1 << (int)((blk - again.FirstDataBlock) % 8))) != 0;

        List<uint> targets = new();
        for (int i = 0; i < Ext2InodeLayout.DirectBlockCount; i++)
        {
            if (inode.Block[i] != 0)
            {
                targets.Add(inode.Block[i]);
            }
        }

        uint indirect = inode.Block[Ext2InodeLayout.SingleIndirectIndex];
        if (indirect != 0)
        {
            Assert.That(Used(indirect), Is.True, "indirect block used in the bitmap");
            byte[] ptrs = new byte[FsBlockSize];
            again.ReadBlocks(indirect, 1, ptrs);
            for (int off = 0; off < ptrs.Length; off += 4)
            {
                uint target = BitConverter.ToUInt32(ptrs, off);
                if (target != 0)
                {
                    targets.Add(target);
                }
            }
        }

        byte[] block = new byte[FsBlockSize];
        foreach (uint target in targets)
        {
            Assert.That(target, Is.LessThan(again.BlocksCount), $"pointer {target} inside the volume");
            Assert.That(Used(target), Is.True, $"block {target} named on disk but free in the bitmap");
            again.ReadBlocks(target, 1, block);
            Assert.That(block.All(b => b == 0xAA), Is.False, $"block {target} named on disk but holds stale disk contents");
        }

        Assert.That(FreeByBitmap(again), Is.EqualTo(again.FreeBlocksCount), "bitmap agrees with the superblock");
        Assert.That((uint)again.GetGroup(0).FreeBlocksCount, Is.EqualTo(again.FreeBlocksCount), "descriptor agrees with the superblock");
        return (inode, targets.Count);
    }

    [Test]
    public void LateRunFails_DiskAfterwards_KeepsOldFileAndNamesOnlyWrittenBlocks()
    {
        // laser review P5 (#577): disk state right after a late run fails,
        // without a later write that would make the inode durable.
        LoggingDevice device = new(Formatted(0xAA));
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "late");
        byte[] old = Pattern(20 * (int)FsBlockSize, seed: 2);
        Assert.That(file.Operations.Write(file, old), Is.EqualTo(old.Length));

        // 600 blocks behind block 20: windows [20,276), [276,532), [532,620).
        // The first window goes out, the run of the second one fails.
        byte[] more = Pattern(600 * (int)FsBlockSize, seed: 4);
        device.FailNextRun = true;
        device.RunsToPass = 1;
        file.Position = old.Length;
        Assert.That(() => file.Operations.Write(file, more), Throws.InstanceOf<IOException>().With.Message.EqualTo("injected run failure"));
        Assert.That(device.FailNextRun, Is.False, "the injected failure struck");

        (Ext2Inode onDisk, int pointers) = AssertDiskPointersSafe(device, "late");

        // The inode is not written after the error: size and i_blocks are
        // the old ones, and the old content reads back unchanged.
        Assert.That(onDisk.FullSize, Is.EqualTo((ulong)old.Length), "size on disk");
        Assert.That(onDisk.Blocks, Is.EqualTo((20u + 1u) * (FsBlockSize / 512)), "i_blocks on disk: 20 data blocks and the indirect block");
        Assert.That(ReadBack(device, "late", old.Length), Is.EqualTo(old));

        // Known leftover (pre-existing since ext2-alloc-cache #558, see the
        // patch file): the held-back indirect block still names the blocks
        // of the first window, past EOF. They hold their data (checked
        // above); the failed window names nothing.
        Assert.That(pointers, Is.EqualTo(276), "20 old blocks and the 256 blocks of the written window");
    }

    [Test]
    public void LateRunFails_ThenDeviceGone_CallerSeesIOException_DiskStaysSafe()
    {
        // laser review P5/E3 (#577): the batch end fails as well. The
        // caller gets an IOException (today the one from the batch end, not
        // the run's: pre-existing, see the patch file), and the disk keeps
        // the old file.
        LoggingDevice device = new(Formatted(0xAA));
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "dead");
        byte[] old = Pattern(20 * (int)FsBlockSize, seed: 6);
        Assert.That(file.Operations.Write(file, old), Is.EqualTo(old.Length));

        device.FailNextRun = true;
        device.RunsToPass = 1;
        device.FailAllAfterRun = true;
        file.Position = old.Length;
        Assert.Throws(Is.InstanceOf<IOException>(), () => file.Operations.Write(file, Pattern(600 * (int)FsBlockSize, seed: 8)));

        (Ext2Inode onDisk, int pointers) = AssertDiskPointersSafe(device, "dead");
        Assert.That(onDisk.FullSize, Is.EqualTo((ulong)old.Length), "size on disk");
        Assert.That(pointers, Is.EqualTo(20), "neither bitmap nor indirect block of the failed call reached the disk");
        Assert.That(ReadBack(device, "dead", old.Length), Is.EqualTo(old));
    }

    [Test]
    public void WriteDataRun_RefusesShortSourceAndRangesOutsideTheVolume()
    {
        Ext2Superblock sb = Mount(Formatted());
        Assert.That(sb.TryAllocateBlock(0, out uint blk), Is.True);
        Assert.Throws<IOException>(() => sb.WriteDataRun(blk, 2, new byte[FsBlockSize]));
        Assert.Throws<IOException>(() => sb.WriteDataRun(sb.BlocksCount - 1, 2, new byte[2 * FsBlockSize]));
        Assert.DoesNotThrow(() => sb.WriteDataRun(blk, 1, new byte[FsBlockSize]));
    }
}
