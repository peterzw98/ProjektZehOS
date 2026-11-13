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
    public string Name => "ZehOS RoundRobin";

    public void InitializeCpu(PerCpuState state) => state.SchedulerData = new RoundRobinQueue<SchedulerThread>(SchedulerThread.MaxThreadCount);
    public void ShutdownCpu(PerCpuState state) => state.SchedulerData = null;
    public void OnThreadCreate(PerCpuState state, SchedulerThread thread) => thread.SchedulerData = new SchedulerTimeSlice(QuantumNs);
    public void OnThreadReady(PerCpuState state, SchedulerThread thread)
    {
        (thread.SchedulerData as SchedulerTimeSlice)?.Reset();
        (state.SchedulerData as RoundRobinQueue<SchedulerThread>)?.Add(thread);
    }
    public void OnThreadBlocked(PerCpuState state, SchedulerThread thread) => (state.SchedulerData as RoundRobinQueue<SchedulerThread>)?.Remove(thread);
    public void OnThreadYield(PerCpuState state, SchedulerThread thread) => OnThreadReady(state, thread);
    public void OnThreadExit(PerCpuState state, SchedulerThread thread)
    {
        OnThreadBlocked(state, thread);
        thread.SchedulerData = null;
    }
    public SchedulerThread? PickNext(PerCpuState state)
    {
        var next = (state.SchedulerData as RoundRobinQueue<SchedulerThread>)?.Take();
        if (next != null)
        {
            selectedThreads[traceNext] = next.Id;
            traceNext = (traceNext + 1) % selectedThreads.Length;
            if (traceCount < selectedThreads.Length) traceCount++;
        }
        return next;
    }
    public void OnPickFailed(PerCpuState state, SchedulerThread thread) => (state.SchedulerData as RoundRobinQueue<SchedulerThread>)?.AddFirst(thread);
    public bool OnTick(PerCpuState state, SchedulerThread current, ulong elapsedNs)
    {
        var queue = state.SchedulerData as RoundRobinQueue<SchedulerThread>;
        if (queue is null || current is null) return false;
        current.TotalRuntime += elapsedNs;
        var slice = current.SchedulerData as SchedulerTimeSlice;
        if (slice != null && !slice.Charge(elapsedNs)) return false;
        if (queue.Count == 0) { slice?.Reset(); return false; }
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
        try { return (state.SchedulerData as RoundRobinQueue<SchedulerThread>)?.Count ?? 0; }
        finally { mask.Dispose(); }
    }
    public SchedulerThread? GetRunQueueThread(PerCpuState state, int index)
    {
        var mask = SchedulerManager.MaskInterrupts();
        try { return (state.SchedulerData as RoundRobinQueue<SchedulerThread>)?.At(index); }
        finally { mask.Dispose(); }
    }
    public (ulong Preemptions, uint[] ThreadIds) Snapshot()
    {
        var mask = SchedulerManager.MaskInterrupts();
        try
        {
            var ids = new uint[traceCount];
            for (int i = 0; i < traceCount; i++) ids[i] = selectedThreads[(traceNext - traceCount + i + selectedThreads.Length) % selectedThreads.Length];
            return (preemptions, ids);
        }
        finally { mask.Dispose(); }
    }
}
