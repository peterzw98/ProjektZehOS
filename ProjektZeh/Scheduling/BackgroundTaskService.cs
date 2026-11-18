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
    public bool Sleep(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(milliseconds, Timeout.Infinite);
        return !Token.WaitHandle.WaitOne(milliseconds);
    }
}

/// <summary>
/// Hintergrundaufgaben auf wiederverwendeten Workern statt einem Thread je Aufgabe: Cosmos 3.0.97 kann spaete Thread-Starts
/// nach Dateilast nicht mehr bedienen (nativer Heap ohne 256 KiB am Stueck, E4.0 1G). Der Kernel legt deshalb beim Boot
/// <see cref="BootWorkers"/> Worker an; nur wenn alle belegt sind, waechst der Pool bis <see cref="Capacity"/>. Scheitert das
/// Wachsen, wartet die Aufgabe als Ready, bis ein Worker frei wird. Worker enden nie.
/// </summary>
internal sealed class BackgroundTaskService
{
    public const int Capacity = 8;
    /// <summary>net auto (dauerhaft), pkg bzw. system migrate und das Paar messages-producer/-consumer.</summary>
    public const int BootWorkers = 4;
    private readonly object gate = new();
    private readonly List<Job> jobs = new(Capacity);
    private readonly Queue<Job> queue = new(Capacity);
    private readonly List<Thread> workers = new(Capacity);
    private readonly Func<ThreadStart, ResultObject<Thread>> startThread;
    // Belegte statt freie Worker zaehlen: ein gerade gestarteter Worker hat seine Warteschleife evtl. noch nicht erreicht.
    private int busyWorkers;
    private int nextId;

    /// <param name="startThread">Nur fuer Tests; sonst <see cref="ThreadStarter.TryStart(ThreadStart, int, bool)"/>.</param>
    public BackgroundTaskService(Func<ThreadStart, ResultObject<Thread>>? startThread = null) =>
        this.startThread = startThread ?? (body => ThreadStarter.TryStart(body, background: true));

    /// <summary>Legt bis zu <paramref name="count"/> Worker an (beim Boot, solange der Heap noch zusammenhaengend ist).</summary>
    public ResultErr StartWorkers(int count = BootWorkers)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (gate)
        {
            while (workers.Count < Math.Min(count, Capacity))
            {
                var started = AddWorkerLocked();
                if (!started.IsOK()) return started;
            }
        }
        return ResultErr.CreateSuccess();
    }

    public ResultObject<int> Start(string name, Action<BackgroundTaskContext> action)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40 || action is null)
            return ResultObjectFactory.CreateFailure<int>(ResultCode.InvalidArgument, "Aufgabenname/Aktion ungueltig.");
        lock (gate)
        {
            if (jobs.Count == Capacity) jobs.RemoveAll(job => job.Finished);
            if (jobs.Count == Capacity || nextId == int.MaxValue)
                return ResultObjectFactory.CreateFailure<int>(ResultCode.Failure, "Aufgabenlimit erreicht (8); laufende Aufgaben zuerst abbrechen.");
            // Nur wachsen, wenn kein Worker frei ist; ein spaeter Start kann unter Speicherdruck scheitern.
            if (workers.Count - busyWorkers <= queue.Count && workers.Count < Capacity)
            {
                var grown = AddWorkerLocked();
                if (!grown.IsOK() && workers.Count == 0) return ResultObjectFactory.CreateFailure<int>(grown);
            }
            var job = new Job(++nextId, name, action);
            jobs.Add(job);
            queue.Enqueue(job);
            Monitor.PulseAll(gate);
            return ResultObjectFactory.CreateSuccess(job.Id);
        }
    }

    private ResultErr AddWorkerLocked()
    {
        var started = startThread(Work);
        if (!started.IsOK()) return started.Status;
        workers.Add(started.Value!);
        return ResultErr.CreateSuccess();
    }

    private void Work()
    {
        while (true)
        {
            Job job;
            lock (gate)
            {
                while (queue.Count == 0) Monitor.Wait(gate);
                busyWorkers++;
                job = queue.Dequeue();
                job.Worker = Thread.CurrentThread;
                job.State = BackgroundTaskState.Running;
            }
            Run(job);
        }
    }

    private void Run(Job job)
    {
        try
        {
            job.Action(new BackgroundTaskContext(progress => Interlocked.Exchange(ref job.Progress, progress), job.Cancellation.Token));
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
        finally
        {
            lock (gate)
            {
                busyWorkers--;
                FinishLocked(job);
            }
        }
    }

    private void FinishLocked(Job job)
    {
        job.Cancellation.Dispose();
        job.Worker = null;
        job.Finished = true;
        Monitor.PulseAll(gate);
    }

    public ResultErr Cancel(int id)
    {
        lock (gate)
        {
            foreach (var job in jobs)
            {
                if (job.Id != id) continue;
                CancelLocked(job);
                return ResultErr.CreateSuccess();
            }
        }
        return ResultErr.CreateFailure(ResultCode.NotFound, "Aufgabe nicht gefunden.");
    }

    // Wie bisher mit eigenem Thread: auch eine noch wartende Aufgabe laeuft an und sieht ihr abgebrochenes Token. Aufrufer wie
    // ShellJob und MessageDemoService raeumen erst in ihrer Aktion auf und blieben sonst dauerhaft "laufend".
    private static void CancelLocked(Job job)
    {
        if (job.State is BackgroundTaskState.Ready or BackgroundTaskState.Running) job.Cancellation.Cancel();
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
                if (state == BackgroundTaskState.Running && job.Worker is { } worker
                    && (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0)
                    state = BackgroundTaskState.Waiting;
                result[i] = new(job.Id, job.Name, state, Interlocked.Read(ref job.Progress), job.Error);
            }
            return result;
        }
    }

    /// <summary>Bricht alle Aufgaben ab und wartet hoechstens <paramref name="timeoutMilliseconds"/> auf ihr Ende; Worker bleiben.</summary>
    public bool StopAll(int timeoutMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeoutMilliseconds);
        var clock = Stopwatch.StartNew();
        lock (gate)
        {
            foreach (var job in jobs.ToArray()) CancelLocked(job);
            while (!jobs.TrueForAll(job => job.Finished))
            {
                long remaining = timeoutMilliseconds - clock.ElapsedMilliseconds;
                if (remaining <= 0) return false;
                Monitor.Wait(gate, (int)Math.Min(remaining, int.MaxValue));
            }
        }
        return true;
    }

    private sealed class Job(int id, string name, Action<BackgroundTaskContext> action)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public Action<BackgroundTaskContext> Action { get; } = action;
        public CancellationTokenSource Cancellation { get; } = new();
        public Thread? Worker { get; set; }
        public BackgroundTaskState State { get; set; } = BackgroundTaskState.Ready;
        public bool Finished { get; set; }
        public long Progress;
        public string? Error { get; set; }
    }
}
