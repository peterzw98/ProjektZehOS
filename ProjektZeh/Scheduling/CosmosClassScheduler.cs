using Cosmos.Kernel.Core.Scheduler;

#pragma warning disable COSMOS0001

namespace ProjektZeh.Scheduling;

/// <summary>
/// Cosmos-Adapter des Klassenschedulers: die einzige Datei mit <see cref="IScheduler"/>-Signaturen. Die Policy liegt in
/// <see cref="ClassRunQueues{T}"/> (hostgetestet); Etappe 5 passt nur diesen Adapter an die neue Schnittstelle an.
/// Ab Etappe 5 (Upstream 5b) ist die Hauptschleife ein normaler Thread der Klasse Interaktiv; die Policy sieht nie einen Idle-Thread.
/// </summary>
internal sealed class CosmosClassScheduler : IScheduler
{
    // Yielded und PickFailed haben keinen Fehlerweg; die Kapazitaet je Klasse muss die Threadzahl decken (Cosmos kennt kein Mehr).
    private const int CapacityPerClass = SchedulerThread.MaxThreadCount;
    private const int TraceLength = 128;

    // Queues der CPU 0: dort laeuft die Hauptschleife (ab Etappe 5 weiter); ohne Sperre lesbarer Hinweis.
    private static ClassRunQueues<SchedulerThread>? firstCpu;
    internal static ulong TickNs => ClassRunQueues<SchedulerThread>.TickNs;
    internal static int WaitingThreads => firstCpu?.Waiting ?? 0;

    private readonly uint[] selectedThreads = new uint[TraceLength];
    private int traceNext;
    private int traceCount;
    private ulong preemptions;

    public string Name => "ZehOS Klassen";

    public void InitializeCpu(PerCpuState state)
    {
        var queues = new ClassRunQueues<SchedulerThread>(CapacityPerClass, thread => thread.SchedulerData as ClassThreadState);
        state.SchedulerData = queues;
        if (state.CpuId == 0) firstCpu = queues;
    }

    public void ShutdownCpu(PerCpuState state)
    {
        if (ReferenceEquals(firstCpu, state.SchedulerData)) firstCpu = null;
        state.SchedulerData = null;
    }

    public void OnThreadCreate(PerCpuState state, SchedulerThread thread) =>
        thread.SchedulerData = new ClassThreadState();

    public void OnThreadReady(PerCpuState state, SchedulerThread thread)
    {
        // Ein nach einer CPU-Ausnahme aufgegebener Wasm-Worker wird nie wieder eingereiht (Roadmap 12.4-I1).
        if (Runtime.FaultIsolation.Quarantine.Contains(thread.Id)) return;
        Queues(state)?.Ready(thread);
    }

    public void OnThreadBlocked(PerCpuState state, SchedulerThread thread) => Queues(state)?.Blocked(thread);

    public void OnThreadYield(PerCpuState state, SchedulerThread thread)
    {
        if (Runtime.FaultIsolation.Quarantine.Contains(thread.Id)) return;
        Queues(state)?.Yielded(thread);
    }

    public void OnThreadExit(PerCpuState state, SchedulerThread thread)
    {
        Queues(state)?.Blocked(thread);
        thread.SchedulerData = null;
    }

    public SchedulerThread? PickNext(PerCpuState state)
    {
        // Ein schlafengelegter, noch vor dem Wechsel geweckter Thread steht in der Queue, obwohl er CurrentThread ist: dann
        // ist sein State nicht Running und Pick bekommt null (Cosmos 3.0.97 SchedulerManager.cs:1213-1280).
        var current = state.CurrentThread;
        var running = current is { State: SchedulerThreadState.Running } ? current : null;
        var next = Queues(state)?.Pick(running);
        if (next != null)
        {
            selectedThreads[traceNext] = next.Id;
            traceNext = (traceNext + 1) % selectedThreads.Length;
            if (traceCount < selectedThreads.Length) traceCount++;
        }
        return next;
    }

    public void OnPickFailed(PerCpuState state, SchedulerThread thread) => Queues(state)?.PickFailed(thread);

    public bool OnTick(PerCpuState state, SchedulerThread current, ulong elapsedNs)
    {
        var queues = Queues(state);
        if (queues is null || current is null) return false;
        current.TotalRuntime += elapsedNs;
        if (!queues.Tick(current, elapsedNs)) return false;
        preemptions++;
        return true;
    }

    // Cosmos 3.0.97 verwendet nur eine CPU; feste Platzierung respektiert Pinning.
    public uint SelectCpu(SchedulerThread thread, uint currentCpu, uint cpuCount) => currentCpu;

    public void OnThreadMigrate(SchedulerThread thread, PerCpuState fromState, PerCpuState toState)
    {
        OnThreadBlocked(fromState, thread);
        OnThreadReady(toState, thread);
    }

    public void Balance(PerCpuState state, PerCpuState[] allCpuStates) { }

    /// <summary>0, 1, 2 = Interaktiv, Normal, Hintergrund; andere Werte werden ignoriert.</summary>
    public void SetPriority(PerCpuState state, SchedulerThread thread, long priority)
    {
        if (priority is < 0 or > 2) return;
        // Cosmos ruft diesen Einstieg ohne Maske auf (SchedulerManager.cs ~Z. 1000): ohne sie kaeme der Tick mitten in SetClass.
        var mask = SchedulerManager.MaskInterrupts();
        try { Queues(state)?.SetClass(thread, (ThreadClass)priority); }
        finally { mask.Dispose(); }
    }

    public long GetPriority(SchedulerThread thread)
    {
        var mask = SchedulerManager.MaskInterrupts();
        try { return thread.SchedulerData is ClassThreadState classState ? (long)classState.Class : (long)ThreadClass.Normal; }
        finally { mask.Dispose(); }
    }

    public int GetRunQueueCount(PerCpuState state)
    {
        var mask = SchedulerManager.MaskInterrupts();
        try { return Queues(state)?.Count ?? 0; }
        finally { mask.Dispose(); }
    }

    public SchedulerThread? GetRunQueueThread(PerCpuState state, int index)
    {
        var mask = SchedulerManager.MaskInterrupts();
        try { return Queues(state)?.At(index); }
        finally { mask.Dispose(); }
    }

    /// <summary>Verdraengungen, Auswahlspur und wartende Threads je Klasse (Interaktiv, Normal, Hintergrund) der CPU 0.</summary>
    public (ulong Preemptions, uint[] ThreadIds, int[] WaitingByClass) Snapshot()
    {
        // Nicht unter der Maske allokieren: seit Cosmos 3.0.97 sperrt der Heap statt zu maskieren, und Parken mit
        // maskierten Interrupts loest eine Panic aus.
        var buffer = new uint[selectedThreads.Length];
        var waitingByClass = new int[3];
        int count;
        ulong taken;
        var mask = SchedulerManager.MaskInterrupts();
        try
        {
            count = traceCount;
            for (int i = 0; i < count; i++) buffer[i] = selectedThreads[(traceNext - count + i + selectedThreads.Length) % selectedThreads.Length];
            taken = preemptions;
            if (firstCpu is { } queues)
                for (int c = 0; c < waitingByClass.Length; c++) waitingByClass[c] = queues.CountOf((ThreadClass)c);
        }
        finally { mask.Dispose(); }
        return (taken, count == buffer.Length ? buffer : buffer[..count], waitingByClass);
    }

    private static ClassRunQueues<SchedulerThread>? Queues(PerCpuState state) => state.SchedulerData as ClassRunQueues<SchedulerThread>;
}
