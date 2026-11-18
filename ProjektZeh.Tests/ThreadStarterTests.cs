using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

/// <summary>
/// Thread-Start unter Speicherdruck: Cosmos 3.0.97 prueft den Threadstack nicht (Page Fault CR2=0x138, E4.0 1G Schritt j).
/// ThreadStarter fragt vorher den nativen Heap in der Groesse, die Cosmos tatsaechlich anfordert.
/// </summary>
[TestClass]
public sealed class ThreadStarterTests
{
    [TestMethod]
    [DataRow(0, 256 * 1024)]
    [DataRow(1, 64 * 1024)]
    [DataRow(64 * 1024, 64 * 1024)]
    [DataRow(70000, 73728)]
    [DataRow(8 * 1024 * 1024, 8 * 1024 * 1024)]
    public void EffectiveStackBytes_MatchesCosmosRounding(int requested, int expected) =>
        Assert.AreEqual(expected, ThreadStarter.EffectiveStackBytes(requested));

    [TestMethod]
    public void FailedProbe_StartsNoThreadAndReportsOutOfMemory()
    {
        bool ran = false;
        int probed = -1;
        var started = ThreadStarter.TryStart(() => ran = true, 8 * 1024 * 1024, bytes => { probed = bytes; return false; });

        Assert.IsFalse(started.IsOK());
        StringAssert.Contains(started.Status.Message, "OutOfMemory");
        Assert.AreEqual(8 * 1024 * 1024, probed, "Probe in Stackgroesse");
        Thread.Sleep(50);
        Assert.IsFalse(ran, "ohne Stack darf kein Thread laufen");
    }

    [TestMethod]
    public void SuccessfulProbe_StartsThreadWithDefaultStack()
    {
        int probed = -1;
        using var done = new ManualResetEventSlim();
        var started = ThreadStarter.TryStart(done.Set, 0, bytes => { probed = bytes; return true; });

        Assert.IsTrue(started.IsOK(), started.Status.Message);
        Assert.IsTrue(done.Wait(5000));
        Assert.IsTrue(started.Value!.Join(5000));
        Assert.AreEqual(ThreadStarter.DefaultStackBytes, probed);
    }

    [TestMethod]
    public void BackgroundTask_FailedStart_IsReportedAndLeavesNoJob()
    {
        bool fail = true;
        var tasks = new BackgroundTaskService(body => fail
            ? ResultObjectFactory.CreateFailure<Thread>(ResultCode.Failure, "cause='OutOfMemory' message='kein Stack'")
            : ThreadStarter.TryStart(body, 0, _ => true));

        var refused = tasks.Start("migrate", _ => { });
        Assert.IsFalse(refused.IsOK());
        StringAssert.Contains(refused.Status.Message, "OutOfMemory");
        Assert.AreEqual(0, tasks.Snapshot().Length, "kein halb eingetragener Job");

        fail = false;
        var accepted = tasks.Start("migrate", _ => { });
        Assert.IsTrue(accepted.IsOK(), accepted.Status.Message);
        Assert.AreEqual(1, accepted.Value, "der gescheiterte Start verbraucht keine Id");
        Assert.IsTrue(tasks.StopAll(5000));
    }

    [TestMethod]
    public void TryStart_WithBackgroundClass_AppliesClassOnTheNewThread()
    {
        var original = ThreadStarter.ApplyClass;
        try
        {
            ThreadClass? applied = null;
            int appliedOn = -1;
            bool bodyRanAfterApply = false;
            ThreadStarter.ApplyClass = threadClass => { applied = threadClass; appliedOn = Environment.CurrentManagedThreadId; };
            var started = ThreadStarter.TryStart(() => bodyRanAfterApply = applied != null, 0, _ => true, threadClass: ThreadClass.Background);
            Assert.IsTrue(started.IsOK());
            Assert.IsTrue(started.Value!.Join(5000));
            Assert.AreEqual(ThreadClass.Background, applied);
            Assert.AreNotEqual(Environment.CurrentManagedThreadId, appliedOn);
            Assert.IsTrue(bodyRanAfterApply);
        }
        finally { ThreadStarter.ApplyClass = original; }
    }

    [TestMethod]
    public void TryStart_NormalClass_DoesNotCallApplyClass()
    {
        var original = ThreadStarter.ApplyClass;
        try
        {
            int calls = 0;
            ThreadStarter.ApplyClass = _ => calls++;
            var started = ThreadStarter.TryStart(() => { }, 0, _ => true);
            Assert.IsTrue(started.IsOK());
            Assert.IsTrue(started.Value!.Join(5000));
            Assert.AreEqual(0, calls);
        }
        finally { ThreadStarter.ApplyClass = original; }
    }
}
