using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class Ext2FeatureGateTests
{
    private const uint Filetype = 0x2;
    private const uint SparseSuper = 0x1;
    private const uint LargeFile = 0x2;
    private const uint NeedsRecovery = 0x4;
    private const uint HasJournal = 0x4;

    private static byte[] Superblock(uint rev = 1, uint logBlock = 2, uint compat = 0, uint incompat = Filetype, uint roCompat = SparseSuper, ushort magic = 0xEF53, ushort state = 1)
    {
        byte[] sb = new byte[1024];
        BitConverter.TryWriteBytes(sb.AsSpan(0, 4), 128u);
        BitConverter.TryWriteBytes(sb.AsSpan(4, 4), 2048u);
        BitConverter.TryWriteBytes(sb.AsSpan(24, 4), logBlock);
        BitConverter.TryWriteBytes(sb.AsSpan(32, 4), 32768u);
        BitConverter.TryWriteBytes(sb.AsSpan(40, 4), 128u);
        BitConverter.TryWriteBytes(sb.AsSpan(56, 2), magic);
        BitConverter.TryWriteBytes(sb.AsSpan(58, 2), state);
        BitConverter.TryWriteBytes(sb.AsSpan(76, 4), rev);
        BitConverter.TryWriteBytes(sb.AsSpan(88, 2), (ushort)128);
        BitConverter.TryWriteBytes(sb.AsSpan(92, 4), compat);
        BitConverter.TryWriteBytes(sb.AsSpan(96, 4), incompat);
        BitConverter.TryWriteBytes(sb.AsSpan(100, 4), roCompat);
        return sb;
    }

    [TestMethod]
    public void Evaluate_FormatterOutput_IsOk() =>
        Assert.AreEqual(Ext2MountDecision.Ok, Ext2FeatureGate.Evaluate(Superblock()));

    [TestMethod]
    public void Evaluate_NoOptionalFeatures_IsOk() =>
        Assert.AreEqual(Ext2MountDecision.Ok, Ext2FeatureGate.Evaluate(Superblock(incompat: 0, roCompat: 0)));

    [TestMethod]
    public void Evaluate_UnknownIncompat_IsReject() =>
        Assert.AreEqual(Ext2MountDecision.Reject, Ext2FeatureGate.Evaluate(Superblock(incompat: Filetype | NeedsRecovery)));

    [TestMethod]
    public void Evaluate_UnknownRoCompat_IsReadOnly() =>
        Assert.AreEqual(Ext2MountDecision.ReadOnly, Ext2FeatureGate.Evaluate(Superblock(roCompat: SparseSuper | 0x8)));

    [TestMethod]
    public void Evaluate_LargeFile_IsReadOnly() =>
        Assert.AreEqual(Ext2MountDecision.ReadOnly, Ext2FeatureGate.Evaluate(Superblock(roCompat: SparseSuper | LargeFile)));

    [TestMethod]
    public void Evaluate_UnknownCompat_IsReadOnly() =>
        Assert.AreEqual(Ext2MountDecision.ReadOnly, Ext2FeatureGate.Evaluate(Superblock(compat: HasJournal)));

    [TestMethod]
    public void Evaluate_RejectBeatsReadOnly() =>
        Assert.AreEqual(Ext2MountDecision.Reject, Ext2FeatureGate.Evaluate(Superblock(incompat: 0x40, roCompat: 0x8)));

    [TestMethod]
    [DataRow(0u)]
    [DataRow(1u)]
    [DataRow(3u)]
    public void Evaluate_BlockSizeNot4096_IsReject(uint logBlock) =>
        Assert.AreEqual(Ext2MountDecision.Reject, Ext2FeatureGate.Evaluate(Superblock(logBlock: logBlock)));

    [TestMethod]
    [DataRow(0u)]
    [DataRow(2u)]
    public void Evaluate_RevisionNotDynamic_IsReject(uint rev) =>
        Assert.AreEqual(Ext2MountDecision.Reject, Ext2FeatureGate.Evaluate(Superblock(rev: rev)));

    [TestMethod]
    public void Evaluate_BadMagic_IsReject() =>
        Assert.AreEqual(Ext2MountDecision.Reject, Ext2FeatureGate.Evaluate(Superblock(magic: 0)));

    [TestMethod]
    public void Evaluate_ShortBuffer_IsReject() =>
        Assert.AreEqual(Ext2MountDecision.Reject, Ext2FeatureGate.Evaluate(new byte[1023]));

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(3)]
    public void Evaluate_UncleanState_IsReadOnly(int state) =>
        Assert.AreEqual(Ext2MountDecision.ReadOnly, Ext2FeatureGate.Evaluate(Superblock(state: (ushort)state)));

    [TestMethod]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(65535)]
    public void Evaluate_UnknownState_IsReject(int state) =>
        Assert.AreEqual(Ext2MountDecision.Reject, Ext2FeatureGate.Evaluate(Superblock(state: (ushort)state, roCompat: 3)));

    [TestMethod]
    public void Evaluate_UncleanWithUnknownIncompat_IsReject() =>
        Assert.AreEqual(Ext2MountDecision.Reject, Ext2FeatureGate.Evaluate(Superblock(state: 2, incompat: 0x40)));

    [TestMethod]
    [DataRow(0, 0u)]
    [DataRow(4, 0u)]
    [DataRow(4, 15u)]
    [DataRow(20, 1u)]
    [DataRow(32, 0u)]
    [DataRow(32, 32769u)]
    [DataRow(40, 0u)]
    [DataRow(40, 32769u)]
    public void Evaluate_InvalidGeometry_IsReject(int offset, uint value)
    {
        byte[] header = Superblock();
        BitConverter.TryWriteBytes(header.AsSpan(offset, 4), value);
        Assert.AreEqual(Ext2MountDecision.Reject, Ext2FeatureGate.Evaluate(header));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(64)]
    [DataRow(129)]
    [DataRow(8192)]
    public void Evaluate_InvalidInodeSize_IsReject(int inodeSize)
    {
        byte[] header = Superblock();
        BitConverter.TryWriteBytes(header.AsSpan(88, 2), (ushort)inodeSize);
        Assert.AreEqual(Ext2MountDecision.Reject, Ext2FeatureGate.Evaluate(header));
    }
}
