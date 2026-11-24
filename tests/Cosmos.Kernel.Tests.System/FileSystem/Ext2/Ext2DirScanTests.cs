// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-dir-scan, see PATCHES.md

using System.Text;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// The block-wise search gives the same answers as <see cref="Ext2DirectoryHelper.TryParseDirectory"/>, and AddEntry
/// leaves the same bytes as the former whole-directory version while writing one block.
/// </summary>
[TestFixture]
public class Ext2DirScanTests
{
    private const int BlockSize = 2048;

    private CountingDevice _device = null!;
    private Ext2Superblock _sb = null!;
    private Ext2Inode _dir = null!;

    /// <summary>Counts calls and remembers the written ranges.</summary>
    private sealed class CountingDevice : IBlockDevice
    {
        private readonly MemoryBlockDevice _inner = new("ram0", 512, 16384); // 8 MiB

        public int Reads { get; private set; }

        public List<(ulong Block, ulong Count)> Writes { get; } = [];

        public string Name => _inner.Name;

        public ulong BlockSize => _inner.BlockSize;

        public ulong BlockCount => _inner.BlockCount;

        public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data)
        {
            Reads++;
            _inner.ReadBlock(blockNo, blockCount, data);
        }

        public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
        {
            Writes.Add((blockNo, blockCount));
            _inner.WriteBlock(blockNo, blockCount, data);
        }

        public void Flush()
        {
        }

