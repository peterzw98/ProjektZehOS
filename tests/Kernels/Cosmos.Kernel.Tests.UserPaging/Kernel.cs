// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System;
using System.Threading;
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
/// </summary>
public unsafe class Kernel : Sys.Kernel
{
    /// <summary>Total number of tests announced to the test runner for this suite.</summary>
    private const int ExpectedTestCount = 13;

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

#if ARCH_X64
    private const ulong AddrMask = 0x000F_FFFF_FFFF_F000;
    private const ulong Present = 1;
    private const ulong UserBit = 4;
    private const ulong AccessedBit = 0x20;
    private const int KernelHalf = 256;
    private const int Entries = 512;

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
