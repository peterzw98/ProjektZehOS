namespace Cosmos.Kernel.Core.Scheduler;

/// <summary>
/// Diagnostic IDs of the experimental API seams exposed by
/// Cosmos.Kernel.Core. An experimental API is usable today but carries no
/// compatibility promise; referencing one produces an error with the ID
/// below until the caller suppresses it, which is the caller's
/// acknowledgement of that contract.
/// </summary>
internal static class Experimentals
{
    /// <summary>
    /// The scheduler policy seam: <see cref="IScheduler"/>,
    /// <see cref="SchedulerManager"/>, <see cref="SchedulerThread"/>,
    /// <see cref="PerCpuState"/>, <see cref="SchedulerExtensible"/>,
    /// <see cref="InterruptMaskScope"/>, the
    /// <see cref="SchedulerThreadState"/>/<see cref="SchedulerThreadFlags"/> enums,
    /// and the CPU exception hook that hands a faulting thread to the scheduler
    /// (<see cref="CPU.CpuExceptionHook"/>, <see cref="CPU.CpuExceptionHandler"/>,
    /// <see cref="CPU.CpuExceptionInfo"/>, <see cref="CPU.CpuExceptionAction"/>).
    /// </summary>
    internal const string SchedulerSeamDiagId = "COSMOS0001";
}
