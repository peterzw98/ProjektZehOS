// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.Memory;
using Cosmos.Kernel.Core.Scheduler;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.Threading;
using Cosmos.TestRunner.Framework;
using Sys = Cosmos.Kernel.System;
using SysThread = System.Threading.Thread;
using TR = Cosmos.TestRunner.Framework.TestRunner;
#if ARCH_X64
using Cosmos.Kernel.Core.X64.Bridge;
using Cosmos.Kernel.Core.X64.Cpu;
#endif

namespace Cosmos.Kernel.Tests.UserMode;

// ZehOS patch user-mode-probe, see PATCHES.md

/// <summary>
/// Ring 3: the programs of Asm/Programs.s on pages below an otherwise empty PML4 entry of the
/// lower half. On ARM64 one cell checks that it is not supported and the rest skip.
/// </summary>
public unsafe partial class Kernel : Sys.Kernel
{
    private const int ExpectedTestCount = 27;
    private const string NoRing3 = "no ring 3 on this architecture";

    protected override void BeforeRun()
    {
        TR.Start("User Mode Tests", expectedTests: ExpectedTestCount);
#if ARCH_X64
        TR.Run("Enter_BeforeInitialize_Refused", () => Assert.True(
            Core.CPU.UserMode.Enter(0, 0) == Core.CPU.UserMode.EnterRefused && !Core.CPU.UserMode.IsSupported, "refused"));
        TR.Run("Initialize_Supported", () => Assert.True(SetUp(), "initialized, pages mapped"));
        TR.Run("KernelHalf_HasNoUserPage", TestKernelHalf);
        TR.Run("Enter_StartsWithEmptyRegisters", () => Equal(0, Run(10)));
        TR.Run("Syscall_Returns", () => Equal(0x1235, Run(0)));
        TR.Run("Fault_UD_Ends", () => TestFault(1, 0, 6, 0, 0));
        TR.Run("Fault_UD_Skipped", TestFaultSkipped);
        TR.Run("Fault_PF_Unmapped_ErrorCode", () => TestFault(2, UserBase + 0x10_0000, 14, 6, UserBase + 0x10_0000));
        TR.Run("Fault_PF_Retried", TestFaultRetried);
        TR.Run("Fault_PF_KernelWrite_ErrorCode", () => TestFault(2, KernelAddress, 14, 7, KernelAddress));
        TR.Run("Fault_PF_KernelExecute", () => TestFault(3, KernelAddress, 14, 5, KernelAddress, 0x10));
        TR.Run("Fault_GP_Cli", () => TestFault(4, 0, 13, 0, 0));
        TR.Run("Fault_UD_SetGsBase", () => TestFault(12, 0, 6, 0, 0));
        TR.Run("FsBase_LowerHalfOnly", TestFsBaseRange);
        TR.Run("BadReturn_Syscall", TestBadSyscallReturn);
        TR.Run("BadReturn_Fault", TestBadFaultReturn);
        TR.Run("BadReturn_EnterAndInterrupt", TestBadEnter);
        TR.Run("BadReturn_NestedTaskFlag", () => Equal(Core.CPU.UserMode.InvalidReturn, Run(7)));
        TR.Run("Registers_SurviveInterruptsAndCalls", () => Equal(0, Run(8)));
        TR.Run("FsBase_PerThread", TestFsBase);
        TR.Run("RequestLeave_EndsLoop_WithCollections", TestRequestLeave);
        TR.Run("TickHook_NeverSeesRing3", TestTickHook);
        TR.RunIf(KernelCpus.Count >= 4, "Nmi_InRing3_Returns", TestNmi, "needs four CPUs");
        TR.RunIf(KernelCpus.Count >= 2, "RequestLeave_OtherCpu_BeforeTick", TestRequestLeaveKick, "needs two CPUs");
        TR.Run("ReturnWork_InterruptReturn_KeepsRegisters", TestReturnWork);
        TR.Run("ReturnWork_StagedSwitch_KeepsRegisters", TestReturnWorkStaged);
        TR.Run("ReturnWork_Fault_RunsOrDrops", TestReturnWorkFault);
#else
        TR.Run("Initialize_NotSupported", () => Assert.True(!Core.CPU.UserMode.Initialize() && !Core.CPU.UserMode.IsSupported, "not supported"));
        for (int i = 1; i < ExpectedTestCount; i++)
        {
            TR.Skip("x64_" + i, NoRing3);
        }
#endif
        TR.Finish();
    }

