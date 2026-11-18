// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-free-inode, see PATCHES.md

using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// <see cref="Ext2Superblock.FreeInode"/> marks the on-disk slot deleted
/// before clearing the bitmap bit and ignores an inode that is already free.
/// On-disk state is checked through a fresh mount, whose inode cache is empty.
/// </summary>
[TestFixture]
public class Ext2FreeInodeTests
{
    private const ulong DeviceBlockSize = 512;
    private const ulong DeviceBlockCount = 16384; // 8 MiB
    private const uint FsBlockSize = 4096;
    private const VfsMode FileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;

    private MemoryBlockDevice _device = null!;
    private Ext2Superblock _sb = null!;

    [SetUp]
    public void SetUp()
    {
        _device = new("ram0", DeviceBlockSize, DeviceBlockCount);
        Ext2FileSystemType fs = new(_device);
        Assert.That(fs.TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = FsBlockSize }), Is.True);
        _sb = Remount();
    }

    private Ext2Superblock Remount()
    {
        Ext2FileSystemType fs = new(_device);
        Assert.That(fs.TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        return (Ext2Superblock)vsb!;
    }

    private Ext2Inode Create(string name)
    {
        Assert.That(_sb.Root.InodeOperations.Create(_sb.Root, name, FileMode, out IVfsInode? inode), Is.True);
        return (Ext2Inode)inode!;
    }

    private Ext2Inode OnDisk(uint inodeNumber) => Remount().ReadInode(inodeNumber, "probe");

    [Test]
    public void Release_LiveInode_WritesLinksZeroAndDtimeThenClearsBit()
    {
        Ext2Inode target = Create("a");
        Ext2Inode neighbour = Create("b");
        ushort mode = target.Mode;
        uint freeBefore = _sb.FreeInodesCount;

        _sb.FreeInode(target.InodeNumber);

        Ext2Inode freed = OnDisk(target.InodeNumber);
        Assert.That(freed.LinksCount, Is.Zero);
        Assert.That(freed.Dtime, Is.Not.Zero);
        Assert.That(freed.Mode, Is.EqualTo(mode), "only links_count and dtime change");
        Assert.That(_sb.FreeInodesCount, Is.EqualTo(freeBefore + 1));

        Ext2Inode other = OnDisk(neighbour.InodeNumber);
        Assert.That(other.LinksCount, Is.EqualTo(1), "neighbour slot in the same table block is unchanged");
        Assert.That(other.Dtime, Is.Zero);

        // The bitmap bit is clear: the lowest free inode is handed out again.
        Assert.That(_sb.TryAllocateInode(0, out uint again), Is.True);
        Assert.That(again, Is.EqualTo(target.InodeNumber));
    }

    [Test]
    public void Release_LastInodeOfTableBlock_MarksOnlyThatSlot_AndSecondTableBlockIsUsed()
    {
        uint inodesPerBlock = FsBlockSize / _sb.InodeSize;
        List<Ext2Inode> created = [];
        Ext2Inode? last = null;
        for (int i = 0; i < 3 * inodesPerBlock && last is null; i++)
        {
            Ext2Inode inode = Create("f" + i);
            created.Add(inode);
            if (_sb.IndexInGroup(inode.InodeNumber) == (2 * inodesPerBlock) - 1)
            {
                last = inode;
            }
        }

        Assert.That(last, Is.Not.Null, "an inode in the last slot of the second table block");
        Ext2Inode next = Create("after");
        Ext2Inode previous = created[^2];

        _sb.FreeInode(last!.InodeNumber);

        Assert.That(OnDisk(last.InodeNumber).LinksCount, Is.Zero);
        Assert.That(OnDisk(last.InodeNumber).Dtime, Is.Not.Zero);
        Assert.That(OnDisk(previous.InodeNumber).LinksCount, Is.EqualTo(1), "previous slot in the block is unchanged");
        Assert.That(OnDisk(next.InodeNumber).LinksCount, Is.EqualTo(1), "first slot of the next block is unchanged");
        Assert.That(OnDisk(next.InodeNumber).Dtime, Is.Zero);
    }

    [Test]
    public void Release_AlreadyFree_LeavesCountersAndSlotUnchanged()
    {
        Ext2Inode target = Create("a");
        _sb.FreeInode(target.InodeNumber);
        uint freeAfterFirst = _sb.FreeInodesCount;
        uint dtimeAfterFirst = OnDisk(target.InodeNumber).Dtime;
        uint onDiskFreeAfterFirst = Remount().FreeInodesCount; // the superblock copy is written lazily
        uint group = _sb.GroupOfInode(target.InodeNumber);
        uint groupFreeAfterFirst = Remount().GetGroup(group).FreeInodesCount; // written immediately

        _sb.FreeInode(target.InodeNumber);

        Assert.That(_sb.FreeInodesCount, Is.EqualTo(freeAfterFirst));
        Assert.That(Remount().FreeInodesCount, Is.EqualTo(onDiskFreeAfterFirst), "on-disk counters unchanged");
        Assert.That(Remount().GetGroup(group).FreeInodesCount, Is.EqualTo(groupFreeAfterFirst), "group descriptor unchanged");
        Assert.That(OnDisk(target.InodeNumber).Dtime, Is.EqualTo(dtimeAfterFirst));
    }

    [Test]
    public void FreeInode_HeldObjectWrittenLater_KeepsDtime()
    {
        Ext2Inode target = Create("a");

        _sb.FreeInode(target.InodeNumber);
        _sb.WriteInode(target); // e.g. an open handle updating mtime after unlink

        Ext2Inode onDisk = OnDisk(target.InodeNumber);
        Assert.That(onDisk.LinksCount, Is.Zero);
        Assert.That(onDisk.Dtime, Is.Not.Zero);
    }

    [Test]
    public void FreeInode_RemovesFromCache()
    {
        Ext2Inode target = Create("a");
        Assert.That(_sb.ReadInode(target.InodeNumber, "a"), Is.SameAs(target), "precondition: inode is cached");

        _sb.FreeInode(target.InodeNumber);

        Ext2Inode reread = _sb.ReadInode(target.InodeNumber, "a");
        Assert.That(reread, Is.Not.SameAs(target));
        Assert.That(reread.LinksCount, Is.Zero);
        Assert.That(reread.Dtime, Is.Not.Zero);
    }

    [Test]
    public void Unlink_WritesDtime()
    {
        Ext2Inode target = Create("a");

        Assert.That(_sb.Root.InodeOperations.Unlink(_sb.Root, "a"), Is.True);

        Ext2Inode freed = OnDisk(target.InodeNumber);
        Assert.That(freed.LinksCount, Is.Zero);
        Assert.That(freed.Dtime, Is.Not.Zero, "e2fsck: \"Deleted inode has zero dtime\"");
    }
}
