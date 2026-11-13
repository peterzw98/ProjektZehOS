using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.TaskManager;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class TaskManagerTests
{
    [TestMethod]
    public void Sample_FirstCall_EstablishesBaselineWithoutInventedCpu()
    {
        var metrics = new SystemMetrics();
        metrics.RecordWork(500000);
        metrics.Sample(10000, 123, 456);
        Assert.IsFalse(metrics.HasCpuSample);
        Assert.AreEqual(10000L, metrics.UptimeMilliseconds);
        Assert.AreEqual(123L, metrics.ManagedBytes);
        metrics.RecordWork(250000);
        metrics.Sample(11000, 234, 567);
        Assert.AreEqual(25, metrics.CpuPercent);
        Assert.AreEqual(1, metrics.HistoryCount);
    }

    [TestMethod]
    public void Sample_SubsecondIntervals_AccumulateWorkUntilNextSecond()
    {
        var metrics = new SystemMetrics();
        metrics.Sample(0, 0, 0);
        metrics.RecordWork(100000);
        metrics.Sample(500, 0, 0);
        Assert.IsFalse(metrics.HasCpuSample);
        metrics.RecordWork(150000);
        metrics.Sample(1000, 0, 0);
        Assert.AreEqual(25, metrics.CpuPercent);
        metrics.Sample(2000, 0, 0);
        Assert.AreEqual(0, metrics.CpuPercent);
    }

    [TestMethod]
    public void Sample_ClockMovesBack_ResetsIntervalAndDiscardsOldWork()
    {
        var metrics = new SystemMetrics();
        metrics.Sample(1000, 0, 0);
        metrics.RecordWork(900000);
        metrics.Sample(500, 0, 0);
        metrics.RecordWork(500000);
        metrics.Sample(1500, 0, 0);
        Assert.AreEqual(50, metrics.CpuPercent);
    }

    [TestMethod]
    public void Sample_ExtremeWork_ClampsCpuAndBoundsHistory()
    {
        var metrics = new SystemMetrics();
        metrics.Sample(0, 0, 0);
        metrics.RecordWork(long.MaxValue);
        metrics.RecordWork(long.MaxValue);
        metrics.RecordWork(-1);
        metrics.Sample(1000, 0, 0);
        Assert.AreEqual(100, metrics.CpuPercent);
        for (int index = 2; index <= 70; index++) metrics.Sample(index * 1000, 0, 0);
        Assert.AreEqual(60, metrics.HistoryCount);
        Assert.AreEqual(0, metrics.GetHistory(0));
        Assert.AreEqual(0, metrics.GetHistory(59));
    }

    [TestMethod]
    public void Sample_PhysicalMemory_RejectsImpossibleFreeValue()
    {
        var metrics = new SystemMetrics();
        metrics.Sample(0, -1, -2, 100, 101);
        Assert.AreEqual(0L, metrics.ManagedBytes);
        Assert.IsFalse(metrics.HasPhysicalMemory);
        metrics.Sample(1000, 1, 2, 100, 40);
        Assert.IsTrue(metrics.HasPhysicalMemory);
        Assert.AreEqual(40UL, metrics.FreeMemory);
    }
}
