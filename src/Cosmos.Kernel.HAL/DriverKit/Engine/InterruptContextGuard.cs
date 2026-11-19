// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Runtime.CompilerServices;
using Cosmos.Kernel.Core;
using Cosmos.Kernel.Core.CPU;

namespace Cosmos.Kernel.HAL.DriverKit.Engine;

/// <summary>
/// Knows whether the current code runs inside a driver's interrupt handler.
/// The trampoline enters it around every handler, and the binding's
/// thread-context members refuse to run while it is entered: a handler that
/// tries to sleep, wait, allocate a resource or publish a device is stopped
/// with the member's name instead of a deadlock. In a synthetic dispatch
/// (a test raising a source from thread context) that is an exception the
/// test can assert; in a real interrupt it is a panic, because building an
/// exception there would allocate in the very place the guard forbids it.
/// </summary>
/// <remarks>
/// ZehOS patch irq-guard-per-cpu, see PATCHES.md. The depth and the synthetic
/// flag are per CPU: a handler on one CPU says nothing about the thread code
/// another CPU runs at the same time. One word for all CPUs made every
/// thread-context member on every other CPU halt (or throw) while any CPU was
/// in a handler. The slots are a static inline array, no allocation and no
/// static constructor, so the first <see cref="Enter"/> may come from a real
/// interrupt. Only the CPU itself writes its slot, with interrupts masked.
/// </remarks>
internal static class InterruptContextGuard
{
    private static PerCpuSlots s_slots;

    /// <summary>True while a driver interrupt handler runs on this CPU.</summary>
    public static bool IsInHandler
    {
        get
        {
            // Index and slot are read with interrupts masked: thread code can otherwise be
            // moved to another CPU between the two and read the old CPU's slot, which may be
            // in a handler by then. A handler itself runs masked already; the scope restores
            // the caller's state.
            using (InternalCpu.DisableInterruptsScope())
            {
                return Volatile.Read(ref CurrentSlot.Depth) > 0;
            }
        }
    }

    // The calling CPU's slot. Interrupts must be masked while the reference is in use.
    private static ref Slot CurrentSlot => ref s_slots[(int)PerCpuBlock.CurrentIndex];

    /// <summary>Marks the start of a handler dispatch. Interrupts are masked by the caller.</summary>
    /// <param name="synthetic">True when a test raises the source from thread context.</param>
    internal static void Enter(bool synthetic)
    {
        ref Slot slot = ref CurrentSlot;
        slot.Synthetic = synthetic;
        slot.Depth++;
    }

    /// <summary>Marks the end of a handler dispatch.</summary>
    internal static void Exit()
    {
        CurrentSlot.Depth--;
    }

    /// <summary>Stops a thread-context member called from a driver interrupt handler.</summary>
    /// <param name="member">The member the caller is in, for the message.</param>
    /// <exception cref="InvalidOperationException">A driver interrupt handler is running, in a synthetic dispatch.</exception>
    public static void ThrowIfInHandler(string member)
    {
        bool inHandler;
        bool synthetic;
        using (InternalCpu.DisableInterruptsScope())
        {
            ref Slot slot = ref CurrentSlot;
            inHandler = slot.Depth > 0;
            synthetic = slot.Synthetic;
        }

        if (!inHandler)
        {
            return;
        }

        if (synthetic)
        {
            throw new InvalidOperationException($"{member} cannot be called from an interrupt handler; hand the work to a work item.");
        }

        Panic.Halt(member);
    }

    // One CPU's state: how deep it is in handler dispatches, and whether the innermost
    // one is synthetic.
    private struct Slot
    {
        public int Depth;
        public bool Synthetic;
    }

    /// <summary>One <see cref="Slot"/> per CPU the per-CPU table can hold.</summary>
    [InlineArray(PerCpuBlock.MaxCpus)]
    private struct PerCpuSlots
    {
        private Slot _element0;
    }
}
