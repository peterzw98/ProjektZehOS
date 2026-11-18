using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime;

namespace ProjektZeh.Tests;

/// <summary>Roadmap 12.4-I1: Nur Gastcode des Wasm-Workers ohne gehaltene Kernelsperre wird isoliert, sonst Halt.</summary>
[TestClass]
public sealed class FaultIsolationPolicyTests
{
    private const int Worker = 4;
    private const long PageFault = 14;

    [TestMethod]
    public void Isolates_WorkerInGuestCodeWithoutLocks() =>
        Assert.AreEqual(IsolationDecision.Isolate, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, true, 0, false, false));

    [TestMethod]
    public void Halts_OtherThread() =>
        Assert.AreEqual(IsolationDecision.NotWorker, FaultIsolationPolicy.Decide(PageFault, 3, Worker, true, 0, false, false));

    [TestMethod]
    public void Halts_WhenNoWorkerRegistered() =>
        Assert.AreEqual(IsolationDecision.NotWorker, FaultIsolationPolicy.Decide(PageFault, -1, -1, true, 0, false, false));

    [TestMethod]
    public void Halts_WorkerOutsideNativeRun() =>
        Assert.AreEqual(IsolationDecision.OutsideGuest, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, false, 0, false, false));

    [TestMethod]
    public void Halts_InsideHostFunction() =>
        Assert.AreEqual(IsolationDecision.InHostFunction, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, true, 1, false, false));

    [TestMethod]
    public void Halts_UnderStorageLock() =>
        Assert.AreEqual(IsolationDecision.HoldsStorageLock, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, true, 0, true, false));

    [TestMethod]
    public void Halts_UnderAppStorageGate() =>
        Assert.AreEqual(IsolationDecision.HoldsAppStorageGate, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, true, 0, false, true));

    [TestMethod]
    [DataRow(0L)]
    [DataRow(6L)]
    [DataRow(13L)]
    [DataRow(14L)]
    public void Isolates_ProgramFaultVectors(long vector) =>
        Assert.AreEqual(IsolationDecision.Isolate, FaultIsolationPolicy.Decide(vector, Worker, Worker, true, 0, false, false));

    // NMI (2), Double Fault (8), Machine Check (18) und die uebrigen Vektoren sind keine App-Fehler.
    [TestMethod]
    [DataRow(1L)]
    [DataRow(2L)]
    [DataRow(3L)]
    [DataRow(8L)]
    [DataRow(12L)]
    [DataRow(18L)]
    [DataRow(19L)]
    [DataRow(32L)]
    public void Halts_OtherVectorsInGuestCode(long vector) =>
        Assert.AreEqual(IsolationDecision.NotProgramFault, FaultIsolationPolicy.Decide(vector, Worker, Worker, true, 0, false, false));

    // Halt ohne frischen Ausnahmekontext (etwa ein anderer Panic-Pfad) darf nie isolieren.
    [TestMethod]
    public void Halts_WithoutRecordedVector() =>
        Assert.AreEqual(IsolationDecision.NotProgramFault,
            FaultIsolationPolicy.Decide(FaultIsolationPolicy.UnknownVector, Worker, Worker, true, 0, false, false));

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
