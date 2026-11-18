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
    public void InodeCache_ManyInodes_DoesNotGrowDictionary()
    {
        MemoryBlockDevice device = new("ram0", 512, 65536); // 32 MiB
        Ext2FileSystemType fs = new(device);
        Assert.That(fs.TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = 4096 }), Is.True);
        Assert.That(fs.TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        Ext2Superblock sb = (Ext2Superblock)vsb!;
        int capacity = sb.InodeCacheCapacity;
        Assert.That(capacity, Is.GreaterThanOrEqualTo((int)Math.Min(sb.InodesCount, (uint)Ext2Superblock.InodeCachePresize)));

        // Every created inode is cached; a dictionary starting small would resize many times.
        const int Files = 1500;
        Assert.That(sb.InodesCount, Is.GreaterThan((uint)Files + 16), "precondition: enough inodes");
        Ext2Inode root = (Ext2Inode)sb.Root;
        for (int i = 0; i < Files; i++)
        {
            Assert.That(root.InodeOperations.Create(root, "f" + i, VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out _), Is.True, $"create {i}");
        }

        Assert.That(root.InodeOperations.ReadDir(root, out IReadOnlyList<IVfsInode> entries), Is.True);
        Assert.That(entries.Count, Is.GreaterThanOrEqualTo(Files));
        Assert.That(sb.InodeCacheCapacity, Is.EqualTo(capacity));
    }
}
