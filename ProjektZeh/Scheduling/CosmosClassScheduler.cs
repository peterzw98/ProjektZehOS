using Cosmos.Kernel.Core.Scheduler;
using Cosmos.Kernel.System.Threading;

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

    /// <summary>Zustand je CPU: Queues, Auswahlspur und Verdraengungen. Nur unter der Queue-Sperre dieser CPU geschrieben.</summary>
    private sealed class CpuQueues(ClassRunQueues<SchedulerThread> queues)
    {
        public readonly ClassRunQueues<SchedulerThread> Queues = queues;
        public readonly uint[] Trace = new uint[TraceLength];
        public int TraceNext;
        public int TraceCount;
        public ulong Preemptions;
    }

    // Queues der CPU 0: dort laeuft die Hauptschleife (Etappe 5: alle Threads auf CPU 0); ohne Sperre lesbarer Hinweis.
    private static ClassRunQueues<SchedulerThread>? firstCpu;
    internal static ulong TickNs => ClassRunQueues<SchedulerThread>.TickNs;
    internal static int WaitingThreads => firstCpu?.Waiting ?? 0;

    public string Name => "ZehOS Klassen";

    // Allokation ist nur hier, in ShutdownCpu und in OnThreadCreate erlaubt (IScheduler-Vertrag, Upstream 5b).
    public void InitializeCpu(PerCpuState state)
    {
        var queues = new ClassRunQueues<SchedulerThread>(CapacityPerClass, thread => thread.SchedulerData as ClassThreadState);
        state.SchedulerData = new CpuQueues(queues);
        if (state.CpuId == 0) firstCpu = queues;
    }

    public void ShutdownCpu(PerCpuState state)
    {
        if (ReferenceEquals(firstCpu, Cpu(state)?.Queues)) firstCpu = null;
        state.SchedulerData = null;
    }

    // Die Policy sieht nie einen Idle-Thread (Upstream 5b); die Hauptschleife ist ein normaler Thread.
    public void OnThreadCreate(PerCpuState state, SchedulerThread thread) => thread.SchedulerData = new ClassThreadState();

    public void OnThreadReady(PerCpuState state, SchedulerThread thread)
    {
        // Ein nach einer CPU-Ausnahme aufgegebener Wasm-Worker wird nie wieder eingereiht (Roadmap 12.4-I1).
        if (Runtime.FaultIsolation.Quarantine.Contains(thread.Id)) return;
        Cpu(state)?.Queues.Ready(thread);
    }

    public void OnThreadBlocked(PerCpuState state, SchedulerThread thread) => Cpu(state)?.Queues.Blocked(thread);

    public void OnThreadYield(PerCpuState state, SchedulerThread thread)
    {
        if (Runtime.FaultIsolation.Quarantine.Contains(thread.Id)) return;
        Cpu(state)?.Queues.Yielded(thread);
    }

    public void OnThreadExit(PerCpuState state, SchedulerThread thread)
    {
        Cpu(state)?.Queues.Blocked(thread);
        thread.SchedulerData = null;
    }

    /// <summary>
    /// Unter der Queue-Sperre. null heisst laut Vertrag: der Laufende behaelt die CPU, sonst laeuft der Idle-Thread. Liefert die
    /// Policy den Laufenden zurueck, wird daraus null, damit der Mechanismus ihn nicht als neuen Thread behandelt.
    /// </summary>
    public SchedulerThread? PickNext(PerCpuState state)
    {
        if (Cpu(state) is not { } cpu) return null;
        var current = state.CurrentThread;
        var running = current is { State: SchedulerThreadState.Running, IsIdle: false } ? current : null;
        var next = cpu.Queues.Pick(running);
        if (next == null || ReferenceEquals(next, running)) return null;
        cpu.Trace[cpu.TraceNext] = next.Id;
        cpu.TraceNext = (cpu.TraceNext + 1) % TraceLength;
        if (cpu.TraceCount < TraceLength) cpu.TraceCount++;
        return next;
    }

    public void OnPickFailed(PerCpuState state, SchedulerThread thread) => Cpu(state)?.Queues.PickFailed(thread);

    public bool OnTick(PerCpuState state, SchedulerThread current, ulong elapsedNs)
    {
        if (Cpu(state) is not { } cpu) return false;
        current.TotalRuntime += elapsedNs;
        if (!cpu.Queues.Tick(current, elapsedNs)) return false;
        cpu.Preemptions++;
        return true;
    }

    // Etappe 5: keine Platzierung; der Thread bleibt auf seiner CPU, wenn sie erlaubt ist. Topologie kommt mit E6-B.
    // Ohne Queue-Sperre, auch aus dem IRQ: nicht allokieren, nichts sperren.
    public uint SelectCpu(SchedulerThread thread, PerCpuState[] cpus, CpuSet allowed) =>
        allowed.Contains(thread.CpuId) ? thread.CpuId : allowed.First;

    /// <summary>
    /// Unter beiden Queue-Sperren. Ready: steht auf der Quelle und zieht um. Running: in keiner Struktur (Verdraengung beim
    /// Wechsel) und wird danach ohne OnThreadReady Ready, deshalb hier auf dem Ziel einreihen.
    /// </summary>
    public void OnThreadMigrate(SchedulerThread thread, PerCpuState fromState, PerCpuState toState)
    {
        if (thread.State == SchedulerThreadState.Ready) OnThreadBlocked(fromState, thread);
        OnThreadReady(toState, thread);
    }

    // Etappe 5: kein Ausgleich zwischen CPUs.
    public SchedulerThread? Balance(PerCpuState state, PerCpuState[] allCpuStates) => null;

    /// <summary>0, 1, 2 = Interaktiv, Normal, Hintergrund; andere Werte werden ignoriert. Der Aufrufer haelt die Queue-Sperre.</summary>
    public void SetPriority(PerCpuState state, SchedulerThread thread, long priority)
    {
        if (priority is < 0 or > 2) return;
        Cpu(state)?.Queues.SetClass(thread, (ThreadClass)priority);
    }

    // Der Aufrufer haelt die Queue-Sperre; die Sperre ist nicht reentrant, deshalb hier weder Maske noch LockCpu.
    public long GetPriority(SchedulerThread thread) =>
        thread.SchedulerData is ClassThreadState classState ? (long)classState.Class : (long)ThreadClass.Normal;

    public int GetRunQueueCount(PerCpuState state) => Cpu(state)?.Queues.Count ?? 0;

    public SchedulerThread? GetRunQueueThread(PerCpuState state, int index) => Cpu(state)?.Queues.At(index);

    /// <summary>
    /// Verdraengungen ueber alle CPUs, Auswahlspur und Wartende je Klasse (Interaktiv, Normal, Hintergrund) der CPU 0.
    /// Thread-Kontext, kein Hook: nimmt die Sperre jeder CPU einzeln, nie zwei zugleich, und allokiert nur ausserhalb.
    /// </summary>
    public (ulong Preemptions, uint[] ThreadIds, int[] WaitingByClass) Snapshot()
    {
        var buffer = new uint[TraceLength];
        var waitingByClass = new int[3];
        int count = 0;
        ulong taken = 0;
        for (uint id = 0; id < (uint)KernelCpus.Count; id++)
        {
            if (SchedulerManager.GetCpuState(id) is not { } state) continue;
            using (SchedulerManager.LockCpu(state))
            {
                if (Cpu(state) is not { } cpu) continue;
                taken += cpu.Preemptions;
                if (id != 0) continue;
                count = cpu.TraceCount;
                for (int i = 0; i < count; i++) buffer[i] = cpu.Trace[(cpu.TraceNext - count + i + TraceLength) % TraceLength];
                for (int c = 0; c < waitingByClass.Length; c++) waitingByClass[c] = cpu.Queues.CountOf((ThreadClass)c);
            }
        }
        return (taken, count == buffer.Length ? buffer : buffer[..count], waitingByClass);
    }

    private static CpuQueues? Cpu(PerCpuState state) => state.SchedulerData as CpuQueues;
}
