// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch fat-read-runs, see PATCHES.md

using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.Tests.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Fat;

/// <summary>
/// Whole clusters of a file are read with one device call per run of
/// contiguous clusters; partial head and tail clusters and fragmented
/// chains still return the right bytes.
/// </summary>
[TestFixture]
public class FatReadRunsTests
{
    private const VfsMode FileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;

    private sealed class CountingDevice : IBlockDevice
    {
        private readonly MemoryBlockDevice _inner;

        public CountingDevice(MemoryBlockDevice inner)
        {
            _inner = inner;
        }

        public int Reads { get; set; }

        public string Name => _inner.Name;

        public ulong BlockSize => _inner.BlockSize;

        public ulong BlockCount => _inner.BlockCount;

        public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data)
        {
            Reads++;
            _inner.ReadBlock(blockNo, blockCount, data);
        }

        public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data) => _inner.WriteBlock(blockNo, blockCount, data);

        public void Flush() => _inner.Flush();
    }

    private MemoryBlockDevice _memory = null!;
    private CountingDevice _device = null!;
    private FatSuperblock _sb = null!;
    private FatInode _root = null!;

    [SetUp]
    public void SetUp()
    {
        _memory = new("ram0", 512, 131072); // 64 MiB
        Assert.That(new FatFileSystemType(_memory).TryFormat(ReadOnlySpan<char>.Empty, new FatFormatOptions { Type = FatType.Fat32 }), Is.True);
        _device = new CountingDevice(_memory);
        Assert.That(new FatFileSystemType(_device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        _sb = (FatSuperblock)vsb!;
        _root = (FatInode)_sb.Root;
    }

    private static byte[] Pattern(int bytes, int seed)
    {
        byte[] data = new byte[bytes];
        for (int i = 0; i < bytes; i++)
        {
            data[i] = (byte)((i * 31) + (i >> 9) + seed);
        }

        return data;
    }

    private FatInode Create(string name, byte[] content)
    {
        Assert.That(_root.InodeOperations.Create(_root, name, FileMode, out IVfsInode? inode), Is.True, name);
        FatInode file = (FatInode)inode!;
        FatOpenFile open = new(file, file.FileOperations!);
        Assert.That(file.FileOperations!.Write(open, content), Is.EqualTo(content.Length));
        return file;
    }

    private byte[] Read(FatInode file, long position, int bytes, out int deviceReads)
    {
        file.ResolveChain();
        FatOpenFile open = new(file, file.FileOperations!) { Position = position };
        byte[] buffer = new byte[bytes];
        _device.Reads = 0;
        long n = file.FileOperations!.Read(open, buffer);
        deviceReads = _device.Reads;
        return buffer.AsSpan(0, (int)n).ToArray();
    }

    private bool IsContiguous(FatInode file)
    {
        List<uint> chain = file.ResolveChain();
        for (int i = 1; i < chain.Count; i++)
        {
            if (chain[i] != chain[0] + (uint)i)
            {
                return false;
            }
        }

        return true;
    }

    [Test]
    public void R1_ContiguousFile_OneDeviceRead()
    {
        byte[] content = Pattern(1 << 20, 1);
        FatInode file = Create("big.bin", content);
        Assume.That(IsContiguous(file), Is.True, "fresh volume should allocate contiguously");

        byte[] read = Read(file, 0, content.Length, out int reads);

        Assert.That(read, Is.EqualTo(content));
        Assert.That(reads, Is.EqualTo(1));
    }

    [Test]
    public void R2_UnalignedHeadAndTail_ThreeDeviceReads()
    {
        byte[] content = Pattern(256 * 1024, 2);
        FatInode file = Create("mid.bin", content);
        int cluster = (int)_sb.Boot.BytesPerCluster;
        int start = cluster / 2 + 7;
        int length = 10 * cluster;

        byte[] read = Read(file, start, length, out int reads);

        Assert.That(read, Is.EqualTo(content.AsSpan(start, length).ToArray()));
        // Head partial, run of whole clusters, tail partial.
        Assert.That(reads, Is.EqualTo(3));
    }

    [Test]
    public void R3_FragmentedChain_OneReadPerRun_SameBytes()
    {
        int cluster = (int)_sb.Boot.BytesPerCluster;
        // Interleave two files two clusters at a time: each chain has a gap
        // after every second cluster (4 runs of 2), so one read per cluster
        // (8) and one per run (4) differ.
        Assert.That(_root.InodeOperations.Create(_root, "a.bin", FileMode, out IVfsInode? ia), Is.True);
        Assert.That(_root.InodeOperations.Create(_root, "b.bin", FileMode, out IVfsInode? ib), Is.True);
        FatInode a = (FatInode)ia!;
        FatInode b = (FatInode)ib!;
        FatOpenFile oa = new(a, a.FileOperations!);
        FatOpenFile ob = new(b, b.FileOperations!);
        byte[] content = Pattern(8 * cluster, 3);
        for (int i = 0; i < 8; i += 2)
        {
            oa.Position = i * cluster;
            Assert.That(a.FileOperations!.Write(oa, content.AsSpan(i * cluster, 2 * cluster)), Is.EqualTo(2 * cluster));
            ob.Position = i * cluster;
            Assert.That(b.FileOperations!.Write(ob, new byte[2 * cluster]), Is.EqualTo(2 * cluster));
        }

        a.InvalidateChain();
        List<uint> chain = a.ResolveChain();
        int runs = 1;
        for (int i = 1; i < chain.Count; i++)
        {
            if (chain[i] != chain[i - 1] + 1)
            {
                runs++;
            }
        }

        Assume.That(runs, Is.InRange(2, 7), "chain should be fragmented into runs longer than one cluster");

        byte[] read = Read(a, 0, content.Length, out int reads);

        Assert.That(read, Is.EqualTo(content));
        Assert.That(reads, Is.EqualTo(runs));
    }

    [Test]
    public void R4_ReadPastEnd_StopsAtSize()
    {
        int cluster = (int)_sb.Boot.BytesPerCluster;
        byte[] content = Pattern(3 * cluster + 100, 4);
        FatInode file = Create("tail.bin", content);

        byte[] read = Read(file, cluster, 8 * cluster, out int reads);

        Assert.That(read, Is.EqualTo(content.AsSpan(cluster).ToArray()));
        Assert.That(reads, Is.EqualTo(2));
    }

    [Test]
    public void R5_DirectoryWithManyEntries_ReadsBack()
    {
        // Enough long names to spread the root over several clusters.
        for (int i = 0; i < 300; i++)
        {
            Create($"a-long-file-name-number-{i:D4}.txt", new byte[1]);
        }

        Assert.That(_root.ResolveChain().Count, Is.GreaterThan(1));
        for (int i = 0; i < 300; i += 37)
        {
            Assert.That(_sb.FindChildEntry(_root, $"a-long-file-name-number-{i:D4}.txt", out _), Is.True, i.ToString());
        }
    }
}
