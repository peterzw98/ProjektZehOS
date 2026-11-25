// This code is licensed under the BSD 3-Clause license (see LICENSE for details)
// ZehOS patch fat-fsinfo, see PATCHES.md

using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Fat;
using Cosmos.Kernel.Tests.System.FileSystem.Ext2;
using NUnit.Framework;

namespace Cosmos.Kernel.Tests.System.FileSystem.Fat;

/// <summary>
/// The FAT32 FSInfo free count must match the FAT after every sync, so
/// fsck.fat and other systems read the volume without a complaint.
/// </summary>
[TestFixture]
public class FatFsInfoTests
{
    private const VfsMode FileMode = VfsMode.RegularFile | VfsMode.OwnerRead | VfsMode.OwnerWrite;
    private const ulong FsInfoLba = 1;
    private const int FreeCountOffset = 488;
    private const int NextFreeOffset = 492;
    private const uint Unknown = 0xFFFF_FFFF;

    private MemoryBlockDevice _device = null!;
    private FatSuperblock _sb = null!;
    private FatInode _root = null!;

    private void Format(FatType type)
    {
        _device = new("ram0", 512, 131072); // 64 MiB
        Assert.That(new FatFileSystemType(_device).TryFormat(ReadOnlySpan<char>.Empty, new FatFormatOptions { Type = type }), Is.True);
    }

    private void Mount()
    {
        Assert.That(new FatFileSystemType(_device).TryMount(ReadOnlySpan<char>.Empty, MountFlags.None, out IVfsSuperblock? vsb), Is.True);
        _sb = (FatSuperblock)vsb!;
        _root = (FatInode)_sb.Root;
    }

    private void Sync()
    {
        Assert.That(_sb.SuperOps.Sync(_sb), Is.True);
    }

    private byte[] Sector(ulong lba)
    {
        byte[] sector = new byte[512];
        _device.ReadBlock(lba, 1, sector);
        return sector;
    }

    private uint Field(int offset) => BitConverter.ToUInt32(Sector(FsInfoLba), offset);

    private void SetField(int offset, uint value)
    {
        byte[] sector = Sector(FsInfoLba);
        BitConverter.TryWriteBytes(sector.AsSpan(offset, 4), value);
        _device.WriteBlock(FsInfoLba, 1, sector);
    }

    private void CreateFile(string name, int bytes)
    {
        Assert.That(_root.InodeOperations.Create(_root, name, FileMode, out IVfsInode? inode), Is.True, name);
        FatInode file = (FatInode)inode!;
        FatOpenFile open = new(file, file.FileOperations!);
        Assert.That(file.FileOperations!.Write(open, new byte[bytes]), Is.EqualTo(bytes));
    }

    [Test]
    public void F1_FormattedUnknown_AfterWriteAndSync_MatchesFat()
    {
        Format(FatType.Fat32);
        Assert.That(Field(FreeCountOffset), Is.EqualTo(Unknown));
        Mount();
        CreateFile("a.bin", 100_000);
        Sync();
        Assert.That(Field(FreeCountOffset), Is.EqualTo(_sb.Fat.CountFree()));
        Assert.That(_sb.Fat.IsDataCluster(Field(NextFreeOffset)), Is.True);
    }

    [Test]
    public void F2_TrustedCount_FollowsAllocateAndFree()
    {
        Format(FatType.Fat32);
        Mount();
        CreateFile("a.bin", 50_000);
        Sync();
        Mount(); // remount: count now comes from disk
        CreateFile("b.bin", 70_000);
        CreateFile("c.bin", 3_000);
        Assert.That(_root.InodeOperations.Unlink(_root, "a.bin"), Is.True);
        Sync();
        Assert.That(Field(FreeCountOffset), Is.EqualTo(_sb.Fat.CountFree()));
    }

    [Test]
    public void F3_ContradictedStoredCount_IsRecounted()
    {
        Format(FatType.Fat32);
        SetField(FreeCountOffset, 0); // plausible, but wrong
        Mount();
        // One cluster: without the recount 0 - 1 would wrap to "unknown".
        CreateFile("a.bin", 1);
        Sync();
        Assert.That(Field(FreeCountOffset), Is.EqualTo(_sb.Fat.CountFree()));
    }

    [Test]
    public void F4_NoChange_LeavesSectorUntouched()
    {
        Format(FatType.Fat32);
        byte[] before = Sector(FsInfoLba);
        Mount();
        Assert.That(_root.InodeOperations.Lookup(_root, "x", out _), Is.False);
        Sync();
        Assert.That(Sector(FsInfoLba), Is.EqualTo(before));
    }

    [Test]
    public void F5_BadSignature_IsNotWritten()
    {
        Format(FatType.Fat32);
        SetField(484, 0); // FSI_StrucSig
        byte[] before = Sector(FsInfoLba);
        Mount();
        CreateFile("a.bin", 10_000);
        Sync();
        Assert.That(Sector(FsInfoLba), Is.EqualTo(before));
    }

    [Test]
    public void F6_Fat16_WritesNoFsInfo()
    {
        // FAT16 has no FSInfo; sector 1 is the FAT itself here.
        Format(FatType.Fat16);
        Mount();
        Assert.That(_sb.Boot.FsInfoSector, Is.Zero);
        CreateFile("a.bin", 10_000);
        Sync();
        Assert.That(Field(0), Is.Not.EqualTo(0x4161_5252u)); // no FSI_LeadSig
        Assert.That(Field(FreeCountOffset), Is.Not.EqualTo(_sb.Fat.CountFree()));
    }

    [Test]
    public void F7_Unmount_WritesCount()
    {
        Format(FatType.Fat32);
        Mount();
        CreateFile("a.bin", 20_000);
        _sb.SuperOps.Drop(_sb);
        Mount();
        Assert.That(Field(FreeCountOffset), Is.EqualTo(_sb.Fat.CountFree()));
    }
}