    protected override void Run()
    {
        Stop();
    }

    protected override void AfterRun()
    {
        TR.Complete();
        Sys.Power.Halt();
    }

#if ARCH_X64
    private const ulong PageSize = 4096;
    private const ulong PagePresentWritableUser = 7;
    private const ulong PageUser = 4;
    private const ulong PageLarge = 1UL << 7;
    private const ulong PageNoExecute = 1UL << 63;
    private const ulong AddressMask = 0x000F_FFFF_FFFF_F000;
    private const int ProgramSize = 512;
    private const int CodePages = 3;
    private const int DataPages = 4;
    private const int WaitMs = 10_000;
    private const byte NmiVector = 2;

    private static ulong s_pml4Index;
    private static ulong s_kernelAddress;
    private static readonly ulong[] s_data = new ulong[DataPages];

    private static int s_faults;
    private static ulong s_faultVector;
    private static ulong s_faultError;
    private static ulong s_faultAddress;
    private static int s_faultAction;
    private static int s_change;

    private static ulong UserBase => s_pml4Index << 39;
    private static ulong KernelAddress => s_kernelAddress;
    private static ulong Code(int program) => UserBase + (ulong)program * ProgramSize;
    private static ulong DataUser(int page) => UserBase + (ulong)(page + 8) * PageSize;

    // Half into the page: a program may push.
    private static ulong Stack(int page) => DataUser(page) + 2048;
    private static ref ulong Slot(int page) => ref *(ulong*)(s_data[page] + 2048);

    [LibraryImport("*", EntryPoint = "cosmos_test_user_programs_address")]
    [SuppressGCTransition]
    private static partial ulong ProgramsAddress();

    [LibraryImport("*", EntryPoint = "cosmos_test_user_programs_size")]
    [SuppressGCTransition]
    private static partial ulong ProgramsSize();

    private static void Equal(ulong expected, ulong actual)
    {
        Assert.True(expected == actual, "expected 0x" + expected.ToString("X") + ", got 0x" + actual.ToString("X"));
    }

    private static ulong* Table(ulong entry) => (ulong*)((entry & AddressMask) + AddressSpace.HhdmOffset);

    private static ulong* NewPage() => (ulong*)PageAllocator.AllocPages(PageType.Unmanaged, 1, zero: true);

    private static ulong Physical(void* page) => PageAllocator.VirtualToPhysical((ulong)page);

    private static bool SetUp()
    {
        ulong* pml4 = Table(X64CpuNative.ReadCr3());
        s_pml4Index = 1;
        while (pml4[s_pml4Index] != 0)
        {
            s_pml4Index++;
        }

        ulong* pdpt = NewPage();
        ulong* pd = NewPage();
        ulong* pt = NewPage();
        ulong* code = (ulong*)PageAllocator.AllocPages(PageType.Unmanaged, CodePages, zero: true);
        ulong size = ProgramsSize();
        Buffer.MemoryCopy((void*)ProgramsAddress(), code, CodePages * PageSize, size);
        for (int i = 0; i < CodePages; i++)
        {
            pt[i] = Physical(code) + (ulong)i * PageSize | 5;
        }

        for (int i = 0; i < DataPages; i++)
        {
            ulong* page = NewPage();
            s_data[i] = (ulong)page;
            pt[8 + i] = Physical(page) | PagePresentWritableUser | PageNoExecute;
        }

        pd[0] = Physical(pt) | PagePresentWritableUser;
        pdpt[0] = Physical(pd) | PagePresentWritableUser;
        Volatile.Write(ref pml4[s_pml4Index], Physical(pdpt) | PagePresentWritableUser);

        // A mapped page of the kernel half.
        s_kernelAddress = s_data[0];

        Core.CPU.UserMode.SyscallHandler = &OnSyscall;
        Core.CPU.UserMode.FaultHandler = &OnFault;
        Core.CPU.UserMode.ReturnHandler = &OnReturnWork;
        return s_pml4Index < 256 && size <= CodePages * PageSize && Core.CPU.UserMode.Initialize() && Core.CPU.UserMode.IsSupported;
    }

