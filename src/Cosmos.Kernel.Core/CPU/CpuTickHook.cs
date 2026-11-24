// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Diagnostics.CodeAnalysis;
using Cosmos.Kernel.Core.Scheduler;

namespace Cosmos.Kernel.Core.CPU;

// ZehOS patch aot-loop-exit, see PATCHES.md

/// <summary>
/// The architecture-neutral facts about the thread a scheduler tick
/// interrupted, all a tick hook may base its decision on.
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public readonly struct CpuTickInfo
{
    /// <summary>Creates the description of one tick.</summary>
    /// <param name="instructionPointer">Address the interrupted code continues at.</param>
    /// <param name="stackPointer">Stack pointer of the interrupted code.</param>
    /// <param name="cpuId">Index of the CPU that took the tick.</param>
    /// <param name="threadId">Id of the interrupted thread.</param>
    public CpuTickInfo(ulong instructionPointer, ulong stackPointer, uint cpuId, uint threadId)
    {
        InstructionPointer = instructionPointer;
        StackPointer = stackPointer;
        CpuId = cpuId;
        ThreadId = threadId;
    }

    /// <summary>Address the interrupted code continues at.</summary>
    public ulong InstructionPointer { get; }

    /// <summary>Stack pointer of the interrupted code.</summary>
    public ulong StackPointer { get; }

    /// <summary>Index of the CPU that took the tick.</summary>
    public uint CpuId { get; }

    /// <summary><see cref="SchedulerThread.Id"/> of the interrupted thread, the one running on that CPU.</summary>
    public uint ThreadId { get; }
}

/// <summary>
/// Decides whether the interrupted thread continues somewhere else. Runs on
/// every tick of every CPU with interrupts masked, possibly on several CPUs at
/// once: it must not allocate, take a lock, log or throw.
/// </summary>
/// <param name="tick">The thread the tick interrupted.</param>
/// <returns>
/// Zero to leave the thread alone, otherwise the address it continues at. Only
/// the instruction pointer changes: the code there finds the registers and the
/// stack pointer of the interrupted code, at an arbitrary point of it, and must
/// itself establish a stack it can use. Whatever the abandoned frames held
/// (locks, memory) stays held, so a hook redirects only code it knows to hold
/// nothing.
/// </returns>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public delegate nuint CpuTickHandler(in CpuTickInfo tick);

/// <summary>
/// Optional hook in the scheduler tick that can make the interrupted thread
/// continue at another address. Only kernel-mode code interrupted with
/// interrupts enabled, on a running thread that is no idle thread, is offered.
/// Called before the tick reschedules. x64 only: ARM64 never calls it.
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public static class CpuTickHook
{
    /// <summary>The installed handler, or null. Set it once at boot.</summary>
    public static CpuTickHandler? Handler { get; set; }

    /// <summary>
    /// The address the thread running on this CPU continues at, zero for the
    /// interrupted one. Called by the timer interrupt, masked, before
    /// <see cref="SchedulerManager.OnTimerInterrupt"/>: until then the running
    /// thread is the one whose frame the interrupt saved.
    /// </summary>
    /// <param name="cpuId">Index of this CPU.</param>
    /// <param name="instructionPointer">Interrupted instruction pointer.</param>
    /// <param name="stackPointer">Interrupted stack pointer.</param>
    internal static nuint Redirect(uint cpuId, ulong instructionPointer, ulong stackPointer)
    {
        CpuTickHandler? handler = Handler;
        if (handler is null || !SchedulerManager.IsRunning)
        {
            return 0;
        }

        SchedulerThread? thread = SchedulerManager.CurrentCpuState?.CurrentThread;
        if (thread is null || thread.IsIdle || thread.State != SchedulerThreadState.Running)
        {
            return 0;
        }

        return handler(new CpuTickInfo(instructionPointer, stackPointer, cpuId, thread.Id));
    }
}
