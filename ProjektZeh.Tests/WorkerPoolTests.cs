using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests;

/// <summary>
/// Hintergrundaufgaben auf wiederverwendeten Workern: Cosmos 3.0.97 kann nach Dateilast keine Threads mehr anlegen (E4.0 1G),
/// deshalb entstehen Worker beim Boot und werden nur bei Bedarf und nur bis Capacity ergaenzt.
/// </summary>
[DoNotParallelize]
[TestClass]
public sealed class WorkerPoolTests
{
    /// <summary>Startet Worker ueber ThreadStarter, solange <paramref name="allowed"/> noch Starts zulaesst.</summary>
    private static Func<ThreadStart, ResultObject<Thread>> Limited(Func<bool> allowed, List<Thread>? started = null) => body =>
    {
        if (!allowed()) return ResultObjectFactory.CreateFailure<Thread>(ResultCode.Failure, "cause='OutOfMemory' message='kein Stack'");
        var thread = ThreadStarter.TryStart(body, 0, _ => true, background: true);
        if (thread.IsOK()) started?.Add(thread.Value!);
        return thread;
    };

    [TestMethod]
    public void BootWorkers_AreReusedWithoutNewThreads()
    {
        var threads = new List<Thread>();
        bool allowed = true;
        var tasks = new BackgroundTaskService(Limited(() => allowed, threads));
        Assert.IsTrue(tasks.StartWorkers().IsOK());
        Assert.AreEqual(BackgroundTaskService.BootWorkers, threads.Count);

        allowed = false; // danach darf kein Thread-Start mehr noetig sein
        for (int round = 0; round < 20; round++)
        {
            var done = tasks.Start("kurz", _ => { });
            Assert.IsTrue(done.IsOK(), done.Status.Message);
            Assert.IsTrue(SpinWait.SpinUntil(() => tasks.Snapshot().Last().State == BackgroundTaskState.Completed, 2000));
        }
        Assert.AreEqual(BackgroundTaskService.BootWorkers, threads.Count);
        Assert.IsTrue(tasks.StopAll(2000));
    }

