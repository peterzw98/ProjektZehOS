using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Tests;

/// <summary>Etappe 7a Schritt 3b: Decide erkennt das GPT-Layout und haengt die ESP immer als /data ein (Spec 7a, Abschnitt 3).</summary>
[TestClass]
public sealed class DataDiskSelectorGptTests
{
    private static readonly DataDiskPartition Esp = new(0, null, GptLayout.EspType);
    private static readonly DataDiskPartition Root = new(0, null, GptLayout.LinuxFileSystemType);
    private static readonly DataDiskPartition Data = new(DataDiskSelector.FatLbaType, "ZEHOSDATA  ");

    private static DataDiskCandidate Disk(string name, params DataDiskPartition[] partitions) => new(name, 512, 67108864, partitions);

    [TestMethod]
    public void EspAndRoot_MountsEsp()
    {
        var decision = DataDiskSelector.Decide([Disk("sata0", Esp, Root)]);
        Assert.AreEqual(DataDiskAction.Mount, decision.Action);
        Assert.AreEqual("sata0", decision.DeviceName);
        Assert.AreEqual(0, decision.PartitionIndex);
    }

    [TestMethod]
    public void EspSecond_MountsByTypeNotByIndex()
    {
        var decision = DataDiskSelector.Decide([Disk("sata0", Root, Esp)]);
        Assert.AreEqual(DataDiskAction.Mount, decision.Action);
        Assert.AreEqual(1, decision.PartitionIndex);
    }

    [TestMethod]
    public void RootEntryDeleted_StillMountsEsp()
    {
        // RootMissing wird erst nach dem Einhaengen ueber den Marker erkannt; Decide liefert dafuer nie Blocked.
        var decision = DataDiskSelector.Decide([Disk("sata0", Esp)]);
        Assert.AreEqual(DataDiskAction.Mount, decision.Action);
        Assert.AreEqual(0, decision.PartitionIndex);
    }

    [TestMethod]
    public void TwoEsps_Blocked()
    {
        var decision = DataDiskSelector.Decide([Disk("sata0", Esp, Esp, Root)]);
        Assert.AreEqual(DataDiskAction.Blocked, decision.Action);
        StringAssert.Contains(decision.Message, "AmbiguousEsp");
    }

    [TestMethod]
    public void InstalledDiskBeforeLabelledDisk_HintOnly()
    {
        var decision = DataDiskSelector.Decide([Disk("sata0", Esp, Root), Disk("sata1", Data)]);
        Assert.AreEqual(DataDiskAction.Mount, decision.Action);
        Assert.AreEqual("sata0", decision.DeviceName);
        Assert.IsTrue(decision.HasHint);
        StringAssert.Contains(decision.Hint, "sata1");
    }

    [TestMethod]
    public void RootOnly_NoEsp_UnchangedBlocked()
    {
        // Ohne ESP kein installiertes Layout: zwei Partitionen ohne 0x0C bleiben wie bisher gesperrt, eine bleibt Rueckfall.
        Assert.AreEqual(DataDiskAction.Blocked, DataDiskSelector.Decide([Disk("sata0", Root, Root)]).Action);
        Assert.AreEqual(DataDiskAction.Mount, DataDiskSelector.Decide([Disk("sata0", Root)]).Action);
    }

    [TestMethod]
    public void MbrPartitionsHaveNoGptType()
    {
        Assert.AreEqual(Guid.Empty, Data.GptType);
        Assert.AreEqual(DataDiskAction.Mount, DataDiskSelector.Decide([Disk("sata0", Data)]).Action);
    }
}
