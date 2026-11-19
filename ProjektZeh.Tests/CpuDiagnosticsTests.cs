using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Apps.TaskManager;
using ProjektZeh.Scheduling;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class CpuDiagnosticsTests
{
    [TestMethod]
    public void FormatLine_PrintsRunningThreadWaitingAndLoad()
    {
        string line = CpuDiagnostics.FormatLine(2, 7, ThreadClass.Background, [1, 2, 3], 25, 100);

        Assert.AreEqual("CPU 2: l\u00e4uft 7 (Hintergrund), wartend Interaktiv=1 Normal=2 Hintergrund=3, Last 25 %", line);
    }

    [TestMethod]
    public void FormatLine_WithoutRunningThread_NamesIdle()
    {
        StringAssert.Contains(CpuDiagnostics.FormatLine(0, null, ThreadClass.Normal, [0, 0, 0], 0, 0), "l\u00e4uft Leerlauf,", StringComparison.Ordinal);
    }

    [TestMethod]
    public void LoadPercent_ZeroTotalIsZeroAndOverflowIsClamped()
    {
        Assert.AreEqual(0, CpuDiagnostics.LoadPercent(5, 0));
        Assert.AreEqual(100, CpuDiagnostics.LoadPercent(300, 100));
        Assert.AreEqual(25, CpuDiagnostics.LoadPercent(2_500_000, 10_000_000));
        Assert.AreEqual(0, CpuDiagnostics.LoadPercent(0, 10_000_000));
    }

    [TestMethod]
    public void SystemMetrics_CpuLoads_UseNanosecondsSinceLastQuery()
    {
        var metrics = new SystemMetrics();
        metrics.SampleCpuLoads([10, 0], 100);
        Assert.AreEqual(2, metrics.CpuLoads.Length);
        Assert.AreEqual(10, metrics.CpuLoads[0]);

        metrics.SampleCpuLoads([60, 0], 200);

        Assert.AreEqual(50, metrics.CpuLoads[0]);
        Assert.AreEqual(0, metrics.CpuLoads[1]);
    }
}
