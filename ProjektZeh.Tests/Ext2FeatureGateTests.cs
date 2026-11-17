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

    private static byte[] Superblock(uint rev = 1, uint logBlock = 2, uint compat = 0, uint incompat = Filetype, uint roCompat = SparseSuper, ushort magic = 0xEF53)
    {
        byte[] sb = new byte[1024];
        BitConverter.TryWriteBytes(sb.AsSpan(24, 4), logBlock);
        BitConverter.TryWriteBytes(sb.AsSpan(56, 2), magic);
        BitConverter.TryWriteBytes(sb.AsSpan(76, 4), rev);
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
}
