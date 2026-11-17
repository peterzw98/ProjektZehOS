using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class Ext2SupportedGeometryTests
{
    private static byte[] Header(int inodeSize)
    {
        var header = new byte[1024];
        BitConverter.TryWriteBytes(header.AsSpan(0), 128u);
        BitConverter.TryWriteBytes(header.AsSpan(4), 2048u);
        BitConverter.TryWriteBytes(header.AsSpan(24), 2u);
        BitConverter.TryWriteBytes(header.AsSpan(32), 32768u);
        BitConverter.TryWriteBytes(header.AsSpan(40), 128u);
        BitConverter.TryWriteBytes(header.AsSpan(56), (ushort)0xEF53);
        BitConverter.TryWriteBytes(header.AsSpan(58), (ushort)1);
        BitConverter.TryWriteBytes(header.AsSpan(76), 1u);
        BitConverter.TryWriteBytes(header.AsSpan(88), (ushort)inodeSize);
        BitConverter.TryWriteBytes(header.AsSpan(96), 2u);
        BitConverter.TryWriteBytes(header.AsSpan(100), 1u);
        return header;
    }

    [TestMethod]
    [DataRow(128)]
    [DataRow(256)]
    [DataRow(512)]
    [DataRow(1024)]
    [DataRow(2048)]
    [DataRow(4096)]
    public void Evaluate_SupportedInodeSize_IsOk(int inodeSize)
    {
        Assert.AreEqual(Ext2MountDecision.Ok, Ext2FeatureGate.Evaluate(Header(inodeSize)));
    }

    [TestMethod]
    [DataRow(128, 2048)]
    [DataRow(32768, 32768)]
    public void Evaluate_SupportedGroupGeometry_IsOk(int groupSize, int inodeCount)
    {
        var header = Header(128);
        BitConverter.TryWriteBytes(header.AsSpan(0), (uint)inodeCount);
        BitConverter.TryWriteBytes(header.AsSpan(32), (uint)groupSize);
        BitConverter.TryWriteBytes(header.AsSpan(40), (uint)groupSize);
        Assert.AreEqual(Ext2MountDecision.Ok, Ext2FeatureGate.Evaluate(header));
    }
}
