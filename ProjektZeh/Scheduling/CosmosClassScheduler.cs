using System;
using System.Diagnostics;
using Cosmos.Kernel.Core.Scheduler;
using Cosmos.Kernel.System.Threading;
// Cosmos.Kernel.System.Threading hat eine gleichnamige oeffentliche CpuSet; IScheduler.SelectCpu verlangt die des Kerns.
using CpuSet = Cosmos.Kernel.Core.Scheduler.CpuSet;

#pragma warning disable COSMOS0001

namespace ProjektZeh.Scheduling;

/// <summary>
/// Cosmos-Adapter des Klassenschedulers: die einzige Datei mit <see cref="IScheduler"/>-Signaturen. Die Policy liegt in
/// <see cref="ClassRunQueues{T}"/> (hostgetestet); Etappe 5 passt nur diesen Adapter an die neue Schnittstelle an.
/// Ab Etappe 5 (Upstream 5b) ist die Hauptschleife ein normaler Thread der Klasse Interaktiv; die Policy sieht nie einen Idle-Thread.
/// </summary>
internal sealed class CosmosClassScheduler(CpuTopology topology) : IScheduler
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
        public ulong BusyNs;
    }

    // Beginnt mit dem ersten Typzugriff (praktisch die Registrierung des Schedulers), nicht mit dem Boot.
    private static readonly long startTimestamp = Stopwatch.GetTimestamp();

    internal static ulong TickNs => ClassRunQueues<SchedulerThread>.TickNs;

    public string Name => "ZehOS Klassen";
    internal CpuTopology Topology { get; } = topology;

    // Allokation ist nur hier, in ShutdownCpu und in OnThreadCreate erlaubt (IScheduler-Vertrag, Upstream 5b).
    public void InitializeCpu(PerCpuState state)
    {
        var queues = new ClassRunQueues<SchedulerThread>(CapacityPerClass, thread => thread.SchedulerData as ClassThreadState);
        state.SchedulerData = new CpuQueues(queues);
    }

    public void ShutdownCpu(PerCpuState state)
    {
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
        // E6-B: die CPU, auf der er zuletzt lief, ist der Cache-Hinweis fuer ClassPlacement.SelectCpu.
        if (next.SchedulerData is ClassThreadState nextState) nextState.LastCpu = (int)state.CpuId;
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
        // Last in echten Nanosekunden (unabhaengig von der Tickrate); der Idle-Thread zaehlt nicht, falls Cosmos ihn doch meldet.
        if (!current.IsIdle) cpu.BusyNs += elapsedNs;
        if (!cpu.Queues.Tick(current, elapsedNs)) return false;
        cpu.Preemptions++;
        return true;
    }

    // Groesste CPU-Zahl, fuer die SelectCpu und Balance ihre Spannen auf dem Stack anlegen. Beide laufen auch im IRQ auf dem
    // Interruptstapel; darueber gilt der Rueckfall (eigene CPU, sonst die erste erlaubte) bzw. kein Ausgleich.
    private const int MaxPlacedCpus = 64;

    /// <summary>
    /// E6-B: Platzierung nach <see cref="ClassPlacement.SelectCpu"/> mit Topologie, Klassen der laufenden Threads und Wartenden.
    /// Ohne Queue-Sperre, auch aus dem IRQ: nicht allokieren, nichts sperren, nichts werfen. Laufende Threads und Queue-Laengen
    /// fremder CPUs sind Schnappschuesse; Cosmos prueft die Antwort gegen die erlaubte Menge.
    /// </summary>
    public uint SelectCpu(SchedulerThread thread, PerCpuState[] cpus, CpuSet allowed)
    {
        uint fallback = allowed.Contains(thread.CpuId) ? thread.CpuId : allowed.First;
        int count = cpus.Length;
        // Weicht die gelesene Topologie von der Zahl der verwalteten CPUs ab, wuerde ClassPlacement werfen.
        if (count != Topology.CpuCount || count > MaxPlacedCpus) return fallback;
        Span<bool> allowedByCpu = stackalloc bool[count];
        Span<ThreadClass?> running = stackalloc ThreadClass?[count];
        Span<int> queued = stackalloc int[count];
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            allowedByCpu[i] = allowed.Contains((uint)i);
            any |= allowedByCpu[i];
            var state = cpus[i];
            var current = state?.CurrentThread;
            running[i] = current == null || current.IsIdle ? null
                : current.SchedulerData is ClassThreadState runningState ? runningState.Class : ThreadClass.Normal;
            queued[i] = state != null && Cpu(state) is { } cpu ? cpu.Queues.Waiting : 0;
        }
        if (!any) return fallback;
        var own = thread.SchedulerData as ClassThreadState;
        int selected = ClassPlacement.SelectCpu(Topology, own?.Class ?? ThreadClass.Normal, own?.LastCpu ?? -1, allowedByCpu, running, queued);
        return selected >= 0 ? (uint)selected : fallback;
    }

    /// <summary>
    /// Unter beiden Queue-Sperren. Ready: steht auf der Quelle und zieht um. Running: in keiner Struktur (Verdraengung beim
    /// Wechsel) und wird danach ohne OnThreadReady Ready, deshalb hier auf dem Ziel einreihen.
    /// </summary>
    public void OnThreadMigrate(SchedulerThread thread, PerCpuState fromState, PerCpuState toState)
    {
        if (thread.State == SchedulerThreadState.Ready) OnThreadBlocked(fromState, thread);
        OnThreadReady(toState, thread);
    }

    /// <summary>
    /// E6-B: Eine CPU ohne eigene Arbeit zieht den vordersten verschiebbaren Thread der hoechsten Klasse einer anderen CPU, in der
    /// Reihenfolge von <see cref="ClassPlacement.StealOrder"/> (Geschwister, Verbund, Rest). Im IRQ ohne Sperre gerufen: nimmt je
    /// fremde CPU deren Sperre, nie zwei zugleich und nie die eigene, und nennt nur einen Thread, der unter dieser Sperre gelesen
    /// wurde. Cosmos prueft die Antwort unter beiden Sperren erneut. Keine Allokation.
    /// </summary>
    public SchedulerThread? Balance(PerCpuState state, PerCpuState[] allCpuStates)
    {
        if (Cpu(state) is not { } own || own.Queues.Waiting > 0) return null;
        // Wie Stride: nur eine CPU ohne Arbeit zieht. Der periodische Aufruf aus dem Tick kommt auch, waehrend hier ein Thread
        // weiterlaeuft; ein Zug dann tauschte bei jedem Aufruf wartende Threads zwischen Nachbarn hin und her.
        if (state.CurrentThread is { IsIdle: false, State: SchedulerThreadState.Running }) return null;
        int count = allCpuStates.Length;
        int self = (int)state.CpuId;
        if (count != Topology.CpuCount || count > MaxPlacedCpus || self >= count) return null;
        Span<int> order = stackalloc int[count];
        int length = ClassPlacement.StealOrder(Topology, self, order);
        uint cpuId = state.CpuId;
        for (int n = 0; n < length; n++)
        {
            var victim = allCpuStates[order[n]];
            if (victim == null || Cpu(victim) is not { } other || other.Queues.Waiting == 0) continue;
            using (SchedulerManager.LockCpu(victim))
            {
                // Eine CPU auf ihrem Idle-Thread behaelt, was sie hat: Der Thread wurde gerade dorthin geweckt, und ihr eigener
                // Interrupt-Ausgang fuehrt ihn gleich aus (wie Stride).
                if (victim.CurrentThread is not { IsIdle: false } busy || busy.State != SchedulerThreadState.Running) continue;
                // PeekStealable nimmt einen Delegaten; ein Lambda mit cpuId allokierte hier im IRQ eine Closure. At(i) liefert
                // dieselbe Reihenfolge (Klassen aufsteigend, je Klasse vorn zuerst).
                var queues = other.Queues;
                for (int i = 0; i < queues.Count; i++)
                    if (queues.At(i) is { } candidate && MayMoveTo(candidate, cpuId)) return candidate;
            }
        }
        return null;
    }

    // Nicht gepinnt (mehr als eine CPU erlaubt), das Ziel erlaubt, bereit und schon einmal gelaufen: Einen Thread im Zustand
    // Created verschiebt Cosmos nicht (wie Stride, LastThreadAllowedOn).
    private static bool MayMoveTo(SchedulerThread thread, uint cpuId) =>
        thread.State == SchedulerThreadState.Ready && thread.Affinity.Count > 1 && thread.Affinity.Contains(cpuId);

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
    public static (ulong Preemptions, uint[] ThreadIds, int[] WaitingByClass) Snapshot()
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

    /// <summary>
    /// Diagnose einer CPU: laufender Thread (null = Idle), seine Klasse, Wartende je Klasse und belegte Nanosekunden (Summe der
    /// OnTick-Zeitanteile mit einem Nicht-Idle-Thread). Thread-Kontext wie <see cref="Snapshot()"/>: nimmt nur die Sperre dieser CPU.
    /// </summary>
    public static (uint? RunningId, ThreadClass RunningClass, int[] WaitingByClass, ulong BusyNs)? Snapshot(uint cpuId)
    {
        if (cpuId >= (uint)KernelCpus.Count || SchedulerManager.GetCpuState(cpuId) is not { } state) return null;
        var waitingByClass = new int[3];
        using (SchedulerManager.LockCpu(state))
        {
            if (Cpu(state) is not { } cpu) return null;
            for (int c = 0; c < waitingByClass.Length; c++) waitingByClass[c] = cpu.Queues.CountOf((ThreadClass)c);
            var current = state.CurrentThread;
            bool running = current is { IsIdle: false };
            var threadClass = running && current!.SchedulerData is ClassThreadState classState ? classState.Class : ThreadClass.Normal;
            return (running ? (uint?)current!.Id : null, threadClass, waitingByClass, cpu.BusyNs);
        }
    }

    /// <summary>Gemessene Nanosekunden seit dem Start der Zaehlung, Nenner fuer die Last aus <see cref="CpuDiagnostics.LoadPercent"/>.</summary>
    public static ulong ElapsedNs => (ulong)(Stopwatch.GetElapsedTime(startTimestamp).Ticks * 100);

    private static CpuQueues? Cpu(PerCpuState state) => state.SchedulerData as CpuQueues;
}
