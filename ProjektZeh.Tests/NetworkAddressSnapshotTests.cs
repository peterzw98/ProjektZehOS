using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Network;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class NetworkAddressSnapshotTests
{
    [TestMethod]
    public void FreeGateReadsFreshAndPublishes()
    {
        object gate = new();
        var snapshot = new NetworkAddressSnapshot(gate);
        Assert.AreEqual("10.0.2.15", snapshot.Read(() => { Assert.IsTrue(Monitor.IsEntered(gate)); return "10.0.2.15"; }));
        Assert.IsFalse(Monitor.IsEntered(gate));
        Assert.AreEqual("10.0.2.15", snapshot.Published);
    }

    [TestMethod]
    public void BusyGateReturnsLastPublishedWithoutTouchingTheStack()
    {
        object gate = new();
        var snapshot = new NetworkAddressSnapshot(gate);
        snapshot.Read(() => "10.0.2.15");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } });
        holder.Start();
        held.Wait();
        try
        {
            Assert.AreEqual("10.0.2.15", snapshot.Read(() => throw new AssertFailedException("Stapel ohne Gate gelesen")));
        }
        finally { release.Set(); holder.Join(); }
    }

    [TestMethod]
    public void WriterUnderGatePublishesForLaterBusyReads()
    {
        object gate = new();
        var snapshot = new NetworkAddressSnapshot(gate);
        lock (gate)
        {
            // Der Schreiber haelt Gate (net dhcp); rekursives Lesen auf demselben Thread ist frisch.
            Assert.AreEqual("192.168.1.5", snapshot.PublishLocked(() => "192.168.1.5"));
            Assert.AreEqual("192.168.1.5", snapshot.Read(() => "192.168.1.5"));
        }
        Assert.AreEqual("192.168.1.5", snapshot.Published);
        Assert.ThrowsException<InvalidOperationException>(() => snapshot.PublishLocked(() => null));
    }

    [TestMethod]
    public void FailedReadReleasesGateAndKeepsLastValue()
    {
        object gate = new();
        var snapshot = new NetworkAddressSnapshot(gate);
        snapshot.Read(() => "10.0.2.15");
        Assert.ThrowsException<InvalidOperationException>(() => snapshot.Read(() => throw new InvalidOperationException("Liste geaendert")));
        Assert.IsFalse(Monitor.IsEntered(gate));
        Assert.AreEqual("10.0.2.15", snapshot.Published);
        Assert.IsNull(snapshot.Read(() => null));
        Assert.IsNull(snapshot.Published);
    }
}