    [UnmanagedCallersOnly]
    private static void OnSyscall(UserFrame* frame)
    {
        switch (frame->Rax)
        {
            case 1:
                frame->Rax = frame->Rdi + 1;
                break;
            case 2:
                Core.CPU.UserMode.LeaveCurrent(frame->Rdi);
                break;
            case 3:
                Change(frame);
                break;
        }
    }

    [UnmanagedCallersOnly]
    private static int OnFault(UserFrame* frame, ulong vector, ulong error, ulong address)
    {
        s_faults++;
        s_faultVector = vector;
        s_faultError = error;
        s_faultAddress = address;
        switch (s_faultAction)
        {
            case 1:
                frame->Rip += 2;
                return 1;
            case 2:
                frame->Rax = DataUser(1);
                return 1;
            case 3:
                Change(frame);
                return 1;
            case 4:
                // ZehOS patch user-return-work, see PATCHES.md
                // Work requested during a fault that is retried: runs on the way back.
                Core.CPU.UserMode.RequestWork(SchedulerManager.CurrentThread);
                frame->Rax = DataUser(1);
                return 1;
            case 5:
                // Work requested during a fault that ends the program: dropped with it.
                Core.CPU.UserMode.RequestWork(SchedulerManager.CurrentThread);
                return 0;
            default:
                return 0;
        }
    }

    private static void Change(UserFrame* frame)
    {
        switch (s_change)
        {
            case 0: frame->Rip = 0x0000_8000_0000_0000; break;
            case 1: frame->Rip = s_kernelAddress; break;
            case 2: frame->Rsp = 0x0000_8000_0000_0000; break;
            case 3: frame->Rsp = s_kernelAddress; break;
            case 4: frame->Rflags |= 0x3000; break;
            case 5: frame->Rflags &= ~0x200UL; break;
            case 6: frame->Rflags |= 0x4000; break;
            case 7: frame->Cs = 0x28; break;
            case 8: frame->Ss = 0x30; break;
            case 9: frame->Rflags |= 1UL << 17; break;
        }
    }

    private const int Changes = 10;

    private static ulong Run(int program, int page = 0) => Core.CPU.UserMode.Enter(Code(program), Stack(page));

    // After each failure a program must run as before.
    private static void StillRuns() => Equal(0x1235, Run(0));

    private static void TestFault(int program, ulong argument, ulong vector, ulong error, ulong address, ulong optionalError = 0)
    {
        s_faults = 0;
        s_faultAction = 0;
        Slot(0) = argument;
        Equal(Core.CPU.UserMode.Faulted, Run(program));
        Equal(1, (ulong)s_faults);
        Equal(vector, s_faultVector);
        Equal(error, s_faultError & ~optionalError);
        Equal(address, s_faultAddress);
        StillRuns();
    }

    private static void TestFaultSkipped()
    {
        s_faultAction = 1;
        Equal(0x77, Run(1));
    }

    private static void TestFaultRetried()
    {
        s_faults = 0;
        s_faultAction = 2;
        Slot(0) = UserBase + 0x20_0000;
        *(ulong*)s_data[1] = 0;
        Equal(0x78, Run(2));
        Equal(1, (ulong)s_faults);
        Equal(1, *(ulong*)s_data[1]);
    }

    private static void TestBadSyscallReturn()
    {
        for (s_change = 0; s_change < Changes; s_change++)
        {
            Equal(Core.CPU.UserMode.InvalidReturn, Run(6));
            StillRuns();
        }

        s_change = -1;
        Equal(0x79, Run(6));
    }

    private static void TestBadFaultReturn()
    {
        s_faultAction = 3;
        for (s_change = 0; s_change < Changes; s_change++)
        {
            Equal(Core.CPU.UserMode.InvalidReturn, Run(1));
            StillRuns();
        }

        s_change = -1;
        s_faultAction = 0;
    }

    private static void TestBadEnter()
    {
        Equal(Core.CPU.UserMode.InvalidReturn, Core.CPU.UserMode.Enter(0x0000_8000_0000_0000, Stack(0)));
        Equal(Core.CPU.UserMode.InvalidReturn, Core.CPU.UserMode.Enter(s_kernelAddress, Stack(0)));
        Equal(Core.CPU.UserMode.InvalidReturn, Core.CPU.UserMode.Enter(Code(0), ulong.MaxValue - 15));
        Equal(Core.CPU.UserMode.InvalidReturn, Run(11));
        StillRuns();
    }

