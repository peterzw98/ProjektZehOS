// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-rmdir, see PATCHES.md

using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// <see cref="Ext2InodeOperations.Rmdir"/> releases every block of the
/// directory, persists the deleted inode and refuses "." and "..".
/// </summary>
[TestFixture]
public class Ext2RmdirTests
{
    private const ulong DeviceBlockSize = 512;
    private const ulong DeviceBlockCount = 32768; // 16 MiB
    private const uint FsBlockSize = 4096;
    private const VfsMode DirMode = VfsMode.Directory | VfsMode.OwnerRead | VfsMode.OwnerWrite | VfsMode.OwnerExecute;
    private const VfsMode FileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;

    private MemoryBlockDevice _device = null!;
    private Ext2Superblock _sb = null!;

    private IInodeOperations Ops => _sb.Root.InodeOperations;

    [SetUp]
    public void SetUp()
    {
        _device = new("ram0", DeviceBlockSize, DeviceBlockCount);
        Ext2FileSystemType fs = new(_device);
        Assert.That(fs.TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = FsBlockSize }), Is.True);
        _sb = Remount();
    }

    // A fresh mount has an empty inode cache, so ReadInode returns what is on disk.
    private Ext2Superblock Remount()
    {
        Ext2FileSystemType fs = new(_device);
        Assert.That(fs.TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        return (Ext2Superblock)vsb!;
    }

    private Ext2Inode Mkdir(string name)
    {
        Assert.That(Ops.Mkdir(_sb.Root, name, DirMode, out IVfsInode? dir), Is.True);
        return (Ext2Inode)dir!;
    }

    [Test]
    public void Rmdir_DirectoryWithManyBlocks_FreesAllBlocksAndPointerBlock()
    {
        uint freeBlocks = _sb.FreeBlocksCount;
        uint freeInodes = _sb.FreeInodesCount;
        Ext2Inode dir = Mkdir("d");

        // ~200-byte records, about 19 per 4 KiB block: 300 entries need more
        // than 12 blocks, so the directory gets a single-indirect block.
        string prefix = new('n', 190);
        const int Files = 300;
        for (int i = 0; i < Files; i++)
        {
            Assert.That(dir.InodeOperations.Create(dir, prefix + i, FileMode, out _), Is.True, $"create {i}");
        }

        Assert.That(dir.Block[Ext2InodeLayout.SingleIndirectIndex], Is.Not.Zero, "precondition: indirect block in use");

        for (int i = 0; i < Files; i++)
        {
            Assert.That(dir.InodeOperations.Unlink(dir, prefix + i), Is.True, $"unlink {i}");
        }

        Assert.That(Ops.Rmdir(_sb.Root, "d"), Is.True);

        Assert.That(_sb.FreeBlocksCount, Is.EqualTo(freeBlocks));
        Assert.That(_sb.FreeInodesCount, Is.EqualTo(freeInodes));
    }

    [Test]
    public void Rmdir_WritesDtimeAndLinksZero_AndRestoresParentCounters()
    {
        ushort parentLinks = ((Ext2Inode)_sb.Root).LinksCount;
        Ext2Inode dir = Mkdir("d");
        uint group = _sb.GroupOfInode(dir.InodeNumber);
        ushort usedDirs = Remount().GetGroup(group).UsedDirsCount;

        Assert.That(Ops.Rmdir(_sb.Root, "d"), Is.True);

        Ext2Superblock fresh = Remount();
        Ext2Inode onDisk = fresh.ReadInode(dir.InodeNumber, "d");
        Assert.That(onDisk.LinksCount, Is.Zero);
        Assert.That(onDisk.Dtime, Is.Not.Zero);
        Assert.That(onDisk.Blocks, Is.Zero);
        Assert.That(((Ext2Inode)fresh.Root).LinksCount, Is.EqualTo(parentLinks), "parent loses the \"..\" link");
        Assert.That(fresh.GetGroup(group).UsedDirsCount, Is.EqualTo(usedDirs - 1));
    }

    // Two directory blocks: ~200-byte records, about 19 per 4 KiB block.
    private Ext2Inode TwoBlockDirectory()
    {
        Ext2Inode dir = Mkdir("d");
        string prefix = new('n', 190);
        for (int i = 0; i < 30; i++)
        {
            Assert.That(dir.InodeOperations.Create(dir, prefix + i, FileMode, out _), Is.True);
        }

        Assert.That(dir.Block[1], Is.Not.Zero, "precondition: second directory block");
        return dir;
    }

    [Test]
    public void Rmdir_DirectoryWithHoleInFirstBlock_IsRefused()
    {
        Ext2Inode dir = TwoBlockDirectory();
        dir.Block[0] = 0; // corrupt: hole inside i_size, entries behind it stay live

        Assert.That(Ops.Rmdir(_sb.Root, "d"), Is.False);
        Assert.That(Ext2DirectoryHelper.TryFind(_sb, (Ext2Inode)_sb.Root, "d", out _), Is.True);
    }

    [Test]
    public void ReadDirAndLookup_WithCorruptRecord_Fail()
    {
        Ext2Inode dir = TwoBlockDirectory();
        _sb.WriteBlocks(dir.Block[1], 1, new byte[FsBlockSize]); // rec_len 0 at the start of block 1

        Assert.That(dir.InodeOperations.ReadDir(dir, out IReadOnlyList<IVfsInode> entries), Is.False, "no partial listing");
        Assert.That(entries, Is.Empty);
        Assert.That(Ext2DirectoryHelper.TryFind(_sb, dir, new string('n', 190) + "0", out _), Is.False);
        Assert.That(Ops.Rmdir(_sb.Root, "d"), Is.False);
    }

    [TestCase(".")]
    [TestCase("..")]
    public void Rmdir_DotEntries_AreRefused(string name)
    {
        Ext2Inode dir = Mkdir("d");
        uint freeBlocks = _sb.FreeBlocksCount;

        Assert.That(dir.InodeOperations.Rmdir(dir, name), Is.False);

        Assert.That(Ext2DirectoryHelper.TryFind(_sb, dir, ".", out _), Is.True, "\".\" still present");
        Assert.That(Ext2DirectoryHelper.TryFind(_sb, dir, "..", out _), Is.True, "\"..\" still present");
        Assert.That(_sb.FreeBlocksCount, Is.EqualTo(freeBlocks));
    }

    [Test]
    public void Rmdir_NonEmpty_IsRefused()
    {
        Ext2Inode dir = Mkdir("d");
        Assert.That(dir.InodeOperations.Create(dir, "f", FileMode, out _), Is.True);

        Assert.That(Ops.Rmdir(_sb.Root, "d"), Is.False);
        Assert.That(Ext2DirectoryHelper.TryFind(_sb, (Ext2Inode)_sb.Root, "d", out _), Is.True);
    }
}
