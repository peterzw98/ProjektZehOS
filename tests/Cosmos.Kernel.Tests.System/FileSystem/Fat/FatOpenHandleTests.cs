// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch fat-rename-open, see PATCHES.md

using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.Tests.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Fat;

/// <summary>
/// An open handle of a renamed or unlinked file must not overwrite the
/// directory entry that later takes its slot.
/// </summary>
[TestFixture]
public class FatOpenHandleTests
{
    private const VfsMode FileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;

    private MemoryBlockDevice _device = null!;
    private FatSuperblock _sb = null!;
    private FatInode _root = null!;

    [SetUp]
    public void SetUp()
    {
        _device = new("ram0", 512, 131072); // 64 MiB
        Assert.That(new FatFileSystemType(_device).TryFormat(ReadOnlySpan<char>.Empty, new FatFormatOptions { Type = FatType.Fat32 }), Is.True);
        Mount();
    }

    private void Mount()
    {
        Assert.That(new FatFileSystemType(_device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        _sb = (FatSuperblock)vsb!;
        _root = (FatInode)_sb.Root;
    }

    private static long Write(FatInode inode, string text, long position = 0)
    {
        FatOpenFile open = new(inode, inode.FileOperations!) { Position = position };
        return inode.FileOperations!.Write(open, global::System.Text.Encoding.ASCII.GetBytes(text));
    }

    private FatInode Create(FatInode parent, string name, string text)
    {
        Assert.That(parent.InodeOperations.Create(parent, name, FileMode, out IVfsInode? inode), Is.True, name);
        if (text.Length > 0)
        {
            Assert.That(Write((FatInode)inode!, text), Is.EqualTo(text.Length));
        }

        return (FatInode)inode!;
    }

    private static FatInode Lookup(FatInode parent, string name)
    {
        Assert.That(parent.InodeOperations.Lookup(parent, name, out IVfsInode? inode), Is.True, name);
        return (FatInode)inode!;
    }

    private static string Read(FatInode inode)
    {
        byte[] buffer = new byte[256];
        long read = inode.FileOperations!.Read(new FatOpenFile(inode, inode.FileOperations!), buffer);
        return global::System.Text.Encoding.ASCII.GetString(buffer, 0, (int)read);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Test]
    public void R1_HeldHandle_AfterRename_IsStillTheObject()
    {
        FatInode held = Create(_root, "a.txt", "alt");
        Assert.That(_root.InodeOperations.Rename(_root, "a.txt", _root, "b.txt"), Is.True);
        Assert.That(Lookup(_root, "b.txt"), Is.SameAs(held));
    }
    [Test]
    public void R2_Rename_LeavesHeldHandleAbleToCorruptNothing()
    {
        FatInode held = Create(_root, "a.txt", "alt");
        Assert.That(_root.InodeOperations.Rename(_root, "a.txt", _root, "b.txt"), Is.True);
        Create(_root, "c.txt", "neuer Inhalt");
        _sb.UpdateInodeEntry(held);
        Assert.That(Read(Lookup(_root, "c.txt")), Is.EqualTo("neuer Inhalt"));
    }
    [Test]
    public void R3_EmptyHeld_Unlinked_SlotReused()
    {
        FatInode held = Create(_root, "a.txt", "");
        Assert.That(_root.InodeOperations.Unlink(_root, "a.txt"), Is.True);
        Create(_root, "c.txt", "neuer Inhalt");
        _sb.UpdateInodeEntry(held);
        Assert.That(Read(Lookup(_root, "c.txt")), Is.EqualTo("neuer Inhalt"));
    }
}
