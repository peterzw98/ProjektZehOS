// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Diagnostics.CodeAnalysis;
using Cosmos.Kernel.Core.Scheduler;

namespace Cosmos.Kernel.Core.CPU;

// ZehOS patch ausnahme-hook, see PATCHES.md

/// <summary>
/// What the kernel does after a CPU exception, once the exception has been
/// reported on the serial port.
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public enum CpuExceptionAction
{
    /// <summary>Halt the system. This is the behavior without a hook.</summary>
    Halt = 0,

    /// <summary>
    /// Give up the faulting thread: switch to another thread and never return
    /// to the faulting code. The kernel marks the thread
    /// <see cref="SchedulerThreadState.Dead"/> and does not hand it back to the
    /// policy; reaping it (stack, registry slot) is the installed scheduler's
    /// job. When no switch can be staged, the kernel halts instead.
    /// </summary>
    AbandonThread = 1,
}

/// <summary>
/// The architecture-neutral facts about a CPU exception that a hook may base
/// its decision on. The full register state stays internal to the kernel; it
/// has a different layout on every architecture.
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public readonly struct CpuExceptionInfo
{
    /// <summary>Creates the description of one exception.</summary>
    /// <param name="vector">Exception vector (x64) or exception type (ARM64: 0 synchronous, 3 SError).</param>
    /// <param name="instructionPointer">Address of the faulting instruction (RIP or ELR).</param>
    /// <param name="stackPointer">Stack pointer of the interrupted code (RSP or SP).</param>
    /// <param name="faultAddress">Faulting data address (CR2 or FAR), zero when the exception has none.</param>
    /// <param name="cpuId">Index of the CPU the exception was raised on.</param>
    /// <param name="threadId">Id of the thread running on that CPU, <see cref="NoThread"/> when none.</param>
    public CpuExceptionInfo(ulong vector, ulong instructionPointer, ulong stackPointer, ulong faultAddress,
        uint cpuId, uint threadId)
    {
        Vector = vector;
        InstructionPointer = instructionPointer;
        StackPointer = stackPointer;
        FaultAddress = faultAddress;
        CpuId = cpuId;
        ThreadId = threadId;
    }

    /// <summary>The <see cref="ThreadId"/> when no thread was running on the CPU.</summary>
    public const uint NoThread = uint.MaxValue;

    /// <summary>Exception vector (x64) or exception type (ARM64: 0 synchronous, 3 SError).</summary>
    public ulong Vector { get; }

    /// <summary>Address of the faulting instruction (RIP or ELR).</summary>
    public ulong InstructionPointer { get; }

    /// <summary>Stack pointer of the interrupted code (RSP or SP).</summary>
    public ulong StackPointer { get; }

    /// <summary>Faulting data address (CR2 or FAR), zero when the exception has none.</summary>
    public ulong FaultAddress { get; }

    /// <summary>
    /// Index of the CPU the exception was raised on. With several CPUs the
    /// hook decides about this CPU's thread, not about a thread it reads
    /// elsewhere.
    /// </summary>
    public uint CpuId { get; }

    /// <summary>
    /// <see cref="SchedulerThread.Id"/> of the thread running on that CPU,
    /// the one <see cref="CpuExceptionAction.AbandonThread"/> gives up;
    /// <see cref="NoThread"/> when the scheduler runs no thread there.
    /// </summary>
    public uint ThreadId { get; }
}

/// <summary>
/// Decides what happens after a CPU exception. Runs in the exception path with
/// interrupts masked (x64: on the faulting thread's stack; ARM64: on the
/// exception stack): it must not allocate, take a lock, or log through anything
/// that may take a lock (the interrupted thread may hold it).
/// </summary>
/// <param name="exception">The exception being handled.</param>
/// <returns>What the kernel does next.</returns>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public delegate CpuExceptionAction CpuExceptionHandler(in CpuExceptionInfo exception);

/// <summary>
/// Optional hook for CPU exceptions. Without a handler every CPU exception
/// halts the system, as before. A second exception raised while a first one is
/// still being reported never reaches the handler and always halts.
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public static class CpuExceptionHook
{
    /// <summary>The installed handler, or null. Set it once at boot.</summary>
    public static CpuExceptionHandler? Handler { get; set; }
}
