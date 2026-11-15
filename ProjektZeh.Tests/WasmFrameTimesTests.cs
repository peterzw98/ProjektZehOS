using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WasmFrameTimesTests
{
    [TestMethod]
    public void ReportsDistributionOnlyAfterWindow()
    {
        var times = new WasmFrameTimes(100);
        Assert.IsNull(times.Record(0));
        for (long now = 10; now <= 80; now += 10) Assert.IsNull(times.Record(now));
        Assert.AreEqual("Bildzeit n=9 min=10 median=10 p95=30 max=30 ms", times.Record(110));
    }

    [TestMethod]
    public void LongIntervalsKeepExactMaximum()
    {
        var times = new WasmFrameTimes(100);
        times.Record(0);
        Assert.AreEqual("Bildzeit n=1 min=2500 median=2500 p95=2500 max=2500 ms", times.Record(2500));
    }

    [TestMethod]
    public void PauseIsNotCountedAsFrameTime()
    {
        var times = new WasmFrameTimes(100);
        times.Record(0);
        times.Record(10);
        times.Restart(5000);
        Assert.AreEqual("Bildzeit n=2 min=10 median=10 p95=10 max=10 ms", times.Record(5010));
    }

    [TestMethod]
    public void NextWindowStartsEmpty()
    {
        var times = new WasmFrameTimes(100);
        times.Record(0);
        Assert.IsNotNull(times.Record(400));
        Assert.IsNull(times.Record(420));
        Assert.AreEqual("Bildzeit n=2 min=20 median=20 p95=90 max=90 ms", times.Record(510));
    }
}
