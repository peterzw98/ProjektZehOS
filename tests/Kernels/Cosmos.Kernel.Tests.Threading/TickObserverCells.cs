using System.Threading;
using Cosmos.Kernel.Core.Scheduler;
using Cosmos.Kernel.Core.Scheduler.Stride;
using Cosmos.Kernel.System.Timers;
using Cosmos.TestRunner.Framework;
using Sys = Cosmos.Kernel.System;
using SysThread = System.Threading.Thread;

namespace Cosmos.Kernel.Tests.Threading;

// ZehOS patch tick-observer, see PATCHES.md
/// <summary>
/// Stride with the tick observer (<see cref="ITickObserver"/>): counts the ticks it sees per CPU,
/// and while <see cref="NudgeTarget"/> names a CPU, nudges it from every other CPU's tick.
/// </summary>
internal sealed class ObservingStride : StrideScheduler, ITickObserver
{
    public static readonly int[] Seen = new int[4];
    public static volatile uint NudgeTarget = uint.MaxValue;
    public static int Nudged;

    public void OnCpuTick(PerCpuState cpuState, PerCpuState[] allCpuStates)
    {
        uint cpu = cpuState.CpuId;
        if (cpu < Seen.Length)
        {
            Interlocked.Increment(ref Seen[cpu]);
        }

        uint target = NudgeTarget;
        if (target != uint.MaxValue && target != cpu)
        {
            SchedulerManager.Nudge(target);
            Interlocked.Increment(ref Nudged);
        }
    }
}

// ZehOS patch tick-observer, see PATCHES.md
/// <summary>
/// The tick observer on four CPUs: every CPU's tick reaches it, an idle CPU's too; a nudge
/// changes nothing else: the spinner the CPU runs stays there and goes on. The cell counts
/// Nudge calls, not deliveries: under QEMU a delivered and a dropped IPI look the same.
/// </summary>
internal static class TickObserverCells
{
    private const int PollMs = 10;
    private const int PollRetries = 300;
    private const int WindowMs = 300;

    private static volatile bool s_stop;
    private static volatile uint s_spinnerCpu = uint.MaxValue;
    private static long s_spins;
    private static Sys.Threading.CpuSet s_spinnerSet;

    private static void Spinner()
    {
        Sys.Threading.ThreadAffinity.Current = s_spinnerSet;
        while (!s_stop)
        {
            s_spinnerCpu = Sys.Threading.KernelCpus.Current;
            Interlocked.Increment(ref s_spins);
        }
    }

    private static bool WaitFor(global::System.Func<bool> condition)
    {
        for (int i = 0; i < PollRetries && !condition(); i++)
        {
            TimerManager.Wait(PollMs);
        }

        return condition();
    }

    public static void SeesEveryCpuAndNudgeKeepsThread()
    {
        s_stop = false;
        s_spinnerCpu = uint.MaxValue;
        ObservingStride.NudgeTarget = uint.MaxValue;
        ObservingStride.Nudged = 0;
        for (int i = 0; i < ObservingStride.Seen.Length; i++)
        {
            ObservingStride.Seen[i] = 0;
        }

        SchedulerManager.SetScheduler(new ObservingStride());
        // Before the spinner: CPUs 1 to 3 run their idle threads, and their ticks count too.
        Assert.True(WaitFor(() => Volatile.Read(ref ObservingStride.Seen[1]) > 5 && Volatile.Read(ref ObservingStride.Seen[2]) > 5
            && Volatile.Read(ref ObservingStride.Seen[3]) > 5 && Volatile.Read(ref ObservingStride.Seen[0]) > 5),
            "every CPU's tick reaches the observer, idle ones included");

        // Out of range and the calling CPU: nothing happens.
        SchedulerManager.Nudge(uint.MaxValue);
        SchedulerManager.Nudge(Sys.Threading.KernelCpus.Current);

        s_spinnerSet = Sys.Threading.CpuSet.Single(2);
        SysThread spinner = new(Spinner);
        try
        {
            spinner.Start();
            Assert.True(WaitFor(() => s_spinnerCpu == 2), "the spinner runs on CPU 2");
            ObservingStride.NudgeTarget = 2;
            Assert.True(WaitFor(() => Volatile.Read(ref ObservingStride.Nudged) > 20), "the other CPUs nudge CPU 2");
            long before = Interlocked.Read(ref s_spins);
            TimerManager.Wait(WindowMs);
            Assert.True(Interlocked.Read(ref s_spins) > before, "the nudged spinner goes on");
            Assert.Equal(2u, s_spinnerCpu, "and stays on its CPU");
        }
        finally
        {
            ObservingStride.NudgeTarget = uint.MaxValue;
            s_stop = true;
            bool joined = spinner.Join(5000);
            SchedulerManager.SetScheduler(new StrideScheduler());
            Assert.True(joined, "the spinner ends");
        }
    }
}
