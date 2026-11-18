// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// ZehOS patch ext2-formatter, see PATCHES.md: sparse_super with superblock backups, group starts that include
/// first_data_block, padding bits in the bitmaps, a last group that cannot hold its metadata is cut off.
/// </summary>
[TestFixture]
public class Ext2FormatterTests
{
    private const ulong SectorSize = 512;

    private static MemoryBlockDevice Device(ulong bytes) => new("ram0", SectorSize, bytes / SectorSize);

    private static byte[] ReadBytes(IBlockDevice device, ulong byteOffset, int length)
    {
        ulong first = byteOffset / SectorSize;
        ulong last = (byteOffset + (ulong)length + SectorSize - 1) / SectorSize;
        byte[] buffer = new byte[(last - first) * SectorSize];
        device.ReadBlock(first, last - first, buffer);
        return buffer.AsSpan((int)(byteOffset % SectorSize), length).ToArray();
    }

    private static uint Field32(IBlockDevice device, ulong superblockByte, int offset)
        => BitConverter.ToUInt32(ReadBytes(device, superblockByte + (ulong)offset, 4));

    private static ushort Field16(IBlockDevice device, ulong superblockByte, int offset)
        => BitConverter.ToUInt16(ReadBytes(device, superblockByte + (ulong)offset, 2));

    [Test]
    public void Format_4K_SetsSparseSuperAndWritesBackups()
    {
        // 10 groups of 2048 blocks (8 MiB): sparse_super keeps copies in groups 1, 3, 5, 7 and 9.
        MemoryBlockDevice device = Device(10UL * 8 * 1024 * 1024 + 4 * 1024 * 1024);
        Assert.That(Ext2Formatter.Format(device, new Ext2FormatOptions { BlockSize = 4096 }), Is.True);

        const ulong BlockSize = 4096;
        Assert.That(Field32(device, Ext2SuperblockLayout.SuperblockOffset, Ext2SuperblockLayout.FeatureRoCompatOffset) & 1u, Is.EqualTo(1u), "sparse_super");
        uint blocksPerGroup = Field32(device, Ext2SuperblockLayout.SuperblockOffset, Ext2SuperblockLayout.BlocksPerGroupOffset);
        foreach (uint group in new uint[] { 1, 3, 5, 7, 9 })
        {
            ulong start = group * blocksPerGroup * BlockSize;
            Assert.That(Field16(device, start, Ext2SuperblockLayout.MagicOffset), Is.EqualTo(Ext2SuperblockLayout.Magic), $"magic in group {group}");
            Assert.That(Field16(device, start, Ext2SuperblockLayout.BlockGroupNrOffset), Is.EqualTo((ushort)group), $"s_block_group_nr in group {group}");
        }

        foreach (uint group in new uint[] { 2, 4, 6, 8 })
        {
            ulong start = group * blocksPerGroup * BlockSize;
            Assert.That(Field16(device, start, Ext2SuperblockLayout.MagicOffset), Is.Not.EqualTo(Ext2SuperblockLayout.Magic), $"no copy in group {group}");
        }
    }

    [Test]
    public void Format_BitmapsHavePaddingBitsSet()
    {
        MemoryBlockDevice device = Device(4 * 1024 * 1024); // 4 MiB, 4 KiB blocks: one group of 1024 blocks
        Assert.That(Ext2Formatter.Format(device, new Ext2FormatOptions { BlockSize = 4096 }), Is.True);

        uint blocksInGroup = Field32(device, Ext2SuperblockLayout.SuperblockOffset, Ext2SuperblockLayout.BlocksCountOffset);
        uint inodesPerGroup = Field32(device, Ext2SuperblockLayout.SuperblockOffset, Ext2SuperblockLayout.InodesPerGroupOffset);
        // Descriptor table of group 0 starts in block 1 for 4 KiB blocks.
        byte[] descriptor = ReadBytes(device, 4096, Ext2SuperblockLayout.GroupDescSize);
        uint blockBitmapBlock = BitConverter.ToUInt32(descriptor, Ext2SuperblockLayout.GroupDescBlockBitmapOffset);
        uint inodeBitmapBlock = BitConverter.ToUInt32(descriptor, Ext2SuperblockLayout.GroupDescInodeBitmapOffset);
        byte[] blockBitmap = ReadBytes(device, blockBitmapBlock * 4096UL, 4096);
        byte[] inodeBitmap = ReadBytes(device, inodeBitmapBlock * 4096UL, 4096);

        Assert.That(AllSet(blockBitmap, blocksInGroup, 4096 * 8), Is.True, "block bitmap padding");
        Assert.That(AllSet(inodeBitmap, inodesPerGroup, 4096 * 8), Is.True, "inode bitmap padding");
    }

    private static bool AllSet(byte[] bitmap, uint from, uint to)
    {
        for (uint bit = from; bit < to; bit++)
        {
            if ((bitmap[bit / 8] & (1 << (int)(bit % 8))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    [Test]
    public void Format_LastGroupTooSmall_IsCutOff()
    {
        // Two full groups plus a 5-block tail that cannot hold bitmaps and an inode table.
        MemoryBlockDevice device = Device((2UL * 2048 + 5) * 4096);
        Assert.That(Ext2Formatter.Format(device, new Ext2FormatOptions { BlockSize = 4096 }), Is.True);

        Assert.That(Field32(device, Ext2SuperblockLayout.SuperblockOffset, Ext2SuperblockLayout.BlocksCountOffset), Is.EqualTo(2u * 2048));
    }

    [Test]
    public void Format_SingleGroup_RoundsBlocksPerGroupToMultipleOfEight()
    {
        MemoryBlockDevice device = Device(100UL * 4096); // 100 blocks of 4 KiB
        Assert.That(Ext2Formatter.Format(device, new Ext2FormatOptions { BlockSize = 4096 }), Is.True);

        uint blocksPerGroup = Field32(device, Ext2SuperblockLayout.SuperblockOffset, Ext2SuperblockLayout.BlocksPerGroupOffset);
        Assert.That(blocksPerGroup % 8, Is.EqualTo(0u));
        Assert.That(blocksPerGroup, Is.EqualTo(96u));
    }

    [Test]
    public void Format_ZeroSectorSize_IsRefused()
    {
        MemoryBlockDevice device = new("ram0", 0, 0);

        Assert.That(Ext2Formatter.Format(device, null), Is.False);
    }

    [Test]
    public void Format_NullOptions_Uses4096ByteBlocks()
    {
        MemoryBlockDevice device = Device(8 * 1024 * 1024);
        Assert.That(Ext2Formatter.Format(device, null), Is.True);

        Assert.That(Field32(device, Ext2SuperblockLayout.SuperblockOffset, Ext2SuperblockLayout.LogBlockSizeOffset), Is.EqualTo(2u));
        Assert.That(Field32(device, Ext2SuperblockLayout.SuperblockOffset, Ext2SuperblockLayout.FirstDataBlockOffset), Is.EqualTo(0u));
    }
}
