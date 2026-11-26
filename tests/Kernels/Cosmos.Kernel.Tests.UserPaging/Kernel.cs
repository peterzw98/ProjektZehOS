// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System;
using System.Threading;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.Memory;
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

namespace Cosmos.Kernel.Tests.UserPaging;

// ZehOS patch user-page-tables, see PATCHES.md

/// <summary>
/// <see cref="UserPageTable"/> without a switch to it: every cell reads the
/// tables back through the HHDM, none runs on them. On ARM64, which has no
/// implementation, one cell checks that it says so and the rest skip.
/// The cells of patch user-cr3 bind threads to tables and run on them.
/// </summary>
public unsafe class Kernel : Sys.Kernel
{
    /// <summary>Total number of tests announced to the test runner for this suite.</summary>
    private const int ExpectedTestCount = 24;

    /// <summary>A user address with a different index on every level.</summary>
    private const ulong User = 0x0000_1234_5678_9000;
    /// <summary>The last page that can be mapped.</summary>
    private const ulong LastPage = 0x0000_7FFF_FFFF_E000;
    private const ulong PageSize = 4096;
    /// <summary>Tables the leak cells create.</summary>
    private const int TableCount = 1000;
    /// <summary>Pages the leak cells may be off by: 1 MiB.</summary>
    private const long LeakTolerancePages = 256;
    /// <summary>Threads, and pages they all try to map, in the concurrency cell.</summary>
    private const int RaceThreads = 4;
    private const int RacePages = 2048;
    /// <summary>Distance between two of those pages: a new page table every fourth page.</summary>
    private const ulong RaceStride = 0x8_0000;
    private const int JoinTimeoutMs = 30_000;

    private const string NoTables = "no user page tables on this architecture";

    private static UserPageTable? s_raceTable;
    private static ulong s_raceFrame;
    private static int s_raceReady;
    private static int s_raceWins;

