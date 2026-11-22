using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

/// <summary>
/// Thread-Start: Klassen- und Platzierungshooks; den Stack prueft Cosmos selbst (smp-phase5b, E5-F).
/// </summary>
[TestClass]
public sealed class ThreadStarterTests
{
    [TestMethod]
    public void BackgroundTask_FailedStart_IsReportedAndLeavesNoJob()
    {
        bool fail = true;
        var tasks = new BackgroundTaskService(body => fail
            ? ResultObjectFactory.CreateFailure<Thread>(ResultCode.Failure, "cause='OutOfMemory' message='kein Stack'")
            : ThreadStarter.TryStart(body));

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
            var started = ThreadStarter.TryStart(() => bodyRanAfterApply = applied != null, threadClass: ThreadClass.Background);
            Assert.IsTrue(started.IsOK());
            Assert.IsTrue(started.Value!.Join(5000));
            Assert.AreEqual(ThreadClass.Background, applied);
            Assert.AreNotEqual(Environment.CurrentManagedThreadId, appliedOn);
            Assert.IsTrue(bodyRanAfterApply);
        }
        finally { ThreadStarter.ApplyClass = original; }
    }

    [TestMethod]
    public void TryStart_WithWorkersPlacement_AppliesPlacementOnTheNewThreadBeforeBody()
    {
        var original = ThreadStarter.ApplyPlacement;
        try
        {
            ThreadPlacement? applied = null;
            int appliedOn = -1;
            bool bodyRanAfterApply = false;
            ThreadStarter.ApplyPlacement = placement => { applied = placement; appliedOn = Environment.CurrentManagedThreadId; };
            var started = ThreadStarter.TryStart(() => bodyRanAfterApply = applied != null,
                placement: ThreadPlacement.Workers);
            Assert.IsTrue(started.IsOK(), started.Status.Message);
            Assert.IsTrue(started.Value!.Join(5000));
            Assert.AreEqual(ThreadPlacement.Workers, applied);
            Assert.AreNotEqual(Environment.CurrentManagedThreadId, appliedOn);
            Assert.IsTrue(bodyRanAfterApply);
        }
        finally { ThreadStarter.ApplyPlacement = original; }
    }

    [TestMethod]
    public void TryStart_NormalClass_DoesNotCallApplyClass()
    {
        var original = ThreadStarter.ApplyClass;
        try
        {
            int calls = 0;
            ThreadStarter.ApplyClass = _ => calls++;
            var started = ThreadStarter.TryStart(() => { });
            Assert.IsTrue(started.IsOK());
            Assert.IsTrue(started.Value!.Join(5000));
            Assert.AreEqual(0, calls);
        }
        finally { ThreadStarter.ApplyClass = original; }
    }
}
