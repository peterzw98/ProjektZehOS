// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-used-dirs, see PATCHES.md

using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// bg_used_dirs_count survives a later descriptor write of another group in
/// the same descriptor table block (e2fsck "Directories count wrong").
/// </summary>
[TestFixture]
public class Ext2UsedDirsTests
{
    private const VfsMode DirMode = VfsMode.Directory | VfsMode.OwnerRead | VfsMode.OwnerWrite | VfsMode.OwnerExecute;

    private static Ext2Superblock Mount(IBlockDevice device, MountFlags flags = MountFlags.None)
    {
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, flags, out IVfsSuperblock? vsb), Is.True);
        return (Ext2Superblock)vsb!;
    }

    [Test]
    public void Mkdir_InSpilledGroup_KeepsCountWhenOtherGroupWritesDescriptor()
    {
        // 160 MiB with 4 KiB blocks: two block groups.
        MemoryBlockDevice device = new("ram0", 512, 160 * 2048);
        Assert.That(new Ext2FileSystemType(device).TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = 4096 }), Is.True);
        Ext2Superblock sb = Mount(device);
        Assert.That(sb.GroupsCount, Is.GreaterThanOrEqualTo(2u));
        ushort group0Dirs = sb.GetGroup(0).UsedDirsCount;

        // Fill group 0's inodes so the new directory's inode spills into group 1
        // while its block stays in group 0.
        while (sb.GetGroup(0).FreeInodesCount > 0)
        {
            Assert.That(sb.TryAllocateInode(0, out uint ino), Is.True);
            Assert.That(sb.GroupOfInode(ino), Is.EqualTo(0u));
        }

        Assert.That(sb.Root.InodeOperations.Mkdir(sb.Root, "spill", DirMode, out IVfsInode? dir), Is.True);
        Assert.That(sb.GroupOfInode(((Ext2Inode)dir!).InodeNumber), Is.EqualTo(1u));

        // Another group's descriptor write rewrites the shared table block.
        Assert.That(sb.TryAllocateBlock(0, out _), Is.True);

        Ext2Superblock again = Mount(device, MountFlags.ReadOnly);
        Assert.That(again.GetGroup(1).UsedDirsCount, Is.EqualTo((ushort)1), "group 1 holds the new directory");
        Assert.That(again.GetGroup(0).UsedDirsCount, Is.EqualTo(group0Dirs), "group 0 unchanged");
    }
}