    private static void TestKernelHalf()
    {
        ulong* pml4 = Table(X64CpuNative.ReadCr3());
        int found = 0;
        for (int i = 256; i < 512; i++)
        {
            found += CountUserPages(pml4[i], 3);
        }

        Equal(0, (ulong)found);
    }

    private static int CountUserPages(ulong entry, int level)
    {
        if ((entry & 1) == 0 || (entry & PageUser) == 0)
        {
            return 0;
        }

        if (level == 0 || (level < 3 && (entry & PageLarge) != 0))
        {
            return 1;
        }

        int found = 0;
        ulong* table = Table(entry);
        for (int i = 0; i < 512; i++)
        {
            found += CountUserPages(table[i], level - 1);
        }

        return found;
    }

    /// <summary>A thread in ring 3.</summary>
    private sealed class Runner
    {
        private readonly SysThread _thread;
        private SchedulerThread? _scheduler;
        private ulong _result;
        private int _done;

        public Runner(int program, int page, ulong fsBase = 0)
        {
            _thread = new SysThread(() =>
            {
                Volatile.Write(ref _scheduler, SchedulerManager.CurrentThread);
                Core.CPU.UserMode.SetFsBase(fsBase);
                _result = Run(program, page);
                Volatile.Write(ref _done, 1);
            });
            _thread.Start();
        }

        public SchedulerThread Scheduler => Volatile.Read(ref _scheduler)!;

