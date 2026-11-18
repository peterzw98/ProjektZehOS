using System;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class SchedulingTests
{
    [TestMethod]
    public void MainLoopIdle_YieldsWithoutSleepingWhenSomeoneWaits()
    {
        int yields = 0, sleeps = 0;
        Assert.IsTrue(MainLoopIdle.Wait(1, () => { yields++; return true; }, () => sleeps++));
        Assert.AreEqual((1, 0), (yields, sleeps));
    }

    [TestMethod]
    public void MainLoopIdle_SleepsWhenTheRescheduleCouldNotBeRaised()
    {
        int yields = 0, sleeps = 0;
        Assert.IsFalse(MainLoopIdle.Wait(2, () => { yields++; return false; }, () => sleeps++));
        Assert.AreEqual((1, 1), (yields, sleeps));
    }

    [TestMethod]
    public void MainLoopIdle_SleepsWhenNobodyWaits()
    {
        int yields = 0, sleeps = 0;
        Assert.IsFalse(MainLoopIdle.Wait(0, () => { yields++; return true; }, () => sleeps++));
        Assert.AreEqual((0, 1), (yields, sleeps));
    }

    [TestMethod]
    public void Tasks_ReportWaitCancelAndKeepTerminalState()
    {
        var tasks = new BackgroundTaskService();
        using var entered = new ManualResetEvent(false);
        var started = tasks.Start("wait", context =>
        {
            context.Report(42);
            entered.Set();
            context.Sleep(Timeout.Infinite);
        });
        try
        {
            Assert.IsTrue(started.IsOK());
            Assert.IsTrue(entered.WaitOne(2000));
            Assert.IsTrue(SpinWait.SpinUntil(() => tasks.Snapshot()[0].State == BackgroundTaskState.Waiting, 2000));
            Assert.AreEqual(42L, tasks.Snapshot()[0].Progress);
            Assert.IsTrue(tasks.Cancel(started.Value).IsOK());
            Assert.IsTrue(SpinWait.SpinUntil(() => tasks.Snapshot()[0].State == BackgroundTaskState.Cancelled, 2000));
        }
        finally { Assert.IsTrue(tasks.StopAll(2000)); }
    }

    [TestMethod]
    public void Tasks_BoundActiveTasksAndReuseFinishedSlotsWithUniqueIds()
    {
        var tasks = new BackgroundTaskService();
        try
        {
            for (int i = 0; i < BackgroundTaskService.Capacity; i++)
                Assert.IsTrue(tasks.Start("wait", context => context.Sleep(Timeout.Infinite)).IsOK());
            Assert.IsFalse(tasks.Start("extra", _ => { }).IsOK());
            Assert.AreEqual(BackgroundTaskService.Capacity, tasks.Snapshot().Select(x => x.Id).Distinct().Count());
            Assert.IsTrue(tasks.StopAll(2000));
            var next = tasks.Start("finished", _ => { });
            Assert.IsTrue(next.IsOK());
            Assert.IsTrue(next.Value > BackgroundTaskService.Capacity);
        }
        finally { Assert.IsTrue(tasks.StopAll(2000)); }
    }

    [TestMethod]
    public void Tasks_FinishedCallbackMayStartAtFullCapacity()
    {
        // #244: der endende Job darf den eigenen Neustart nicht blockieren, wenn alle Plaetze belegt sind.
        var tasks = new BackgroundTaskService();
        using var restarted = new ManualResetEventSlim();
        ResultObject<int>? result = null;
        try
        {
            var first = tasks.Start("first", context => context.Sleep(Timeout.Infinite),
                () => { result = tasks.Start("again", _ => restarted.Set()); });
            for (int i = 1; i < BackgroundTaskService.Capacity; i++)
                Assert.IsTrue(tasks.Start("wait", context => context.Sleep(Timeout.Infinite)).IsOK());
            Assert.IsTrue(tasks.Cancel(first.Value).IsOK());
            Assert.IsTrue(restarted.Wait(2000));
            Assert.IsTrue(result!.IsOK());
        }
        finally { Assert.IsTrue(tasks.StopAll(2000)); }
    }

    [TestMethod]
    public void Tasks_FinishedCallbackExceptionIsRecordedAndKeepsTheWorker()
    {
        var tasks = new BackgroundTaskService();
        using var done = new ManualResetEventSlim();
        try
        {
            var first = tasks.Start("first", _ => { }, () => throw new InvalidOperationException("kaputt"));
            Assert.IsTrue(SpinWait.SpinUntil(() => tasks.Snapshot().Any(x => x.Id == first.Value && x.Error != null), 2000));
            StringAssert.Contains(tasks.Snapshot().Single(x => x.Id == first.Value).Error, "kaputt");
            Assert.IsTrue(tasks.Start("second", _ => done.Set()).IsOK());
            Assert.IsTrue(done.Wait(2000));
        }
        finally { Assert.IsTrue(tasks.StopAll(2000)); }
    }

    [TestMethod]
    public void StopAll_UnderStorageLock_IsRefusedInsteadOfDeadlocking()
    {
        var tasks = new BackgroundTaskService();
        Assert.ThrowsException<InvalidOperationException>(() => ProjektZeh.Storage.StorageLock.Run(() => tasks.StopAll(0)));
    }

    [TestMethod]
    public void Tasks_FailureIsVisibleAndDoesNotKillOtherTasks()
    {
        var tasks = new BackgroundTaskService();
        try
        {
            Assert.IsFalse(tasks.Start("", _ => { }).IsOK());
            Assert.IsFalse(tasks.Cancel(123).IsOK());
            tasks.Start("bad", _ => throw new InvalidOperationException("expected"));
            tasks.Start("good", context => context.Report(7));
            Assert.IsTrue(SpinWait.SpinUntil(() => tasks.Snapshot().All(x => x.State is BackgroundTaskState.Completed or BackgroundTaskState.Faulted), 2000));
            var snapshot = tasks.Snapshot();
            Assert.AreEqual(BackgroundTaskState.Faulted, snapshot[0].State);
            Assert.AreEqual("expected", snapshot[0].Error);
            Assert.AreEqual(7L, snapshot[1].Progress);
        }
        finally { Assert.IsTrue(tasks.StopAll(2000)); }
    }

    [TestMethod]
    public void Tasks_CancelAndFaultDisposeWaitResources()
    {
        var tasks = new BackgroundTaskService();
        WaitHandle? cancelledHandle = null;
        WaitHandle? failedHandle = null;
        try
        {
            tasks.Start("cancel", context =>
            {
                cancelledHandle = context.Token.WaitHandle;
                Assert.IsFalse(context.Sleep(Timeout.Infinite));
            });
            tasks.Start("fault", context =>
            {
                failedHandle = context.Token.WaitHandle;
                throw new InvalidOperationException("resource test");
            });
            Assert.IsTrue(SpinWait.SpinUntil(() => cancelledHandle != null && failedHandle != null, 2000));
            Assert.IsTrue(tasks.StopAll(2000));
            Assert.IsTrue(cancelledHandle!.SafeWaitHandle.IsClosed);
            Assert.IsTrue(failedHandle!.SafeWaitHandle.IsClosed);
        }
        finally { tasks.StopAll(2000); }
    }
}
