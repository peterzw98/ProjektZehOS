using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DataDiskSelectorTests
{
    private static DataDiskPartition Data => new(0x0C, "ZEHOSDATA");
    private static DataDiskPartition Other => new(0x0C, "ZEHOS");

    private static DataDiskCandidate Disk(string name, params DataDiskPartition[] partitions) =>
        new(name, 512, 131072, partitions);

    [TestMethod]
    public void Decide_ExistingOnSata0_WinsOverLabelledDisk_AndHintsAtIt()
    {
        var result = DataDiskSelector.Decide([Disk("sata0", Other), Disk("sata1", Data)]);

        Assert.AreEqual(DataDiskAction.Mount, result.Action);
        Assert.AreEqual("sata0", result.DeviceName);
        Assert.AreEqual(0, result.PartitionIndex);
        StringAssert.Contains(result.Hint!, "sata1");
        StringAssert.Contains(result.Hint!, "disk use sata1 --yes");
    }

    [TestMethod]
    public void Decide_ExistingOnSata0_WithoutOtherLabelledDisk_HasNoHint()
    {
        Assert.IsNull(DataDiskSelector.Decide([Disk("sata0", Data)]).Hint, "sata0 selbst ist kein Hinweisgrund");
        Assert.IsNull(DataDiskSelector.Decide([Disk("sata0", Other), Disk("sata1", Other)]).Hint);
    }

    [TestMethod]
    public void Decide_AnyPartitionOnSata0_CountsAsExisting_EvenWithOtherType()
    {
        var result = DataDiskSelector.Decide([Disk("sata0", new DataDiskPartition(0x83, null)), Disk("sata1", Data)]);

        Assert.AreEqual(DataDiskAction.Mount, result.Action, "im Zweifel Bestand");
    }

    [TestMethod]
    public void Decide_TwoPartitionsOnSata0_IsBlocked_NotReplacedByLabelledDisk()
    {
        var result = DataDiskSelector.Decide([Disk("sata0", Other, Other), Disk("sata1", Data)]);

        Assert.AreEqual(DataDiskAction.Blocked, result.Action);
        StringAssert.Contains(result.Message, "UnexpectedPartitionCount");
    }

    [TestMethod]
    public void Decide_NoExisting_OneLabelledDisk_IsOfferedNeverMounted()
    {
        var result = DataDiskSelector.Decide([Disk("sata0"), Disk("sata1", Other, Data)]);

        Assert.AreEqual(DataDiskAction.LabelledAvailable, result.Action);
        Assert.AreEqual("sata1", result.DeviceName);
        Assert.AreEqual(1, result.PartitionIndex);
        StringAssert.Contains(result.Message, "disk use sata1 --yes");
    }

    [TestMethod]
    public void Decide_NoExisting_LabelIgnoresCaseAndPadding()
    {
        var result = DataDiskSelector.Decide([Disk("sata1", new DataDiskPartition(0x0C, "zehosdata  "))]);

        Assert.AreEqual(DataDiskAction.LabelledAvailable, result.Action);
    }

    [TestMethod]
    public void Decide_NoExisting_TwoLabelledPartitions_IsBlocked()
    {
        foreach (var devices in new[] { new[] { Disk("sata1", Data), Disk("sata2", Data) }, new[] { Disk("sata1", Data, Data) } })
        {
            var result = DataDiskSelector.Decide(devices);

            Assert.AreEqual(DataDiskAction.Blocked, result.Action);
            StringAssert.Contains(result.Message, "AmbiguousDataDisk");
        }
    }

    [TestMethod]
    public void Decide_NothingUsable_IsNoDataDisk()
    {
        Assert.AreEqual(DataDiskAction.NoDataDisk, DataDiskSelector.Decide([]).Action);
        Assert.AreEqual(DataDiskAction.NoDataDisk, DataDiskSelector.Decide([Disk("sata0")]).Action);
        var wrongType = DataDiskSelector.Decide([Disk("sata0"), Disk("sata1", new DataDiskPartition(0x83, "ZEHOSDATA"))]);
        Assert.AreEqual(DataDiskAction.NoDataDisk, wrongType.Action, "Label ohne Typ 0x0C zaehlt nicht");
    }

    [TestMethod]
    public void FindLabelled_OnePartition_ReturnsIndex_OtherwiseFails()
    {
        Assert.AreEqual(1, DataDiskSelector.FindLabelled(Disk("sata1", Other, Data)).Value);
        StringAssert.Contains(DataDiskSelector.FindLabelled(Disk("sata1", Other)).Status.Message, "NoDataPartition");
        StringAssert.Contains(DataDiskSelector.FindLabelled(Disk("sata1", Data, Data)).Status.Message, "AmbiguousDataDisk");
    }

    [TestMethod]
    public void CheckOverwrite_RequiresConfirmationOnlyWithExisting()
    {
        var blank = new[] { Disk("sata0"), Disk("sata1") };
        var existing = new[] { Disk("sata0", Other), Disk("sata1") };

        Assert.IsTrue(DataDiskSelector.CheckOverwrite(blank, false, false).IsOK(), "nichts vorhanden: keine Bestaetigung");
        Assert.IsFalse(DataDiskSelector.CheckOverwrite(existing, false, false).IsOK());
        Assert.IsFalse(DataDiskSelector.CheckOverwrite(blank, true, false).IsOK(), "/data eingebunden");
        StringAssert.Contains(DataDiskSelector.CheckOverwrite(existing, false, false).Message, "--yes-overwrite");
        Assert.IsTrue(DataDiskSelector.CheckOverwrite(existing, true, true).IsOK());
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
        if (!allowed) StringAssert.Contains(result.Message, "UnsupportedDiskSize");
    }

    [TestMethod]
    public void CheckInitSize_RejectsOtherSectorSizeAndOverflowingCounts()
    {
        StringAssert.Contains(DataDiskSelector.CheckInitSize(4096, 131072).Message, "UnsupportedSectorSize");
        Assert.IsFalse(DataDiskSelector.CheckInitSize(512, 0).IsOK());
        Assert.IsFalse(DataDiskSelector.CheckInitSize(512, ulong.MaxValue).IsOK());
    }
}
