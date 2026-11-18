using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

/// <summary>Spec 7b §2 und §4 Hosttest 3/4: Reihenfolge, Ruecknahme und Schreibsperre der Energieaktion im Root-Profil.</summary>
[TestClass]
public sealed class RootPowerSequenceTests
{
    [TestMethod]
    public void Prepare_UnmountsExtraDisksInReverseThenEfiThenClosesRoot()
    {
        var platform = new Platform("/boot/efi", "/mnt/a", "/mnt/b");
        Assert.IsTrue(new RootPowerSequence(platform).Prepare().IsOK());
        CollectionAssert.AreEqual(new[] { "block", "sync /mnt/b", "unmount /mnt/b", "sync /mnt/a", "unmount /mnt/a",
            "sync /boot/efi", "unmount /boot/efi", "close" }, platform.Log);
        Assert.IsTrue(platform.Blocked, "Die Schreibsperre bleibt bis zur Energieaktion.");
    }

    [TestMethod]
    public void Prepare_RunningAppOrWriter_RefusesBeforeAnyUnmount()
    {
        var busy = new Platform("/boot/efi") { Busy = "Paket-App classicube" };
        Assert.IsFalse(new RootPowerSequence(busy).Prepare().IsOK());
        Assert.AreEqual(0, busy.Log.Count);

        var writing = new Platform("/boot/efi") { BlockFails = true };
        Assert.IsFalse(new RootPowerSequence(writing).Prepare().IsOK());
        CollectionAssert.AreEqual(new[] { "block" }, writing.Log);
    }

    [TestMethod]
    public void EfiSyncFailure_RemountsExtraDisksInOrderAndReleasesWrites()
    {
        var platform = new Platform("/boot/efi", "/mnt/a", "/mnt/b") { FailSync = "/boot/efi" };
        var result = new RootPowerSequence(platform).Prepare();
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "PowerUnmountFailed", StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { "block", "sync /mnt/b", "unmount /mnt/b", "sync /mnt/a", "unmount /mnt/a",
            "sync /boot/efi", "release", "remount /mnt/a", "remount /mnt/b" }, platform.Log);
        Assert.IsFalse(platform.Blocked);
        CollectionAssert.AreEquivalent(new[] { "/boot/efi", "/mnt/a", "/mnt/b" }, platform.Mounted);
    }

    [TestMethod]
    public void UnmountThatThrowsAfterRemoving_IsRemountedToo()
    {
        var platform = new Platform("/boot/efi") { ThrowAfterUnmount = "/boot/efi" };
        Assert.IsFalse(new RootPowerSequence(platform).Prepare().IsOK());
        CollectionAssert.Contains(platform.Log, "remount /boot/efi");
        CollectionAssert.Contains(platform.Mounted, "/boot/efi");
    }

    [TestMethod]
    public void CloseRootFailure_ReopensBeforeReleasingWritesThenRemounts()
    {
        var platform = new Platform("/boot/efi") { CloseFails = true };
        Assert.IsFalse(new RootPowerSequence(platform).Prepare().IsOK());
        CollectionAssert.AreEqual(new[] { "block", "sync /boot/efi", "unmount /boot/efi", "close", "reopen", "release",
            "remount /boot/efi" }, platform.Log);
    }

    [TestMethod]
    public void ReopenFailure_KeepsRootWriteLockedUntilALaterReopenSucceeds()
    {
        var platform = new Platform("/boot/efi") { CloseFails = true, ReopenFails = true };
        var sequence = new RootPowerSequence(platform);
        var result = sequence.Prepare();
        StringAssert.Contains(result.Message, "Root schreibgesperrt", StringComparison.Ordinal);
        Assert.IsTrue(platform.Blocked);
        Assert.IsTrue(sequence.RootWriteLocked);
        CollectionAssert.Contains(platform.Mounted, "/boot/efi", "Lesen bleibt moeglich.");

        platform.ReopenFails = false;
        platform.CloseFails = false;
        platform.Log.Clear();
        Assert.IsTrue(sequence.Prepare().IsOK());
        CollectionAssert.AreEqual(new[] { "reopen", "release", "block", "sync /boot/efi", "unmount /boot/efi", "close" }, platform.Log);
        Assert.IsFalse(sequence.RootWriteLocked);
    }

    [TestMethod]
    public void EfiRemountFailure_ReportsUpdatesLockedButReleasesWrites()
    {
        var platform = new Platform("/boot/efi") { CloseFails = true, RemountFails = true };
        var result = new RootPowerSequence(platform).Prepare();
        StringAssert.Contains(result.Message, "Updates gesperrt", StringComparison.Ordinal);
        Assert.IsFalse(platform.Blocked);
    }

    [TestMethod]
    public void Restore_AfterReturnedPowerAction_ReopensThenReleasesThenRemounts()
    {
        var platform = new Platform("/boot/efi", "/mnt/a");
        var sequence = new RootPowerSequence(platform);
        Assert.IsTrue(sequence.Prepare().IsOK());
        platform.Log.Clear();
        Assert.IsTrue(sequence.Restore().IsOK());
        CollectionAssert.AreEqual(new[] { "reopen", "release", "remount /boot/efi", "remount /mnt/a" }, platform.Log);
        Assert.IsFalse(platform.Blocked);
    }

    private sealed class Platform(params string[] mounts) : IRootPowerPlatform
    {
        public List<string> Log { get; } = [];
        public List<string> Mounted { get; } = [.. mounts];
        public bool Blocked { get; private set; }
        public string? Busy { get; init; }
        public bool BlockFails { get; init; }
        public string? FailSync { get; init; }
        public string? ThrowAfterUnmount { get; init; }
        public bool CloseFails { get; set; }
        public bool ReopenFails { get; set; }
        public bool RemountFails { get; init; }

        public string? BusyReason() => Busy;
        public bool TryBlockWrites() { Log.Add("block"); if (BlockFails) return false; Blocked = true; return true; }
        public void ReleaseWrites() { Log.Add("release"); Blocked = false; }
        public IReadOnlyList<string> PersistentMounts() => [.. mounts];
        public bool IsMounted(string mountPoint) => Mounted.Contains(mountPoint);
        public bool Sync(string mountPoint) { Log.Add("sync " + mountPoint); return mountPoint != FailSync; }
        public bool Unmount(string mountPoint)
        {
            Log.Add("unmount " + mountPoint);
            Mounted.Remove(mountPoint);
            if (mountPoint == ThrowAfterUnmount) throw new IOException("Flush beim Aushaengen");
            return true;
        }
        public bool Remount(string mountPoint)
        {
            Log.Add("remount " + mountPoint);
            if (RemountFails) return false;
            Mounted.Add(mountPoint);
            return true;
        }
        public ResultErr CloseRoot() { Log.Add("close"); return CloseFails ? ResultErr.CreateFailure(ResultCode.Failure, "Sync") : ResultErr.CreateSuccess(); }
        public ResultErr ReopenRoot() { Log.Add("reopen"); return ReopenFails ? ResultErr.CreateFailure(ResultCode.Failure, "Flush") : ResultErr.CreateSuccess(); }
    }
}
