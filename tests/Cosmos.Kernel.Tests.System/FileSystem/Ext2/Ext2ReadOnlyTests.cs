// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch ext2-readonly, see PATCHES.md

using System.Security.Cryptography;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Ext2;

/// <summary>
/// Feature check at mount and the read-only mode: unknown incompat bits are
/// refused, unmaintained compat/ro_compat bits and newer revisions mount
/// read-only, and a read-only mount never writes the device.
/// </summary>
[TestFixture]
public class Ext2ReadOnlyTests
{
    private const ulong SectorSize = 512;
    private const ulong Sectors = 16384; // 8 MiB
    private const int SuperblockByte = 1024;

    /// <summary>Counts device writes and flushes on top of a RAM device.</summary>
    private sealed class CountingDevice(MemoryBlockDevice inner) : IBlockDevice
    {
        public int Writes { get; private set; }

        public int Flushes { get; private set; }

        public string Name => inner.Name;

        public ulong BlockSize => inner.BlockSize;

        public ulong BlockCount => inner.BlockCount;

        public void ReadBlock(ulong blockNo, ulong blockCount, Span<byte> data) => inner.ReadBlock(blockNo, blockCount, data);

        public void WriteBlock(ulong blockNo, ulong blockCount, ReadOnlySpan<byte> data)
        {
            Writes++;
            inner.WriteBlock(blockNo, blockCount, data);
        }

        public void Flush()
        {
            Flushes++;
            inner.Flush();
        }

        public byte[] Hash()
        {
            byte[] all = new byte[BlockCount * BlockSize];
            inner.ReadBlock(0, BlockCount, all);
            return SHA256.HashData(all);
        }
    }

    private sealed class OpenFile(IVfsInode inode) : IVfsOpenFile
    {
        public IVfsInode Inode { get; } = inode;

        public IFileOperations Operations => Inode.FileOperations!;

        public long Position { get; set; }
    }

    private static MemoryBlockDevice Formatted()
    {
        MemoryBlockDevice device = new("ram0", SectorSize, Sectors);
        Assert.That(new Ext2FileSystemType(device).TryFormat(ReadOnlySpan<char>.Empty, new Ext2FormatOptions { BlockSize = 4096 }), Is.True);
        return device;
    }

    private static void PatchSuperblock(MemoryBlockDevice device, int offset, uint value)
    {
        byte[] sector = new byte[SectorSize];
        ulong lba = (ulong)(SuperblockByte + offset) / SectorSize;
        device.ReadBlock(lba, 1, sector);
        BitConverter.TryWriteBytes(sector.AsSpan((int)((ulong)(SuperblockByte + offset) % SectorSize), 4), value);
        device.WriteBlock(lba, 1, sector);
    }

