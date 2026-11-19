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
        Assert.AreEqual(IsolationDecision.Isolate, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, true, true, 0, false, false));

    [TestMethod]
    public void Halts_OtherThread() =>
        Assert.AreEqual(IsolationDecision.NotWorker, FaultIsolationPolicy.Decide(PageFault, 3, Worker, true, true, 0, false, false));

    [TestMethod]
    public void Halts_WhenNoWorkerRegistered() =>
        Assert.AreEqual(IsolationDecision.NotWorker, FaultIsolationPolicy.Decide(PageFault, -1, -1, true, true, 0, false, false));

    [TestMethod]
    public void Halts_WorkerOutsideNativeRun() =>
        Assert.AreEqual(IsolationDecision.OutsideGuest, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, false, true, 0, false, false));

    [TestMethod]
    public void Halts_InsideHostFunction() =>
        Assert.AreEqual(IsolationDecision.InHostFunction, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, true, true, 1, false, false));

    [TestMethod]
    public void Halts_UnderStorageLock() =>
        Assert.AreEqual(IsolationDecision.HoldsStorageLock, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, true, true, 0, true, false));

    [TestMethod]
    public void Halts_UnderAppStorageGate() =>
        Assert.AreEqual(IsolationDecision.HoldsAppStorageGate, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, true, true, 0, false, true));

    // E6-C.10 (#390): Ein Interrupt-Handler laeuft maskiert auf dem Stack des Workers, den er im Gastcode unterbrochen hat;
    // Cosmos meldet dann den Worker als Thread. Eine Ausnahme dort ist Kernelcode und haelt an, auch wenn alles andere passt.
    [TestMethod]
    [DataRow(0L)]
    [DataRow(6L)]
    [DataRow(13L)]
    [DataRow(14L)]
    public void Halts_ProgramFaultInInterruptHandlerOverGuestCode(long vector) =>
        Assert.AreEqual(IsolationDecision.InterruptsMasked, FaultIsolationPolicy.Decide(vector, Worker, Worker, true, false, 0, false, false));

    // Die maskierte Lage entscheidet vor Hosttiefe und Sperren: Der Handler kann den Worker auch in einer Hostfunktion treffen.
    [TestMethod]
    public void Halts_InterruptsMaskedBeforeHostDepthAndLocks() =>
        Assert.AreEqual(IsolationDecision.InterruptsMasked, FaultIsolationPolicy.Decide(PageFault, Worker, Worker, true, false, 1, true, true));

    // Kein Programmfehler bleibt kein Programmfehler, auch maskiert (Reihenfolge der Gruende unveraendert).
    [TestMethod]
    public void Halts_NmiWithInterruptsMaskedAsNotProgramFault() =>
        Assert.AreEqual(IsolationDecision.NotProgramFault, FaultIsolationPolicy.Decide(2, Worker, Worker, true, false, 0, false, false));

    [TestMethod]
    [DataRow(0L)]
    [DataRow(6L)]
    [DataRow(13L)]
    [DataRow(14L)]
    public void Isolates_ProgramFaultVectors(long vector) =>
        Assert.AreEqual(IsolationDecision.Isolate, FaultIsolationPolicy.Decide(vector, Worker, Worker, true, true, 0, false, false));

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
        Assert.AreEqual(IsolationDecision.NotProgramFault, FaultIsolationPolicy.Decide(vector, Worker, Worker, true, true, 0, false, false));

    // Halt ohne frischen Ausnahmekontext (etwa ein anderer Panic-Pfad) darf nie isolieren.
    [TestMethod]
    public void Halts_WithoutRecordedVector() =>
        Assert.AreEqual(IsolationDecision.NotProgramFault,
            FaultIsolationPolicy.Decide(FaultIsolationPolicy.UnknownVector, Worker, Worker, true, true, 0, false, false));

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

    // E6-C.9 (#390): Die Hauptschleife uebernimmt den Worker erst, wenn seine CPU ihn nicht mehr fuehrt.
    [TestMethod]
    public void Handoff_EmptyYieldsNothing()
    {
        var handoff = new IsolationHandoff();
        Assert.AreEqual(-1, handoff.PendingCpu);
        Assert.AreEqual(-1, handoff.TryTake(IsolationHandoff.NoThread));
    }

    [TestMethod]
    public void Handoff_WaitsWhileWorkerStillRunsOnItsCpu()
    {
        var handoff = new IsolationHandoff();
        handoff.Stage(Worker, 2);
        Assert.AreEqual(2, handoff.PendingCpu);
        // Gegenprobe: Cosmos hat den Wechsel noch nicht angelegt oder haelt an; nichts darf aufgeraeumt werden.
        Assert.AreEqual(-1, handoff.TryTake(Worker));
        Assert.AreEqual(-1, handoff.TryTake(Worker));
        Assert.AreEqual(2, handoff.PendingCpu);
    }

    [TestMethod]
    public void Handoff_DeliversOnceAfterSwitch()
    {
        var handoff = new IsolationHandoff();
        handoff.Stage(Worker, 1);
        Assert.AreEqual(Worker, handoff.TryTake(7));
        Assert.AreEqual(-1, handoff.PendingCpu);
        Assert.AreEqual(-1, handoff.TryTake(7));
    }

    [TestMethod]
    public void Handoff_DeliversWhenCpuRunsNoThread()
    {
        var handoff = new IsolationHandoff();
        handoff.Stage(Worker, 0);
        Assert.AreEqual(Worker, handoff.TryTake(IsolationHandoff.NoThread));
    }

    [TestMethod]
    public void Handoff_NextIsolationAfterTake()
    {
        var handoff = new IsolationHandoff();
        handoff.Stage(Worker, 1);
        Assert.AreEqual(Worker, handoff.TryTake(0));
        handoff.Stage(9, 3);
        Assert.AreEqual(3, handoff.PendingCpu);
        Assert.AreEqual(-1, handoff.TryTake(9));
        Assert.AreEqual(9, handoff.TryTake(0));
    }
}
