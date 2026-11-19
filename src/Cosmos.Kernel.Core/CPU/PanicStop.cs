// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Diagnostics;
using System.Threading;
using Cosmos.Kernel.Core.Scheduler;

namespace Cosmos.Kernel.Core.CPU;

// ZehOS patch panic-stop-cpus, see PATCHES.md
/// <summary>
/// Stops every other CPU for good when one CPU halts the system, so nothing writes to a disk,
/// the screen or the serial port after the panic report. The first CPU to halt claims the panic
/// by compare-exchange and sends a non-maskable interrupt to every other online CPU; the NMI
/// handler (<see cref="HaltIfStopping"/>) halts the CPU it lands on, with interrupts masked,
/// and counts it. An NMI reaches a CPU that spins on a lock with interrupts masked, which a
/// fixed IPI (<see cref="CpuStop"/>) would not. The owner waits for the count at most
/// <see cref="TimeoutMilliseconds"/>, then goes on whether every CPU answered or not.
/// <para>Takes no lock and allocates nothing, so it works while the halting CPU holds any
/// lock (heap, serial, a queue). A CPU that halts while another owns the panic halts silently.
/// Giving up a faulting thread (the CPU exception hook) never comes here. Only the x64 Local
/// APIC sends the NMI; on ARM64 the other CPUs are not reached and the wait times out.</para>
/// </summary>
internal static class PanicStop
{
    /// <summary>The longest the halting CPU waits for the others to answer.</summary>
    internal const long TimeoutMilliseconds = 100;

    // The index + 1 of the CPU that owns the panic; 0 while nothing halts.
    private static int s_owner;

    // CPUs halted by the NMI. Each CPU counts at most once: the NMI handler never returns,
    // and the CPU takes no further NMI before an IRET.
    private static int s_halted;

    /// <summary>True once a CPU has claimed the panic.</summary>
    internal static bool IsStopping => Volatile.Read(ref s_owner) != 0;

    /// <summary>
    /// Claims the panic for the calling CPU and stops every other online CPU. Interrupts are
    /// masked by the caller. Returns true when the calling CPU owns the panic (also when it had
    /// claimed it before, a nested halt), false when another CPU owns it: the caller then halts
    /// without a report of its own, since the owner's report is the one that matters and the
    /// owner may stop this CPU at any point of a second one.
    /// </summary>
    internal static bool StopOthers()
    {
        // One CPU, or a halt before the application processors start (the count is set with
        // them): nothing to stop, and nothing claims, so the per-CPU register is not read
        // before it may be loaded.
        uint cpuCount = SchedulerManager.CpuCount;
        if (cpuCount <= 1)
        {
            return true;
        }

        uint self = PerCpuBlock.CurrentIndex;
        int previous = Interlocked.CompareExchange(ref s_owner, (int)self + 1, 0);
        if (previous != 0)
        {
            return previous == (int)self + 1;
        }

        int sent = 0;
        for (uint cpu = 0; cpu < cpuCount; cpu++)
        {
            if (cpu == self || Volatile.Read(ref PerCpuBlock.At(cpu).Online) == 0)
            {
                continue;
            }

            InterruptManager.SendPanicStop(PerCpuBlock.At(cpu).HardwareId);
            sent++;
        }

        if (sent == 0)
        {
            return true;
        }

        // Bounded: a CPU that does not answer (NMI blocked, not delivered, ARM64) costs at
        // most the timeout, never the report.
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * TimeoutMilliseconds / 1000;
        while (Volatile.Read(ref s_halted) < sent && Stopwatch.GetTimestamp() < deadline)
        {
        }

        return true;
    }

    /// <summary>
    /// The NMI's first step: when another CPU owns the panic, halts the calling CPU for good,
    /// interrupts masked, and returns never. Returns when nothing halts or when the calling CPU
    /// owns the panic, so an NMI from outside still reaches the exception path.
    /// </summary>
    internal static void HaltIfStopping()
    {
        int owner = Volatile.Read(ref s_owner);
        // A claimed panic means more than one CPU, so the per-CPU register is loaded.
        if (owner == 0 || owner == (int)PerCpuBlock.CurrentIndex + 1)
        {
            return;
        }

        Interlocked.Increment(ref s_halted);
        HaltForever();
    }

    /// <summary>Masks interrupts and halts the calling CPU for good. Allocation-free; any context.</summary>
    internal static void HaltForever()
    {
        InternalCpu.DisableInterrupts();
        while (true)
        {
            InternalCpu.Halt();
        }
    }
}
