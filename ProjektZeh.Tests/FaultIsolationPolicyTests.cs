using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime;

namespace ProjektZeh.Tests;

/// <summary>Roadmap 12.4-I1: Nur Gastcode des Wasm-Workers ohne gehaltene Kernelsperre wird isoliert, sonst Halt.</summary>
[TestClass]
public sealed class FaultIsolationPolicyTests
{
    private const int Worker = 4;

    [TestMethod]
    public void Isolates_WorkerInGuestCodeWithoutLocks() =>
        Assert.AreEqual(IsolationDecision.Isolate, FaultIsolationPolicy.Decide(Worker, Worker, true, 0, false, false));

    [TestMethod]
    public void Halts_OtherThread() =>
        Assert.AreEqual(IsolationDecision.NotWorker, FaultIsolationPolicy.Decide(3, Worker, true, 0, false, false));

    [TestMethod]
    public void Halts_WhenNoWorkerRegistered() =>
        Assert.AreEqual(IsolationDecision.NotWorker, FaultIsolationPolicy.Decide(-1, -1, true, 0, false, false));

    [TestMethod]
    public void Halts_WorkerOutsideNativeRun() =>
        Assert.AreEqual(IsolationDecision.OutsideGuest, FaultIsolationPolicy.Decide(Worker, Worker, false, 0, false, false));

    [TestMethod]
    public void Halts_InsideHostFunction() =>
        Assert.AreEqual(IsolationDecision.InHostFunction, FaultIsolationPolicy.Decide(Worker, Worker, true, 1, false, false));

    [TestMethod]
    public void Halts_UnderStorageLock() =>
        Assert.AreEqual(IsolationDecision.HoldsStorageLock, FaultIsolationPolicy.Decide(Worker, Worker, true, 0, true, false));

    [TestMethod]
    public void Halts_UnderAppStorageGate() =>
        Assert.AreEqual(IsolationDecision.HoldsAppStorageGate, FaultIsolationPolicy.Decide(Worker, Worker, true, 0, false, true));

    [TestMethod]
    public void Quarantine_RemembersOnlyAddedThreads()
    {
        var quarantine = new ThreadQuarantine(8);
        Assert.IsTrue(quarantine.Add(4));
        Assert.IsTrue(quarantine.Contains(4));
        Assert.IsFalse(quarantine.Contains(5));
    }

    [TestMethod]
    public void Quarantine_RejectsIdsOutsideTable()
    {
        var quarantine = new ThreadQuarantine(8);
        Assert.IsFalse(quarantine.Add(8));
        Assert.IsFalse(quarantine.Add(-1));
        Assert.IsFalse(quarantine.Contains(8));
    }
}
