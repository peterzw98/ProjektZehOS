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

    /// <summary>
    /// RAM device that fails one armed read or write. A failing read first
    /// overwrites part of the caller's buffer, like a transfer that stopped
    /// half way.
    /// </summary>
    private sealed class FaultDevice(MemoryBlockDevice inner) : IBlockDevice
    {
        /// <summary>The next device write throws.</summary>
        public bool FailNextWrite { get; set; }

        /// <summary>A read starting at this sector zeroes 128 bytes and throws; ulong.MaxValue = off.</summary>
        public ulong FailReadAt { get; set; } = ulong.MaxValue;

        public string Name => inner.Name;

        public ulong BlockSize => inner.BlockSize;

        public ulong BlockCount => inner.BlockCount;

        public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data)
        {
            if (blockNo == FailReadAt)
            {
                FailReadAt = ulong.MaxValue;
                data[..128].Clear();
                throw new IOException("injected partial read");
            }

            inner.ReadBlock(blockNo, blockCount, data);
        }

        public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("injected write failure");
            }

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

    private static Ext2Superblock Mount(IBlockDevice device, MountFlags flags = MountFlags.None)
    {
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, flags, out IVfsSuperblock? vsb), Is.True);
        return (Ext2Superblock)vsb!;
    }

    /// <summary>Second look at the disk next to a live writable mount (read-only, as only one writer is allowed).</summary>
    private static Ext2Superblock Inspect(IBlockDevice device) => Mount(device, MountFlags.ReadOnly);

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
    private static uint FreeByBitmap(Ext2Superblock sb) => FreeByBitmap(sb, 0, sb.BlocksCount - sb.FirstDataBlock);

    /// <summary>Free blocks among the first <paramref name="max"/> bits of a group's on-disk bitmap.</summary>
    private static uint FreeByBitmap(Ext2Superblock sb, uint group, uint max)
    {
        byte[] bmp = new byte[sb.BlockSize];
        sb.ReadBlocks(sb.GetGroup(group).BlockBitmap, 1, bmp);
        uint free = 0;
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

        Ext2Superblock again = Inspect(device);
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
            Ext2Superblock probe = Inspect(device);
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

        Ext2Superblock again = Inspect(device);
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
    [Test]
    public void SecondWritableMount_OfSameDevice_IsRefusedUntilDrop()
    {
        MemoryBlockDevice device = Formatted();
        Ext2Superblock first = Mount(device);

        // Two writers with their own cached bitmaps would hand out the same blocks.
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out _), Is.False);
        Ext2Superblock reader = Inspect(device);
        Assert.That(reader.IsReadOnly, Is.True);
        Assert.That(Mount(Formatted()).IsReadOnly, Is.False, "another device is not affected");

        first.Drop();
        Ext2Superblock again = Mount(device);
        Assert.That(again.IsReadOnly, Is.False, "the claim ends with Drop");
        again.Drop();
    }

    [Test]
    public void FailedDataWrite_IntoHole_LeavesHoleAndConsistentBitmap()
    {
        FaultDevice device = new(Formatted(0xAA));
        Ext2Superblock sb = Mount(device);
        OpenFile file = Create(sb, "holes");
        // Blocks 13 (allocates the indirect block) and 22 hold data; the
        // file is 23 blocks long with holes at 5 (direct) and 17 (indirect).
        foreach (long block in new long[] { 13, 22 })
        {
            file.Position = block * FsBlockSize;
            Assert.That(file.Operations.Write(file, "abcdefghij"u8), Is.EqualTo(10));
        }

        uint freeBefore = sb.FreeBlocksCount;
        byte[] full = Pattern((int)FsBlockSize);
        foreach (long hole in new long[] { 17, 5 })
        {
            device.FailNextWrite = true;
            file.Position = hole * FsBlockSize;
            Assert.Throws<IOException>(() => file.Operations.Write(file, full), "hole " + hole);
        }

        // A later successful write persists the inode (direct pointers).
        file.Position = 22 * FsBlockSize;
        Assert.That(file.Operations.Write(file, "ABCDEFGHIJ"u8), Is.EqualTo(10));
        uint freeInMemory = sb.FreeBlocksCount;
        file.Operations.Release(file);

        Ext2Superblock again = Inspect(device);
        Assert.That(again.Root.InodeOperations.Lookup(again.Root, "holes", out IVfsInode? inode), Is.True);
        OpenFile reader = new(inode!);
        long size = 22 * FsBlockSize + 10;
        byte[] back = new byte[size];
        Assert.That(reader.Operations.Read(reader, back), Is.EqualTo(size));
        byte[] expected = new byte[size];
        "abcdefghij"u8.CopyTo(expected.AsSpan(13 * (int)FsBlockSize));
        "ABCDEFGHIJ"u8.CopyTo(expected.AsSpan(22 * (int)FsBlockSize));
        Assert.That(back.AsSpan(5 * (int)FsBlockSize, (int)FsBlockSize).ToArray(), Is.EqualTo(new byte[FsBlockSize]), "direct hole reads zeros");
        Assert.That(back.AsSpan(17 * (int)FsBlockSize, (int)FsBlockSize).ToArray(), Is.EqualTo(new byte[FsBlockSize]), "indirect hole reads zeros");
        Assert.That(back, Is.EqualTo(expected));
        Assert.That(freeInMemory, Is.EqualTo(freeBefore), "both failed allocations rolled back");
        Assert.That(FreeByBitmap(again), Is.EqualTo(freeInMemory), "bitmap on disk");
        Assert.That(again.FreeBlocksCount, Is.EqualTo(freeInMemory), "superblock counter on disk");
    }

    [Test]
    public void PartialBitmapRead_OnGroupChange_DoesNotCorruptCachedGroup()
    {
        // 160 MiB with 4 KiB blocks: two block groups.
        MemoryBlockDevice ram = new("ram1", SectorSize, 160 * 2048);
        Assert.That(new Ext2FileSystemType(ram).TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = FsBlockSize }), Is.True);
        FaultDevice device = new(ram);
        Ext2Superblock sb = Mount(device);
        Assert.That(sb.GroupsCount, Is.GreaterThanOrEqualTo(2u));
        Assert.That(sb.TryAllocateBlock(0, out uint first), Is.True);
        Assert.That(first / sb.BlocksPerGroup, Is.EqualTo(0u));

        // The read of group 1's bitmap fills part of the shared buffer, then fails.
        device.FailReadAt = (ulong)sb.GetGroup(1).BlockBitmap * (FsBlockSize / SectorSize);
        Assert.Throws<IOException>(() => sb.TryAllocateBlock(1, out _));

        Assert.That(sb.TryAllocateBlock(0, out uint second), Is.True);
        Assert.That(second, Is.EqualTo(first + 1), "group 0 bitmap reread, metadata blocks stay used");

        Ext2Superblock probe = Inspect(device);
        Assert.That(FreeByBitmap(probe, 0, sb.BlocksPerGroup), Is.EqualTo((uint)probe.GetGroup(0).FreeBlocksCount), "group 0 bitmap matches its descriptor");
    }
}
