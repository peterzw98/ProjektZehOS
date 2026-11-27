using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class StalledTickWatchTests
{
    [TestMethod]
    public void AdvancingTicks_NeverNudge()
    {
        var watch = new StalledTickWatch(2);
        for (uint tick = 1; tick < 100; tick++) Assert.IsFalse(watch.Observe(1, tick, runsThread: true));
    }

    [TestMethod]
    public void StalledRunningCpu_NudgedAfterStallTicks_ThenAgainAfterStallTicks()
    {
        var watch = new StalledTickWatch(2);
        Assert.IsFalse(watch.Observe(1, 7, true));
        Assert.IsFalse(watch.Observe(1, 7, true));
        Assert.IsTrue(watch.Observe(1, 7, true));
        Assert.IsFalse(watch.Observe(1, 7, true));
        Assert.IsTrue(watch.Observe(1, 7, true));
    }

    [TestMethod]
    public void IdleCpu_IsNotNudged()
    {
        var watch = new StalledTickWatch(2);
        for (int i = 0; i < 10; i++) Assert.IsFalse(watch.Observe(1, 7, runsThread: false));
    }

    [TestMethod]
    public void TickAfterStall_ResetsCount()
    {
        var watch = new StalledTickWatch(2);
        watch.Observe(1, 7, true);
        watch.Observe(1, 7, true);
        Assert.IsFalse(watch.Observe(1, 8, true));
        Assert.IsFalse(watch.Observe(1, 8, true));
        Assert.IsTrue(watch.Observe(1, 8, true));
    }

    [TestMethod]
    public void CpusAreTrackedSeparately()
    {
        var watch = new StalledTickWatch(3);
        watch.Observe(1, 5, true);
        watch.Observe(2, 5, true);
        Assert.IsFalse(watch.Observe(1, 5, true));
        Assert.IsFalse(watch.Observe(2, 6, true));
        Assert.IsTrue(watch.Observe(1, 5, true));
        Assert.IsFalse(watch.Observe(2, 6, true));
    }
}
