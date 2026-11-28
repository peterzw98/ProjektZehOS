using System.Diagnostics.CodeAnalysis;

namespace Cosmos.Kernel.Core.Scheduler;

/// <summary>
/// Per-CPU scheduling state.
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public sealed class PerCpuState : SchedulerExtensible
{
    /// <summary>
    /// Per-CPU state is created by <see cref="SchedulerManager"/> during CPU
    /// bring-up. A scheduler receives one through its <see cref="IScheduler"/>
    /// hooks and attaches its own state to
    /// <see cref="SchedulerExtensible.SchedulerData"/>.
    /// </summary>
    internal PerCpuState()
    {
    }

    // ===== Identity =====

    /// <summary>
    /// ID of the CPU this state belongs to.
    /// </summary>
    public uint CpuId { get; internal set; }

    // ===== Current Execution =====

    /// <summary>
    /// Thread currently executing on this CPU, or <see langword="null"/>
    /// before the scheduler has run. Written by the switch on this CPU under
    /// its queue lock; a read from another CPU is a snapshot.
    /// </summary>
    public SchedulerThread? CurrentThread { get; internal set; }

    /// <summary>
    /// This CPU's idle thread, scheduled when no other thread is runnable, or
    /// <see langword="null"/> before the scheduler has set one up. Once set it
    /// stays set: the idle thread is never unregistered.
    /// </summary>
    public SchedulerThread? IdleThread { get; internal set; }

    // ===== Tick =====

    internal uint _tickCount;

    /// <summary>
    /// Timer interrupts this CPU handled since it started its tick. Each CPU's timer fires on
    /// its own, so the per-CPU count is the one that says a CPU is alive;
    /// <see cref="SchedulerManager.TickCount"/> is CPU 0's and keeps the pace of the thread
    /// clock (the timed waits, the suites' tick budgets) whatever the CPU count. Written by the
    /// tick on this CPU alone, read from any thread. Wraps: compare differences.
    /// </summary>
    public uint TickCount => Volatile.Read(ref _tickCount);

    // Set by ReadyThread when it wakes a thread (typically an ISR-side
    // InterruptEvent.Signal); consumed by ReschedulePendingFromIrq on
    // hardware-IRQ exit so the woken thread runs immediately instead of
    // sitting in the run queue until the next timer tick. BlockThread and
    // YieldThread set it to give up the CPU on that same exit. Written on
    // another CPU by a wake that queued a thread here (SMP phase 3a), read on
    // this one by its interrupt exit: always through Volatile. A remote
    // writer sets it under this CPU's queue lock and sends the reschedule
    // interrupt after; this CPU's own request (YieldThread, Park, the swap
    // with the world stopped) is a bare volatile store, since the exit that
    // consumes it runs here, under the lock, and a store that lands after
    // its read is served by the next exit. Park and YieldThread then raise
    // the reschedule interrupt here so that exit comes at once; YieldThread
    // stores and raises with interrupts masked, so both name the CPU it runs
    // on (ZehOS patch yield-now, see PATCHES.md).
    internal bool _needReschedule;

    // ZehOS patch yield-now, see PATCHES.md
    // The thread that last gave up the CPU here through YieldThread, until the
    // next switch-out on this CPU takes the request: that switch-out serves a
    // yield when it takes this same thread off, still Running. Written by
    // YieldThread with interrupts masked and read by the switch-out in
    // interrupt context, both on this CPU.
    internal SchedulerThread? _yieldRequestedBy;

    // ZehOS patch yield-now, see PATCHES.md
    /// <summary>
    /// <see langword="true"/> while the reschedule under way on this CPU takes
    /// off a thread that gave up the CPU through <c>Thread.Yield</c>, not one
    /// a tick or a wake preempts. Valid in <see cref="IScheduler.PickNext"/>
    /// and <see cref="IScheduler.OnThreadYield"/> of that reschedule, under
    /// this CPU's queue lock, <see langword="false"/> everywhere else. Since a
    /// yield switches at once, before any tick charged the slice, a policy
    /// that puts a preempted thread back first with the rest of its slice
    /// would hand a yielding one the CPU again at every turn it takes.
    /// </summary>
    public bool SwitchIsYield { get; internal set; }

    // The thread the last switch-out on this CPU evicted because its set
    // excludes this CPU (SMP phase 3b): left out of the run structure, still
    // Running when it was, and moved by SchedulerManager.MigratePending on
    // the same interrupt exit once this CPU's queue lock is released, under
    // both CPUs' locks in order. Written and read on this CPU alone, in
    // interrupt context.
    internal SchedulerThread? _pendingMigration;

    // ZehOS patch smt-sibling-yield, see PATCHES.md
    // The CPU the policy named in this CPU's last tick for the thread it runs
    // (IVoluntaryMove), or SchedulerManager.NoCpu: written by the tick before
    // the queue lock, consumed by the switch-out of the same exit. And the
    // destination of a voluntary eviction for SchedulerManager.MigratePending,
    // which consumes it with _pendingMigration. Both on this CPU alone, in
    // interrupt context.
    internal uint _voluntaryTarget = SchedulerManager.NoCpu;
    internal uint _pendingTarget = SchedulerManager.NoCpu;
    // ===== Address space =====

    // ZehOS patch user-cr3, see PATCHES.md
    // The user page table this CPU's CR3 holds, null for its kernel root, and
    // that root, read from CR3 before the first user table replaced it.
    // Written and read on this CPU alone, with interrupts masked.
    internal Memory.UserPageTable? _loadedTable;
    internal ulong _kernelRoot;

    // ZehOS patch tlb-shootdown, see PATCHES.md
    // Counts this CPU's CR3 writes: odd from just before a write, even from
    // just after it (UserPageTable.LoadAndCount), both interlocked. Written on
    // this CPU alone with interrupts masked, read by a shootdown on any CPU.
    internal long _tlbEpoch;

    // ===== Synchronization =====

    // The queue lock: held around every policy hook that touches this CPU's
    // run structure, around CurrentThread and around _needReschedule. The
    // owning CPU takes it from its tick and its interrupt exits (interrupts
    // already masked there, so the plain acquire); a remote CPU takes it, with
    // its own interrupts masked, to wake a thread onto this CPU or to pull one
    // off it (SchedulerManager.LockCpu). Two queue locks are held at once only
    // by a migration, in ascending CPU index; the registry lock comes after
    // either, the heap lock after that. Nothing allocates while it is held.
    internal SpinLock _lock;
}
