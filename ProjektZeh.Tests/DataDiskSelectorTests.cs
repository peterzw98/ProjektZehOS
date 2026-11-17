using System;
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

    private static DataDiskPartition Foreign => new(0x0C, "FREMD");
    private static DataDiskPartition Linux => new(0x83, null);

    [TestMethod]
    public void Decide_ExistingWithForeignLabel_WinsOverLabelledDisk_WithHint()
    {
        var result = DataDiskSelector.Decide([Disk("sata0", Foreign), Disk("sata1", Data)]);

        Assert.AreEqual(DataDiskAction.Mount, result.Action);
        Assert.AreEqual("sata0", result.DeviceName);
        Assert.AreEqual(0, result.PartitionIndex);
        Assert.IsTrue(result.HasHint);
        StringAssert.Contains(result.Hint!, "disk use sata1 --yes");
    }

    [TestMethod]
    public void Decide_ExistingOnSata0_WithoutOtherLabelledDisk_HasNoHint()
    {
        Assert.IsFalse(DataDiskSelector.Decide([Disk("sata0", Data)]).HasHint, "sata0 selbst ist kein Hinweisgrund");
        Assert.IsFalse(DataDiskSelector.Decide([Disk("sata0", Foreign), Disk("sata1", Other)]).HasHint);
    }

    [TestMethod]
    public void Decide_OnlyLabelledDisk_SataZeroEmpty_IsOfferedNeverMounted()
    {
        var result = DataDiskSelector.Decide([Disk("sata0"), Disk("sata1", Other, Data)]);

        Assert.AreEqual(DataDiskAction.LabelledAvailable, result.Action);
        StringAssert.Contains(result.Message, "DataDiskNotMounted");
        StringAssert.Contains(result.Message, "disk use sata1 --yes");
        Assert.AreEqual("sata1", result.DeviceName);
        Assert.AreEqual(1, result.PartitionIndex);
        Assert.IsFalse(result.HasHint);
    }

    [TestMethod]
    public void Decide_OnlyLabelledDisk_NoSata0AtAll_IsOffered_LabelIgnoresCaseAndPadding()
    {
        var result = DataDiskSelector.Decide([Disk("sata2", new DataDiskPartition(0x0C, "zehosdata  "))]);

        Assert.AreEqual(DataDiskAction.LabelledAvailable, result.Action);
        Assert.AreEqual("sata2", result.DeviceName);
    }

    [TestMethod]
    public void Decide_SataZeroWithoutFatPartition_LabelledDiskIsOfferedBeforeFallback()
    {
        var result = DataDiskSelector.Decide([Disk("sata0", Linux), Disk("sata1", Data)]);

        Assert.AreEqual(DataDiskAction.LabelledAvailable, result.Action);
    }

    [TestMethod]
    public void Decide_LabelledDiskWithAccountsFile_IsMounted_WithoutOneNotAtAll()
    {
        var devices = new[] { Disk("sata0"), Disk("sata1", Other, Data) };
        var asked = new List<(string, int)>();

        var withAccounts = DataDiskSelector.Decide(devices, (device, index) => { asked.Add((device, index)); return true; });
        var without = DataDiskSelector.Decide(devices, (_, _) => false);
        var noCheck = DataDiskSelector.Decide(devices);

        Assert.AreEqual(DataDiskAction.MountLabelled, withAccounts.Action);
        Assert.AreEqual("sata1", withAccounts.DeviceName);
        Assert.AreEqual(1, withAccounts.PartitionIndex);
        CollectionAssert.AreEqual(new[] { ("sata1", 1) }, asked);
        Assert.AreEqual(DataDiskAction.LabelledAvailable, without.Action, "leere ZEHOSDATA nie automatisch");
        StringAssert.Contains(without.Message, "DataDiskNotMounted");
        Assert.AreEqual(DataDiskAction.LabelledAvailable, noCheck.Action, "ohne Pruefung im Zweifel nicht einbinden");
    }

    [TestMethod]
    public void Decide_AccountsCheckRunsOnlyForTheSingleLabelledCase()
    {
        int calls = 0;
        Func<string, int, bool> check = (_, _) => { calls++; return true; };

        DataDiskSelector.Decide([Disk("sata0", Foreign), Disk("sata1", Data)], check);
        DataDiskSelector.Decide([Disk("sata1", Data), Disk("sata2", Data)], check);
        DataDiskSelector.Decide([Disk("sata0")], check);

        Assert.AreEqual(0, calls, "Bestand, Mehrdeutigkeit und keine Platte pruefen nichts (kein Mount fremder Platten)");
    }

    [TestMethod]
    public void Decide_TwoLabelledPartitions_IsBlocked()
    {
        foreach (var devices in new[] { new[] { Disk("sata1", Data), Disk("sata2", Data) }, new[] { Disk("sata1", Data, Data) } })
        {
            var result = DataDiskSelector.Decide(devices);

            Assert.AreEqual(DataDiskAction.Blocked, result.Action);
            StringAssert.Contains(result.Message, "AmbiguousDataDisk");
        }
    }

    [TestMethod]
    public void Decide_ExistingWinsEvenWhenTwoLabelledDisksExist()
    {
        var result = DataDiskSelector.Decide([Disk("sata0", Foreign), Disk("sata1", Data), Disk("sata2", Data)]);

        Assert.AreEqual(DataDiskAction.Mount, result.Action, "Bestand zuerst, nie Mehrdeutigkeit gegen einen Bestand");
    }

    [TestMethod]
    public void Decide_Fallback_SataZeroWithSingleNonFatPartition_Mounts()
    {
        var result = DataDiskSelector.Decide([Disk("sata0", Linux)]);

        Assert.AreEqual(DataDiskAction.Mount, result.Action);
        Assert.AreEqual("sata0", result.DeviceName);
    }

    [TestMethod]
    public void Decide_TwoPartitionsOnSata0_IsBlocked()
    {
        foreach (var partitions in new[] { new[] { Foreign, Foreign }, new[] { Linux, Linux } })
        {
            var result = DataDiskSelector.Decide([Disk("sata0", partitions), Disk("sata1", Other)]);

            Assert.AreEqual(DataDiskAction.Blocked, result.Action);
            StringAssert.Contains(result.Message, "UnexpectedPartitionCount");
        }
    }

    [TestMethod]
    public void Decide_NothingUsable_IsNoDataDisk_WithMessageAsBefore()
    {
        var absent = DataDiskSelector.Decide([]);
        var blank = DataDiskSelector.Decide([Disk("sata0")]);
        var wrongType = DataDiskSelector.Decide([Disk("sata0"), Disk("sata1", new DataDiskPartition(0x83, "ZEHOSDATA"))]);

        Assert.AreEqual(DataDiskAction.NoDataDisk, absent.Action);
        StringAssert.Contains(absent.Message, "DeviceNotFound");
        Assert.AreEqual(DataDiskAction.NoDataDisk, blank.Action);
        StringAssert.Contains(blank.Message, "UnexpectedPartitionCount");
        Assert.AreEqual(DataDiskAction.NoDataDisk, wrongType.Action, "Label ohne Typ 0x0C zaehlt nicht");
    }

    [TestMethod]
    public void SetupBlockedReason_OnlyForBlockedOrFailedChosenMount()
    {
        var ambiguous = DataDiskSelector.Decide([Disk("sata1", Data), Disk("sata2", Data)]);
        var existing = DataDiskSelector.Decide([Disk("sata0", Foreign)]);
        var labelled = DataDiskSelector.Decide([Disk("sata0"), Disk("sata1", Data)]);
        var mountedLabelled = DataDiskSelector.Decide([Disk("sata0"), Disk("sata1", Data)], (_, _) => true);
        var none = DataDiskSelector.Decide([]);

        StringAssert.Contains(DataDiskSelector.SetupBlockedReason(ambiguous, false)!, "AmbiguousDataDisk");
        StringAssert.Contains(DataDiskSelector.SetupBlockedReason(existing, false)!, "sata0");
        StringAssert.Contains(DataDiskSelector.SetupBlockedReason(labelled, false)!, "DataDiskNotMounted");
        StringAssert.Contains(DataDiskSelector.SetupBlockedReason(labelled, false)!, "sata1");
        Assert.IsNull(DataDiskSelector.SetupBlockedReason(existing, true), "erfolgreicher Mount: normale Ersteinrichtung moeglich");
        Assert.IsNull(DataDiskSelector.SetupBlockedReason(mountedLabelled, true));
        StringAssert.Contains(DataDiskSelector.SetupBlockedReason(mountedLabelled, false)!, "sata1");
        Assert.IsNull(DataDiskSelector.SetupBlockedReason(none, false), "Boot ohne Datenplatte bleibt unveraendert");
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
