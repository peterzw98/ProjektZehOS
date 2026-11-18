using Cosmos.Kernel.Core.Scheduler;

#pragma warning disable COSMOS0001

namespace ProjektZeh.Scheduling;

internal sealed class CosmosRoundRobinScheduler : IScheduler
{
    public const ulong QuantumNs = 2 * SchedulerManager.DefaultQuantumNs;
    private readonly uint[] selectedThreads = new uint[128];
    private int traceNext;
    private int traceCount;
    private ulong preemptions;
    // Der Boot-Thread (Idle-Thread, unsere Hauptschleife) entsteht ohne OnThreadCreate und haette sonst keine Scheibe.
    private readonly SchedulerTimeSlice idleSlice = new(QuantumNs);
    public string Name => "ZehOS RoundRobin";

    public void InitializeCpu(PerCpuState state) => state.SchedulerData =
        new RoundRobinCore<SchedulerThread>(SchedulerThread.MaxThreadCount,
            thread => thread.SchedulerData as SchedulerTimeSlice ?? (thread.IsIdle ? idleSlice : null), thread => thread.IsIdle);
    public void ShutdownCpu(PerCpuState state) => state.SchedulerData = null;
    public void OnThreadCreate(PerCpuState state, SchedulerThread thread) => thread.SchedulerData = new SchedulerTimeSlice(QuantumNs);
    public void OnThreadReady(PerCpuState state, SchedulerThread thread) => (state.SchedulerData as RoundRobinCore<SchedulerThread>)?.Ready(thread);
    public void OnThreadBlocked(PerCpuState state, SchedulerThread thread) => (state.SchedulerData as RoundRobinCore<SchedulerThread>)?.Blocked(thread);
    public void OnThreadYield(PerCpuState state, SchedulerThread thread) => OnThreadReady(state, thread);
    public void OnThreadExit(PerCpuState state, SchedulerThread thread)
    {
        OnThreadBlocked(state, thread);
        thread.SchedulerData = null;
    }
    public SchedulerThread? PickNext(PerCpuState state)
    {
        var next = (state.SchedulerData as RoundRobinCore<SchedulerThread>)?.Pick();
        if (next != null)
        {
            selectedThreads[traceNext] = next.Id;
            traceNext = (traceNext + 1) % selectedThreads.Length;
            if (traceCount < selectedThreads.Length) traceCount++;
        }
        return next;
    }
    public void OnPickFailed(PerCpuState state, SchedulerThread thread) => (state.SchedulerData as RoundRobinCore<SchedulerThread>)?.PickFailed(thread);
    public bool OnTick(PerCpuState state, SchedulerThread current, ulong elapsedNs)
    {
        var queue = state.SchedulerData as RoundRobinCore<SchedulerThread>;
        if (queue is null || current is null) return false;
        current.TotalRuntime += elapsedNs;
        if (!queue.Tick(current, elapsedNs)) return false;
        preemptions++;
        return true;
    }
    // Cosmos 3.0.94 verwendet nur eine CPU; feste Platzierung respektiert Pinning.
    public uint SelectCpu(SchedulerThread thread, uint currentCpu, uint cpuCount) => currentCpu;
    public void OnThreadMigrate(SchedulerThread thread, PerCpuState fromState, PerCpuState toState)
    {
        OnThreadBlocked(fromState, thread);
        OnThreadReady(toState, thread);
    }
    public void Balance(PerCpuState state, PerCpuState[] allCpuStates) { }
    public void SetPriority(PerCpuState state, SchedulerThread thread, long priority) { }
    public long GetPriority(SchedulerThread thread) => 0;
    public int GetRunQueueCount(PerCpuState state)
    {
        var mask = SchedulerManager.MaskInterrupts();
        try { return (state.SchedulerData as RoundRobinCore<SchedulerThread>)?.Count ?? 0; }
        finally { mask.Dispose(); }
    }
    public SchedulerThread? GetRunQueueThread(PerCpuState state, int index)
    {
        var mask = SchedulerManager.MaskInterrupts();
        try { return (state.SchedulerData as RoundRobinCore<SchedulerThread>)?.At(index); }
        finally { mask.Dispose(); }
    }
    public (ulong Preemptions, uint[] ThreadIds) Snapshot()
    {
        // Nicht unter der Maske allokieren: seit Cosmos 3.0.97 sperrt der Heap statt zu maskieren, und Parken mit
        // maskierten Interrupts loest eine Panic aus.
        var buffer = new uint[selectedThreads.Length];
        int count;
        ulong taken;
        var mask = SchedulerManager.MaskInterrupts();
        try
        {
            count = traceCount;
            for (int i = 0; i < count; i++) buffer[i] = selectedThreads[(traceNext - count + i + selectedThreads.Length) % selectedThreads.Length];
            taken = preemptions;
        }
        finally { mask.Dispose(); }
        return (taken, count == buffer.Length ? buffer : buffer[..count]);
    }
}
