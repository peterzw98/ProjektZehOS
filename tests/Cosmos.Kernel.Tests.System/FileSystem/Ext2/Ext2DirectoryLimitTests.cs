// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// ZehOS patch ext2-directory-limit, see PATCHES.md: free records no longer count against a parse limit, and a
/// directory beyond the size limits is refused instead of being listed as empty. Listing goes through the public
/// ReadDir so the tests read the same on the unpatched driver.
/// </summary>
[TestFixture]
public class Ext2DirectoryLimitTests
{
    private const ulong DeviceBlockSize = 512;
    private const ulong DeviceBlockCount = 16384; // 8 MiB
    private const int BlockSize = 1024;
    private const uint MaxDirectoryBlocks = 4096;

    private Ext2Superblock _sb = null!;
    private Ext2Inode _root = null!;

    [SetUp]
    public void Mount()
    {
        MemoryBlockDevice device = new("ram0", DeviceBlockSize, DeviceBlockCount);
        Ext2FileSystemType fs = new(device);
        Assert.That(fs.TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = BlockSize }), Is.True);
        Assert.That(fs.TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vfs), Is.True);
        _sb = (Ext2Superblock)vfs!;
        _root = (Ext2Inode)_sb.Root;
    }

    private static string Name(int index) => $"f{index:D5}";

    private void AddMany(int count)
    {
        for (int index = 0; index < count; index++)
        {
            Assert.That(Ext2DirectoryHelper.AddEntry(_sb, _root, Name(index), (uint)(20 + index), 1), Is.True, Name(index));
        }
    }

    [Test]
    public void Lookup_OfEntryBeyondOldParseLimit_Finds()
    {
        // Only API that exists before the patch: the 9000th entry lies past the old MaxParseIters of 8192 records.
        for (int index = 0; index < 9000; index++)
        {
            Assert.That(Ext2DirectoryHelper.AddEntry(_sb, _root, Name(index), Ext2SuperblockLayout.RootInodeNumber, 1), Is.True, Name(index));
        }

        Assert.That(_root.InodeOperations.Lookup(_root, Name(8999), out _), Is.True);
    }

    [Test]
    public void Directory_With10000Entries_ListsAll()
    {
        // Fake inode numbers: ReadDir would read their inodes, so list through the parser here.
        AddMany(10000);
        Assert.That(Ext2DirectoryHelper.TryParseDirectory(_sb, _root, out List<Ext2DirEntry> entries), Is.True);
        Assert.That(entries.Count(entry => entry.Name.StartsWith('f')), Is.EqualTo(10000));
    }

    [Test]
    public void Directory_AfterDeletingEveryOtherEntry_StillListsAllLiving()
    {
        AddMany(10000);
        for (int index = 0; index < 10000; index += 2)
        {
            Assert.That(Ext2DirectoryHelper.RemoveEntry(_sb, _root, Name(index)), Is.True, Name(index));
        }

        Assert.That(Ext2DirectoryHelper.TryParseDirectory(_sb, _root, out List<Ext2DirEntry> entries), Is.True);
        Assert.That(entries.Count(entry => entry.Name.StartsWith('f')), Is.EqualTo(5000));
        Assert.That(entries.Any(entry => entry.Name == Name(1)), Is.True);
        Assert.That(entries.Any(entry => entry.Name == Name(9999)), Is.True);
    }

    [Test]
    public void AddEntry_BeyondMaxDirectoryBlocks_IsRefused()
    {
        // A directory already at the block limit with no usable slot: zeroed first block (the scan stops at rec_len 0),
        // size at the limit, no further real blocks needed.
        _sb.WriteBlocks(_sb.GetBlockPointer(_root, 0, false, out _), 1, new byte[BlockSize]);
        _root.Size = MaxDirectoryBlocks * BlockSize;
        _sb.WriteInode(_root);

        Assert.That(Ext2DirectoryHelper.AddEntry(_sb, _root, "one-too-many", 30, 1), Is.False);
        Assert.That(_root.FullSize, Is.EqualTo((ulong)MaxDirectoryBlocks * BlockSize), "size unchanged");
    }

    [Test]
    public void Rmdir_OfDirectoryThatCannotBeParsed_IsRefused()
    {
        Assert.That(_root.InodeOperations.Mkdir(_root, "big", VfsMode.OwnerRead | VfsMode.OwnerWrite | VfsMode.OwnerExecute, out IVfsInode? created), Is.True);
        Ext2Inode child = (Ext2Inode)created!;
        child.Size = (MaxDirectoryBlocks + 1) * BlockSize;
        _sb.WriteInode(child);

        Assert.That(_root.InodeOperations.Rmdir(_root, "big"), Is.False);
        Assert.That(_root.InodeOperations.ReadDir(child, out _), Is.False, "an unparsable directory is not listed as empty");
        Assert.That(_root.InodeOperations.Lookup(_root, "big", out _), Is.True, "entry still present");
    }
}
