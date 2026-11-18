// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Text;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// ZehOS patch ext2-remove-entry, see PATCHES.md: a removed entry merges only into its predecessor in the same
/// directory block; the first record of a block is only freed (inode 0). Directory layouts are written through the
/// real driver (blocks allocated via GetBlockPointer) and read back raw.
/// </summary>
[TestFixture]
public class Ext2RemoveEntryTests
{
    private const ulong DeviceBlockSize = 512;
    private const ulong DeviceBlockCount = 16384; // 8 MiB
    private const int BlockSize = 2048;

    private MemoryBlockDevice _device = null!;
    private Ext2Superblock _sb = null!;
    private Ext2Inode _dir = null!;

    [SetUp]
    public void Mount()
    {
        _device = new MemoryBlockDevice("ram0", DeviceBlockSize, DeviceBlockCount);
        Ext2FileSystemType fs = new(_device);
        Assert.That(fs.TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = BlockSize }), Is.True);
        Assert.That(fs.TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vfs), Is.True);
        _sb = (Ext2Superblock)vfs!;
        _dir = (Ext2Inode)_sb.Root;
    }

    /// <summary>Directory data as Cosmos AddEntry lays it out: packed per block, the last record reaches the block end.</summary>
    private static byte[] BuildDirectory(int count, out List<(string Name, int Offset)> layout)
    {
        List<string> names = [".", ".."];
        for (int index = 0; index < count; index++)
        {
            names.Add($"n{index:D4}");
        }

        layout = [];
        List<byte[]> blocks = [];
        byte[] block = new byte[BlockSize];
        int pos = 0;
        int previous = -1;
        uint inode = 2;
        foreach (string name in names)
        {
            int length = (8 + name.Length + 3) & ~3;
            if (pos + length > BlockSize)
            {
                Set16(block, previous + 4, (ushort)(BlockSize - previous));
                blocks.Add(block);
                block = new byte[BlockSize];
                pos = 0;
            }

            Set32(block, pos, inode++);
            Set16(block, pos + 4, (ushort)length);
            block[pos + 6] = (byte)name.Length;
            block[pos + 7] = 1;
            Encoding.ASCII.GetBytes(name).CopyTo(block, pos + 8);
            layout.Add((name, blocks.Count * BlockSize + pos));
            previous = pos;
            pos += length;
        }

        Set16(block, previous + 4, (ushort)(BlockSize - previous));
        blocks.Add(block);
        byte[] data = new byte[blocks.Count * BlockSize];
        for (int index = 0; index < blocks.Count; index++)
        {
            blocks[index].CopyTo(data, index * BlockSize);
        }

        return data;
    }

    private static void Set16(byte[] data, int offset, ushort value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 2), value);

    private static void Set32(byte[] data, int offset, uint value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 4), value);

    /// <summary>Replaces the root directory's content with <paramref name="data"/>.</summary>
    private void WriteDirectory(byte[] data)
    {
        int blocks = data.Length / BlockSize;
        for (int index = 0; index < blocks; index++)
        {
            uint blk = _sb.GetBlockPointer(_dir, (uint)index, true, out _);
            Assert.That(blk, Is.Not.EqualTo(0u));
            _sb.WriteBlocks(blk, 1, data.AsSpan(index * BlockSize, BlockSize));
        }

        _dir.Size = (uint)data.Length;
        _sb.WriteInode(_dir);
    }

    private byte[] ReadDirectory()
    {
        int blocks = (int)((_dir.FullSize + BlockSize - 1) / BlockSize);
        byte[] data = new byte[blocks * BlockSize];
        for (int index = 0; index < blocks; index++)
        {
            uint blk = _sb.GetBlockPointer(_dir, (uint)index, false, out _);
            _sb.ReadBlocks(blk, 1, data.AsSpan(index * BlockSize, BlockSize));
        }

        return data;
    }

    private bool Remove(string name) => Ext2DirectoryHelper.RemoveEntry(_sb, _dir, name);

    /// <summary>Living names in directory order; fails when a record crosses its block.</summary>
    private static List<string> Live(byte[] data)
    {
        List<string> result = [];
        for (int block = 0; block < data.Length; block += BlockSize)
        {
            int pos = block;
            while (pos < block + BlockSize)
            {
                ushort recLen = BitConverter.ToUInt16(data, pos + 4);
                Assert.That(recLen >= 8 && recLen % 4 == 0, Is.True, $"rec_len {recLen} at {pos}");
                Assert.That(pos + recLen <= block + BlockSize, Is.True, $"record at {pos} crosses the block boundary ({recLen})");
                if (BitConverter.ToUInt32(data, pos) != 0)
                {
                    result.Add(Encoding.ASCII.GetString(data, pos + 8, data[pos + 6]));
                }

                pos += recLen;
            }

            Assert.That(pos, Is.EqualTo(block + BlockSize), "records do not fill the block exactly");
        }

        return result;
    }

    [Test]
    public void Layout_SpansSeveralBlocks()
    {
        byte[] data = BuildDirectory(600, out var layout);
        Assert.That(data.Length, Is.GreaterThanOrEqualTo(3 * BlockSize));
        Assert.That(layout.Exists(entry => entry.Offset == BlockSize), Is.True, "an entry starts block 1");
        WriteDirectory(data);
        Assert.That(Live(ReadDirectory()), Has.Count.EqualTo(602));
    }

    [Test]
    public void FirstEntryOfLaterBlock_OnlyFreedNotMerged()
    {
        byte[] data = BuildDirectory(600, out var layout);
        WriteDirectory(data);
        string first = layout.Find(entry => entry.Offset == BlockSize).Name;
        ushort before = BitConverter.ToUInt16(data, BlockSize + 4);

        Assert.That(Remove(first), Is.True);

        byte[] after = ReadDirectory();
        Assert.That(BitConverter.ToUInt32(after, BlockSize), Is.EqualTo(0u), "inode of the first record is 0");
        Assert.That(BitConverter.ToUInt16(after, BlockSize + 4), Is.EqualTo(before), "rec_len stays");
        List<string> live = Live(after);
        Assert.That(live, Does.Not.Contain(first));
        Assert.That(live, Has.Count.EqualTo(601));
    }

    [Test]
    public void EntryInsideBlock_MergesWithPredecessorInSameBlock()
    {
        byte[] data = BuildDirectory(600, out var layout);
        WriteDirectory(data);
        var (name, offset) = layout[10];
        var predecessor = layout[9];
        ushort merged = (ushort)(BitConverter.ToUInt16(data, predecessor.Offset + 4) + BitConverter.ToUInt16(data, offset + 4));

        Assert.That(Remove(name), Is.True);

        byte[] after = ReadDirectory();
        Assert.That(BitConverter.ToUInt16(after, predecessor.Offset + 4), Is.EqualTo(merged));
        Assert.That(Live(after), Does.Not.Contain(name));
    }

    [Test]
    public void LastEntryOfBlock_MergesUpToBlockEnd()
    {
        byte[] data = BuildDirectory(600, out var layout);
        WriteDirectory(data);
        var (name, _) = layout.FindLast(entry => entry.Offset < BlockSize);

        Assert.That(Remove(name), Is.True);

        Live(ReadDirectory());
    }

    [TestCase(300)]
    [TestCase(2000)]
    public void ManyConsecutiveDeletes_NoRecordCrossesBlockOrOverflows(int deletes)
    {
        int count = Math.Max(600, deletes + 100);
        WriteDirectory(BuildDirectory(count, out _));
        for (int index = 0; index < deletes; index++)
        {
            Assert.That(Remove($"n{index:D4}"), Is.True, $"n{index:D4}");
        }

        List<string> live = Live(ReadDirectory());
        Assert.That(live, Has.Count.EqualTo(count - deletes + 2));
        Assert.That(live, Does.Not.Contain("n0000"));
    }

    [Test]
    public void FreedFirstSlotThenNextEntry_MergesIntoFreedSlot()
    {
        byte[] data = BuildDirectory(600, out var layout);
        WriteDirectory(data);
        int firstIndex = layout.FindIndex(entry => entry.Offset == BlockSize);

        Assert.That(Remove(layout[firstIndex].Name), Is.True);
        Assert.That(Remove(layout[firstIndex + 1].Name), Is.True);

        byte[] after = ReadDirectory();
        Assert.That(BitConverter.ToUInt32(after, BlockSize), Is.EqualTo(0u));
        Live(after);
    }

    [Test]
    public void OnlyEntryOfBlock_FreedInPlace()
    {
        byte[] data = BuildDirectory(600, out var layout);
        WriteDirectory(data);
        var inBlock1 = layout.FindAll(entry => entry.Offset >= BlockSize && entry.Offset < 2 * BlockSize);
        for (int index = 1; index < inBlock1.Count; index++)
        {
            Assert.That(Remove(inBlock1[index].Name), Is.True);
        }

        Assert.That(BitConverter.ToUInt16(ReadDirectory(), BlockSize + 4), Is.EqualTo((ushort)BlockSize), "the only record fills the block");

        Assert.That(Remove(inBlock1[0].Name), Is.True);

        byte[] after = ReadDirectory();
        Assert.That(BitConverter.ToUInt32(after, BlockSize), Is.EqualTo(0u));
        Live(after);
    }

    [Test]
    public void Missing_ReturnsFalseAndChangesNothing()
    {
        WriteDirectory(BuildDirectory(600, out _));
        byte[] before = ReadDirectory();

        Assert.That(Remove("missing"), Is.False);

        Assert.That(ReadDirectory(), Is.EqualTo(before));
    }

    [Test]
    public void RecordCrossingBlock_RefusedWithoutWrite()
    {
        byte[] data = BuildDirectory(600, out var layout);
        var last = layout.FindLast(entry => entry.Offset < BlockSize);
        Set16(data, last.Offset + 4, (ushort)(BlockSize - last.Offset + 16));
        WriteDirectory(data);
        byte[] before = ReadDirectory();

        Assert.That(Remove("n0500"), Is.False);

        Assert.That(ReadDirectory(), Is.EqualTo(before));
    }

    [Test]
    public void ZeroRecLen_RefusedWithoutWrite()
    {
        byte[] data = BuildDirectory(10, out _);
        Set16(data, 12 + 4, 0);
        WriteDirectory(data);
        byte[] before = ReadDirectory();

        Assert.That(Remove("n0005"), Is.False);

        Assert.That(ReadDirectory(), Is.EqualTo(before));
    }

    [Test]
    public void NamesCompareExactBytes()
    {
        WriteDirectory(BuildDirectory(10, out _));

        Assert.That(Remove("N0001"), Is.False);
        Assert.That(Remove("n000"), Is.False);
        Assert.That(Remove("n0001"), Is.True);
    }

    [Test]
    public void DirectoryWithHole_DoesNotWriteBlockZero()
    {
        // Pointer 1 of a three-block directory is 0 (sparse directory). The old code wrote every block back,
        // including "block 0" of the device (boot area; with 2 KiB blocks it also covers the primary superblock).
        WriteDirectory(BuildDirectory(400, out _));
        Assert.That(_dir.FullSize, Is.GreaterThanOrEqualTo(3u * BlockSize));
        _dir.Block[1] = 0;
        byte[] marker = new byte[DeviceBlockSize];
        Array.Fill(marker, (byte)0xAA);
        _device.WriteBlock(0, 1, marker);
        byte[] superblockSector = new byte[DeviceBlockSize];
        _device.ReadBlock(2, 1, superblockSector);

        Assert.That(Remove("n0001"), Is.True);

        byte[] after = new byte[DeviceBlockSize];
        _device.ReadBlock(0, 1, after);
        Assert.That(after, Is.EqualTo(marker), "device sector 0 untouched");
        _device.ReadBlock(2, 1, after);
        Assert.That(after, Is.EqualTo(superblockSector), "device sector 2 (superblock) untouched");
    }

    [Test]
    public void CorruptHugeDirectorySize_IsRefusedWithoutAllocation()
    {
        WriteDirectory(BuildDirectory(10, out _));
        _dir.Size = uint.MaxValue;
        _dir.SizeHigh = 1;

        Assert.That(Remove("n0001"), Is.False);
    }
}
