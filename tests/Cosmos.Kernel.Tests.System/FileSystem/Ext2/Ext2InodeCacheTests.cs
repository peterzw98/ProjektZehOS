// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-inode-cache, see PATCHES.md

using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// The inode cache is sized at mount, so reading many inodes never grows
/// (copies) the dictionary.
/// </summary>
[TestFixture]
public class Ext2InodeCacheTests
{
    [Test]
    public void InodeCache_ReadDirOf30000Entries_DoesNotGrowDictionary()
    {
        // 256 MiB with 2 KiB blocks: 8 groups of 4096 inodes, directory fits into direct + single indirect blocks.
        MemoryBlockDevice device = new("ram0", 512, 524288);
        Assert.That(new Ext2FileSystemType(device).TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = 2048 }), Is.True);

        const int Files = 30_000;
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? writer), Is.True);
        Assert.That(((Ext2Superblock)writer!).InodesCount, Is.GreaterThan((uint)Files + 16), "precondition: enough inodes");
        Ext2Inode writeRoot = (Ext2Inode)writer.Root;
        for (int i = 0; i < Files; i++)
        {
            Assert.That(writeRoot.InodeOperations.Create(writeRoot, "f" + i, VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out _), Is.True, $"create {i}");
        }

        // Fresh mount: ReadDir alone fills the cache with all 30,000 inodes; a dictionary starting small would resize many times.
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        Ext2Superblock sb = (Ext2Superblock)vsb!;
        int capacity = sb.InodeCacheCapacity;
        Assert.That(capacity, Is.GreaterThanOrEqualTo(Ext2Superblock.InitialInodeCacheSize(sb.InodesCount)));

        Ext2Inode root = (Ext2Inode)sb.Root;
        Assert.That(root.InodeOperations.ReadDir(root, out IReadOnlyList<IVfsInode> entries), Is.True);
        Assert.That(entries.Count, Is.GreaterThanOrEqualTo(Files));
        Assert.That(sb.InodeCacheCapacity, Is.EqualTo(capacity));
    }

    [TestCase(0u, 0)]
    [TestCase(2048u, 2048)]
    [TestCase(65_536u, 65_536)]
    [TestCase(1_000_000u, 65_536)]
    [TestCase(uint.MaxValue, 65_536)]
    public void InitialInodeCacheSize_IsCapped(uint inodesCount, int expected)
    {
        Assert.That(Ext2Superblock.InitialInodeCacheSize(inodesCount), Is.EqualTo(expected));
    }
}
