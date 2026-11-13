using System;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class SchedulingTests
{
    [TestMethod]
    public void ReadyYieldBlock_QueueRemainsFifoWithoutDuplicates()
    {
        var queue = new RoundRobinQueue<object>(3);
        var first = new object();
        var second = new object();
        queue.Add(first);
        queue.Add(second);
        queue.Add(first);
        Assert.AreEqual(2, queue.Count);
        Assert.AreSame(first, queue.Take());
        queue.Add(first);
        queue.Remove(second);
        Assert.AreSame(first, queue.Take());
        Assert.IsNull(queue.Take());
    }

    [TestMethod]
    public void Slice_ExpiresOnlyAfterBothTicksAndSaturates()
    {
        var slice = new SchedulerTimeSlice(20);
        Assert.IsFalse(slice.Charge(10));
        Assert.IsTrue(slice.Charge(10));
        slice.Reset();
        Assert.IsTrue(slice.Charge(ulong.MaxValue));
        Assert.IsTrue(slice.Charge(1));
    }

    [TestMethod]
    public void Queue_CapacityAndFailedPickPreserveOrder()
    {
        var queue = new RoundRobinQueue<object>(2);
        var first = new object();
        var second = new object();
        Assert.IsTrue(queue.Add(first));
        Assert.IsTrue(queue.Add(second));
        Assert.IsFalse(queue.Add(new object()));
        Assert.AreSame(first, queue.Take());
        Assert.IsTrue(queue.AddFirst(first));
        Assert.AreSame(first, queue.Take());
        Assert.AreSame(second, queue.Take());
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