    [TestMethod]
    public void FullPoolAndFailedGrowth_JobWaitsAsReadyAndRunsWhenAWorkerIsFree()
    {
        bool allowed = true;
        var tasks = new BackgroundTaskService(Limited(() => allowed));
        Assert.IsTrue(tasks.StartWorkers(1).IsOK());
        using var release = new ManualResetEventSlim();
        var blocker = tasks.Start("blocker", _ => release.Wait());
        Assert.IsTrue(SpinWait.SpinUntil(() => tasks.Snapshot()[0].State is BackgroundTaskState.Running or BackgroundTaskState.Waiting, 2000));

        allowed = false;
        bool ran = false;
        var queued = tasks.Start("danach", _ => ran = true);
        Assert.IsTrue(queued.IsOK(), "eingereiht statt abgelehnt, solange ein Worker existiert");
        Thread.Sleep(50);
        Assert.AreEqual(BackgroundTaskState.Ready, tasks.Snapshot()[1].State);
        Assert.IsFalse(ran);

        release.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() => tasks.Snapshot()[1].State == BackgroundTaskState.Completed, 2000));
        Assert.IsTrue(ran);
        Assert.IsTrue(blocker.IsOK());
        Assert.IsTrue(tasks.StopAll(2000));
    }

    [TestMethod]
    public void QueuedJobs_RunInFifoOrder_AndWithoutAnyWorkerStartIsRefused()
    {
        var refused = new BackgroundTaskService(Limited(() => false));
        Assert.IsFalse(refused.Start("ohne Worker", _ => { }).IsOK(), "ganz ohne Worker wuerde die Aufgabe nie laufen");
        Assert.AreEqual(0, refused.Snapshot().Length);

        int starts = 0;
        var tasks = new BackgroundTaskService(Limited(() => starts++ < 1)); // genau ein Worker, Wachstum scheitert
        Assert.IsTrue(tasks.StartWorkers(1).IsOK());
        using var hold = new ManualResetEventSlim();
        tasks.Start("blocker", _ => hold.Wait());
        var order = new List<int>();
        for (int index = 0; index < 3; index++)
        {
            int value = index;
            Assert.IsTrue(tasks.Start($"job{value}", _ => { lock (order) order.Add(value); }).IsOK());
        }
        hold.Set();
        Assert.IsTrue(SpinWait.SpinUntil(() => { lock (order) return order.Count == 3; }, 2000));
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, order);
        Assert.IsTrue(tasks.StopAll(2000));
    }

    [TestMethod]
    public void CancelQueuedJob_StillRunsWithCancelledTokenLikeBefore()
    {
        bool allowed = true;
        var tasks = new BackgroundTaskService(Limited(() => allowed));
        Assert.IsTrue(tasks.StartWorkers(1).IsOK());
        allowed = false;
        tasks.Start("laeuft", context => context.Sleep(Timeout.Infinite));
        bool? sawCancel = null;
        var queued = tasks.Start("wartet", context => sawCancel = context.Token.IsCancellationRequested);
        Assert.IsTrue(SpinWait.SpinUntil(() => tasks.Snapshot()[0].State == BackgroundTaskState.Waiting, 2000));
        Assert.AreEqual(BackgroundTaskState.Ready, tasks.Snapshot()[1].State);

        Assert.IsTrue(tasks.Cancel(queued.Value).IsOK());
        Assert.IsTrue(tasks.StopAll(2000));
        Assert.AreEqual(true, sawCancel, "Aufrufer raeumen in ihrer Aktion auf und muessen den Abbruch sehen");
        Assert.AreEqual(BackgroundTaskState.Cancelled, tasks.Snapshot()[1].State);
    }

    [TestMethod]
    public void TaskList_ShowsQueuedJobAsWaitingForWorker()
    {
        bool allowed = true;
        var tasks = new BackgroundTaskService(Limited(() => allowed));
        Assert.IsTrue(tasks.StartWorkers(1).IsOK());
        allowed = false;
        tasks.Start("producer", context => context.Sleep(Timeout.Infinite));
        tasks.Start("consumer", _ => { });
        var output = new Output();
        var shell = new ShellService(new TaskShellCommands(tasks, () => []).Commands);
        Assert.IsTrue(shell.ExecuteLine("tasks list", output).IsOK());
        Assert.IsTrue(output.Lines.Exists(line => line.Contains("consumer Ready (wartet auf freien Worker)", StringComparison.Ordinal)),
            string.Join("\n", output.Lines));
        Assert.IsTrue(tasks.StopAll(2000));
    }

    [TestMethod]
    public void DedicatedWorker_RunsInOrderSurvivesFaultsAndRetriesFailedStart()
    {
        bool allowed = false;
        var logged = new List<string>();
        var worker = new DedicatedWorker(logged.Add, Limited(() => allowed));
        Assert.IsFalse(worker.Start().IsOK());
        Assert.IsFalse(worker.Post(() => { }).IsOK(), "ohne Thread kein stilles Einreihen");

        allowed = true;
        var order = new List<int>();
        using var done = new ManualResetEventSlim();
        Assert.IsTrue(worker.Post(() => order.Add(1)).IsOK());
        Assert.IsTrue(worker.Post(() => throw new InvalidOperationException("geheim")).IsOK());
        Assert.IsTrue(worker.Post(() => { order.Add(2); done.Set(); }).IsOK());
        Assert.IsTrue(done.Wait(2000));
        CollectionAssert.AreEqual(new[] { 1, 2 }, order);
        Assert.AreEqual(1, logged.Count);
        Assert.IsFalse(logged[0].Contains("geheim", StringComparison.Ordinal), "nur der Ausnahmetyp, keine Meldung");
    }

    private sealed class Output : ITextOutput
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string text) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }
}
