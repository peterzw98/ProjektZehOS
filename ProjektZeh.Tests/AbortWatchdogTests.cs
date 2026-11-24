using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

/// <summary>
/// Frist zwischen Schliessen und erzwungenem Abbruch (Roadmap 12.4-I2-A): Ein Abbruch trifft nur den geschlossenen Lauf,
/// erst nach der Frist und genau einmal; ein spaeter gestarteter Lauf bleibt unberuehrt.
/// </summary>
[TestClass]
public sealed class AbortWatchdogTests
{
    [TestMethod]
    public void ShouldAbort_NothingRequested_False()
    {
        var watchdog = new AbortWatchdog();
        Assert.IsFalse(watchdog.ShouldAbort(new object(), 10_000));
    }

    [TestMethod]
    public void ShouldAbort_BeforeGrace_False()
    {
        var watchdog = new AbortWatchdog();
        var run = new object();
        watchdog.CloseRequested(run, 1000);
        Assert.IsFalse(watchdog.ShouldAbort(run, 5999));
    }

    [TestMethod]
    public void ShouldAbort_AfterGrace_TrueOnce()
    {
        var watchdog = new AbortWatchdog();
        var run = new object();
        watchdog.CloseRequested(run, 1000);
        Assert.IsTrue(watchdog.ShouldAbort(run, 6000));
        Assert.IsFalse(watchdog.ShouldAbort(run, 6001));
    }

    [TestMethod]
    public void ShouldAbort_RunEndedBeforeGrace_False()
    {
        var watchdog = new AbortWatchdog();
        var run = new object();
        watchdog.CloseRequested(run, 0);
        watchdog.RunEnded(run);
        Assert.IsFalse(watchdog.ShouldAbort(run, 9000));
    }

    [TestMethod]
    public void ShouldAbort_OtherRunCurrent_False()
    {
        var watchdog = new AbortWatchdog();
        var a = new object();
        var b = new object();
        watchdog.CloseRequested(a, 0);
        Assert.IsFalse(watchdog.ShouldAbort(b, 9000));
        Assert.IsTrue(watchdog.ShouldAbort(a, 9000));
    }

    [TestMethod]
    public void ShouldAbort_NoCurrentRun_False()
    {
        var watchdog = new AbortWatchdog();
        watchdog.CloseRequested(new object(), 0);
        Assert.IsFalse(watchdog.ShouldAbort(null, 9000));
    }

    [TestMethod]
    public void CloseRequested_Twice_KeepsFirstTime()
    {
        var watchdog = new AbortWatchdog();
        var run = new object();
        watchdog.CloseRequested(run, 0);
        watchdog.CloseRequested(run, 4000);
        Assert.IsTrue(watchdog.ShouldAbort(run, 5000));
    }

    [TestMethod]
    public void IsAborting_AfterAbortUntilRunEnded()
    {
        var watchdog = new AbortWatchdog();
        var run = new object();
        watchdog.CloseRequested(run, 0);
        Assert.IsFalse(watchdog.IsAborting(run));
        Assert.IsTrue(watchdog.ShouldAbort(run, 5000));
        Assert.IsTrue(watchdog.IsAborting(run));
        Assert.IsFalse(watchdog.IsAborting(new object()));
        Assert.IsFalse(watchdog.IsAborting(null));
        watchdog.RunEnded(run);
        Assert.IsFalse(watchdog.IsAborting(run));
    }

    [TestMethod]
    public void CloseRequested_AfterAbort_DoesNotRestartGrace()
    {
        var watchdog = new AbortWatchdog();
        var run = new object();
        watchdog.CloseRequested(run, 0);
        Assert.IsTrue(watchdog.ShouldAbort(run, 5000));
        watchdog.CloseRequested(run, 6000);
        Assert.IsFalse(watchdog.ShouldAbort(run, 20_000));
        Assert.IsTrue(watchdog.IsAborting(run));
    }

    [TestMethod]
    public void RunEnded_OtherRun_KeepsRequest()
    {
        var watchdog = new AbortWatchdog();
        var a = new object();
        watchdog.CloseRequested(a, 0);
        watchdog.RunEnded(new object());
        Assert.IsTrue(watchdog.ShouldAbort(a, 5000));
    }
}
