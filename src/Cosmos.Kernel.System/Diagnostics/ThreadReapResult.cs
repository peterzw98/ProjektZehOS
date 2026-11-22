// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

namespace Cosmos.Kernel.System.Diagnostics;

// ZehOS patch abandoned-thread-reap, see PATCHES.md
/// <summary>
/// Outcome of <see cref="SchedulerDiagnostics.TryReapAbandoned"/>.
/// </summary>
public enum ThreadReapResult : byte
{
    /// <summary>
    /// No live thread has the requested ID: it was never there, it has been
    /// reaped already, or another caller is reaping it right now. Also the
    /// answer when no scheduler is installed.
    /// </summary>
    NotFound,

    /// <summary>
    /// The thread exists but was not given up at a CPU exception
    /// (<c>CpuExceptionAction.AbandonThread</c>): it is alive, it exited
    /// normally, or a kill only marked it dead. Such a thread may have
    /// stopped inside the kernel, so it is never reaped from here.
    /// </summary>
    NotAbandoned,

    /// <summary>
    /// The thread was given up, but its CPU has not left its stack yet (the
    /// exception handler is still reporting). Nothing changed; try again
    /// later.
    /// </summary>
    StillOnCpu,

    /// <summary>
    /// The thread went through the exit path: its managed side is stopped
    /// (joiners released, held mutexes freed), its allocation context is
    /// retired, the policy forgot it, its registry slot is free and its stack
    /// has been given back.
    /// </summary>
    Reaped,
}
