// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Cosmos.Kernel.Core.Scheduler;

// ZehOS patch irq-mask-scope, see PATCHES.md
/// <summary>
/// A kernel spinlock for data a consumer shares between an interrupt handler and a thread,
/// on any number of CPUs: <see cref="Acquire"/> masks interrupts on the calling CPU, then
/// spins until the lock is free; disposing the scope releases the lock, then restores the
/// interrupt state the acquire found. The mask alone protects only against the calling CPU's
/// own interrupts; the lock protects against every other CPU. The spin answers a stop for a
/// collection in its place, so a waiter never holds a collection up.
/// <para>The holder takes no other lock of the kernel, calls nothing that may block, schedule
/// or log, and does not allocate (a collection under it is a hang or a panic, see
/// <see cref="SpinLock"/>). Not reentrant: a holder that acquires again spins forever. A
/// mutable struct: keep it in a non-readonly field and acquire it there, a copy is a second
/// lock. A default instance is free.</para>
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public struct IrqSpinLock
{
    private SpinLock _lock;

    /// <summary>
    /// Masks interrupts on the calling CPU and takes the lock. Any context; a <c>using</c>
    /// binds to the returned scope and releases the lock.
    /// </summary>
    [UnscopedRef]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IrqSpinLockScope Acquire()
    {
        return new IrqSpinLockScope(_lock.AcquireIrqSafe());
    }
}

// ZehOS patch irq-mask-scope, see PATCHES.md
/// <summary>
/// The held state of an <see cref="IrqSpinLock"/>, returned by <see cref="IrqSpinLock.Acquire"/>.
/// Disposed once, in the context that acquired; a second call does nothing. A default instance
/// holds nothing and disposes to nothing.
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public ref struct IrqSpinLockScope
{
    // default(IrqSpinLockScope) is reachable from any consumer and holds nothing; only a scope
    // handed out by Acquire has a lock to release and flags to restore.
    private readonly bool _taken;
    private IrqLockScope _scope;

    internal IrqSpinLockScope(IrqLockScope scope)
    {
        _taken = true;
        _scope = scope;
    }

    /// <summary>Releases the lock, then restores the interrupt state the acquire found.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (_taken)
        {
            // IrqLockScope keeps its own disposed flag: a second Dispose releases nothing.
            _scope.Dispose();
        }
    }
}
