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
    /// to the faulting code. Keeping the thread off the CPU afterwards is the
    /// installed scheduler's job (it must not queue it again). When no switch
    /// can be staged, the kernel halts instead.
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
    public CpuExceptionInfo(ulong vector, ulong instructionPointer, ulong stackPointer, ulong faultAddress)
    {
        Vector = vector;
        InstructionPointer = instructionPointer;
        StackPointer = stackPointer;
        FaultAddress = faultAddress;
    }

    /// <summary>Exception vector (x64) or exception type (ARM64: 0 synchronous, 3 SError).</summary>
    public ulong Vector { get; }

    /// <summary>Address of the faulting instruction (RIP or ELR).</summary>
    public ulong InstructionPointer { get; }

    /// <summary>Stack pointer of the interrupted code (RSP or SP).</summary>
    public ulong StackPointer { get; }

    /// <summary>Faulting data address (CR2 or FAR), zero when the exception has none.</summary>
    public ulong FaultAddress { get; }
}

/// <summary>
/// Decides what happens after a CPU exception. Runs with interrupts masked on
/// the faulting thread's stack: it must not allocate, take a lock, or log
/// through anything that may take a lock (the interrupted thread may hold it).
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
