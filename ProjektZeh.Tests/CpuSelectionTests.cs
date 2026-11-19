using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class CpuSelectionTests
{
    [TestMethod]
    public void Workers_FourCpus_AllButZero() => AssertWorkers(CpuTopology.Flat(4), null, false, 1, 2, 3);

    [TestMethod]
    public void Workers_SingleCpu_FallsBackToZero() => AssertWorkers(CpuTopology.Flat(1), null, false, 0);

    [TestMethod]
    public void Workers_LimitOne_OnlyZero() => AssertWorkers(CpuTopology.Flat(4), 1, false, 0);

    [TestMethod]
    public void Workers_LimitThree() => AssertWorkers(CpuTopology.Flat(8), 3, false, 1, 2);

    [TestMethod]
    public void Workers_SmtOff_DropsSecondThreads() =>
        AssertWorkers(CpuTopology.FromApicIds([0, 1, 2, 3, 4, 5, 6, 7], 1, 3), null, true, 2, 4, 6);

    [TestMethod]
    public void Workers_SmtOff_TwoCoresTwoThreads_KeepsOneWorkerCpu() =>
        AssertWorkers(CpuTopology.FromApicIds([0, 1, 2, 3], 1, 2), null, true, 2);

    [TestMethod]
    public void Workers_SmtOff_WithoutSiblings_ChangesNothing() => AssertWorkers(CpuTopology.Flat(4), null, true, 1, 2, 3);

    [TestMethod]
    public void Workers_TwoCpus() => AssertWorkers(CpuTopology.Flat(2), null, false, 1);

    private static void AssertWorkers(CpuTopology topology, int? limit, bool smtOff, params int[] expected)
    {
        bool[] workers = CpuSelection.Workers(topology, limit, smtOff);
        Assert.AreEqual(topology.CpuCount, workers.Length);
        CollectionAssert.AreEqual(expected, Enumerable.Range(0, workers.Length).Where(cpu => workers[cpu]).ToArray());
    }
}
