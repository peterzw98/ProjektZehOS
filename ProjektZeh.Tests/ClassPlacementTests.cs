using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class ClassPlacementTests
{
    private const ThreadClass I = ThreadClass.Interactive, N = ThreadClass.Normal, B = ThreadClass.Background;
    private static readonly CpuTopology T4x1 = CpuTopology.Flat(4);
    private static readonly CpuTopology T2x2 = CpuTopology.FromApicIds([0, 1, 2, 3], 1, 2);

    private static int Select(CpuTopology topology, ThreadClass threadClass, int lastCpu, ThreadClass?[] running, int[]? queued = null, bool[]? allowed = null)
    {
        allowed ??= Array.ConvertAll(new bool[topology.CpuCount], _ => true);
        queued ??= new int[topology.CpuCount];
        return ClassPlacement.SelectCpu(topology, threadClass, lastCpu, allowed, running, queued);
    }

    [TestMethod]
    public void SelectCpu_LastCpuFree_StaysForCacheWarmth() =>
        Assert.AreEqual(2, Select(T4x1, N, 2, [null, null, null, null]));

    [TestMethod]
    public void SelectCpu_PrefersWholeFreeCore() =>
        Assert.AreEqual(2, Select(T2x2, N, -1, [N, null, null, null]));

    [TestMethod]
    public void SelectCpu_AvoidsSiblingOfInteractive() =>
        Assert.AreEqual(3, Select(T2x2, N, -1, [I, null, N, null]));

    [TestMethod]
    public void SelectCpu_InteractiveMayShareWithLowerClass() =>
        Assert.AreEqual(1, Select(T2x2, I, -1, [B, null, B, null]));

    [TestMethod]
    public void SelectCpu_LastCpuNextToInteractive_Moves() =>
        Assert.AreEqual(2, Select(T2x2, N, 1, [I, null, null, null]));

    [TestMethod]
    public void SelectCpu_NothingFree_LeastLoaded() =>
        Assert.AreEqual(1, Select(T4x1, N, -1, [N, N, N, N], [2, 0, 1, 3]));

    [TestMethod]
    public void SelectCpu_RespectsAllowed() =>
        Assert.AreEqual(3, Select(T4x1, N, -1, [null, null, null, null], allowed: [false, false, false, true]));

    [TestMethod]
    public void SelectCpu_NoneAllowed_Throws() =>
        Assert.ThrowsException<ArgumentException>(() => Select(T4x1, N, -1, [null, null, null, null], allowed: [false, false, false, false]));

    [TestMethod]
    public void StealOrder_SiblingThenPackageThenRest()
    {
        var topology = CpuTopology.FromApicIds([0, 1, 2, 3, 16, 17], 1, 4);
        var order = new int[5];
        Assert.AreEqual(5, ClassPlacement.StealOrder(topology, 0, order));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, order);
    }

    [TestMethod]
    public void StealOrder_FromSecondThread()
    {
        var topology = CpuTopology.FromApicIds([0, 1, 2, 3, 4, 5, 6, 7], 1, 3);
        var order = new int[7];
        Assert.AreEqual(7, ClassPlacement.StealOrder(topology, 5, order));
        CollectionAssert.AreEqual(new[] { 4, 0, 1, 2, 3, 6, 7 }, order);
    }

    // #674 V2: freiwilliger Umzug eines laufenden Threads weg vom belegten SMT-Geschwister.
    private const long Long = 1_000, Short = 1;

    private static int Move(CpuTopology topology, int cpu, ThreadClass?[] running, int[]? queued = null, bool[]? allowed = null,
        long onCpu = Long, long sinceMove = Long)
    {
        allowed ??= Array.ConvertAll(new bool[topology.CpuCount], _ => true);
        queued ??= new int[topology.CpuCount];
        return ClassPlacement.SelectMoveTarget(topology, cpu, allowed, running, queued, onCpu, sinceMove, minOnCpu: 100, minSinceMove: 100);
    }

    [TestMethod]
    public void SelectMoveTarget_SiblingBusy_WholeCoreFree_Moves() =>
        Assert.AreEqual(2, Move(T2x2, 0, [N, N, null, null]));

    [TestMethod]
    public void SelectMoveTarget_FromSecondCore_MovesToFirst() =>
        Assert.AreEqual(0, Move(T2x2, 3, [null, null, B, N]));

    [TestMethod]
    public void SelectMoveTarget_PrefersCoreWhollyAllowed()
    {
        // Drei Kerne {0,1} {2,3} {4,5}; CPU 0 nicht erlaubt (Hauptschleife): Kern {4,5} vor {0,1}, obwohl beide frei sind.
        var t3x2 = CpuTopology.FromApicIds([0, 1, 2, 3, 4, 5], 1, 3);
        Assert.AreEqual(4, Move(t3x2, 2, [null, null, N, N, null, null], allowed: [false, true, true, true, true, true]));
    }

    [TestMethod]
    public void SelectMoveTarget_OnlyPartlyAllowedCoreFree_TakesIt()
    {
        var t3x2 = CpuTopology.FromApicIds([0, 1, 2, 3, 4, 5], 1, 3);
        Assert.AreEqual(1, Move(t3x2, 2, [null, null, N, N, N, null], allowed: [false, true, true, true, true, true]));
    }

    [TestMethod]
    public void SelectMoveTarget_SiblingIdle_Stays() =>
        Assert.AreEqual(-1, Move(T2x2, 0, [N, null, null, null]));

    [TestMethod]
    public void SelectMoveTarget_NoWholeFreeCore_Stays() =>
        Assert.AreEqual(-1, Move(T2x2, 0, [N, N, N, null]));

    [TestMethod]
    public void SelectMoveTarget_FullLoad_Stays() =>
        Assert.AreEqual(-1, Move(T2x2, 0, [N, N, N, N]));

    [TestMethod]
    public void SelectMoveTarget_QueuedOnFreeCore_Stays() =>
        Assert.AreEqual(-1, Move(T2x2, 0, [N, N, null, null], queued: [0, 0, 0, 1]));

    [TestMethod]
    public void SelectMoveTarget_FreeCoreNotAllowed_Stays() =>
        Assert.AreEqual(-1, Move(T2x2, 0, [N, N, null, null], allowed: [true, true, false, false]));

    [TestMethod]
    public void SelectMoveTarget_OnlyOneCpuOfFreeCoreAllowed_MovesThere() =>
        Assert.AreEqual(3, Move(T2x2, 0, [N, N, null, null], allowed: [true, true, false, true]));

    [TestMethod]
    public void SelectMoveTarget_NoSmt_Stays() =>
        Assert.AreEqual(-1, Move(T4x1, 0, [N, N, null, null]));

    [TestMethod]
    public void SelectMoveTarget_NotAQuantumOnCpuYet_Stays() =>
        Assert.AreEqual(-1, Move(T2x2, 0, [N, N, null, null], onCpu: Short));

    [TestMethod]
    public void SelectMoveTarget_MovedRecently_Stays() =>
        Assert.AreEqual(-1, Move(T2x2, 0, [N, N, null, null], sinceMove: Short));

    [TestMethod]
    public void SelectMoveTarget_WrongLengths_Throws() =>
        Assert.ThrowsException<ArgumentException>(() => ClassPlacement.SelectMoveTarget(T2x2, 0, new bool[3], new ThreadClass?[4], new int[4], Long, Long, 100, 100));
}
