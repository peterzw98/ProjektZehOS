using System;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[DoNotParallelize]
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

    private sealed class FakeThread(string name, bool idle = false)
    {
        public SchedulerTimeSlice Slice { get; } = new(20);
        public bool Idle { get; } = idle;
        public override string ToString() => name;
    }

    private static RoundRobinCore<FakeThread> Core() => new(8, thread => thread.Slice, thread => thread.Idle);

    [TestMethod]
    public void IdleThreadWokenEveryTick_ReadyWorkerRunsAfterOneTick()
    {
        // Ablauf aus dem Gast: Hauptschleife (Idle-Thread) laeuft, schlaeft, wird von jedem Tick geweckt; der Worker ist bereit.
        var core = Core();
        var idle = new FakeThread("idle", idle: true);
        var worker = new FakeThread("worker");
        core.Ready(worker);
        core.Ready(idle);
        Assert.IsTrue(core.Tick(idle, 10));
        Assert.AreSame(worker, core.Pick());
    }

    [TestMethod]
    public void ThreadWokenWhileRunning_DoesNotRefillItsSlice()
    {
        // Frueher setzte OnThreadReady die Scheibe zurueck: ein laufender Thread, den jeder Tick weckt, lief nie ab.
        var core = Core();
        var main = new FakeThread("main");
        var worker = new FakeThread("worker");
        core.Ready(worker);
        core.Ready(main);
        Assert.AreSame(worker, core.Pick());
        core.Ready(worker);
        Assert.AreSame(main, core.Pick());
        core.Ready(main);
        Assert.IsFalse(core.Tick(main, 10));
        core.Ready(main);
        Assert.IsTrue(core.Tick(main, 10));
        Assert.AreSame(worker, core.Pick());
    }

    [TestMethod]
    public void Waiting_FollowsReadyBlockedPickAndPickFailed()
    {
        var core = Core();
        var first = new FakeThread("first");
        var second = new FakeThread("second");
        Assert.AreEqual(0, core.Waiting);
        core.Ready(first);
        core.Ready(second);
        Assert.AreEqual(2, core.Waiting);
        Assert.AreSame(first, core.Pick());
        Assert.AreEqual(1, core.Waiting);
        core.PickFailed(first);
        Assert.AreEqual(2, core.Waiting);
        core.Blocked(second);
        Assert.AreEqual(1, core.Waiting);
        Assert.AreSame(first, core.Pick());
        Assert.AreEqual(0, core.Waiting);
    }

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
    public void Idle_KeepsCpuWithoutWaitingThreadsAndIsNotPickedAgainFirst()
    {
        var core = Core();
        var idle = new FakeThread("idle", idle: true);
        Assert.IsFalse(core.Tick(idle, 10));
        core.Ready(idle);
        Assert.IsFalse(core.Tick(idle, 10));
        var worker = new FakeThread("worker");
        core.Ready(worker);
        Assert.IsTrue(core.Tick(idle, 10));
        Assert.AreSame(worker, core.Pick());
        Assert.AreSame(idle, core.Pick());
    }

    [TestMethod]
    public void IdleComputing_PreemptedAtFirstTickWhenWorkerWaits()
    {
        // Auch eine rechnende Hauptschleife (nicht geweckt, nicht in der Queue) gibt ab: Parken ist im Tick nicht erkennbar.
        var core = Core();
        var idle = new FakeThread("idle", idle: true);
        var worker = new FakeThread("worker");
        core.Ready(worker);
        Assert.IsTrue(core.Tick(idle, 10));
        Assert.AreSame(worker, core.Pick());
    }

    [TestMethod]
    public void BusyWorkerAndComputingMainLoop_MainLoopGetsOneTickPerRound()
    {
        // Cosmos-Ablauf je Tick: Tick; bei Verdraengung OnThreadYield (Ready) des bisherigen Threads, dann PickNext ?? Idle.
        var core = Core();
        var idle = new FakeThread("idle", idle: true);
        var worker = new FakeThread("worker");
        core.Ready(worker);
        FakeThread current = idle;
        int idleTicks = 0;
        for (int tick = 0; tick < 12; tick++)
        {
            if (ReferenceEquals(current, idle)) idleTicks++;
            if (!core.Tick(current, 10)) continue;
            core.Ready(current);
            current = core.Pick() ?? idle;
        }
        // Ein Tick Hauptschleife je zwei Ticks Worker: ein Drittel der Ticks fuer Desktop und Eingabe unter Dauerlast.
        Assert.AreEqual(4, idleTicks);
    }

    [TestMethod]
    public void BusyWorkerAndParkingMainLoop_MainLoopGetsOneTickPerRound()
    {
        // Die Hauptschleife parkt nach kurzer Arbeit (Thread.Sleep); der Tick weckt sie, und sie gibt sofort ab.
        var core = Core();
        var idle = new FakeThread("idle", idle: true);
        var worker = new FakeThread("worker");
        core.Ready(worker);
        FakeThread current = idle;
        int idleTicks = 0;
        for (int tick = 0; tick < 12; tick++)
        {
            if (ReferenceEquals(current, idle))
            {
                idleTicks++;
                core.Ready(idle);
            }
            if (!core.Tick(current, 10)) continue;
            core.Ready(current);
            current = core.Pick() ?? idle;
        }
        Assert.AreEqual(4, idleTicks);
    }

    [TestMethod]
    public void TwoBusyThreads_AlternateAfterFullSlices()
    {
        var core = Core();
        var first = new FakeThread("first");
        var second = new FakeThread("second");
        core.Ready(first);
        core.Ready(second);
        FakeThread current = core.Pick()!;
        var runs = new System.Collections.Generic.List<string>();
        for (int tick = 0; tick < 8; tick++)
        {
            runs.Add(current.ToString());
            if (!core.Tick(current, 10)) continue;
            core.Ready(current);
            current = core.Pick()!;
        }
        CollectionAssert.AreEqual(new[] { "first", "first", "second", "second", "first", "first", "second", "second" }, runs);
    }

    [TestMethod]
    public void BusyThreadAlone_KeepsCpuAndStartsFreshSliceWhenOtherArrives()
    {
        var core = Core();
        var busy = new FakeThread("busy");
        core.Ready(busy);
        Assert.AreSame(busy, core.Pick());
        Assert.IsFalse(core.Tick(busy, 10));
        Assert.IsFalse(core.Tick(busy, 10));
        core.Ready(new FakeThread("other"));
        Assert.IsFalse(core.Tick(busy, 10));
        Assert.IsTrue(core.Tick(busy, 10));
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
