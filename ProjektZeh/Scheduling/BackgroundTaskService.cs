using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using ProjektZeh.Shared;

namespace ProjektZeh.Scheduling;

internal enum BackgroundTaskState { Ready, Running, Waiting, Completed, Cancelled, Faulted }

internal sealed record BackgroundTaskSnapshot(int Id, string Name, BackgroundTaskState State, long Progress, string? Error);

internal sealed class BackgroundTaskContext(Action<long> report, CancellationToken token)
{
    public CancellationToken Token { get; } = token;
    public void Report(long progress) => report(progress);
    public void Sleep(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(milliseconds, Timeout.Infinite);
        Token.ThrowIfCancellationRequested();
        if (Token.WaitHandle.WaitOne(milliseconds)) Token.ThrowIfCancellationRequested();
    }
}

internal sealed class BackgroundTaskService
{
    public const int Capacity = 8;
    private readonly object gate = new();
    private readonly List<Job> jobs = new(Capacity);
    private int nextId;

    public ResultObject<int> Start(string name, Action<BackgroundTaskContext> action)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40 || action is null)
            return ResultObjectFactory.CreateFailure<int>(ResultCode.InvalidArgument, "Aufgabenname/Aktion ungueltig.");
        lock (gate)
        {
            if (jobs.Count == Capacity)
            {
                for (int i = jobs.Count - 1; i >= 0; i--)
                {
                    if (jobs[i].Thread.IsAlive) continue;
                    jobs.RemoveAt(i);
                }
            }
            if (jobs.Count == Capacity || nextId == int.MaxValue)
                return ResultObjectFactory.CreateFailure<int>(ResultCode.Failure, "Aufgabenlimit erreicht (8); laufende Aufgaben zuerst abbrechen.");
            var job = new Job(++nextId, name);
            job.Thread = new Thread(() => Run(job, action));
            jobs.Add(job);
            try { job.Thread.Start(); }
            catch (Exception exception)
            {
                jobs.RemoveAt(jobs.Count - 1);
                job.Cancellation.Dispose();
                return ResultObjectFactory.CreateFromException<int>(exception);
            }
            return ResultObjectFactory.CreateSuccess(job.Id);
        }
    }

    private void Run(Job job, Action<BackgroundTaskContext> action)
    {
        lock (gate) job.State = BackgroundTaskState.Running;
        try
        {
            action(new BackgroundTaskContext(progress => Interlocked.Exchange(ref job.Progress, progress), job.Cancellation.Token));
            lock (gate) job.State = job.Cancellation.IsCancellationRequested ? BackgroundTaskState.Cancelled : BackgroundTaskState.Completed;
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            lock (gate) job.State = BackgroundTaskState.Cancelled;
        }
        catch (Exception exception)
        {
            lock (gate)
            {
                job.Error = exception.Message;
                job.State = BackgroundTaskState.Faulted;
            }
        }
        finally { lock (gate) job.Cancellation.Dispose(); }
    }

    public ResultErr Cancel(int id)
    {
        lock (gate)
        {
            foreach (var job in jobs)
            {
                if (job.Id != id) continue;
                if (job.State is BackgroundTaskState.Ready or BackgroundTaskState.Running) job.Cancellation.Cancel();
                return ResultErr.CreateSuccess();
            }
        }
        return ResultErr.CreateFailure(ResultCode.NotFound, "Aufgabe nicht gefunden.");
    }

    public BackgroundTaskSnapshot[] Snapshot()
    {
        lock (gate)
        {
            var result = new BackgroundTaskSnapshot[jobs.Count];
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                var state = job.State;
                if (state is BackgroundTaskState.Ready or BackgroundTaskState.Running)
                {
                    var threadState = job.Thread.ThreadState;
                    state = (threadState & System.Threading.ThreadState.Unstarted) != 0 ? BackgroundTaskState.Ready
                        : (threadState & System.Threading.ThreadState.WaitSleepJoin) != 0 ? BackgroundTaskState.Waiting
                        : BackgroundTaskState.Running;
                }
                result[i] = new(job.Id, job.Name, state, Interlocked.Read(ref job.Progress), job.Error);
            }
            return result;
        }
    }

    public bool StopAll(int timeoutMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMilliseconds);
        Job[] snapshot;
        lock (gate)
        {
            snapshot = jobs.ToArray();
            foreach (var job in snapshot)
                if (job.State is BackgroundTaskState.Ready or BackgroundTaskState.Running) job.Cancellation.Cancel();
        }
        var clock = Stopwatch.StartNew();
        bool stopped = true;
        foreach (var job in snapshot)
        {
            int remaining = Math.Max(0, timeoutMilliseconds - (int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue));
            if (!job.Thread.Join(remaining)) stopped = false;
        }
        return stopped;
    }

    private sealed class Job(int id, string name)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public CancellationTokenSource Cancellation { get; } = new();
        public Thread Thread { get; set; } = null!;
        public BackgroundTaskState State { get; set; } = BackgroundTaskState.Ready;
        public long Progress;
        public string? Error { get; set; }
    }
}
