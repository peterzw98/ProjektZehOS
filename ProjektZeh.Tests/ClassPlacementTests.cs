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
}
