using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DataDiskSelectorTests
{
    private static DataDiskPartition Data => new(0x0C, "ZEHOSDATA");
    private static DataDiskPartition Other => new(0x0C, "NO NAME");

    private static DataDiskCandidate Disk(string name, params DataDiskPartition[] partitions) =>
        new(name, 512, 131072, partitions);

    [TestMethod]
    public void Select_OneLabelledPartition_IsChosenOverFallback()
    {
        var result = DataDiskSelector.Select([Disk("sata0", Other), Disk("sata1", Other, Data)]);

        Assert.IsTrue(result.IsOK());
        Assert.AreEqual(new DataDiskSelection("sata1", 1, false), result.Value);
    }

    [TestMethod]
    public void Select_LabelIgnoresCaseAndPadding()
    {
        var result = DataDiskSelector.Select([Disk("sata2", new DataDiskPartition(0x0C, "zehosdata  "))]);

        Assert.AreEqual(new DataDiskSelection("sata2", 0, false), result.Value);
    }

    [TestMethod]
    public void Select_TwoLabelledPartitions_FailsInsteadOfGuessing()
    {
        foreach (var devices in new[] { new[] { Disk("sata0", Data), Disk("sata1", Data) }, new[] { Disk("sata1", Data, Data) } })
        {
            var result = DataDiskSelector.Select(devices);
            Assert.IsFalse(result.IsOK());
            StringAssert.Contains(result.Status.Message, "AmbiguousDataDisk");
        }
    }

    [TestMethod]
    public void Select_NoLabel_FallsBackToSata0WithSinglePartition()
    {
        var result = DataDiskSelector.Select([Disk("sata1", Other), Disk("sata0", Other)]);

        Assert.AreEqual(new DataDiskSelection("sata0", 0, true), result.Value);
    }

    [TestMethod]
    public void Select_LabelOnWrongPartitionType_IsNotAMatch()
    {
        var result = DataDiskSelector.Select([Disk("sata0", new DataDiskPartition(0x83, "ZEHOSDATA"))]);

        Assert.AreEqual(new DataDiskSelection("sata0", 0, true), result.Value);
    }

    [TestMethod]
    public void Select_NoLabelAndNoUsableSata0_Fails()
    {
        StringAssert.Contains(DataDiskSelector.Select([]).Status.Message, "DeviceNotFound");
        StringAssert.Contains(DataDiskSelector.Select([Disk("sata1", Other)]).Status.Message, "DeviceNotFound");
        StringAssert.Contains(DataDiskSelector.Select([Disk("sata0")]).Status.Message, "UnexpectedPartitionCount");
        StringAssert.Contains(DataDiskSelector.Select([Disk("sata0", Other, Other)]).Status.Message, "UnexpectedPartitionCount");
    }

    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    [DataTestMethod]
    [DataRow(64 * MiB - 512, false)]
    [DataRow(64 * MiB, true)]
    [DataRow(128 * GiB, true)]
    [DataRow(128 * GiB + 512, false)]
    public void CheckInitSize_Bounds(ulong bytes, bool allowed)
    {
        var result = DataDiskSelector.CheckInitSize(512, bytes / 512);

        Assert.AreEqual(allowed, result.IsOK());
        if (!allowed) StringAssert.Contains(result.Status.Message, "UnsupportedDiskSize");
    }

    [TestMethod]
    public void CheckInitSize_RejectsOtherSectorSizeAndOverflowingCounts()
    {
        StringAssert.Contains(DataDiskSelector.CheckInitSize(4096, 131072).Status.Message, "UnsupportedSectorSize");
        Assert.IsFalse(DataDiskSelector.CheckInitSize(512, 0).IsOK());
        Assert.IsFalse(DataDiskSelector.CheckInitSize(512, ulong.MaxValue).IsOK());
    }
}
