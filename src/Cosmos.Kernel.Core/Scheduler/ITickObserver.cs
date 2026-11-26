using System.Diagnostics.CodeAnalysis;

namespace Cosmos.Kernel.Core.Scheduler;

// ZehOS patch tick-observer, see PATCHES.md
/// <summary>
/// An optional extension of <see cref="IScheduler"/>: a policy that also
/// implements it sees every timer tick of every CPU, the ticks of a CPU on its
/// idle thread included, which <see cref="IScheduler.OnTick"/> never sees. With
/// <see cref="SchedulerManager.Nudge"/> it can then interrupt another CPU.
/// <para>
/// What for: a hypervisor may hold back a CPU's expired timer interrupt until
/// that virtual CPU next exits to it (measured under VMware Workstation, ZehOS
/// #727). A CPU that computes without ever exiting then goes without ticks, and
/// nothing preempts it, for tens of milliseconds up to seconds. A halted CPU
/// exits and takes its ticks on time; seeing that another CPU's
/// <see cref="PerCpuState.TickCount"/> stands still, it can send it an
/// interrupt, whose delivery makes it exit and take the held-back tick.
/// </para>
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public interface ITickObserver
{
    /// <summary>
    /// Called on <paramref name="cpuState"/>'s CPU from each of its timer
    /// interrupts, only on a machine with several CPUs, before the policy's
    /// <see cref="IScheduler.OnTick"/> and whether or not that CPU runs its
    /// idle thread. Under no lock, with interrupts masked, like
    /// <see cref="IScheduler.Balance"/>: what the hook reads of other CPUs is a
    /// snapshot. Must not allocate, block or log.
    /// </summary>
    /// <param name="cpuState">The CPU whose tick this is.</param>
    /// <param name="allCpuStates">Every CPU the scheduler manages, by index.</param>
    void OnCpuTick(PerCpuState cpuState, PerCpuState[] allCpuStates);
}
