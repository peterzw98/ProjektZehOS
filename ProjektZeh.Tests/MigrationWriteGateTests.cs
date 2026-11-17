using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class MigrationWriteGateTests
{
    [TestMethod]
    public void ExistingWritersPreventMigrationUntilTheirLastLeaseEnds()
    {
        var gate = new MigrationWriteGate();
        var first = gate.TryBeginWrite();
        var second = gate.TryBeginWrite();
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsNull(gate.TryLockSource());
        Assert.IsFalse(gate.IsLocked);
        first.Dispose();
        first.Dispose();
        Assert.IsNull(gate.TryLockSource());
        second.Dispose();
        using var migration = gate.TryLockSource();
        Assert.IsNotNull(migration);
        Assert.IsTrue(gate.IsLocked);
    }

    [TestMethod]
    public void MigrationRejectsNewWritersAndAnotherMigrationUntilReleased()
    {
        var gate = new MigrationWriteGate();
        var migration = gate.TryLockSource();
        Assert.IsNotNull(migration);
        Assert.IsNull(gate.TryBeginWrite());
        Assert.IsNull(gate.TryLockSource());
        migration.Dispose();
        migration.Dispose();
        Assert.IsFalse(gate.IsLocked);
        using var writer = gate.TryBeginWrite();
        Assert.IsNotNull(writer);
        Assert.IsNull(gate.TryLockSource());
    }

    [TestMethod]
    public void SimultaneousWriterAndMigrationNeverAcquireTogether()
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var gate = new MigrationWriteGate();
            using var barrier = new Barrier(2);
            IDisposable? writer = null;
            IDisposable? migration = null;
            var first = new Thread(() => { barrier.SignalAndWait(); writer = gate.TryBeginWrite(); });
            var second = new Thread(() => { barrier.SignalAndWait(); migration = gate.TryLockSource(); });
            first.Start();
            second.Start();
            Assert.IsTrue(first.Join(2000));
            Assert.IsTrue(second.Join(2000));
            try { Assert.IsTrue((writer != null) != (migration != null), "Genau ein konkurrierender Erwerb darf gelingen."); }
            finally { writer?.Dispose(); migration?.Dispose(); }
            using var after = gate.TryLockSource();
            Assert.IsNotNull(after);
        }
    }

    [TestMethod]
    public void AWriterCanFinishNestedMetadataWritesWhileACompatibilityBlockWaits()
    {
        var gate = new MigrationWriteGate();
        var writer = gate.TryBeginWrite();
        Assert.IsNotNull(writer);
        var blocked = new Thread(gate.Block);
        blocked.Start();
        try
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => gate.IsLocked, 2000));
            using var metadata = gate.TryBeginWrite();
            Assert.IsNotNull(metadata, "Der bestehende Schreiber muss seine verschachtelte Metadatensicherung abschliessen koennen.");
            IDisposable? foreign = null;
            var other = new Thread(() => foreign = gate.TryBeginWrite());
            other.Start();
            Assert.IsTrue(other.Join(2000));
            try { Assert.IsNull(foreign); }
            finally { foreign?.Dispose(); }
        }
        finally
        {
            writer.Dispose();
            Assert.IsTrue(blocked.Join(2000));
            gate.ReleaseBlock();
        }
    }

    [TestMethod]
    public void LeaseDisposalOnAnotherThreadReleasesTheOriginalWriter()
    {
        var gate = new MigrationWriteGate();
        var writer = gate.TryBeginWrite();
        Assert.IsNotNull(writer);
        var other = new Thread(writer.Dispose);
        other.Start();
        Assert.IsTrue(other.Join(2000));
        using var migration = gate.TryLockSource();
        Assert.IsNotNull(migration);
    }
}