        public void Reset()
        {
            Reads = 0;
            Writes.Clear();
        }
    }

    [SetUp]
    public void Mount()
    {
        _device = new CountingDevice();
        Ext2FileSystemType fs = new(_device);
        Assert.That(fs.TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = BlockSize }), Is.True);
        Assert.That(fs.TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vfs), Is.True);
        _sb = (Ext2Superblock)vfs!;
        _dir = (Ext2Inode)_sb.Root;
    }

    private static string Name(int index) => $"n{index:D4}";

    private void Fill(int count)
    {
        for (int index = 0; index < count; index++)
        {
            Assert.That(Add(Name(index)), Is.True, Name(index));
        }
    }

    private bool Add(string name) => Ext2DirectoryHelper.AddEntry(_sb, _dir, name, Ext2SuperblockLayout.RootInodeNumber, 1);

    private byte[] ReadDirectory()
    {
        int blocks = (int)((_dir.FullSize + BlockSize - 1) / BlockSize);
        byte[] data = new byte[blocks * BlockSize];
        for (int index = 0; index < blocks; index++)
        {
            _sb.ReadBlocks(_sb.GetBlockPointer(_dir, (uint)index, false, out _), 1, data.AsSpan(index * BlockSize, BlockSize));
        }

        return data;
    }

    private void WriteDirectory(byte[] data)
    {
        for (int index = 0; index < data.Length / BlockSize; index++)
        {
            _sb.WriteBlocks(_sb.GetBlockPointer(_dir, (uint)index, false, out _), 1, data.AsSpan(index * BlockSize, BlockSize));
        }
    }

    /// <summary>The answer the former TryFind gave: parse everything, then compare strings.</summary>
    private bool FindByParsing(string name, out Ext2DirEntry? entry)
    {
        entry = null;
        if (!Ext2DirectoryHelper.TryParseDirectory(_sb, _dir, out List<Ext2DirEntry> entries))
        {
            return false;
        }

        entry = entries.Find(candidate => candidate.Name == name);
        return entry is not null;
    }

    private void AssertSameAsParsing(string name, string context)
    {
        bool expected = FindByParsing(name, out Ext2DirEntry? reference);
        bool actual = Ext2DirectoryHelper.TryFind(_sb, _dir, name, out Ext2DirEntry entry);
        Assert.That(actual, Is.EqualTo(expected), $"{context}: {name}");
        if (!expected)
        {
            return;
        }

        Assert.That(
            (entry.Inode, entry.RecLen, entry.NameLen, entry.FileType, entry.Name, entry.Offset),
            Is.EqualTo((reference!.Inode, reference.RecLen, reference.NameLen, reference.FileType, reference.Name, reference.Offset)),
            $"{context}: {name}");
    }

    /// <summary>The bytes the former AddEntry left when a record had room; null when it appended a block.</summary>
    private static byte[]? ReferenceInsert(byte[] source, byte[] name, uint inodeNumber, byte fileType)
    {
        byte[] data = (byte[])source.Clone();
        int needed = (8 + name.Length + 3) & ~3;
        for (int pos = 0; pos < data.Length;)
        {
            uint inode = BitConverter.ToUInt32(data, pos);
            int recLen = BitConverter.ToUInt16(data, pos + 4);
            int used = inode == 0 ? 0 : (8 + data[pos + 6] + 3) & ~3;
            if (recLen - used >= needed)
            {
                if (inode != 0)
                {
                    BitConverter.TryWriteBytes(data.AsSpan(pos + 4, 2), (ushort)used);
                }

                int at = pos + used;
                BitConverter.TryWriteBytes(data.AsSpan(at, 4), inodeNumber);
                BitConverter.TryWriteBytes(data.AsSpan(at + 4, 2), (ushort)(recLen - used));
                data[at + 6] = (byte)name.Length;
                data[at + 7] = fileType;
                data.AsSpan(at + 8, recLen - used - 8).Clear();
                name.CopyTo(data, at + 8);
                return data;
            }

            pos += recLen;
        }

        return null;
    }

    [Test]
    public void LookupOfEarlyEntry_ReadsOneBlock_ChangingCallersCheckAll()
    {
        Fill(600);
        int blocks = (int)(_dir.FullSize / BlockSize);
        Assert.That(blocks, Is.InRange(3, 12), "precondition: direct blocks only");

        _device.Reset();
        Assert.That(Ext2DirectoryHelper.TryFindFirst(_sb, _dir, Name(0), out _), Is.True);
        Assert.That(_device.Reads, Is.EqualTo(1));

        _device.Reset();
        Assert.That(Ext2DirectoryHelper.TryFind(_sb, _dir, Name(0), out _), Is.True);
        Assert.That(_device.Reads, Is.EqualTo(blocks));
    }

    [Test]
    public void DamageBehindHit_OnlyReadingLookupSucceeds()
    {
        Fill(600);
        byte[] data = ReadDirectory();
        data.AsSpan(BlockSize, BlockSize).Clear(); // rec_len 0 at the start of block 1
        WriteDirectory(data);

        Assert.That(Ext2DirectoryHelper.TryFindFirst(_sb, _dir, Name(0), out _), Is.True);
        Assert.That(Ext2DirectoryHelper.TryFindFirst(_sb, _dir, Name(599), out _), Is.False, "behind the damage");
        Assert.That(Ext2DirectoryHelper.TryFind(_sb, _dir, Name(0), out _), Is.False);
        Assert.That(_dir.InodeOperations.Unlink(_dir, Name(0)), Is.False);
        Assert.That(_dir.InodeOperations.Rename(_dir, Name(0), _dir, "other"), Is.False);
        Assert.That(ReadDirectory(), Is.EqualTo(data), "nothing written");
    }

    [Test]
    public void AddEntry_IntoFreedRoom_WritesOnlyThatBlock()
    {
        Fill(600);
        Assert.That(Ext2DirectoryHelper.TryFind(_sb, _dir, Name(250), out Ext2DirEntry victim), Is.True);
        int block = victim.Offset / BlockSize;
        Assert.That(block, Is.InRange(1, (int)(_dir.FullSize / BlockSize) - 2), "precondition: inner block");
        Assert.That(Ext2DirectoryHelper.RemoveEntry(_sb, _dir, Name(250)), Is.True);
        uint target = _sb.GetBlockPointer(_dir, (uint)block, false, out _);

        _device.Reset();
        Assert.That(Add("x0250"), Is.True);

        ulong perBlock = BlockSize / _device.BlockSize;
        Assert.That(_device.Writes, Has.Count.EqualTo(1));
        Assert.That(_device.Writes[0], Is.EqualTo((target * perBlock, perBlock)));
    }

    [Test]
    public void AddEntry_DamageBehindFreeRoom_WritesNothing()
    {
        Fill(600);
        Assert.That(Ext2DirectoryHelper.RemoveEntry(_sb, _dir, Name(10)), Is.True);
        byte[] data = ReadDirectory();
        data.AsSpan(data.Length - BlockSize, BlockSize).Clear();
        WriteDirectory(data);

        _device.Reset();
        Assert.That(Add("x0010"), Is.False);
        Assert.That(_device.Writes, Is.Empty);
    }

    [Test]
    public void AddEntry_ToRegularFile_IsRefused()
    {
        Assert.That(_dir.InodeOperations.Create(_dir, "file", VfsMode.OwnerRead | VfsMode.OwnerWrite, out IVfsInode? file), Is.True);

        _device.Reset();
        Assert.That(Ext2DirectoryHelper.AddEntry(_sb, (Ext2Inode)file!, "child", 20, 1), Is.False);
        Assert.That(_device.Writes, Is.Empty);
    }

    [Test]
    public void AddEntry_RandomAddsAndRemoves_LeaveSameBytesAsFormerVersion()
    {
        Random random = new(542);
        List<string> live = [];
        int inserted = 0;
        int appended = 0;
        for (int step = 0; step < 1500; step++)
        {
            if (live.Count > 0 && random.Next(3) == 0)
            {
                int index = random.Next(live.Count);
                Assert.That(Ext2DirectoryHelper.RemoveEntry(_sb, _dir, live[index]), Is.True);
                live.RemoveAt(index);
                continue;
            }

            string name = $"{step:D4}" + new string('y', random.Next(0, 60));
            byte[] before = ReadDirectory();
            byte[]? expected = ReferenceInsert(before, Encoding.UTF8.GetBytes(name), Ext2SuperblockLayout.RootInodeNumber, 1);
            Assert.That(Add(name), Is.True, name);
            byte[] after = ReadDirectory();
            if (expected is null)
            {
                appended++;
                Assert.That(after, Has.Length.EqualTo(before.Length + BlockSize));
                Assert.That(after.AsSpan(0, before.Length).SequenceEqual(before), Is.True, "old blocks unchanged");
                Assert.That(BitConverter.ToUInt16(after, before.Length + 4), Is.EqualTo((ushort)BlockSize));
            }
            else
            {
                inserted++;
                Assert.That(after.AsSpan().SequenceEqual(expected), Is.True, $"step {step}");
            }

            live.Add(name);
        }

        Assert.That(inserted, Is.GreaterThan(100));
        Assert.That(appended, Is.GreaterThan(5));
        foreach (string name in live)
        {
            AssertSameAsParsing(name, "final");
        }
    }

    [Test]
    public void TryFind_OnDamagedDirectories_AgreesWithParsing()
    {
        Fill(300);
        byte[] sound = ReadDirectory();
        uint size = _dir.Size;
        List<int> records = [];
        for (int pos = 0; pos < sound.Length; pos += BitConverter.ToUInt16(sound, pos + 4))
        {
            records.Add(pos);
        }

        Random random = new(4000);
        string[] probes = [".", "..", Name(0), Name(150), Name(299), "missing", string.Empty, "n�001"];
        int sane = 0;
        int refused = 0;
        for (int round = 0; round < 500; round++)
        {
            byte[] data = (byte[])sound.Clone();
            int record = records[random.Next(records.Count)];
            switch (random.Next(5))
            {
                case 0:
                    BitConverter.TryWriteBytes(data.AsSpan(record + 4, 2), (ushort)random.Next(0, 64));
                    break;
                case 1:
                    data[record + 6] = (byte)random.Next(256);
                    break;
                case 2:
                    data.AsSpan(record, 4).Clear();
                    break;
                case 3:
                    data[record + 9] = (byte)random.Next(256);
                    break;
                default:
                    // Last record of a block ends short of the block: the rest cannot hold a record.
                    int last = records.FindLast(pos => pos < BlockSize * random.Next(1, sound.Length / BlockSize + 1));
                    int length = BitConverter.ToUInt16(data, last + 4);
                    BitConverter.TryWriteBytes(data.AsSpan(last + 4, 2), (ushort)Math.Max(0, length - 4));
                    break;
            }

            WriteDirectory(data);
            _dir.Size = random.Next(4) == 0 ? size - (uint)random.Next(1, BlockSize) : size;
            if (Ext2DirectoryHelper.TryParseDirectory(_sb, _dir, out _))
            {
                sane++;
            }
            else
            {
                refused++;
            }

            foreach (string probe in probes)
            {
                AssertSameAsParsing(probe, $"round {round}");
            }
        }

        Assert.That(sane, Is.GreaterThan(20));
        Assert.That(refused, Is.GreaterThan(20));
    }

    [Test]
    public void NameWithInvalidUtf8_IsFoundUnderItsListedName()
    {
        Fill(10);
        Assert.That(Ext2DirectoryHelper.TryFind(_sb, _dir, Name(5), out Ext2DirEntry entry), Is.True);
        byte[] data = ReadDirectory();
        data[entry.Offset + 9] = 0xFF;
        WriteDirectory(data);

        Assert.That(Ext2DirectoryHelper.TryFind(_sb, _dir, "n�005", out _), Is.True);
        Assert.That(Ext2DirectoryHelper.TryFindFirst(_sb, _dir, "n�005", out _), Is.True);
        Assert.That(Ext2DirectoryHelper.TryFind(_sb, _dir, Name(5), out _), Is.False);
    }

    [Test]
    public void RecordCrossingBlockEnd_IsRefusedByAllPaths()
    {
        Fill(600);
        Assert.That(Ext2DirectoryHelper.RemoveEntry(_sb, _dir, Name(10)), Is.True, "room before the damage");
        byte[] data = ReadDirectory();
        int last = BlockSize;
        for (int pos = BlockSize; pos < 2 * BlockSize; pos += BitConverter.ToUInt16(data, pos + 4))
        {
            last = pos;
        }

        // One step (4 bytes) past the end: the smallest overrun that keeps rec_len aligned.
        BitConverter.TryWriteBytes(data.AsSpan(last + 4, 2), (ushort)((2 * BlockSize) - last + 4));
        WriteDirectory(data);

        Assert.That(Ext2DirectoryHelper.TryParseDirectory(_sb, _dir, out _), Is.False, "precondition");
        Assert.That(Ext2DirectoryHelper.TryFind(_sb, _dir, Name(0), out _), Is.False);
        Assert.That(Ext2DirectoryHelper.TryFindFirst(_sb, _dir, Name(599), out _), Is.False);
        _device.Reset();
        Assert.That(Add("x0010"), Is.False);
        Assert.That(_device.Writes, Is.Empty);
    }

    [Test]
    public void DirectorySizeWrappingBlockCount_IsRefused()
    {
        Fill(10);
        _dir.Size = 0;
        _dir.SizeHigh = BlockSize; // 2^32 blocks: the block count alone would wrap to 0

        Assert.That(Ext2DirectoryHelper.TryFind(_sb, _dir, Name(1), out _), Is.False);
        _device.Reset();
        Assert.That(Add("new"), Is.False);
        Assert.That(_device.Writes, Is.Empty);
    }

    [Test]
    public void DirectoryOverSizeLimit_IsRefused()
    {
        Fill(10);
        _dir.Size = uint.MaxValue;
        _dir.SizeHigh = 1;

        Assert.That(Ext2DirectoryHelper.TryFind(_sb, _dir, Name(1), out _), Is.False);
        Assert.That(Ext2DirectoryHelper.TryFindFirst(_sb, _dir, Name(1), out _), Is.False);
        Assert.That(Add("new"), Is.False);
    }
}
