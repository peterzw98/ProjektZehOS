using System.Threading;
using Cosmos.Kernel.Core.Scheduler;
using Cosmos.Kernel.Core.Scheduler.Stride;
using Cosmos.Kernel.System.Timers;
using Cosmos.TestRunner.Framework;
using Sys = Cosmos.Kernel.System;
using SysThread = System.Threading.Thread;

namespace Cosmos.Kernel.Tests.Threading;

// ZehOS patch smt-sibling-yield, see PATCHES.md
/// <summary>
/// Stride with the voluntary move (<see cref="IVoluntaryMove"/>): moves the one thread the
/// cell names to the CPU it names, and counts how often it was asked about a pinned one.
/// </summary>
internal sealed class MovingStride : StrideScheduler, IVoluntaryMove
{
    public static volatile uint MoveThreadId = uint.MaxValue;
    public static volatile uint MoveTarget = uint.MaxValue;
    public static volatile uint PinnedThreadId = uint.MaxValue;
    public static int AskedForPinned;
    public static int Asked;

    public uint SelectMoveTarget(PerCpuState cpuState, SchedulerThread current, PerCpuState[] allCpuStates, CpuSet allowed)
    {
        if (current.Id == PinnedThreadId)
        {
            Interlocked.Increment(ref AskedForPinned);
        }

        if (current.Id != MoveThreadId)
        {
            return cpuState.CpuId;
        }

        Interlocked.Increment(ref Asked);
        uint target = MoveTarget;
        return target == uint.MaxValue ? cpuState.CpuId : target;
    }
}

// ZehOS patch smt-sibling-yield, see PATCHES.md
/// <summary>
/// The voluntary move on four CPUs: a running spinner the policy names moves to the CPU
/// it names, its set unchanged; a pinned spinner is never asked about; a destination that
/// runs a thread is stale and the spinner stays.
/// </summary>
internal static class VoluntaryMoveCells
{
    private const int PollMs = 10;
    private const int PollRetries = 300;
    private const int StaleWindowMs = 500;

    private static volatile bool s_stop;
    private static volatile uint s_moverCpu = uint.MaxValue;
    private static volatile uint s_moverId = uint.MaxValue;
    private static volatile uint s_pinnedId = uint.MaxValue;
    private static volatile uint s_pinnedCpu = uint.MaxValue;

    private static void Mover()
    {
        s_moverId = SchedulerManager.CurrentThread!.Id;
        while (!s_stop)
        {
            s_moverCpu = Sys.Threading.KernelCpus.Current;
        }
    }

    private static void Pinned()
    {
        s_pinnedId = SchedulerManager.CurrentThread!.Id;
        while (!s_stop)
        {
            s_pinnedCpu = Sys.Threading.KernelCpus.Current;
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

    public static void MovesRunningThreadAndKeepsSet()
    {
        s_stop = false;
        s_moverCpu = uint.MaxValue;
        s_pinnedCpu = uint.MaxValue;
        MovingStride.MoveThreadId = uint.MaxValue;
        MovingStride.MoveTarget = uint.MaxValue;
        MovingStride.AskedForPinned = 0;
        MovingStride.Asked = 0;

        MovingStride policy = new();
        SchedulerManager.SetScheduler(policy);
        Sys.Threading.CpuSet moverSet = Sys.Threading.CpuSet.Single(1).With(2);
        SysThread mover = new(Mover);
        SysThread pinned = new(Pinned);
        try
        {
            Assert.True(Sys.Threading.ThreadAffinity.Set(pinned, Sys.Threading.CpuSet.Single(3)), "the pinned spinner gets CPU 3");
            Assert.True(Sys.Threading.ThreadAffinity.Set(mover, moverSet), "the mover may run on CPU 1 and 2");
            pinned.Start();
            mover.Start();
            Assert.True(WaitFor(() => s_moverCpu is 1 or 2 && s_pinnedCpu == 3), "both spinners run where their sets say");
            MovingStride.PinnedThreadId = s_pinnedId;

            // The move: to the other CPU of its set, which runs its idle thread.
            uint from = s_moverCpu;
            uint to = from == 1 ? 2u : 1u;
            MovingStride.MoveTarget = to;
            MovingStride.MoveThreadId = s_moverId;
            Assert.True(WaitFor(() => s_moverCpu == to), "the running mover moved to the CPU the policy named");
            Assert.True(MovingStride.Asked > 0, "the tick asked the policy");
            Assert.True(Sys.Threading.ThreadAffinity.TryGet(mover, out Sys.Threading.CpuSet after) && after == moverSet,
                "and its set is unchanged");

            // Stale: the policy names CPU 3, outside the set: dropped, the mover stays.
            MovingStride.MoveTarget = 3;
            TimerManager.Wait(StaleWindowMs);
            Assert.Equal(to, s_moverCpu, "a target outside the set is dropped");
            Assert.Equal(0, MovingStride.AskedForPinned, "the pinned spinner was never asked about");
            Assert.Equal(3u, s_pinnedCpu, "and stays on its CPU");
        }
        finally
        {
            MovingStride.MoveThreadId = uint.MaxValue;
            s_stop = true;
            mover.Join(5000);
            pinned.Join(5000);
            SchedulerManager.SetScheduler(new StrideScheduler());
        }
    }

    public static void BusyTargetIsStale()
    {
        s_stop = false;
        s_moverCpu = uint.MaxValue;
        s_pinnedCpu = uint.MaxValue;
        MovingStride.MoveThreadId = uint.MaxValue;
        MovingStride.MoveTarget = uint.MaxValue;
        MovingStride.Asked = 0;

        SchedulerManager.SetScheduler(new MovingStride());
        SysThread mover = new(Mover);
        SysThread busy = new(Pinned);
        try
        {
            Assert.True(Sys.Threading.ThreadAffinity.Set(busy, Sys.Threading.CpuSet.Single(2)), "the busy spinner gets CPU 2");
            Assert.True(Sys.Threading.ThreadAffinity.Set(mover, Sys.Threading.CpuSet.Single(1).With(2)), "the mover may run on CPU 1 and 2");
            busy.Start();
            Assert.True(WaitFor(() => s_pinnedCpu == 2), "the busy spinner runs on CPU 2");
            mover.Start();
            Assert.True(WaitFor(() => s_moverCpu == 1), "the mover runs on the free CPU 1");

            // CPU 2 runs a thread: the destination is not free, every proposal is stale.
            MovingStride.MoveTarget = 2;
            MovingStride.MoveThreadId = s_moverId;
            Assert.True(WaitFor(() => MovingStride.Asked >= 3), "the tick asked the policy");
            TimerManager.Wait(StaleWindowMs);
            Assert.Equal(1u, s_moverCpu, "a busy destination is stale: the mover stays");
            Assert.Equal(2u, s_pinnedCpu, "and the busy spinner keeps its CPU");
        }
        finally
        {
            MovingStride.MoveThreadId = uint.MaxValue;
            s_stop = true;
            mover.Join(5000);
            busy.Join(5000);
            SchedulerManager.SetScheduler(new StrideScheduler());
        }
    }
}
