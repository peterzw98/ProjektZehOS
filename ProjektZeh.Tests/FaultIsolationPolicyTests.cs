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

    // E6-G (#433): Die Hauptschleife haelt den aufgegebenen Worker, bis Cosmos ihn abgeraeumt hat.
    [TestMethod]
    public void PendingReap_HoldsIdUntilFinished()
    {
        var reap = new PendingReap();
        Assert.IsFalse(reap.HasPending);
        reap.Begin(7);
        Assert.IsTrue(reap.HasPending);
        Assert.AreEqual(7, reap.ThreadId);
        Assert.AreEqual(7, reap.Finish());
        Assert.IsFalse(reap.HasPending);
        Assert.AreEqual(-1, reap.ThreadId);
    }

    [TestMethod]
    public void PendingReap_WarnsOnceAtThreshold()
    {
        var reap = new PendingReap();
        reap.Begin(3);
        int warnings = 0;
        for (int i = 0; i < PendingReap.WarnAfterRounds * 2; i++)
        {
            if (reap.Next(ReapOutcome.StillOnCpu) == ReapStep.WaitAndWarn) warnings++;
        }
        Assert.AreEqual(1, warnings);
        // Ein neuer Versuch zaehlt von vorn.
        reap.Finish();
        reap.Begin(4);
        for (int i = 1; i < PendingReap.WarnAfterRounds; i++) Assert.AreEqual(ReapStep.Wait, reap.Next(ReapOutcome.StillOnCpu));
        Assert.AreEqual(ReapStep.WaitAndWarn, reap.Next(ReapOutcome.StillOnCpu));
    }

    // Review laser #576, Befund 3 und 5: Weiter (Ressourcen schliessen, Ersatzworker) nur, wenn der Worker nie wieder laeuft.
    [TestMethod]
    [DataRow((int)ReapOutcome.Reaped)]
    [DataRow((int)ReapOutcome.NotFound)]
    [DataRow((int)ReapOutcome.Failed)]
    public void PendingReap_ProceedsWhenWorkerCannotRun(int outcome)
    {
        var reap = new PendingReap();
        reap.Begin(5);
        Assert.AreEqual(ReapStep.Proceed, reap.Next((ReapOutcome)outcome));
    }

    [TestMethod]
    [DataRow((int)ReapOutcome.StillOnCpu)]
    [DataRow((int)ReapOutcome.NotAbandoned)]
    public void PendingReap_WaitsWhileWorkerMayRun(int outcome)
    {
        var reap = new PendingReap();
        reap.Begin(5);
        Assert.AreEqual(ReapStep.Wait, reap.Next((ReapOutcome)outcome));
        Assert.IsTrue(reap.HasPending);
        Assert.AreEqual(5, reap.ThreadId);
    }

    // Wechselnde Wartegruende zaehlen gemeinsam; nach dem Warten geht es mit Reaped normal weiter.
    [TestMethod]
    public void PendingReap_WaitThenReaped()
    {
        var reap = new PendingReap();
        reap.Begin(9);
        int warnings = 0;
        for (int i = 0; i < PendingReap.WarnAfterRounds; i++)
        {
            var outcome = i % 2 == 0 ? ReapOutcome.StillOnCpu : ReapOutcome.NotAbandoned;
            if (reap.Next(outcome) == ReapStep.WaitAndWarn) warnings++;
        }
        Assert.AreEqual(1, warnings);
        Assert.AreEqual(ReapStep.Proceed, reap.Next(ReapOutcome.Reaped));
        Assert.AreEqual(9, reap.Finish());
        Assert.IsFalse(reap.HasPending);
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

    // #582: Freigabe des nativen Laufs nach einer Isolation.

    [TestMethod]
    public void NativeRelease_Wasm3EndsArena() =>
        Assert.AreEqual(NativeReleasePlan.EndArena, NativeReleasePolicy.Plan(NativeEngine.Wasm3, true, AotReclaim.NotActive));

    [TestMethod]
    public void NativeRelease_Wasm3WithoutArenaLeaks() =>
        Assert.AreEqual(NativeReleasePlan.Leaked, NativeReleasePolicy.Plan(NativeEngine.Wasm3, false, AotReclaim.NotActive));

    [TestMethod]
    public void NativeRelease_ReleasedAotEndsArenaForTheRest() =>
        Assert.AreEqual(NativeReleasePlan.EndArena, NativeReleasePolicy.Plan(NativeEngine.Aot, true, AotReclaim.Released));

    [TestMethod]
    public void NativeRelease_ReleasedAotWithoutArenaIsFreed() =>
        Assert.AreEqual(NativeReleasePlan.Freed, NativeReleasePolicy.Plan(NativeEngine.Aot, false, AotReclaim.Released));

    [TestMethod]
    public void NativeRelease_KeptAotNeverEndsArena()
    {
        // WAMRs globale Listen zeigten sonst auf freigegebene Bloecke.
        Assert.AreEqual(NativeReleasePlan.KeepArena, NativeReleasePolicy.Plan(NativeEngine.Aot, true, AotReclaim.Kept));
        Assert.AreEqual(NativeReleasePlan.Leaked, NativeReleasePolicy.Plan(NativeEngine.Aot, false, AotReclaim.Kept));
    }

    [TestMethod]
    public void NativeRelease_AotBeforeWamrStartEndsArena()
    {
        Assert.AreEqual(NativeReleasePlan.EndArena, NativeReleasePolicy.Plan(NativeEngine.Aot, true, AotReclaim.NotActive));
        Assert.AreEqual(NativeReleasePlan.Nothing, NativeReleasePolicy.Plan(NativeEngine.Aot, false, AotReclaim.NotActive));
    }

    [TestMethod]
    public void NativeRelease_NoNativeRunLeavesNothing() =>
        Assert.AreEqual(NativeReleasePlan.Nothing, NativeReleasePolicy.Plan(NativeEngine.None, false, AotReclaim.NotActive));

    [TestMethod]
    public void NativeRelease_OnlyKeepAndLeakLeaveMemory()
    {
        Assert.IsTrue(NativeReleasePolicy.LeavesNativeMemory(NativeReleasePlan.KeepArena));
        Assert.IsTrue(NativeReleasePolicy.LeavesNativeMemory(NativeReleasePlan.Leaked));
        Assert.IsFalse(NativeReleasePolicy.LeavesNativeMemory(NativeReleasePlan.EndArena));
        Assert.IsFalse(NativeReleasePolicy.LeavesNativeMemory(NativeReleasePlan.Freed));
        Assert.IsFalse(NativeReleasePolicy.LeavesNativeMemory(NativeReleasePlan.Nothing));
    }

    [TestMethod]
    public void Budget_CleanIsolationsNeverRetire()
    {
        // Vor #582 war nach drei Isolationen Schluss, auch wenn jede alles freigab.
        var budget = new IsolationBudget();
        for (int i = 0; i < 100; i++)
            Assert.IsFalse(budget.Record(IsolationBudget.LeavesResources(ReapOutcome.Reaped, NativeReleasePlan.EndArena, true)));
        Assert.AreEqual(0, budget.Unclean);
    }

    [TestMethod]
    public void Budget_RetiresAtThirdUncleanIsolation()
    {
        var budget = new IsolationBudget();
        Assert.IsFalse(budget.Record(true));
        Assert.IsFalse(budget.Record(false));
        Assert.IsFalse(budget.Record(true));
        Assert.IsTrue(budget.Record(true));
        Assert.AreEqual(IsolationBudget.MaximumUnclean, budget.Unclean);
    }

    [TestMethod]
    public void Budget_UnreapedThreadOrKeptMemoryCounts()
    {
        foreach (ReapOutcome reap in new[] { ReapOutcome.NotFound, ReapOutcome.Failed })
            Assert.IsTrue(IsolationBudget.LeavesResources(reap, NativeReleasePlan.EndArena, true), reap.ToString());
        Assert.IsTrue(IsolationBudget.LeavesResources(ReapOutcome.Reaped, NativeReleasePlan.KeepArena, true));
        Assert.IsTrue(IsolationBudget.LeavesResources(ReapOutcome.Reaped, NativeReleasePlan.Leaked, true));
        Assert.IsFalse(IsolationBudget.LeavesResources(ReapOutcome.Reaped, NativeReleasePlan.Freed, true));
        Assert.IsFalse(IsolationBudget.LeavesResources(ReapOutcome.Reaped, NativeReleasePlan.Nothing, true));
    }

    [TestMethod]
    public void Budget_UncleanRunResourcesCount()
    {
        // Review #582, P3-1: Schliessen Dateien oder Sockets nicht sauber, zaehlt die Isolation, auch wenn Thread, Stack und
        // nativer Speicher frei sind; drei solche Fehler fuehren wie vor #582 in den Ruhestand.
        Assert.IsTrue(IsolationBudget.LeavesResources(ReapOutcome.Reaped, NativeReleasePlan.EndArena, false));
        Assert.IsTrue(IsolationBudget.LeavesResources(ReapOutcome.Reaped, NativeReleasePlan.Freed, false));
        var budget = new IsolationBudget();
        bool retired = false;
        for (int i = 0; i < IsolationBudget.MaximumUnclean; i++)
            retired = budget.Record(IsolationBudget.LeavesResources(ReapOutcome.Reaped, NativeReleasePlan.EndArena, false));
        Assert.IsTrue(retired);
    }
}
