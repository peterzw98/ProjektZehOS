using System.Diagnostics.CodeAnalysis;

namespace Cosmos.Kernel.Core.Scheduler;

// ZehOS patch smt-sibling-yield, see PATCHES.md
/// <summary>
/// An optional extension of <see cref="IScheduler"/>: a policy that also
/// implements it can move a thread that is running, not only one that waits.
/// Without it a thread leaves its CPU only when it blocks, ends, or its set
/// excludes the CPU, so a thread that computes without ever blocking stays
/// where it was placed, next to a busy SMT sibling, for as long as it runs,
/// even when a whole core has come free meanwhile. <see cref="IScheduler.Balance"/>
/// cannot help: it pulls only queued threads, never one a CPU is on.
/// <para>
/// The move is a push from the CPU that runs the thread: once in
/// <see cref="SchedulerManager.VoluntaryMoveIntervalTicks"/> of its ticks the
/// mechanism asks <see cref="SelectMoveTarget"/>, and on an answer it takes
/// the thread off at that same interrupt exit and moves it through the path
/// an eviction takes (<see cref="IScheduler.OnThreadMigrate"/> for a Running
/// thread, then the reschedule interrupt to the destination). The thread's
/// set is not touched.
/// </para>
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public interface IVoluntaryMove
{
    /// <summary>
    /// Name a CPU the thread <paramref name="current"/>, running on
    /// <paramref name="cpuState"/>'s CPU, should move to now, or that CPU's
    /// own id for none. Called on that CPU, from its timer interrupt, once in
    /// <see cref="SchedulerManager.VoluntaryMoveIntervalTicks"/> of its ticks,
    /// only on a machine with several CPUs, only for a thread of a policy's
    /// that is Running and whose effective set names more than this CPU.
    /// Under no lock, with interrupts masked, like <see cref="IScheduler.Balance"/>:
    /// what the hook reads of other CPUs is a snapshot. Must not allocate,
    /// block or log.
    /// <para>
    /// The mechanism drops an answer outside <paramref name="allowed"/>. It
    /// checks the destination again under both CPUs' queue locks: a CPU that
    /// runs anything but its idle thread by then, or that has a reschedule
    /// pending, is stale, and the thread goes back into this CPU's run
    /// structure as a preemption would have put it. The policy keeps a moved
    /// thread from moving back and forth; the mechanism does not.
    /// </para>
    /// </summary>
    /// <param name="cpuState">The CPU the thread runs on.</param>
    /// <param name="current">The thread that CPU runs.</param>
    /// <param name="allCpuStates">Every CPU the scheduler manages, by index.</param>
    /// <param name="allowed">The thread's effective set without this CPU; never empty.</param>
    /// <returns>The CPU to move to, one of <paramref name="allowed"/>, or <paramref name="cpuState"/>'s id for none.</returns>
    uint SelectMoveTarget(PerCpuState cpuState, SchedulerThread current, PerCpuState[] allCpuStates, CpuSet allowed);
}