    private static Ext2Superblock Mount(IBlockDevice device, MountFlags flags = MountFlags.None)
    {
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, flags, out IVfsSuperblock? vsb), Is.True);
        return (Ext2Superblock)vsb!;
    }

    [Test]
    public void Mount_UnknownIncompat_IsRefused()
    {
        MemoryBlockDevice device = Formatted();
        PatchSuperblock(device, Ext2SuperblockLayout.FeatureIncompatOffset, 0x0002 | 0x0040); // FILETYPE | extents
        Assert.That(new Ext2FileSystemType(device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out _), Is.False);
    }

    [TestCase(0x0008u, TestName = "Mount_UnknownCompat_IsReadOnly(ext_attr)")]
    [TestCase(0x0010u, TestName = "Mount_UnknownCompat_IsReadOnly(resize_inode)")]
    [TestCase(0x0020u, TestName = "Mount_UnknownCompat_IsReadOnly(dir_index)")]
    public void Mount_UnknownCompat_IsReadOnly(uint compat)
    {
        MemoryBlockDevice device = Formatted();
        PatchSuperblock(device, Ext2SuperblockLayout.FeatureCompatOffset, compat);
        Assert.That(Mount(device).IsReadOnly, Is.True);
    }

    [Test]
    public void Mount_UnknownRoCompat_IsReadOnly()
    {
        MemoryBlockDevice device = Formatted();
        PatchSuperblock(device, Ext2SuperblockLayout.FeatureRoCompatOffset, 0x0001 | 0x0008); // SPARSE_SUPER | huge_file
        Assert.That(Mount(device).IsReadOnly, Is.True);
    }

    [Test]
    public void Mount_LargeFile_IsWritable()
    {
        MemoryBlockDevice device = Formatted();
        PatchSuperblock(device, Ext2SuperblockLayout.FeatureRoCompatOffset, 0x0001 | Ext2SuperblockLayout.FeatureRoCompatLargeFile);
        Assert.That(Mount(device).IsReadOnly, Is.False);
    }

    [Test]
    public void Mount_Rev0_IgnoresFeatureFields()
    {
        MemoryBlockDevice device = Formatted();
        PatchSuperblock(device, Ext2SuperblockLayout.RevLevelOffset, 0);
        PatchSuperblock(device, Ext2SuperblockLayout.FeatureCompatOffset, 0xFFFF_FFFF);
        PatchSuperblock(device, Ext2SuperblockLayout.FeatureIncompatOffset, 0xFFFF_FFFF);
        PatchSuperblock(device, Ext2SuperblockLayout.FeatureRoCompatOffset, 0xFFFF_FFFF);
        Assert.That(Mount(device).IsReadOnly, Is.False);
    }

    [Test]
    public void Mount_RevAboveOne_IsReadOnly()
    {
        MemoryBlockDevice device = Formatted();
        PatchSuperblock(device, Ext2SuperblockLayout.RevLevelOffset, 2);
        Assert.That(Mount(device).IsReadOnly, Is.True);
    }

    [Test]
    public void Mount_ReadOnlyFlag_IsReadOnly()
    {
        Assert.That(Mount(Formatted(), MountFlags.ReadOnly).IsReadOnly, Is.True);
        Assert.That(Mount(Formatted()).IsReadOnly, Is.False);
    }

    [Test]
    public void ReadOnlyMount_OpenReadClose_DoesNotChangeDevice()
    {
        MemoryBlockDevice ram = Formatted();
        Ext2Superblock writer = Mount(ram);
        Ext2Inode wroot = (Ext2Inode)writer.Root;
        Assert.That(wroot.InodeOperations.Create(wroot, "a.txt", VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out IVfsInode? created), Is.True);
        OpenFile wf = new(created!);
        Assert.That(wf.Operations.Write(wf, "hello"u8), Is.EqualTo(5));
        wf.Operations.Release(wf);
        writer.Drop();

        CountingDevice device = new(ram);
        byte[] before = device.Hash();
        Ext2Superblock sb = Mount(device, MountFlags.ReadOnly);
        Ext2Inode root = (Ext2Inode)sb.Root;
        Assert.That(root.InodeOperations.Lookup(root, "a.txt", out IVfsInode? file), Is.True);
        OpenFile rf = new(file!);
        byte[] buf = new byte[16];
        Assert.That(rf.Operations.Read(rf, buf), Is.EqualTo(5));
        Assert.That(rf.Operations.Fsync(rf), Is.True);
        rf.Operations.Release(rf);
        sb.Flush();
        sb.Drop();

        Assert.That(device.Writes, Is.Zero, "WriteBlock calls");
        Assert.That(device.Flushes, Is.Zero, "Flush calls");
        Assert.That(device.Hash(), Is.EqualTo(before));
    }

    [Test]
    public void ReadOnlyMount_Mutations_AreRefused()
    {
        MemoryBlockDevice ram = Formatted();
        Ext2Superblock writer = Mount(ram);
        Ext2Inode wroot = (Ext2Inode)writer.Root;
        Assert.That(wroot.InodeOperations.Create(wroot, "f", VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out _), Is.True);
        Assert.That(wroot.InodeOperations.Mkdir(wroot, "d", VfsMode.Directory | VfsMode.OwnerRead | VfsMode.OwnerWrite | VfsMode.OwnerExecute, out _), Is.True);
        writer.Drop();

        CountingDevice device = new(ram);
        byte[] before = device.Hash();
        Ext2Superblock sb = Mount(device, MountFlags.ReadOnly);
        uint freeBlocks = sb.FreeBlocksCount;
        uint freeInodes = sb.FreeInodesCount;
        Ext2Inode root = (Ext2Inode)sb.Root;
        IInodeOperations ops = root.InodeOperations;
        VfsMode fileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;
        Assert.That(ops.Lookup(root, "f", out IVfsInode? f), Is.True);

        Assert.That(ops.Create(root, "new", fileMode, out _), Is.False, "Create");
        Assert.That(ops.Mkdir(root, "nd", VfsMode.Directory | VfsMode.OwnerRead | VfsMode.OwnerWrite | VfsMode.OwnerExecute, out _), Is.False, "Mkdir");
        Assert.That(ops.Symlink(root, "l", "f", out _), Is.False, "Symlink");
        Assert.That(ops.Unlink(root, "f"), Is.False, "Unlink");
        Assert.That(ops.Rmdir(root, "d"), Is.False, "Rmdir");
        Assert.That(ops.Rename(root, "f", root, "g"), Is.False, "Rename");
        VfsStat attr = new() { Size = 4096 };
        Assert.That(ops.SetAttr(f!, SetAttrFlags.Size, attr), Is.False, "SetAttr");
        OpenFile of = new(f!);
        Assert.That(of.Operations.Write(of, "x"u8), Is.Zero, "Write");
        of.Operations.Release(of);
        sb.Drop();

        Assert.That(sb.FreeBlocksCount, Is.EqualTo(freeBlocks));
        Assert.That(sb.FreeInodesCount, Is.EqualTo(freeInodes));
        Assert.That(device.Writes, Is.Zero, "WriteBlock calls");
        Assert.That(device.Hash(), Is.EqualTo(before));
        Ext2Superblock after = Mount(ram);
        Assert.That(after.InodeOps.Lookup(after.Root, "f", out _), Is.True, "f still there");
    }

    [Test]
    public void ReadWriteMount_StillWrites()
    {
        CountingDevice device = new(Formatted());
        Ext2Superblock sb = Mount(device);
        Ext2Inode root = (Ext2Inode)sb.Root;
        Assert.That(root.InodeOperations.Create(root, "f", VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out _), Is.True);
        sb.Drop();
        Assert.That(device.Writes, Is.GreaterThan(0));
        Assert.That(device.Flushes, Is.GreaterThan(0));
    }

    [Test]
    public void Write_Beyond2GiB_WithoutLargeFile_IsRefused()
    {
        Ext2Superblock sb = Mount(Formatted());
        Ext2Inode root = (Ext2Inode)sb.Root;
        Assert.That(root.InodeOperations.Create(root, "big", VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out IVfsInode? big), Is.True);
        OpenFile of = new(big!);

        // A write crossing 2 GiB - 1 stops short at the limit; one starting there writes nothing.
        of.Position = int.MaxValue - 2;
        Assert.That(of.Operations.Write(of, "abcd"u8), Is.EqualTo(2));
        Assert.That(((Ext2Inode)big!).FullSize, Is.EqualTo((ulong)int.MaxValue));
        Assert.That(((Ext2Inode)big).SizeHigh, Is.Zero);
        of.Position = int.MaxValue;
        Assert.That(of.Operations.Write(of, "e"u8), Is.Zero);

        VfsStat attr = new() { Size = (ulong)int.MaxValue + 1 };
        Assert.That(root.InodeOperations.SetAttr(big, SetAttrFlags.Size, attr), Is.False);
        Assert.That(((Ext2Inode)big).FullSize, Is.EqualTo((ulong)int.MaxValue));
    }

    [Test]
    public void Write_Beyond2GiB_Rev0WithRawLargeFileBit_IsRefused()
    {
        MemoryBlockDevice device = Formatted();
        PatchSuperblock(device, Ext2SuperblockLayout.RevLevelOffset, 0);
        PatchSuperblock(device, Ext2SuperblockLayout.FeatureRoCompatOffset, 0x0001 | Ext2SuperblockLayout.FeatureRoCompatLargeFile);
        Ext2Superblock sb = Mount(device);
        Assert.That(sb.IsReadOnly, Is.False);
        Ext2Inode root = (Ext2Inode)sb.Root;
        Assert.That(root.InodeOperations.Create(root, "big", VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out IVfsInode? big), Is.True);
        OpenFile of = new(big!) { Position = int.MaxValue };
        Assert.That(of.Operations.Write(of, "ab"u8), Is.Zero);
        VfsStat attr = new() { Size = (ulong)int.MaxValue + 1 };
        Assert.That(root.InodeOperations.SetAttr(big!, SetAttrFlags.Size, attr), Is.False);
    }

    [Test]
    public void Write_Beyond2GiB_WithLargeFile_IsAllowed()
    {
        MemoryBlockDevice device = Formatted();
        PatchSuperblock(device, Ext2SuperblockLayout.FeatureRoCompatOffset, 0x0001 | Ext2SuperblockLayout.FeatureRoCompatLargeFile);
        Ext2Superblock sb = Mount(device);
        Ext2Inode root = (Ext2Inode)sb.Root;
        Assert.That(root.InodeOperations.Create(root, "big", VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite, out IVfsInode? big), Is.True);
        OpenFile of = new(big!) { Position = int.MaxValue };
        Assert.That(of.Operations.Write(of, "ab"u8), Is.EqualTo(2));
        Assert.That(((Ext2Inode)big!).FullSize, Is.EqualTo((ulong)int.MaxValue + 2));
    }
}
