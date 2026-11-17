using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Tests;

/// <summary>Quellschutz 4.5 (P3-g): je Fehlerstelle ein Test, ob /data eingehaengt und ob der Quelllease gehalten bleibt.</summary>
[TestClass]
public sealed class MigrationSourceSwapTests
{
    private sealed class Fixture
    {
        public readonly MigrationWriteGate Gate = new();
        public readonly List<string> Steps = [];
        public readonly List<string> Log = [];
        public bool UnmountWritable = true, MountReadOnly = true, UnmountReadOnly = true;
        public Queue<bool> MountWritable = new([true]);
        public string Mounted = "rw";

        public MigrationSourceSwap Create() => new(Gate.TryLockSource, () => Gate.BusyDescription,
            () => Step("unmount-rw", UnmountWritable, "none"),
            () => Step("mount-ro", MountReadOnly, "ro"),
            () => Step("unmount-ro", UnmountReadOnly, "none"),
            () => Step("mount-rw", MountWritable.Count == 0 || MountWritable.Dequeue(), "rw"),
            Log.Add);

        private bool Step(string name, bool succeeds, string state)
        {
            Steps.Add(name);
            if (succeeds) Mounted = state;
            return succeeds;
        }
    }

    private static string Cause(Action action) => Assert.ThrowsException<IOException>(action).Message;

    [TestMethod]
    public void SuccessfulSwapReadsFromTheReadOnlyMountAndRestoresTheWritableOne()
    {
        var fixture = new Fixture();
        var swap = fixture.Create();
        var restore = swap.Acquire();
        Assert.AreEqual("ro", fixture.Mounted);
        Assert.IsNull(fixture.Gate.TryBeginWrite(), "Waehrend der Migration darf niemand auf die Quelle schreiben.");
        restore.Dispose();
        restore.Dispose();
        CollectionAssert.AreEqual(new[] { "unmount-rw", "mount-ro", "unmount-ro", "mount-rw" }, fixture.Steps, "Zweites Dispose ohne Wirkung.");
        Assert.AreEqual("rw", fixture.Mounted);
        Assert.IsNull(swap.RestoreFailure);
        using var writer = fixture.Gate.TryBeginWrite();
        Assert.IsNotNull(writer);
    }

    [TestMethod]
    public void BusySourceIsRefusedWithoutTouchingTheMount()
    {
        var fixture = new Fixture();
        using var install = fixture.Gate.TryBeginWrite("Paketinstallation");
        StringAssert.Contains(Cause(() => fixture.Create().Acquire()), "Paketinstallation");
        Assert.AreEqual(0, fixture.Steps.Count);
        Assert.AreEqual("rw", fixture.Mounted);
    }

    [TestMethod]
    public void FailedUnmountLeavesDataWritableAndReleasesTheLock()
    {
        var fixture = new Fixture { UnmountWritable = false };
        StringAssert.Contains(Cause(() => fixture.Create().Acquire()), "SourceUnmountFailed");
        Assert.AreEqual("rw", fixture.Mounted);
        using var writer = fixture.Gate.TryBeginWrite();
        Assert.IsNotNull(writer);
    }

    [TestMethod]
    public void FailedReadOnlyMountRestoresTheWritableMountAndReleasesTheLock()
    {
        var fixture = new Fixture { MountReadOnly = false };
        var swap = fixture.Create();
        StringAssert.Contains(Cause(() => swap.Acquire()), "SourceReadOnlyMountFailed");
        Assert.AreEqual("rw", fixture.Mounted);
        Assert.IsNull(swap.RestoreFailure);
        using var writer = fixture.Gate.TryBeginWrite();
        Assert.IsNotNull(writer);
    }

    // Review P3-a: ein Lesemount, der erst nach dem Einhaengen wirft, wird vor dem Zurueckhaengen ausgehaengt.
    [TestMethod]
    public void FailedReadOnlyMountIsUnmountedBeforeTheWritableMountReturns()
    {
        var fixture = new Fixture { MountReadOnly = false };
        Assert.ThrowsException<IOException>(() => fixture.Create().Acquire());
        CollectionAssert.AreEqual(new[] { "unmount-rw", "mount-ro", "unmount-ro", "mount-rw" }, fixture.Steps);
    }

    [TestMethod]
    public void FailedReadOnlyMountAndFailedRestoreKeepTheSourceLocked()
    {
        var fixture = new Fixture { MountReadOnly = false, MountWritable = new([false]) };
        var swap = fixture.Create();
        StringAssert.Contains(Cause(() => swap.Acquire()), "SourceRestoreFailed");
        Assert.AreEqual("none", fixture.Mounted);
        Assert.IsNotNull(swap.RestoreFailure);
        Assert.IsTrue(fixture.Gate.IsMigrationLocked, "Ohne /data bleibt die Sperre bis zum Neustart.");
        Assert.IsNull(fixture.Gate.TryBeginWrite());
    }

    [TestMethod]
    public void FailedUnmountOfTheReadOnlyMountKeepsTheSourceLocked()
    {
        var fixture = new Fixture { UnmountReadOnly = false };
        var swap = fixture.Create();
        swap.Acquire().Dispose();
        Assert.AreEqual("ro", fixture.Mounted);
        StringAssert.Contains(swap.RestoreFailure, "nur lesend");
        Assert.IsTrue(fixture.Gate.IsMigrationLocked);
        CollectionAssert.DoesNotContain(fixture.Steps, "mount-rw", "Kein zweiter Mount auf /data ueber dem Lesemount.");
    }

    [TestMethod]
    public void FailedWritableRemountAfterTheMigrationKeepsTheSourceLocked()
    {
        var fixture = new Fixture { MountWritable = new([false]) };
        var swap = fixture.Create();
        swap.Acquire().Dispose();
        Assert.AreEqual("none", fixture.Mounted);
        StringAssert.Contains(swap.RestoreFailure, "nicht eingehaengt");
        Assert.IsTrue(fixture.Gate.IsMigrationLocked);
        Assert.IsTrue(fixture.Log.Exists(line => line.Contains("nicht eingehaengt", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ThrowingMountStepCountsAsFailureAndIsLogged()
    {
        var fixture = new Fixture();
        var swap = new MigrationSourceSwap(fixture.Gate.TryLockSource, () => fixture.Gate.BusyDescription, () => true,
            () => throw new InvalidOperationException("Treiberfehler"), () => true, () => true, fixture.Log.Add);
        StringAssert.Contains(Cause(() => swap.Acquire()), "SourceReadOnlyMountFailed");
        Assert.IsTrue(fixture.Log.Exists(line => line.Contains("Treiberfehler", StringComparison.Ordinal)));
        Assert.IsFalse(fixture.Gate.IsMigrationLocked);
    }

    [TestMethod]
    public void LayoutMigrationReportsASwapFailureAsResult()
    {
        var fixture = new Fixture { UnmountWritable = false };
        using var target = new FakeMigrationTarget(Guid.NewGuid());
        var result = LayoutMigration.Run("/gibt-es-nicht", target, fixture.Create().Acquire, _ => { });
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Status.Message, "SourceUnmountFailed");
    }
}
