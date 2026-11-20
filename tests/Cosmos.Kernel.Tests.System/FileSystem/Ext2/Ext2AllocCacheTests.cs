// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-alloc-cache, see PATCHES.md

using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// Block allocation with the cached bitmap and descriptor table: file data,
/// bitmap and counters agree on disk after a write call, new data blocks are
/// zero-filled where the caller wrote nothing, and a large write costs about
/// one device write per data block.
/// </summary>
[TestFixture]
public class Ext2AllocCacheTests
{
    private const ulong SectorSize = 512;
    private const ulong Sectors = 16384; // 8 MiB, one block group
    private const uint FsBlockSize = 4096;
    private const VfsMode FileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;

    /// <summary>Counts device writes on top of a RAM device.</summary>
    private sealed class CountingDevice(MemoryBlockDevice inner) : IBlockDevice
    {
        public int Writes { get; set; }

        public string Name => inner.Name;

        public ulong BlockSize => inner.BlockSize;

        public ulong BlockCount => inner.BlockCount;

        public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data) => inner.ReadBlock(blockNo, blockCount, data);

        public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
        {
            Writes++;
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
            // Stale bytes from an earlier use of the disk: a new data block
            // must never show them.
            byte[] junk = new byte[SectorSize * Sectors];
            junk.AsSpan().Fill(fill);
            device.WriteBlock(0, Sectors, junk);
        }

        Assert.That(new Ext2FileSystemType(device).TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = FsBlockSize }), Is.True);
        return device;
    }

    private static Ext2Superblock Mount(IBlockDevice device)
    {
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        return (Ext2Superblock)vsb!;
    }

    private static OpenFile Create(Ext2Superblock sb, string name)
    {
        Assert.That(sb.Root.InodeOperations.Create(sb.Root, name, FileMode, out IVfsInode? inode), Is.True);
        return new OpenFile(inode!);
    }

    private static byte[] Pattern(int length)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)((i * 7) + (i >> 12) + 1);
        }

        return data;
    }

    /// <summary>Free blocks by the on-disk bitmap of group 0 (the only group).</summary>
    private static uint FreeByBitmap(Ext2Superblock sb)
    {
        byte[] bmp = new byte[sb.BlockSize];
        sb.ReadBlocks(sb.GetGroup(0).BlockBitmap, 1, bmp);
        uint free = 0;
        uint max = sb.BlocksCount - sb.FirstDataBlock;
        for (uint i = 0; i < max; i++)
        {
            if ((bmp[i / 8] & (1 << (int)(i % 8))) == 0)
            {
                free++;
            }
        }

        return free;
    }

    [Test]
    public void LargeWrite_Remount_SameContentAndBitmapMatchesCounters()
    {
        MemoryBlockDevice device = Formatted();
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "big");
        byte[] data = Pattern(5 * 1024 * 1024 + 123); // direct, single and double indirect
        for (int off = 0; off < data.Length; off += 65536)
        {
            int len = Math.Min(65536, data.Length - off);
            Assert.That(file.Operations.Write(file, data.AsSpan(off, len)), Is.EqualTo(len));
            file.Position += len;
        }

        uint freeInMemory = sb.FreeBlocksCount;
        file.Operations.Release(file);

        Ext2Superblock again = Mount(device);
        Assert.That(again.Root.InodeOperations.Lookup(again.Root, "big", out IVfsInode? inode), Is.True);
        OpenFile reader = new(inode!);
        byte[] back = new byte[data.Length];
        Assert.That(reader.Operations.Read(reader, back), Is.EqualTo(data.Length));
        Assert.That(back, Is.EqualTo(data));

        Assert.That(again.FreeBlocksCount, Is.EqualTo(freeInMemory), "superblock counter on disk");
        Assert.That((uint)again.GetGroup(0).FreeBlocksCount, Is.EqualTo(freeInMemory), "group descriptor on disk");
        Assert.That(FreeByBitmap(again), Is.EqualTo(freeInMemory), "bitmap on disk");
    }

    [Test]
    public void EachWriteCall_LeavesBitmapAndDescriptorConsistentOnDisk()
    {
        MemoryBlockDevice device = Formatted();
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "f");
        byte[] chunk = Pattern(20_000);
        for (int i = 0; i < 4; i++)
        {
            Assert.That(file.Operations.Write(file, chunk), Is.EqualTo(chunk.Length));
            file.Position += chunk.Length;

            // No Release or Flush: a hard stop here must find matching metadata.
            Ext2Superblock probe = Mount(device);
            Assert.That((uint)probe.GetGroup(0).FreeBlocksCount, Is.EqualTo(sb.FreeBlocksCount), "descriptor after write " + i);
            Assert.That(FreeByBitmap(probe), Is.EqualTo(sb.FreeBlocksCount), "bitmap after write " + i);
            Assert.That(probe.FreeBlocksCount, Is.EqualTo(sb.FreeBlocksCount), "superblock after write " + i);
        }
    }

    [Test]
    public void PartialWritesIntoNewBlocks_ReadZerosAroundData()
    {
        MemoryBlockDevice device = Formatted(0xAA);
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "sparse");
        long[] offsets = [5 * FsBlockSize + 7, 13 * FsBlockSize + 100, (12 + 1024 + 3) * (long)FsBlockSize + 4000];
        foreach (long offset in offsets)
        {
            file.Position = offset;
            Assert.That(file.Operations.Write(file, "abcdefghij"u8), Is.EqualTo(10));
        }

        file.Operations.Release(file);

        Ext2Superblock again = Mount(device);
        Assert.That(again.Root.InodeOperations.Lookup(again.Root, "sparse", out IVfsInode? inode), Is.True);
        OpenFile reader = new(inode!);
        long size = offsets[^1] + 10;
        byte[] back = new byte[size];
        Assert.That(reader.Operations.Read(reader, back), Is.EqualTo(size));
        byte[] expected = new byte[size];
        foreach (long offset in offsets)
        {
            "abcdefghij"u8.CopyTo(expected.AsSpan((int)offset));
        }

        Assert.That(back, Is.EqualTo(expected));
    }

    [Test]
    public void OneMiBWrite_CostsAboutOneDeviceWritePerBlock()
    {
        CountingDevice device = new(Formatted());
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "w");
        byte[] data = Pattern(1024 * 1024);
        device.Writes = 0;

        Assert.That(file.Operations.Write(file, data), Is.EqualTo(data.Length));

        // 256 data blocks, then once each: zeroed indirect block, held-back
        // indirect block, bitmap, descriptor block, superblock and inode.
        // Before the patch every block also cost a zero write, a bitmap
        // write, a descriptor write, a superblock write and (past the 12
        // direct blocks) an indirect block write.
        Assert.That(device.Writes, Is.LessThanOrEqualTo(256 + 8));
    }

    [Test]
    public void FreedBlock_IsHandedOutAgain()
    {
        Ext2Superblock sb = Mount(Formatted());
        Assert.That(sb.TryAllocateBlock(0, out uint first), Is.True);
        Assert.That(sb.TryAllocateBlock(0, out uint second), Is.True);
        Assert.That(second, Is.GreaterThan(first));

        sb.FreeBlock(first);

        Assert.That(sb.TryAllocateBlock(0, out uint reused), Is.True);
        Assert.That(reused, Is.EqualTo(first), "the scan start moves back to a freed block");
    }
}