    protected override void BeforeRun()
    {
        TR.Start("User Paging Tests", expectedTests: ExpectedTestCount);
#if ARCH_X64
        const bool supported = true;
#else
        const bool supported = false;
#endif
        TR.RunIf(supported, "TryCreate_BeforeShare_ReturnsNull", TestCreateBeforeShare, NoTables);
        TR.Run("ShareKernelHalf_ReportsSupport", () => TestShare(supported));
        TR.RunIf(supported, "ShareKernelHalf_FillsUpperHalf", TestShareFillsUpperHalf, NoTables);
        TR.RunIf(supported, "Create_LowerEmpty_UpperSharedWithoutUser", TestCreateHalves, NoTables);
        TR.RunIf(supported, "Map_Translate_Unmap", TestMapTranslateUnmap, NoTables);
        TR.RunIf(supported, "Map_EntryBits", TestEntryBits, NoTables);
        TR.RunIf(supported, "Map_RefusesBadArguments", TestRefusals, NoTables);
        TR.RunIf(supported, "Protect_ChangesAccess", TestProtect, NoTables);
        TR.RunIf(supported, "Tables_AreIndependent_AndDisposeKeepsFrames", TestIndependent, NoTables);
        TR.RunIf(supported, "Frames_AreZeroed_AndChecked", TestFrames, NoTables);
        TR.RunIf(supported, "Churn_ThousandTables_NoLeak", TestChurn, NoTables);
        TR.RunIf(supported, "DeviceMapper_AfterShare_AddsNoPml4Entry", TestDeviceMapper, NoTables);
        TR.RunIf(supported && KernelCpus.Count >= RaceThreads, "Map_FourThreads_EachPageOnce", TestRace, "needs four CPUs");
        // ZehOS patch user-cr3, see PATCHES.md
        bool twoCpus = supported && KernelCpus.Count >= 2;
        TR.RunIf(supported, "Bind_LoadsTable_KeepsItAcrossSwitches", TestBindSwitch, NoTables);
        TR.RunIf(supported, "TwoTables_SameAddress_OwnContents", TestTwoTables, NoTables);
        TR.RunIf(supported, "Bind_Refusals_AndDisposeWhileBound", TestBindRefusals, NoTables);
        TR.RunIf(supported, "Bound_GcAndAllocation_UnderUserTable", TestGcUnderTable, NoTables);
        TR.RunIf(supported, "ThreadEnd_WithoutUnbind_DisposeAfterJoin", TestThreadEndUnbinds, NoTables);
        TR.RunIf(supported, "Kill_QueuedBound_EndsBinding", TestKillQueued, NoTables);
        TR.RunIf(supported, "Abandon_BoundAtFault_EndsBinding", TestAbandon, NoTables);
        TR.RunIf(twoCpus, "ActiveOnOtherCpu_ProtectUnmapRefused", TestActiveElsewhere, "needs two CPUs");
        TR.RunIf(twoCpus, "BoundNotRunning_ChangeSeenAfterSwitch", TestBoundNotRunning, "needs two CPUs");
        TR.RunIf(twoCpus, "Kill_RunningBound_EndsBindingAtSwitch", TestKillRunning, "needs two CPUs");
        TR.RunIf(twoCpus, "Protect_RacingSwitches_RefusalWritesNothing", TestProtectRace, "needs two CPUs");
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

    private static UserPageTable Create()
    {
        UserPageTable? table = UserPageTable.TryCreate();
        Assert.NotNull(table, "a table is created");
        return table!;
    }

    private static ulong Frame()
    {
        ulong frame = UserPageTable.AllocateFrame();
        Assert.True(frame != 0, "a frame is allocated");
        return frame;
    }

    private static ulong* At(ulong physical) => (ulong*)(physical + AddressSpace.HhdmOffset);

    private static void TestCreateBeforeShare()
    {
        Assert.True(UserPageTable.TryCreate() == null, "no table before the kernel half is shared");
    }

    private static void TestShare(bool supported)
    {
        Assert.Equal(supported, UserPageTable.ShareKernelHalf(), "the first call");
        Assert.Equal(supported, UserPageTable.ShareKernelHalf(), "the second call");
    }

    private static void TestMapTranslateUnmap()
    {
        using UserPageTable table = Create();
        ulong frame = Frame();
        Assert.False(table.TryTranslate(User, false, out _), "nothing there yet");
        Assert.True(table.Unmap(User) == 0, "nothing to unmap yet");
        Assert.True(table.TryMap(User, frame, writable: true, executable: false), "map");
        Assert.True(table.TryTranslate(User + 0x123, true, out ulong physical), "translate");
        Assert.True(physical == frame + 0x123, "the offset is kept");
        Assert.False(table.TryTranslate(User + PageSize, false, out ulong none), "the next page is not mapped");
        Assert.True(none == 0, "0 on failure");
        Assert.False(table.TryMap(User, frame, writable: false, executable: false), "a mapped page is not replaced");
        Assert.True(table.TryTranslate(User, true, out _), "and keeps its access");
        Assert.True(table.Unmap(User) == frame, "unmap returns the frame");
        Assert.False(table.TryTranslate(User, false, out _), "gone");
        Assert.True(table.Unmap(User) == 0, "only once");

        Assert.True(table.TryMap(PageSize, frame, true, false), "the lowest page");
        Assert.True(table.TryMap(LastPage, frame, true, false), "the highest page");
        Assert.True(table.TryTranslate(LastPage + PageSize - 1, true, out physical), "its last byte");
        Assert.True(physical == frame + PageSize - 1, "at the frame's last byte");
        table.Unmap(PageSize);
        table.Unmap(LastPage);
        UserPageTable.FreeFrame(frame);
    }

    private static void TestRefusals()
    {
        using UserPageTable table = Create();
        ulong frame = Frame();
        Assert.False(table.TryMap(User, frame, true, true), "writable and executable");
        Assert.False(table.TryMap(User + 8, frame, true, false), "unaligned user address");
        Assert.False(table.TryMap(User, frame + 8, true, false), "unaligned frame");
        Assert.False(table.TryMap(User, 0, true, false), "frame 0");
        Assert.False(table.TryMap(User, 1UL << 52, true, false), "frame past 52 bits");
        Assert.False(table.TryMap(0, frame, true, false), "the null page");
        Assert.False(table.TryMap(LastPage + PageSize, frame, true, false), "the last page of the half");
        Assert.False(table.TryMap(0x0000_8000_0000_0000, frame, true, false), "non-canonical");
        Assert.False(table.TryMap(AddressSpace.KernelSpaceStart, frame, true, false), "the kernel half");
        Assert.False(table.TryMap(0xFFFF_FFFF_FFFF_F000, frame, true, false), "the top page");
        Assert.False(table.TryProtect(AddressSpace.KernelSpaceStart, false, false), "protect in the kernel half");
        Assert.True(table.Unmap(AddressSpace.KernelSpaceStart) == 0, "unmap in the kernel half");
        Assert.False(table.TryTranslate(AddressSpace.HhdmOffset + frame, false, out _), "translate in the kernel half");
        Assert.False(table.TryTranslate(User, false, out _), "nothing was mapped");

        table.Dispose();
        table.Dispose();
        Assert.False(table.TryMap(User, frame, true, false), "disposed: map");
        Assert.False(table.TryProtect(User, true, false), "disposed: protect");
        Assert.False(table.TryTranslate(User, false, out _), "disposed: translate");
        Assert.True(table.Unmap(User) == 0, "disposed: unmap");
        UserPageTable.FreeFrame(frame);
    }

    private static void TestProtect()
    {
        using UserPageTable table = Create();
        ulong frame = Frame();
        Assert.False(table.TryProtect(User, false, false), "not mapped");
        Assert.True(table.TryMap(User, frame, true, false), "map writable");
        Assert.True(table.TryProtect(User, false, true), "read and execute");
        Assert.False(table.TryTranslate(User, true, out _), "no longer writable");
        Assert.True(table.TryTranslate(User, false, out ulong physical) && physical == frame, "still readable, same frame");
        Assert.False(table.TryProtect(User, true, true), "writable and executable");
        Assert.False(table.TryTranslate(User, true, out _), "the refusal changed nothing");
        Assert.True(table.TryProtect(User, true, false), "writable again");
        Assert.True(table.TryTranslate(User, true, out _), "writable");
        Assert.True(table.Unmap(User) == frame, "unmap");
        UserPageTable.FreeFrame(frame);
    }

    private static void TestIndependent()
    {
        UserPageTable first = Create();
        UserPageTable second = Create();
        ulong a = Frame();
        ulong b = Frame();
        Assert.True(first.TryMap(User, a, true, false), "first");
        Assert.False(second.TryTranslate(User, false, out _), "second is empty");
        Assert.True(second.TryMap(User, b, true, false), "second");
        *At(a) = 0xA;
        first.Dispose();
        Assert.True(second.TryTranslate(User, false, out ulong physical) && physical == b, "second survives the first");
        Assert.True(*At(a) == 0xA, "the frame survives its table");
        second.Dispose();
        UserPageTable.FreeFrame(a);
        UserPageTable.FreeFrame(b);
    }

    private static void TestFrames()
    {
        ulong frame = Frame();
        for (int i = 0; i < 512; i++)
        {
            At(frame)[i] = ulong.MaxValue;
        }

        UserPageTable.FreeFrame(frame);
        ulong again = Frame();
        Assert.True((again & (PageSize - 1)) == 0, "page-aligned");
        for (int i = 0; i < 512; i++)
        {
            Assert.True(At(again)[i] == 0, "zeroed");
        }

        UserPageTable.FreeFrame(0);
        Assert.True(Throws(again + 8), "unaligned");
        Assert.True(Throws(0xFFFF_FFFF_FFFF_F000), "outside memory");
        UserPageTable.FreeFrame(again);
        Assert.True(Throws(again), "a second time");
    }

    private static bool Throws(ulong physical)
    {
        try
        {
            UserPageTable.FreeFrame(physical);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void TestChurn()
    {
        // One round first: what the collector keeps for the objects is not a leak.
        ChurnOneByOne();
        GC.Collect();
        long before = (long)PageAllocator.FreePageCount;
        ChurnOneByOne();
        GC.Collect();
        long after = (long)PageAllocator.FreePageCount;
        Log.WriteString($"[UserPaging] one by one: {before} -> {after} free pages\n");
        Assert.True(Math.Abs(before - after) <= LeakTolerancePages, "one by one, within 1 MiB");

        before = after;
        var tables = new UserPageTable[TableCount];
        for (int i = 0; i < TableCount; i++)
        {
            tables[i] = Create();
            Assert.True(tables[i].TryMap(User, Frame(), true, false), "map");
        }

        long during = (long)PageAllocator.FreePageCount;
        for (int i = 0; i < TableCount; i++)
        {
            UserPageTable.FreeFrame(tables[i].Unmap(User));
            tables[i].Dispose();
        }

        GC.Collect();
        after = (long)PageAllocator.FreePageCount;
        Log.WriteString($"[UserPaging] all at once: {before} -> {during} -> {after} free pages\n");
        // Root, three tables and the frame: the count proves the cell measures what it claims.
        Assert.True(before - during >= 5L * TableCount, "the tables and frames are counted");
        Assert.True(Math.Abs(before - after) <= LeakTolerancePages, "all at once, within 1 MiB");
    }

    private static void ChurnOneByOne()
    {
        for (int i = 0; i < TableCount; i++)
        {
            UserPageTable table = Create();
            ulong frame = Frame();
            Assert.True(table.TryMap(User, frame, true, false), "map");
            Assert.True(table.TryMap(LastPage, frame, false, true), "map again");
            Assert.True(table.TryMap(PageSize, frame, false, false), "and again");
            // The tables go with pages still mapped in them.
            table.Dispose();
            UserPageTable.FreeFrame(frame);
        }
    }

    private static void TestRace()
    {
        s_raceTable = Create();
        s_raceFrame = Frame();
        s_raceReady = 0;
        s_raceWins = 0;
        var threads = new SysThread[RaceThreads];
        for (int i = 0; i < RaceThreads; i++)
        {
            threads[i] = new SysThread(RaceWorker);
            threads[i].Start();
        }

        for (int i = 0; i < RaceThreads; i++)
        {
            Assert.True(threads[i].Join(JoinTimeoutMs), "a worker ends");
        }

        Assert.Equal(RacePages, s_raceWins, "each page was mapped by one thread");
        for (int i = 0; i < RacePages; i++)
        {
            Assert.True(s_raceTable.Unmap(User + (ulong)i * RaceStride) == s_raceFrame, "and is there");
        }

        s_raceTable.Dispose();
        UserPageTable.FreeFrame(s_raceFrame);
    }

    private static void RaceWorker()
    {
        Interlocked.Increment(ref s_raceReady);
        while (Volatile.Read(ref s_raceReady) < RaceThreads)
        {
            SysThread.Yield();
        }

        for (int i = 0; i < RacePages; i++)
        {
            if (s_raceTable!.TryMap(User + (ulong)i * RaceStride, s_raceFrame, true, false))
            {
                Interlocked.Increment(ref s_raceWins);
            }
        }
    }

    // ===== ZehOS patch user-cr3, see PATCHES.md =====

    private const ulong ValueA = 0xA11CE;
    private const ulong ValueB = 0xB0B;
    private const int SwitchRounds = 200;
    private const int GcRounds = 100;
    private const int PollMs = 10;
    private const int PollRetries = 500;

    private static UserPageTable? s_tableA;
    private static UserPageTable? s_tableB;
    private static volatile string? s_failure;
    private static volatile int s_step;
    private static volatile bool s_release;
    private static volatile uint s_workerCpu;
    private static volatile uint s_victimId = CpuExceptionInfo.NoThread;
    private static ulong s_sink;
    private static int s_seenChanges;
    private const int ProtectRaceRounds = 200_000;
    private const int ProtectRaceEnough = 1000;

#if ARCH_X64
    /// <summary>The root this CPU translates through.</summary>
    private static ulong Cr3() => X64CpuNative.ReadCr3() & 0x000F_FFFF_FFFF_F000;
#else
    private static ulong Cr3() => 0;
#endif

    /// <summary>The first failure a worker saw; a worker cannot assert.</summary>
    private static void Fail(string what) => s_failure ??= what;

    private static void Reset()
    {
        s_failure = null;
        s_step = 0;
        s_release = false;
        s_victimId = CpuExceptionInfo.NoThread;
    }

    private static void AssertWorkerClean() => Assert.True(s_failure == null, s_failure ?? "the workers saw no error");

    /// <summary>A table with one page at <see cref="User"/> that holds <paramref name="value"/>.</summary>
    private static UserPageTable TableWith(ulong value, out ulong frame)
    {
        UserPageTable table = Create();
        frame = Frame();
        *At(frame) = value;
        Assert.True(table.TryMap(User, frame, writable: true, executable: false), "map the page");
        return table;
    }

    /// <summary>Frees the tables; the frames stay the caller's.</summary>
    private static void Release(UserPageTable table) => table.Dispose();

    /// <summary>Moves the calling thread to <paramref name="cpu"/> and stays there.</summary>
    private static bool PinHere(uint cpu)
    {
        ThreadAffinity.Current = CpuSet.Single(cpu);
        SysThread.Yield();
        return KernelCpus.Current == cpu;
    }

    /// <summary>Polls <paramref name="condition"/>, once per round: it may have an effect (a reap, an unmap).</summary>
    private static bool WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < PollRetries; i++)
        {
            if (condition())
            {
                return true;
            }

            SysThread.Sleep(PollMs);
        }

        return false;
    }

    private static uint CurrentThreadId()
        => SchedulerDiagnostics.TryGetCurrentThread(KernelCpus.Current, out KernelThreadInfo info) ? info.Id : CpuExceptionInfo.NoThread;

    // Bind loads the table, every switch back to the thread loads it again,
    // Unbind loads the kernel root; Main never runs on the table.
    private static void TestBindSwitch()
    {
        Reset();
        ulong mainRoot = Cr3();
        s_tableA = TableWith(ValueA, out ulong frame);
        var worker = new SysThread(BindSwitchWorker);
        worker.Start();
        // On one CPU Main runs between the worker's rounds: the switch away
        // from the bound thread has to load the kernel root again.
        Assert.True(WaitFor(() => s_step == 1), "the worker is bound");
        Assert.True(Cr3() == mainRoot, "Main runs on its kernel root while the worker is bound");
        s_release = true;
        Assert.True(worker.Join(JoinTimeoutMs), "the worker ends");
        AssertWorkerClean();
        Assert.True(Cr3() == mainRoot, "Main still runs on its kernel root");
        Assert.True(*At(frame) == ValueA + 1, "the worker wrote through the table");
        Assert.True(s_tableA.Unmap(User) == frame, "unmap after the worker left");
        Release(s_tableA);
        UserPageTable.FreeFrame(frame);
    }

    private static void BindSwitchWorker()
    {
        if (!PinHere(KernelCpus.Current))
        {
            Fail("the worker stays on its CPU");
            return;
        }

        ulong kernelRoot = Cr3();
        s_tableA!.BindToCurrentThread();
        if (Cr3() != s_tableA.Root)
        {
            Fail("bind loads the table at once");
        }

        s_step = 1;
        for (int i = 0; (i < SwitchRounds || !s_release) && s_failure == null; i++)
        {
            if (*(ulong*)User != ValueA)
            {
                Fail("the thread reads its page");
            }

            // A yield may come back without a switch, a sleep goes through idle.
            if ((i & 1) == 0)
            {
                SysThread.Yield();
            }
            else
            {
                SysThread.Sleep(1);
            }

            if (Cr3() != s_tableA.Root)
            {
                Fail("the switch back loads the table again");
            }
        }

        *(ulong*)User = ValueA + 1;
        UserPageTable.UnbindCurrentThread();
        if (Cr3() != kernelRoot)
        {
            Fail("unbind loads the CPU's kernel root");
        }
    }

    // Two threads on one CPU, the same address in two tables: each reads its
    // own page after every switch between them.
    private static void TestTwoTables()
    {
        Reset();
        s_tableA = TableWith(ValueA, out ulong a);
        s_tableB = TableWith(ValueB, out ulong b);
        s_workerCpu = (uint)(KernelCpus.Count - 1);
        var first = new SysThread(() => TwoTablesWorker(s_tableA!, ValueA));
        var second = new SysThread(() => TwoTablesWorker(s_tableB!, ValueB));
        first.Start();
        second.Start();
        Assert.True(first.Join(JoinTimeoutMs) && second.Join(JoinTimeoutMs), "the workers end");
        AssertWorkerClean();
        Release(s_tableA);
        Release(s_tableB);
        UserPageTable.FreeFrame(a);
        UserPageTable.FreeFrame(b);
    }

    private static void TwoTablesWorker(UserPageTable table, ulong value)
    {
        if (!PinHere(s_workerCpu))
        {
            Fail("both workers share one CPU");
            return;
        }

        table.BindToCurrentThread();
        Interlocked.Increment(ref s_step);
        while (s_step < 2)
        {
            SysThread.Yield();
        }

        for (int i = 0; i < SwitchRounds && s_failure == null; i++)
        {
            if (*(ulong*)User != value || Cr3() != table.Root)
            {
                Fail("each thread runs on its own table");
            }

            SysThread.Yield();
        }

        UserPageTable.UnbindCurrentThread();
    }

    private static void TestBindRefusals()
    {
        Reset();
        ulong mainRoot = Cr3();
        s_tableA = TableWith(ValueA, out ulong frame);
        var worker = new SysThread(BindRefusalsWorker);
        worker.Start();
        Assert.True(worker.Join(JoinTimeoutMs), "the worker ends");
        AssertWorkerClean();
        Release(s_tableA);
        Assert.True(ThrowsOnBind(s_tableA) == 2, "a disposed table refuses the bind");
        Assert.True(Cr3() == mainRoot, "and loads nothing");
        UserPageTable.FreeFrame(frame);
    }

    private static void BindRefusalsWorker()
    {
        UserPageTable table = s_tableA!;
        // Unbound: nothing happens.
        UserPageTable.UnbindCurrentThread();
        table.BindToCurrentThread();
        if (ThrowsOnBind(table) != 1)
        {
            Fail("a second bind is refused");
        }

        UserPageTable other = Create();
        if (ThrowsOnBind(other) != 1)
        {
            Fail("a bound thread cannot bind another table");
        }

        other.Dispose();
        if (!ThrowsOnDispose(table))
        {
            Fail("dispose is refused while a thread is bound");
        }

        if (*(ulong*)User != ValueA || !table.TryTranslate(User, true, out _))
        {
            Fail("the refused dispose changed nothing");
        }

        UserPageTable.UnbindCurrentThread();
        UserPageTable.UnbindCurrentThread();
    }

    /// <summary>0: bound; 1: InvalidOperationException; 2: ObjectDisposedException.</summary>
    private static int ThrowsOnBind(UserPageTable table)
    {
        try
        {
            table.BindToCurrentThread();
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 2;
        }
        catch (InvalidOperationException)
        {
            return 1;
        }
    }

    private static bool ThrowsOnDispose(UserPageTable table)
    {
        try
        {
            table.Dispose();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    // The kernel half is shared: allocation and collection work under the table.
    private static void TestGcUnderTable()
    {
        Reset();
        s_tableA = TableWith(ValueA, out ulong frame);
        var worker = new SysThread(GcWorker);
        worker.Start();
        Assert.True(worker.Join(JoinTimeoutMs), "the worker ends");
        AssertWorkerClean();
        Release(s_tableA);
        UserPageTable.FreeFrame(frame);
    }

    private static void GcWorker()
    {
        s_tableA!.BindToCurrentThread();
        long sum = 0;
        for (int i = 0; i < GcRounds && s_failure == null; i++)
        {
            var garbage = new byte[4096 + i];
            garbage[i] = (byte)i;
            sum += garbage[i];
            GC.Collect();
            if (*(ulong*)User != ValueA || Cr3() != s_tableA.Root)
            {
                Fail("the table stays loaded across a collection");
            }
        }

        if (sum != (long)GcRounds * (GcRounds - 1) / 2)
        {
            Fail("the allocations hold their data");
        }

        UserPageTable.UnbindCurrentThread();
    }

    // A thread that ends bound releases the table: Dispose after Join succeeds.
    private static void TestThreadEndUnbinds()
    {
        Reset();
        s_tableA = TableWith(ValueA, out ulong frame);
        var worker = new SysThread(() =>
        {
            s_tableA!.BindToCurrentThread();
            s_sink = *(ulong*)User;
        });
        worker.Start();
        Assert.True(worker.Join(JoinTimeoutMs), "the worker ends");
        Assert.True(s_sink == ValueA, "it read its page");
        Assert.False(ThrowsOnDispose(s_tableA), "the binding ended with the thread");
        Assert.True(*At(frame) == ValueA, "the frame survives the table");
        UserPageTable.FreeFrame(frame);
    }

    private static void BoundSpinner()
    {
        if (!PinHere(s_workerCpu))
        {
            Fail("the victim runs on Main's CPU");
        }

        s_victimId = CurrentThreadId();
        s_tableA!.BindToCurrentThread();
        s_step = 1;
        while (!s_release)
        {
            s_sink = *(ulong*)User;
            SysThread.Yield();
        }

        UserPageTable.UnbindCurrentThread();
    }

    // A killed thread that waited in the run queue ends through ExitThread.
    private static void TestKillQueued()
    {
        Reset();
        s_tableA = TableWith(ValueA, out ulong frame);
        CpuSet mainSet = ThreadAffinity.Current;
        try
        {
            // Victim and Main share Main's CPU: while Main kills, the victim is queued.
            s_workerCpu = KernelCpus.Current;
            Assert.True(PinHere(s_workerCpu), "Main stays on its CPU");
            var victim = new SysThread(BoundSpinner);
            victim.Start();
            Assert.True(WaitFor(() => s_step == 1), "the victim is bound");
            Assert.Equal((int)ThreadKillResult.Killed, (int)SchedulerDiagnostics.RequestKill(s_victimId), "a queued thread is killed");
            Assert.True(victim.Join(JoinTimeoutMs), "Join returns");
            Assert.False(ThrowsOnDispose(s_tableA), "the binding ended with the kill");
        }
        finally
        {
            s_release = true;
            ThreadAffinity.Current = mainSet;
        }

        UserPageTable.FreeFrame(frame);
    }

    // A thread given up at a fault never comes back through ExitThread: the
    // switch away from it ends the binding.
    private static CpuExceptionAction AbandonVictimOnly(in CpuExceptionInfo exception)
        => exception.ThreadId == s_victimId && exception.ThreadId != CpuExceptionInfo.NoThread
            ? CpuExceptionAction.AbandonThread
            : CpuExceptionAction.Halt;

    private static void TestAbandon()
    {
        Reset();
        s_tableA = TableWith(ValueA, out ulong frame);
        CpuExceptionHandler? previous = CpuExceptionHook.Handler;
        CpuExceptionHook.Handler = AbandonVictimOnly;
        try
        {
            var victim = new SysThread(() =>
            {
                s_victimId = CurrentThreadId();
                s_tableA!.BindToCurrentThread();
                s_step = 1;
                // Page 0 is not mapped in the user table either.
                s_sink = *(ulong*)0;
            });
            victim.Start();
            Assert.True(WaitFor(() => s_step == 1), "the victim is bound");
            ThreadReapResult result = ThreadReapResult.NotAbandoned;
            Assert.True(WaitFor(() =>
            {
                result = SchedulerDiagnostics.TryReapAbandoned(s_victimId);
                return result != ThreadReapResult.NotAbandoned && result != ThreadReapResult.StillOnCpu;
            }), "the reap finishes");
            Assert.Equal((int)ThreadReapResult.Reaped, (int)result, "the victim is reaped");
            Assert.True(victim.Join(JoinTimeoutMs), "Join returns");
            Assert.False(ThrowsOnDispose(s_tableA), "the binding ended at the switch away");
        }
        finally
        {
            CpuExceptionHook.Handler = previous;
        }

        UserPageTable.FreeFrame(frame);
    }

    private static uint OtherCpu() => (KernelCpus.Current + 1) % (uint)KernelCpus.Count;

    // Option B: no shootdown before N4, so a change is refused while another
    // CPU runs on the table, and the entry stays as it was.
    private static void TestActiveElsewhere()
    {
        Reset();
        s_tableA = TableWith(ValueA, out ulong frame);
        ulong second = Frame();
        CpuSet mainSet = ThreadAffinity.Current;
        try
        {
            Assert.True(PinHere(KernelCpus.Current), "Main stays on its CPU");
            s_workerCpu = OtherCpu();
            var worker = new SysThread(ActiveWorker);
            worker.Start();
            Assert.True(WaitFor(() => s_step == 1), "the worker runs bound on the other CPU");
            AssertWorkerClean();
            Assert.False(s_tableA.TryProtect(User, false, false), "protect is refused");
            Assert.True(s_tableA.Unmap(User) == 0, "unmap is refused");
            Assert.True(s_tableA.TryTranslate(User, true, out ulong physical) && physical == frame, "the entry is unchanged");
            Assert.True(s_tableA.TryMap(User + PageSize, second, true, false), "a new page needs no shootdown");
            s_release = true;
            Assert.True(worker.Join(JoinTimeoutMs), "the worker ends");
            AssertWorkerClean();
            Assert.True(s_tableA.TryProtect(User, false, false), "protect once no CPU runs on the table");
            Assert.True(s_tableA.Unmap(User) == frame && s_tableA.Unmap(User + PageSize) == second, "unmap too");
            Release(s_tableA);
        }
        finally
        {
            s_release = true;
            ThreadAffinity.Current = mainSet;
        }

        UserPageTable.FreeFrame(frame);
        UserPageTable.FreeFrame(second);
    }

    private static void ActiveWorker()
    {
        if (!PinHere(s_workerCpu))
        {
            Fail("the worker runs on the other CPU");
            s_step = 1;
            return;
        }

        s_tableA!.BindToCurrentThread();
        s_step = 1;
        // No yield: the CPU holds the table until the release.
        while (!s_release)
        {
            s_sink = *(ulong*)User;
        }

        if (*(ulong*)User != ValueA)
        {
            Fail("the refused change left the page");
        }

        UserPageTable.UnbindCurrentThread();
    }

    // A bound thread on the other CPU switches in and out without pause,
    // against a filler thread there, while Main flips the page's write bit.
    // A refusal must not touch the entry at all (no write taken back later):
    // the worker, each time it runs, reads the entry and counts every change
    // of the bit it sees. It may miss changes, never see more than Main made.
    private static void TestProtectRace()
    {
        Reset();
        s_seenChanges = 0;
        s_tableA = TableWith(ValueA, out ulong frame);
        CpuSet mainSet = ThreadAffinity.Current;
        int done = 0;
        int refused = 0;
        bool writable = true;
        try
        {
            Assert.True(PinHere(KernelCpus.Current), "Main stays on its CPU");
            s_workerCpu = OtherCpu();
            var filler = new SysThread(FillerWorker);
            var worker = new SysThread(RacingWorker);
            filler.Start();
            worker.Start();
            Assert.True(WaitFor(() => s_step == 1), "the worker runs bound on the other CPU");
            for (int i = 0; i < ProtectRaceRounds && (done < ProtectRaceEnough || refused < ProtectRaceEnough); i++)
            {
                if (s_tableA.TryProtect(User, !writable, false))
                {
                    writable = !writable;
                    done++;
                }
                else
                {
                    refused++;
                    if (LeafWritable(s_tableA, User) != writable)
                    {
                        Fail("a refused protect left the entry changed");
                    }
                }
            }

            s_release = true;
            Assert.True(worker.Join(JoinTimeoutMs) && filler.Join(JoinTimeoutMs), "the workers end");
            AssertWorkerClean();
            Assert.True(done > 0 && refused > 0, "changes went through and were refused, both");
            Assert.True(Volatile.Read(ref s_seenChanges) <= done, "the worker saw no change Main did not make");
            Assert.True(s_tableA.TryTranslate(User, true, out _) == writable, "the entry holds the last change");
            Assert.True(s_tableA.Unmap(User) == frame, "unmap once no CPU runs on the table");
            Release(s_tableA);
        }
        finally
        {
            s_release = true;
            ThreadAffinity.Current = mainSet;
        }

        UserPageTable.FreeFrame(frame);
    }

    private static void RacingWorker()
    {
        if (!PinHere(s_workerCpu))
        {
            Fail("the worker runs on the other CPU");
            s_step = 1;
            return;
        }

        s_tableA!.BindToCurrentThread();
        s_step = 1;
        bool last = LeafWritable(s_tableA, User);
        while (!s_release)
        {
            for (int i = 0; i < 64; i++)
            {
                bool now = LeafWritable(s_tableA, User);
                if (now != last)
                {
                    last = now;
                    Interlocked.Increment(ref s_seenChanges);
                }
            }

            if (*(ulong*)User != ValueA)
            {
                Fail("the worker reads its page");
            }

            // To the filler: the CPU loads its kernel root and back.
            SysThread.Yield();
        }

        UserPageTable.UnbindCurrentThread();
    }

    private static void FillerWorker()
    {
        if (!PinHere(s_workerCpu))
        {
            Fail("the filler runs on the other CPU");
            return;
        }

        while (!s_release)
        {
            SysThread.Yield();
        }
    }

    // A bound thread that sleeps leaves its CPU on the kernel root: the change
    // goes through, and the switch back walks the new entry.
    private static void TestBoundNotRunning()
    {
        Reset();
        s_tableA = TableWith(ValueA, out ulong a);
        ulong b = Frame();
        *At(b) = ValueB;
        CpuSet mainSet = ThreadAffinity.Current;
        try
        {
            Assert.True(PinHere(KernelCpus.Current), "Main stays on its CPU");
            s_workerCpu = OtherCpu();
            var worker = new SysThread(SleepingWorker);
            worker.Start();
            Assert.True(WaitFor(() => s_step == 1), "the worker is bound and asleep");
            AssertWorkerClean();
            // The worker wakes every few milliseconds; a refusal then is right, so try again.
            ulong unmapped = 0;
            Assert.True(WaitFor(() => (unmapped = s_tableA!.Unmap(User)) != 0), "unmap while the thread sleeps");
            Assert.True(unmapped == a, "the old frame comes back");
            Assert.True(s_tableA.TryMap(User, b, true, false), "another frame at the same address");
            s_step = 2;
            Assert.True(worker.Join(JoinTimeoutMs), "the worker ends");
            AssertWorkerClean();
            Assert.True(s_tableA.Unmap(User) == b, "unmap");
            Release(s_tableA);
        }
        finally
        {
            s_release = true;
            ThreadAffinity.Current = mainSet;
        }

        UserPageTable.FreeFrame(a);
        UserPageTable.FreeFrame(b);
    }

    private static void SleepingWorker()
    {
        if (!PinHere(s_workerCpu))
        {
            Fail("the worker runs on the other CPU");
        }

        s_tableA!.BindToCurrentThread();
        if (*(ulong*)User != ValueA)
        {
            Fail("the worker reads the first frame");
        }

        s_step = 1;
        for (int i = 0; i < PollRetries && s_step != 2; i++)
        {
            SysThread.Sleep(PollMs);
        }

        if (*(ulong*)User != ValueB)
        {
            Fail("the worker sees the new frame, not a stale translation");
        }

        UserPageTable.UnbindCurrentThread();
    }

    // A running thread is only marked dead; the switch away from it ends the
    // binding, its CPU loads the kernel root, and Dispose succeeds. The thread
    // keeps its slot (ThreadKillResult.MarkedForExit), as without the patch.
    private static void TestKillRunning()
    {
        Reset();
        s_tableA = TableWith(ValueA, out ulong frame);
        CpuSet mainSet = ThreadAffinity.Current;
        try
        {
            Assert.True(PinHere(KernelCpus.Current), "Main stays on its CPU");
            s_workerCpu = OtherCpu();
            var victim = new SysThread(RunningVictim);
            victim.Start();
            Assert.True(WaitFor(() => s_step == 1), "the victim runs bound on the other CPU");
            AssertWorkerClean();
            Assert.Equal((int)ThreadKillResult.MarkedForExit, (int)SchedulerDiagnostics.RequestKill(s_victimId), "a running thread is marked");
            Assert.True(WaitFor(() => !ThrowsOnDispose(s_tableA!)), "the binding ends at the switch away");
            Assert.True(*At(frame) == ValueA, "the frame survives");
        }
        finally
        {
            s_release = true;
            ThreadAffinity.Current = mainSet;
        }

        UserPageTable.FreeFrame(frame);
    }

    private static void RunningVictim()
    {
        if (!PinHere(s_workerCpu))
        {
            Fail("the victim runs on the other CPU");
        }

        s_victimId = CurrentThreadId();
        s_tableA!.BindToCurrentThread();
        s_step = 1;
        while (!s_release)
        {
            s_sink = *(ulong*)User;
        }
    }

#if ARCH_X64
    private const ulong AddrMask = 0x000F_FFFF_FFFF_F000;
    private const ulong Present = 1;
    private const ulong UserBit = 4;
    private const ulong AccessedBit = 0x20;
    private const int KernelHalf = 256;
    private const int Entries = 512;

    private static bool LeafWritable(UserPageTable table, ulong user) => (Leaf(table, user, out _) & 2) != 0;

    private static ulong* KernelPml4() => At(X64CpuNative.ReadCr3() & AddrMask);

    private static void TestShareFillsUpperHalf()
    {
        for (int i = KernelHalf; i < Entries; i++)
        {
            Assert.True((KernelPml4()[i] & Present) != 0, "every entry of the upper half is present");
        }
    }

    private static void TestCreateHalves()
    {
        using UserPageTable table = Create();
        ulong* root = At(table.Root);
        for (int i = 0; i < KernelHalf; i++)
        {
            Assert.True(root[i] == 0, "the lower half is empty");
        }

        for (int i = KernelHalf; i < Entries; i++)
        {
            Assert.True((root[i] & UserBit) == 0, "the upper half is closed to ring 3");
            // The CPU sets the accessed bit in the kernel's table only.
            ulong mask = ~(UserBit | AccessedBit);
            Assert.True((root[i] & mask) == (KernelPml4()[i] & mask), "and the kernel's");
        }
    }

    private static ulong Leaf(UserPageTable table, ulong user, out bool userAllTheWay)
    {
        ulong entry = table.Root | Present | UserBit;
        userAllTheWay = true;
        for (int shift = 39; shift >= 12; shift -= 9)
        {
            entry = At(entry & AddrMask)[(user >> shift) & 0x1FF];
            userAllTheWay &= (entry & UserBit) != 0;
        }

        return entry;
    }

    private static void TestEntryBits()
    {
        using UserPageTable table = Create();
        ulong frame = Frame();
        const ulong noExecute = 1UL << 63;
        Assert.True(table.TryMap(User, frame, false, false), "read");
        Assert.True(Leaf(table, User, out bool user) == (frame | 5 | noExecute) && user, "present, user, no execute");
        Assert.True(table.TryProtect(User, true, false), "write");
        Assert.True(Leaf(table, User, out user) == (frame | 7 | noExecute) && user, "and writable");
        Assert.True(table.TryProtect(User, false, true), "execute");
        Assert.True(Leaf(table, User, out user) == (frame | 5) && user, "executable, not writable");
        Assert.True(table.Unmap(User) == frame, "unmap");
        Assert.True(Leaf(table, User, out _) == 0, "the entry is cleared");
        UserPageTable.FreeFrame(frame);
    }

    private static void TestDeviceMapper()
    {
        using UserPageTable table = Create();
        ulong* pml4 = KernelPml4();
        var before = new ulong[Entries];
        for (int i = 0; i < Entries; i++)
        {
            before[i] = pml4[i] & ~AccessedBit;
        }

        // The HHDM slot of 512 GiB: never accessed, only mapped, so
        // it does not matter whether the CPU has that physical address bit.
        Assert.True(DeviceMapper.EnsureMapped(1UL << 39), "a block in a slot Limine left empty");
        // 2^47 past the HHDM base leaves the kernel half, into a user space's.
        Assert.False(DeviceMapper.EnsureMapped(1UL << 47), "an address that wraps into the lower half");
        for (int i = 0; i < Entries; i++)
        {
            Assert.True((pml4[i] & ~AccessedBit) == before[i], "no PML4 entry was added or changed");
        }

        // The block reached the table made before it, through the shared slot.
        int slot = (int)(((AddressSpace.HhdmOffset + (1UL << 39)) >> 39) & 0x1FF);
        ulong pdpt = At(table.Root)[slot] & AddrMask;
        Assert.True((At(pdpt)[0] & Present) != 0, "a table created before sees the mapping");
    }
#else
    private static bool LeafWritable(UserPageTable table, ulong user) => false;

    private static void TestShareFillsUpperHalf()
    {
    }

    private static void TestCreateHalves()
    {
    }

    private static void TestEntryBits()
    {
    }

    private static void TestDeviceMapper()
    {
    }
#endif
}
