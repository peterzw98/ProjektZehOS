using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AppStorageGateLeaseTests
{
    [TestCleanup]
    public void Cleanup() => AppStorageGate.Release();

    [TestMethod]
    public void MigrationUsesTheSameGateAsAppOperationsAndReleaseCannotUnlockIt()
    {
        AppStorageGate.Release();
        using var migration = AppStorageGate.TryLockSource();
        Assert.IsNotNull(migration);
        AppStorageGate.Release();
        Assert.IsTrue(AppStorageGate.IsBlocked);
        Assert.IsTrue(AppStorageGate.IsMigrationLocked);
        Assert.IsNull(AppStorageGate.TryBeginWrite());
        Assert.IsFalse(AppStorageGate.TryBlock());
        Assert.AreEqual(-1, AppStorageGate.Run(() => throw new AssertFailedException("Kein App-Schreiben waehrend Migration."), -1));
    }

    [TestMethod]
    public void ExistingVolumeBlockCannotBeBorrowedAsMigrationOwnership()
    {
        AppStorageGate.Block();
        Assert.IsNull(AppStorageGate.TryLockSource());
        Assert.IsFalse(AppStorageGate.IsMigrationLocked);
        AppStorageGate.Release();
        using var migration = AppStorageGate.TryLockSource();
        Assert.IsNotNull(migration);
    }

    [TestMethod]
    public void ActiveAppOperationPreventsMigration()
    {
        AppStorageGate.Release();
        Assert.AreEqual(7, AppStorageGate.Run(() =>
        {
            Assert.IsNull(AppStorageGate.TryLockSource());
            return 7;
        }, -1));
        using var migration = AppStorageGate.TryLockSource();
        Assert.IsNotNull(migration);
    }

    [TestMethod]
    public void UnmountCanRefuseWithoutWaitingForAnExistingWriter()
    {
        AppStorageGate.Release();
        using var writer = AppStorageGate.TryBeginWrite();
        Assert.IsNotNull(writer);
        bool? acquired = null;
        var thread = new Thread(() => acquired = AppStorageGate.TryBlock());
        thread.Start();
        Assert.IsTrue(thread.Join(2000), "Ein belegter Schreiber darf den Aufrufer nicht warten lassen.");
        Assert.AreEqual(false, acquired);
        Assert.IsFalse(AppStorageGate.IsBlocked);
    }
}