        public ulong Wait()
        {
            WaitFor(() => Volatile.Read(ref _done) != 0, "the program left");
            _thread.Join();
            return _result;
        }
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(start).TotalMilliseconds < WaitMs * 6, what);
            SysThread.Sleep(1);
        }
    }

    private static void WaitProgress(int page)
    {
        ulong before = Volatile.Read(ref Slot(page));
        WaitFor(() => Volatile.Read(ref Slot(page)) != before, "the program runs");
    }

    private static void TestFsBase()
    {
        Runner[] runners = new Runner[DataPages];
        for (int i = 0; i < DataPages; i++)
        {
            *(ulong*)(s_data[i] + 64) = 0x1000 + (ulong)i;
            Slot(i) = 0x1000 + (ulong)i;
            runners[i] = new Runner(9, i, DataUser(i) + 64);
        }

        foreach (Runner runner in runners)
        {
            Equal(0, runner.Wait());
        }
    }

    private static void TestFsBaseRange()
    {
        Assert.True(Refused(0x0000_8000_0000_0000) && Refused(KernelAddress) && Refused(ulong.MaxValue), "upper half refused");
        Core.CPU.UserMode.SetFsBase(0x0000_7FFF_FFFF_FFFF);
        Core.CPU.UserMode.SetFsBase(0);
    }

    private static bool Refused(ulong fsBase)
    {
        try
        {
            Core.CPU.UserMode.SetFsBase(fsBase);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }

    private static void TestRequestLeave()
    {
        for (int round = 0; round < 50; round++)
        {
            Slot(0) = 0;
            Runner runner = new(5, 0);
            WaitProgress(0);
            GC.Collect();
            WaitProgress(0);
            Core.CPU.UserMode.RequestLeave(runner.Scheduler, 0x4000 + (ulong)round);
            Equal(0x4000 + (ulong)round, runner.Wait());
        }
    }

    // ZehOS patch user-return-work, see PATCHES.md
    private const int WorkRounds = 64;  // Asm/Programs.s WORK_ROUNDS
    private const int WorkPage = 1;
    private static int s_workRuns;

    [LibraryImport("*", EntryPoint = "cosmos_test_user_clobber_fpu")]
    [SuppressGCTransition]
    private static partial void ClobberFpu();

    // The program's count of handler runs is the word above its counter.
    private static ref ulong WorkSlot => ref *(ulong*)(s_data[WorkPage] + 2048 + 8);

    [UnmanagedCallersOnly]
    private static void OnReturnWork(UserFrame* frame)
    {
        // Leaves the FPU and SSE registers as kernel code may: the ramp must give the program
        // its own back from the frame's image.
        ClobberFpu();
        Interlocked.Increment(ref s_workRuns);
        Volatile.Write(ref WorkSlot, Volatile.Read(ref WorkSlot) + 1);
    }

    // A program computes without calls; each RequestWork must run the handler on a return from
    // an interrupt (the kick on another CPU, a tick or a switch on the same) and the program
    // must find every register as it set it. The counter-check loses the image on purpose.
    private static void TestReturnWork()
    {
#if COSMOS_TEST_HOOKS
        Volatile.Write(ref Core.CPU.UserMode.TestWorkLoseState, true);
        ulong lost;
        try
        {
            lost = WorkRun(false, out _, out _);
        }
        finally
        {
            Volatile.Write(ref Core.CPU.UserMode.TestWorkLoseState, false);
        }

        Log.WriteString($"[UserMode] work, state lost on purpose: result 0x{lost:X}\n");
        Assert.True(lost != 0, "a ramp that loses the state is seen (counter-probe)");
        int kicksBefore = Volatile.Read(ref Core.CPU.UserMode.TestWorkKicks);
#endif
        ulong result = WorkRun(false, out int runs, out long worst);
        Log.WriteString($"[UserMode] work: result 0x{result:X}, {runs} runs for {WorkRounds} requests, worst {worst * 1_000_000 / Stopwatch.Frequency} us, {KernelCpus.Count} CPUs\n");
        Equal(0, result);
        Assert.True(runs >= WorkRounds, "every request ran the handler");
        if (KernelCpus.Count >= 2)
        {
            // The kick: well below the 10 ms tick of the other CPU.
            Assert.True(worst * 1000 / Stopwatch.Frequency < 2, "requests run before the other CPU's next tick");
        }
#if COSMOS_TEST_HOOKS
        int kicks = Volatile.Read(ref Core.CPU.UserMode.TestWorkKicks) - kicksBefore;
        Log.WriteString($"[UserMode] work: {kicks} marks set\n");
        Assert.True(kicks == runs, "only a request that sets the mark kicks");
#endif
    }

    // The program and main on one CPU: each request finds the program switched out, so the
    // ramp starts from the context of the staged switch that resumes it, not from an
    // interrupted frame.
    private static void TestReturnWorkStaged()
    {
#if COSMOS_TEST_HOOKS
        int stagedBefore = Volatile.Read(ref Core.CPU.UserMode.TestWorkStaged);
#endif
        ulong result = WorkRun(true, out int runs, out _);
        Log.WriteString($"[UserMode] work, one CPU: result 0x{result:X}, {runs} runs for {WorkRounds} requests\n");
        Equal(0, result);
        Assert.True(runs >= WorkRounds, "every request ran the handler");
#if COSMOS_TEST_HOOKS
        int staged = Volatile.Read(ref Core.CPU.UserMode.TestWorkStaged) - stagedBefore;
        Log.WriteString($"[UserMode] work, one CPU: {staged} ramps from a staged switch\n");
        Assert.True(staged == runs, "every run started from a staged switch");
#endif
    }

    // Work requested in the fault handler: a retried fault runs it before the instruction again,
    // with the frame the handler changed; a fault that ends the program drops it, and the next
    // Enter does not run it.
    private static void TestReturnWorkFault()
    {
        Volatile.Write(ref s_workRuns, 0);
        s_faults = 0;
        s_faultAction = 4;
        Slot(0) = UserBase + 0x20_0000;
        *(ulong*)s_data[1] = 0;
        ulong retried = Run(2);
        int runsRetried = Volatile.Read(ref s_workRuns);
        Log.WriteString($"[UserMode] work, fault retried: result 0x{retried:X}, {runsRetried} runs\n");
        Equal(0x78, retried);
        Equal(1, (ulong)s_faults);
        Equal(1, *(ulong*)s_data[1]);
        Equal(1, (ulong)runsRetried);

        Volatile.Write(ref s_workRuns, 0);
        s_faults = 0;
        s_faultAction = 5;
        ulong ended = Run(1);
        s_faultAction = 0;
        int runsEnded = Volatile.Read(ref s_workRuns);
        StillRuns();
        int runsAfter = Volatile.Read(ref s_workRuns);
        Log.WriteString($"[UserMode] work, fault ends: result 0x{ended:X}, {runsEnded} runs, {runsAfter} after the next Enter\n");
        Equal(Core.CPU.UserMode.Faulted, ended);
        Equal(1, (ulong)s_faults);
        Equal(0, (ulong)runsEnded);
        Equal(0, (ulong)runsAfter);
    }

    private static ulong WorkRun(bool oneCpu, out int runs, out long worst)
    {
        Slot(WorkPage) = 0;
        WorkSlot = 0;
        Volatile.Write(ref s_workRuns, 0);
        worst = 0;

        // With two CPUs: main on CPU 0 waits actively, the program computes on CPU 1, so each
        // request must arrive by the kick, not by CPU 1's next tick. With one: by a tick or switch.
        bool two = !oneCpu && KernelCpus.Count >= 2;
        var mainSet = ThreadAffinity.Current;
        SchedulerThread? scheduler = null;
        ulong result = 0;
        int done = 0;
        try
        {
            if (two || oneCpu)
            {
                ThreadAffinity.Current = Sys.Threading.CpuSet.Single(0);
            }

            var thread = new SysThread(() =>
            {
                if (two)
                {
                    ThreadAffinity.Current = Sys.Threading.CpuSet.Single(1);
                }
                else if (oneCpu)
                {
                    ThreadAffinity.Current = Sys.Threading.CpuSet.Single(0);
                }

                Volatile.Write(ref scheduler, SchedulerManager.CurrentThread);
                result = Run(13, WorkPage);
                Volatile.Write(ref done, 1);
            });
            thread.Start();
            WaitProgress(WorkPage);
            WaitProgress(WorkPage);
            SchedulerThread target = Volatile.Read(ref scheduler)!;
            if (two || oneCpu)
            {
                Equal(two ? 1UL : 0UL, target.CpuId);
            }

            Equal(0, Volatile.Read(ref WorkSlot));
            for (int round = 1; round <= WorkRounds; round++)
            {
                long sent = Stopwatch.GetTimestamp();
                Core.CPU.UserMode.RequestWork(target);
                if (two)
                {
                    while (Volatile.Read(ref WorkSlot) < (ulong)round)
                    {
                        Assert.True(Stopwatch.GetElapsedTime(sent).TotalMilliseconds < WaitMs, "the handler ran");
                    }
                }
                else
                {
                    WaitFor(() => Volatile.Read(ref WorkSlot) >= (ulong)round, "the handler ran");
                }

                worst = Math.Max(worst, Stopwatch.GetTimestamp() - sent);
                if (round < WorkRounds)
                {
                    // The program runs between two requests, so they are separate.
                    WaitProgress(WorkPage);
                }
            }

            WaitFor(() => Volatile.Read(ref done) != 0, "the program left");
            thread.Join();
        }
        finally
        {
            ThreadAffinity.Current = mainSet;
        }

        runs = Volatile.Read(ref s_workRuns);
        return result;
    }

    private const int KickRounds = 200;
    private static long s_leftAt;

    // ZehOS patch user-leave-kick: a request reaches a program in ring 3 on another CPU at once,
    // not with that CPU's next tick. Without the interrupt the wait spreads over the tick
    // period, so about half the rounds would take longer than half a tick.
    private static void TestRequestLeaveKick()
    {
        ulong tickNs = SchedulerDiagnostics.TickPeriodNs;
        Assert.True(tickNs > 0, "the timer ticks");
        long halfTick = (long)(tickNs / 2) * Stopwatch.Frequency / 1_000_000_000;
        var mainSet = ThreadAffinity.Current;
        try
        {
            ThreadAffinity.Current = Sys.Threading.CpuSet.Single(0);
#if COSMOS_TEST_HOOKS
            Volatile.Write(ref Core.CPU.UserMode.TestSkipKick, true);
            int slowSkipped = LeaveRounds(halfTick, out long worstSkipped);
            Volatile.Write(ref Core.CPU.UserMode.TestSkipKick, false);
            Log.WriteString($"[UserMode] without kick: tick {tickNs} ns, {slowSkipped}/{KickRounds} after half a tick, worst {worstSkipped * 1_000_000 / Stopwatch.Frequency} us\n");
            Assert.True(slowSkipped > KickRounds / 10, "without the kick requests wait for the tick (counter-probe)");
#endif
            int slow = LeaveRounds(halfTick, out long worst);
            Log.WriteString($"[UserMode] kick: tick {tickNs} ns, {slow}/{KickRounds} after half a tick, worst {worst * 1_000_000 / Stopwatch.Frequency} us\n");
            Assert.True(slow <= KickRounds / 20, "requests end before the next tick");
        }
        finally
        {
#if COSMOS_TEST_HOOKS
            Volatile.Write(ref Core.CPU.UserMode.TestSkipKick, false);
#endif
            ThreadAffinity.Current = mainSet;
        }
    }

    // Main on CPU 0 asks a program spinning in ring 3 on CPU 1 to leave; counts the rounds
    // whose leave took longer than the limit.
    private static int LeaveRounds(long limit, out long worst)
    {
        int slow = 0;
        worst = 0;
        for (int round = 0; round < KickRounds; round++)
        {
            Slot(0) = 0;
            SchedulerThread? scheduler = null;
            ulong result = 0;
            Volatile.Write(ref s_leftAt, 0);
            var thread = new SysThread(() =>
            {
                ThreadAffinity.Current = Sys.Threading.CpuSet.Single(1);
                Volatile.Write(ref scheduler, SchedulerManager.CurrentThread);
                result = Run(5, 0);
                Volatile.Write(ref s_leftAt, Stopwatch.GetTimestamp());
            });
            thread.Start();
            WaitProgress(0);
            WaitProgress(0);
            SchedulerThread target = Volatile.Read(ref scheduler)!;
            Equal(1, target.CpuId);

            long sent = Stopwatch.GetTimestamp();
            Core.CPU.UserMode.RequestLeave(target, 0x5000 + (ulong)round);
            thread.Join();
            Equal(0x5000 + (ulong)round, result);
            long took = Volatile.Read(ref s_leftAt) - sent;
            worst = Math.Max(worst, took);
            if (took > limit)
            {
                slow++;
            }
        }

        return slow;
    }

    private static int s_hookCalls;
    private static int s_hookRing3;

    private static nuint OnTick(in CpuTickInfo tick)
    {
        Interlocked.Increment(ref s_hookCalls);
        if (tick.InstructionPointer < 0x0000_8000_0000_0000)
        {
            Interlocked.Increment(ref s_hookRing3);
        }

        return 0;
    }

    private static void TestTickHook()
    {
        CpuTickHook.Handler = OnTick;
        Runner runner = new(5, 0);
        WaitProgress(0);
        SysThread.Sleep(500);
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 100)
        {
        }

        CpuTickHook.Handler = null;
        Core.CPU.UserMode.RequestLeave(runner.Scheduler, 1);
        Equal(1, runner.Wait());
        Assert.True(s_hookCalls > 0, "the hook ran");
        Equal(0, (ulong)s_hookRing3);
    }

    private static int s_nmis;
    private static int s_nmisInRing3;
    private static int s_nmisBadBlock;

    private static void OnNmi(ref IRQContext ctx)
    {
        Interlocked.Increment(ref s_nmis);
        if ((ctx.cs & 3) != 0)
        {
            Interlocked.Increment(ref s_nmisInRing3);
        }

        // Through GS: a program's base would fault here or name no CPU.
        if (PerCpuBlock.CurrentIndex >= KernelCpus.Count)
        {
            Interlocked.Increment(ref s_nmisBadBlock);
        }
    }

    // While the program calls all the time, NMIs meet the entry, the exit and ring 3 itself.
    private static void TestNmi()
    {
        InterruptManager.SetHandler(NmiVector, OnNmi);
        Runner calls = new(8, 1);
        Runner spin = new(5, 0);
        WaitProgress(0);
        for (int i = 0; i < 2000; i++)
        {
            using (InternalCpu.DisableInterruptsScope())
            {
                uint self = PerCpuBlock.CurrentIndex;
                for (uint cpu = 0; cpu < KernelCpus.Count; cpu++)
                {
                    if (cpu != self)
                    {
                        LocalApic.SendNmi(PerCpuBlock.At(cpu).HardwareId);
                    }
                }
            }
        }

        Equal(0, calls.Wait());
        Core.CPU.UserMode.RequestLeave(spin.Scheduler, 5);
        Equal(5, spin.Wait());
        InterruptManager.SetHandler(NmiVector, null);
        Assert.True(s_nmisInRing3 > 0, "an NMI met ring 3");
        Equal(0, (ulong)s_nmisBadBlock);
    }
#endif
}
