// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.Scheduler;
using SpinLock = Cosmos.Kernel.Core.Scheduler.SpinLock;

namespace Cosmos.Kernel.Core.Memory;

// ZehOS patch user-page-tables, see PATCHES.md

/// <summary>
/// The architecture's half of <see cref="UserPageTable"/>. An abstract class
/// rather than an interface: its members run under the table's spin lock, and
/// the first call through an interface dispatch cell may allocate. Every
/// address arrives validated: page-aligned (but for
/// <see cref="TryTranslate"/>) and inside the user range.
/// </summary>
internal abstract class UserPageTableArch
{
    /// <summary>Populates the shared kernel half. Callable again after either outcome.</summary>
    public abstract bool ShareKernelHalf();

    /// <summary>Allocates a root with an empty user half and the shared kernel half. 0 without memory.</summary>
    public abstract ulong CreateRoot();

    /// <summary>Installs a page where none is mapped. Caller holds the table's lock.</summary>
    public abstract bool TryMap(ulong root, ulong user, ulong physical, bool writable, bool executable);

    /// <summary>Changes the access of a mapped page. Caller holds the table's lock.</summary>
    public abstract bool TryProtect(ulong root, ulong user, bool writable, bool executable);

    /// <summary>Removes a page and returns its frame, or 0. Caller holds the table's lock.</summary>
    public abstract ulong Unmap(ulong root, ulong user);

    /// <summary>The physical address a user address reaches. Caller holds the table's lock.</summary>
    public abstract bool TryTranslate(ulong root, ulong user, bool write, out ulong physical);

    /// <summary>Frees the root and every table of its user half, not the pages mapped there.</summary>
    public abstract void Destroy(ulong root);

    // ZehOS patch user-cr3, see PATCHES.md
    /// <summary>The root this CPU translates through now. Interrupts masked.</summary>
    public abstract ulong ReadRoot();

    /// <summary>Makes this CPU translate through <paramref name="root"/>. Interrupts masked.</summary>
    public abstract void LoadRoot(ulong root);

    /// <summary>Drops this CPU's cached translation of one page. Interrupts masked.</summary>
    public abstract void InvalidatePage(ulong user);
}

/// <summary>
/// The page tables of one user address space: an empty lower half of its own,
/// and the kernel's upper half, whose top-level entries every space shares.
/// The table owns its tables and not the pages mapped in it: the caller frees
/// those (<see cref="FreeFrame"/>), before or after <see cref="Dispose"/>.
/// <para>
/// A thread runs on a table once it is bound to it
/// (<see cref="BindToCurrentThread"/>): the switch to the thread loads the
/// table on its CPU, every other switch loads that CPU's kernel root.
/// <see cref="Unmap"/>, <see cref="UnmapRange"/>, <see cref="TryProtect"/>
/// and <see cref="TryProtectRange"/> shoot the change down on every CPU that
/// holds the table (ZehOS patch tlb-shootdown): they return once no CPU can
/// reach the old entry any more, so a frame unmapped is the caller's to free.
/// They wait for the other CPUs and therefore run in thread context only,
/// with interrupts enabled and no spin lock held; with interrupts masked they
/// panic rather than risk two callers waiting on each other.
/// </para>
/// <para>
/// Thread context only. Members of one table may be called from any thread;
/// its lock takes the <see cref="PageAllocator"/> lock beneath it and no
/// other. A table that is empty after <see cref="Unmap"/> stays until
/// <see cref="Dispose"/>. There is no finalizer: an undisposed table leaks
/// its pages.
/// </para>
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public sealed unsafe class UserPageTable : IDisposable
{
    /// <summary>
    /// First address that can be mapped. The page at 0 cannot: kernel code
    /// will run on this table too, and its null dereferences must keep
    /// faulting rather than reach memory a program controls.
    /// </summary>
    private const ulong UserStart = PageAllocator.PageSize;

    /// <summary>
    /// End of the range that can be mapped, one page below the end of the
    /// canonical lower half: a <c>syscall</c> as the last instruction of that
    /// page would leave a non-canonical return address, which <c>sysret</c>
    /// faults on in ring 0.
    /// </summary>
    private const ulong UserEnd = 0x0000_7FFF_FFFF_F000;

    /// <summary>End of the physical addresses a table entry can hold (52 bits).</summary>
    private const ulong PhysicalEnd = 1UL << 52;

    /// <summary>Mask of an address's offset inside its page.</summary>
    private const ulong PageOffsetMask = PageAllocator.PageSize - 1;

    /// <summary>Registered once at boot by the platform; null where there is no implementation (ARM64).</summary>
    private static UserPageTableArch? s_arch;

    /// <summary>Whether the kernel half is shared. Set once, after the half is complete.</summary>
    private static bool s_kernelHalfShared;

    /// <summary>Guards the tables below <see cref="_root"/>, and the field itself.</summary>
    private SpinLock _lock;

    /// <summary>Physical address of the top-level table; 0 once disposed.</summary>
    private ulong _root;

    // ZehOS patch user-cr3, see PATCHES.md
    /// <summary>
    /// Threads bound to the table. Raised under <see cref="_lock"/>, so that
    /// <see cref="Dispose"/> sees every binding it races with; lowered with a
    /// bare interlocked step, from interrupt context too (a thread given up).
    /// </summary>
    private int _bound;

    // ZehOS patch tlb-shootdown, see PATCHES.md
    /// <summary>
    /// CPUs whose CR3 holds the table, one bit each. A CPU sets its bit,
    /// interlocked, before the CR3 write that loads the table, and clears it,
    /// interlocked, after the CR3 write that loads another root; a switch
    /// between two threads of the table writes no CR3 and leaves the bit (the
    /// CPU's TLB keeps the entries). A change reads the mask after the entry's
    /// write, with a full barrier between: a CPU missing from it loads CR3, and
    /// walks the new entry, only afterwards.
    /// </summary>
    private ActiveMask _active;

    /// <summary>
    /// How long a shootdown waits for another CPU before it panics, in
    /// milliseconds; 0 waits forever. A frame freed without the other CPU's
    /// flush would let a program reach memory that is no longer its own, so
    /// the wait never gives up quietly. Generous by default: under emulation
    /// the host may take a virtual CPU away for tenths of a second. Set by the
    /// system's policy (ZehOS: boot option zehos.tlbtimeout), from any thread.
    /// </summary>
    public static int ShootdownTimeoutMs
    {
        get => Volatile.Read(ref s_shootdownTimeoutMs);
        set => Volatile.Write(ref s_shootdownTimeoutMs, value < 0 ? 0 : value);
    }

    private static int s_shootdownTimeoutMs = 5000;

    /// <summary>After this long a CPU that did not answer gets the interrupt again, in milliseconds.</summary>
    private const int ShootdownResendMs = 100;

    private static long s_shootdowns;
    private static long s_longestShootdownTicks;

    /// <summary>Shootdowns that had to interrupt another CPU, over the kernel's life.</summary>
    public static long Shootdowns => Volatile.Read(ref s_shootdowns);

    /// <summary>The longest wait of such a shootdown, in microseconds, over the kernel's life.</summary>
    public static long LongestShootdownMicroseconds
        => Volatile.Read(ref s_longestShootdownTicks) * 1_000_000 / Stopwatch.Frequency;

    private UserPageTable(ulong root)
    {
        _root = root;
    }

    /// <summary>Physical address of the top-level table, 0 once disposed.</summary>
    internal ulong Root => Volatile.Read(ref _root);

    /// <summary>Called once by the platform initializer, before any thread but the boot one runs.</summary>
    internal static void RegisterArch(UserPageTableArch arch)
    {
        s_arch = arch;
    }

    /// <summary>
    /// Creates every top-level entry of the kernel half, once, so that tables
    /// created afterwards see each kernel mapping made later. Costs up to
    /// 1 MiB, which is kept. Callable again: true right away after a success.
    /// </summary>
    /// <returns>False when the platform has no user page tables, or without memory.</returns>
    public static bool ShareKernelHalf()
    {
        if (Volatile.Read(ref s_kernelHalfShared))
        {
            return true;
        }

        if (s_arch == null || !s_arch.ShareKernelHalf())
        {
            return false;
        }

        Volatile.Write(ref s_kernelHalfShared, true);
        return true;
    }

    /// <summary>Creates a table with an empty user half.</summary>
    /// <returns>Null before <see cref="ShareKernelHalf"/> succeeded, or without memory.</returns>
    public static UserPageTable? TryCreate()
    {
        if (!Volatile.Read(ref s_kernelHalfShared))
        {
            return null;
        }

        // The object first: an allocation that throws must not leak a root.
        UserPageTable table = new(0);
        ulong root = s_arch!.CreateRoot();
        if (root == 0)
        {
            return null;
        }

        Volatile.Write(ref table._root, root);
        return table;
    }

    /// <summary>Maps one 4 KiB page.</summary>
    /// <param name="user">Page-aligned user address; neither the first nor the last page of the half.</param>
    /// <param name="physical">Page-aligned physical address of the frame, not 0.</param>
    /// <param name="writable">Whether the program may write. Not together with <paramref name="executable"/>.</param>
    /// <param name="executable">Whether the program may execute.</param>
    /// <returns>False, with nothing mapped, for a refused argument, a page already mapped, without memory, or once disposed.</returns>
    public bool TryMap(ulong user, ulong physical, bool writable, bool executable)
    {
        if (!IsUserPage(user) || (writable && executable)
            || physical == 0 || physical >= PhysicalEnd || (physical & PageOffsetMask) != 0)
        {
            return false;
        }

        using (_lock.AcquireIrqSafe())
        {
            return _root != 0 && s_arch!.TryMap(_root, user, physical, writable, executable);
        }
    }

    /// <summary>
    /// Changes the access of a mapped page, on every CPU: returns once no CPU
    /// can use the old access any more. Thread context, interrupts enabled.
    /// </summary>
    /// <param name="user">Page-aligned user address.</param>
    /// <param name="writable">Whether the program may write. Not together with <paramref name="executable"/>.</param>
    /// <param name="executable">Whether the program may execute.</param>
    /// <returns>False, with nothing changed, for a refused argument, a page not mapped, or once disposed.</returns>
    public bool TryProtect(ulong user, bool writable, bool executable)
    {
        return TryProtectRange(user, 1, writable, executable);
    }

    // ZehOS patch tlb-shootdown, see PATCHES.md
    /// <summary>
    /// <see cref="TryProtect"/> for <paramref name="count"/> pages from
    /// <paramref name="start"/>, with one shootdown for all of them. Every
    /// page must be mapped; otherwise nothing changes.
    /// </summary>
    /// <param name="start">Page-aligned user address of the first page.</param>
    /// <param name="count">Number of pages, at least 1.</param>
    /// <param name="writable">Whether the program may write. Not together with <paramref name="executable"/>.</param>
    /// <param name="executable">Whether the program may execute.</param>
    /// <returns>False, with nothing changed, for a refused argument, a page not mapped, or once disposed.</returns>
    public bool TryProtectRange(ulong start, int count, bool writable, bool executable)
    {
        if (!IsUserRange(start, count) || (writable && executable))
        {
            return false;
        }

        RequireWaitableContext();
        Span<long> epochs = stackalloc long[EpochSlots()];
        ShootdownSet set;
        using (_lock.AcquireIrqSafe())
        {
            if (_root == 0)
            {
                return false;
            }

            // All or nothing: the pages are checked before the first is changed.
            for (int i = 0; i < count; i++)
            {
                if (!s_arch!.TryTranslate(_root, start + (ulong)i * PageAllocator.PageSize, false, out _))
                {
                    return false;
                }
            }

            for (int i = 0; i < count; i++)
            {
                ulong user = start + (ulong)i * PageAllocator.PageSize;
                s_arch!.TryProtect(_root, user, writable, executable);
                InvalidateHere(user);
            }

            set = Snapshot(epochs);
        }

        Shootdown(in set, epochs);
        return true;
    }

    /// <summary>
    /// Removes a page; returns once no CPU can reach it any more, so its
    /// frame is the caller's afterwards. Thread context, interrupts enabled.
    /// </summary>
    /// <param name="user">Page-aligned user address.</param>
    /// <returns>The frame's physical address; 0, with nothing changed, when nothing is mapped there or once disposed.</returns>
    public ulong Unmap(ulong user)
    {
        if (!IsUserPage(user))
        {
            return 0;
        }

        Span<ulong> frame = stackalloc ulong[1];
        UnmapRange(user, 1, frame);
        return frame[0];
    }

    // ZehOS patch tlb-shootdown, see PATCHES.md
    /// <summary>
    /// <see cref="Unmap"/> for <paramref name="count"/> pages from
    /// <paramref name="start"/>, with one shootdown for all of them.
    /// </summary>
    /// <param name="start">Page-aligned user address of the first page.</param>
    /// <param name="count">Number of pages, at least 1.</param>
    /// <param name="frames">Receives each page's frame, 0 where nothing was mapped; at least <paramref name="count"/> long.</param>
    /// <returns>The number of pages that were mapped; 0 for a refused argument or once disposed.</returns>
    public int UnmapRange(ulong start, int count, Span<ulong> frames)
    {
        if (!IsUserRange(start, count) || frames.Length < count)
        {
            return 0;
        }

        frames.Slice(0, count).Clear();
        RequireWaitableContext();
        Span<long> epochs = stackalloc long[EpochSlots()];
        ShootdownSet set;
        int removed = 0;
        using (_lock.AcquireIrqSafe())
        {
            if (_root == 0)
            {
                return 0;
            }

            for (int i = 0; i < count; i++)
            {
                ulong user = start + (ulong)i * PageAllocator.PageSize;
                ulong frame = s_arch!.Unmap(_root, user);
                if (frame != 0)
                {
                    frames[i] = frame;
                    removed++;
                    InvalidateHere(user);
                }
            }

            // Nothing removed, nothing any CPU could hold: x86 caches no entry that is not present.
            if (removed == 0)
            {
                return 0;
            }

            set = Snapshot(epochs);
        }

        Shootdown(in set, epochs);
        return removed;
    }

    /// <summary>Translates a user address, aligned or not, as the program could reach it.</summary>
    /// <param name="user">User address.</param>
    /// <param name="write">Whether the page must be writable.</param>
    /// <param name="physical">The physical address, offset included; 0 on failure.</param>
    /// <returns>False when the program could not read (or write) there.</returns>
    public bool TryTranslate(ulong user, bool write, out ulong physical)
    {
        physical = 0;
        if (user < UserStart || user >= UserEnd)
        {
            return false;
        }

        using (_lock.AcquireIrqSafe())
        {
            return _root != 0 && s_arch!.TryTranslate(_root, user, write, out physical);
        }
    }

    /// <summary>
    /// Frees the tables, not the pages mapped in them. Waits for the CPUs that
    /// still hold the table in their CR3 since an unbound thread left them.
    /// Further calls do nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">A thread is still bound to the table.</exception>
    public void Dispose()
    {
        ulong root;
        using (_lock.AcquireIrqSafe())
        {
            if (Volatile.Read(ref _bound) > 0)
            {
                throw new InvalidOperationException("A thread is bound to the table.");
            }

            root = _root;
            _root = 0;
        }

        // With no thread bound no switch loads the table again; a CPU that
        // still holds it loads its kernel root at its next switch.
        while (!_active.IsEmpty)
        {
            Thread.SpinWait(1);
        }

        // Outside the lock: nothing reaches the tables any more, and a large
        // space has too many to free with interrupts masked.
        if (root != 0)
        {
            s_arch!.Destroy(root);
        }
    }

    // ZehOS patch user-cr3, see PATCHES.md
    /// <summary>
    /// Binds the calling thread to the table and loads it on this CPU at once.
    /// A thread is bound to one table at most; the binding ends with
    /// <see cref="UnbindCurrentThread"/> or with the thread.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    /// <exception cref="InvalidOperationException">The thread is bound already, or is no scheduler thread.</exception>
    public void BindToCurrentThread()
    {
        using (_lock.AcquireIrqSafe())
        {
            ObjectDisposedException.ThrowIf(_root == 0, this);
            PerCpuState? state = SchedulerManager.CurrentCpuState;
            SchedulerThread? thread = state?.CurrentThread;
            if (thread is null || thread.IsIdle || thread._pageTable is not null)
            {
                throw new InvalidOperationException("The thread cannot be bound.");
            }

            // Interrupts are masked: the thread stays on this CPU until the load.
            Interlocked.Increment(ref _bound);
            thread._pageTable = this;
            SwitchTo(state!, this);
        }
    }

    /// <summary>Ends the calling thread's binding and loads this CPU's kernel root. Unbound: nothing.</summary>
    public static void UnbindCurrentThread()
    {
        using (InternalCpu.DisableInterruptsScope())
        {
            PerCpuState? state = SchedulerManager.CurrentCpuState;
            SchedulerThread? thread = state?.CurrentThread;
            if (thread is null)
            {
                return;
            }

            Unbind(thread);
            SwitchTo(state!, null);
        }
    }

    /// <summary>
    /// Whether this CPU runs on its kernel root: what code that follows the
    /// identity map (EFI runtime services) needs. Interrupts masked.
    /// </summary>
    internal static bool KernelRootLoaded()
    {
        PerCpuState? state = SchedulerManager.CurrentCpuState;
        return state is null || state._loadedTable is null;
    }

    /// <summary>
    /// Ends a thread's binding, without a load: for a thread that is not
    /// running, or that the caller switches away from. Interrupt context too.
    /// </summary>
    internal static void Unbind(SchedulerThread thread)
    {
        UserPageTable? table = thread._pageTable;
        if (table is not null)
        {
            thread._pageTable = null;
            Interlocked.Decrement(ref table._bound);
        }
    }

    /// <summary>
    /// Loads the table <paramref name="next"/> runs on, or the CPU's kernel
    /// root, when the CPU holds another. Called by a switch under the CPU's
    /// queue lock, before the CPU leaves the outgoing stack: from then on the
    /// outgoing thread can die and its table go, so the CPU must no longer
    /// hold it. No allocation, no lock, no log.
    /// </summary>
    internal static void SwitchFor(PerCpuState state, SchedulerThread next)
    {
        if (!ReferenceEquals(state._loadedTable, next._pageTable))
        {
            SwitchTo(state, next._pageTable);
        }
    }

    /// <summary>Loads <paramref name="table"/> (null: the kernel root) on this CPU. Interrupts masked.</summary>
    private static void SwitchTo(PerCpuState state, UserPageTable? table)
    {
        UserPageTable? old = state._loadedTable;
        if (ReferenceEquals(old, table))
        {
            return;
        }

        if (old is null)
        {
            // The root the CPU came up with: its own, not CPU 0's.
            state._kernelRoot = s_arch!.ReadRoot();
        }

        // ZehOS patch tlb-shootdown, see PATCHES.md
        // Set before the write, cleared after the next one: a shootdown
        // never misses a CPU that may hold a translation of the table.
        if (table is not null)
        {
            table._active.Set(state.CpuId);
        }

        LoadAndCount(state, table is null ? state._kernelRoot : table._root);
        state._loadedTable = table;
        if (old is not null)
        {
            old._active.Clear(state.CpuId);
        }
    }

    // ZehOS patch tlb-shootdown, see PATCHES.md
    /// <summary>
    /// Writes CR3 and counts the flush in the CPU's epoch, which goes odd
    /// before the write and even after it. A shootdown that read the epoch
    /// waits for the next even value that is not its own (see
    /// <see cref="EpochTarget"/>): a write that may have come before the
    /// entry's change never counts. Interrupts masked.
    /// </summary>
    private static void LoadAndCount(PerCpuState state, ulong root)
    {
        Interlocked.Increment(ref state._tlbEpoch);
        s_arch!.LoadRoot(root);
        Interlocked.Increment(ref state._tlbEpoch);
    }

    /// <summary>
    /// The shootdown interrupt's handler (x64 vector 0xF2): reloads CR3 with
    /// the value it holds, which drops every user translation (user pages are
    /// never global), and counts the flush. The register, not
    /// <c>_loadedTable</c>: the interrupt may land between a switch's CR3
    /// write and that field's update, and not under a switch at all, since
    /// the switch masks interrupts. Interrupt context: no lock, no allocation,
    /// no log.
    /// </summary>
    internal static void HandleShootdownInterrupt()
    {
        PerCpuState? state = SchedulerManager.CurrentCpuState;
        if (s_arch is null || state is null)
        {
            return;
        }

        LoadAndCount(state, s_arch.ReadRoot());
    }

    /// <summary>The epoch a CPU has to reach once <paramref name="seen"/> was read: the second even value after it if odd, the next if even.</summary>
    private static long EpochTarget(long seen) => (seen + 3) & ~1L;

    /// <summary>Slots for <see cref="Snapshot"/>: one epoch per CPU the scheduler manages.</summary>
    private static int EpochSlots()
    {
        uint count = SchedulerManager.CpuCount;
        return count == 0 ? 1 : (int)Math.Min(count, (uint)CpuSet.Capacity);
    }

    /// <summary>
    /// What a shootdown needs from under <see cref="_lock"/>: the CPUs that
    /// hold the table, and the CPU the change ran on, which invalidated its
    /// own copy. The epochs go to the caller's stack.
    /// </summary>
    private struct ShootdownSet
    {
        public ActiveMask Mask;
        public uint Self;
        public bool Others;
    }

    /// <summary>
    /// Reads the mask after the entries' write and each other CPU's epoch.
    /// Caller holds <see cref="_lock"/>: interrupts are masked, the caller
    /// cannot move to another CPU between the invalidation and this read.
    /// </summary>
    private ShootdownSet Snapshot(Span<long> epochs)
    {
        // Full barrier: the entries' writes before the read of the mask. A
        // CPU that sets its bit afterwards (interlocked) loads CR3 later still.
        Interlocked.MemoryBarrier();
        ShootdownSet set = default;
        set.Mask = _active.Read();
        set.Self = SchedulerManager.GetCurrentCpuId();
        PerCpuState[]? states = SchedulerManager.GetAllCpuStates();
        for (uint cpu = 0; cpu < (uint)epochs.Length; cpu++)
        {
            if (cpu == set.Self || !set.Mask.Contains(cpu) || states is null || cpu >= (uint)states.Length)
            {
                continue;
            }

            epochs[(int)cpu] = Volatile.Read(ref states[cpu]._tlbEpoch);
            set.Others = true;
        }

        return set;
    }

    /// <summary>
    /// Sends the shootdown interrupt to every CPU of <paramref name="set"/>
    /// but the one that ran the change, and waits until each has flushed
    /// since. Resends after <see cref="ShootdownResendMs"/>, panics after
    /// <see cref="ShootdownTimeoutMs"/>. Interrupts enabled, no lock held: a
    /// CPU that waits here answers the shootdowns of the others.
    /// </summary>
    private static void Shootdown(in ShootdownSet set, Span<long> epochs)
    {
        if (!set.Others)
        {
            return;
        }

#if COSMOS_TEST_HOOKS
        if (Volatile.Read(ref TestSkipShootdown))
        {
            return;
        }
#endif
        PerCpuState[] states = SchedulerManager.GetAllCpuStates()!;
        long start = Stopwatch.GetTimestamp();
        Send(in set, epochs, states, resendOnly: false);
        long resendAt = start + Stopwatch.Frequency * ShootdownResendMs / 1000;
        int timeoutMs = ShootdownTimeoutMs;
        long deadline = timeoutMs == 0 ? long.MaxValue : start + Stopwatch.Frequency * timeoutMs / 1000;
        while (true)
        {
            bool waiting = false;
            for (uint cpu = 0; cpu < (uint)epochs.Length; cpu++)
            {
                if (cpu != set.Self && set.Mask.Contains(cpu)
                    && Volatile.Read(ref states[cpu]._tlbEpoch) < EpochTarget(epochs[(int)cpu]))
                {
                    waiting = true;
                    break;
                }
            }

            if (!waiting)
            {
                break;
            }

            long now = Stopwatch.GetTimestamp();
            if (now >= deadline)
            {
                ReportLate(in set, epochs, states);
            }

            if (now >= resendAt)
            {
                Send(in set, epochs, states, resendOnly: true);
                resendAt = now + Stopwatch.Frequency * ShootdownResendMs / 1000;
            }

            Thread.SpinWait(1);
        }

        Interlocked.Increment(ref s_shootdowns);
        long took = Stopwatch.GetTimestamp() - start;
        long longest = Volatile.Read(ref s_longestShootdownTicks);
        while (took > longest)
        {
            long seen = Interlocked.CompareExchange(ref s_longestShootdownTicks, took, longest);
            if (seen == longest)
            {
                break;
            }

            longest = seen;
        }
    }

    /// <summary>
    /// The interrupt to each CPU of the set that has not flushed yet, or to
    /// all but the one that ran the change. Interrupts masked around each
    /// send, as the ICR protocol asks. A CPU the caller moved to meanwhile
    /// gets one too, which is harmless: it is taken once interrupts are back.
    /// </summary>
    private static void Send(in ShootdownSet set, Span<long> epochs, PerCpuState[] states, bool resendOnly)
    {
        for (uint cpu = 0; cpu < (uint)epochs.Length; cpu++)
        {
            if (cpu == set.Self || !set.Mask.Contains(cpu))
            {
                continue;
            }

            if (resendOnly && Volatile.Read(ref states[cpu]._tlbEpoch) >= EpochTarget(epochs[(int)cpu]))
            {
                continue;
            }

            using (InternalCpu.DisableInterruptsScope())
            {
                InterruptManager.SendTlbShootdown(PerCpuBlock.At(cpu).HardwareId);
            }
        }
    }

    [DoesNotReturn]
    private static void ReportLate(in ShootdownSet set, Span<long> epochs, PerCpuState[] states)
    {
        for (uint cpu = 0; cpu < (uint)epochs.Length; cpu++)
        {
            if (cpu != set.Self && set.Mask.Contains(cpu)
                && Volatile.Read(ref states[cpu]._tlbEpoch) < EpochTarget(epochs[(int)cpu]))
            {
                IO.Serial.WriteString("[UserPageTable] CPU ");
                IO.Serial.WriteNumber(cpu);
                IO.Serial.WriteString(" did not answer the TLB shootdown\n");
            }
        }

        Panic.Halt("A CPU did not flush its TLB for a shootdown: it masks interrupts for longer than ShootdownTimeoutMs.");
    }

    /// <summary>
    /// A change that may wait for other CPUs needs interrupts enabled: two
    /// callers waiting on each other with interrupts masked would never take
    /// each other's interrupt. A kernel bug, so a panic naming the caller.
    /// </summary>
    private static void RequireWaitableContext([CallerMemberName] string caller = "")
    {
        if (!InternalCpu.InterruptsEnabled)
        {
            Panic.Halt("UserPageTable changes wait for a TLB shootdown and need interrupts enabled.", caller);
        }
    }

#if COSMOS_TEST_HOOKS
    // ZehOS patch tlb-shootdown, see PATCHES.md
    // Nur im Testbau (COSMOS_TEST_DEFINES=COSMOS_TEST_HOOKS): laesst Interrupt
    // und Warten des Abschusses aus. Die Gegenprobe im Testkern zeigt damit,
    // dass die Zelle einen veralteten TLB-Eintrag der anderen CPU sieht.
    internal static bool TestSkipShootdown;

#endif
    private bool IsLoadedHere()
    {
        PerCpuState? state = SchedulerManager.CurrentCpuState;
        return state is not null && ReferenceEquals(state._loadedTable, this);
    }

    private void InvalidateHere(ulong user)
    {
        if (IsLoadedHere())
        {
            s_arch!.InvalidatePage(user);
        }
    }

    // ZehOS patch user-frame-reserve, see PATCHES.md
    /// <summary>Pages <see cref="AllocateFrame"/> leaves free for the kernel; 0 (the default) keeps none.</summary>
    private static ulong s_reservedPages;

    /// <summary>
    /// Pages that <see cref="AllocateFrame"/> leaves free for the kernel: a
    /// frame that would take one of them is refused (0), so a program that
    /// fills the memory fails on its own instead of leaving the kernel heap
    /// without pages. Every other allocation may still use them. 0, the
    /// default, keeps none. Set by the system's policy, from any thread.
    /// </summary>
    public static ulong ReservedPages
    {
        get => Volatile.Read(ref s_reservedPages);
        set => Volatile.Write(ref s_reservedPages, value);
    }

    /// <summary>Pages the page allocator manages, free or not; fixed after boot.</summary>
    public static ulong TotalPages => PageAllocator.TotalPageCount;

    /// <summary>Pages free at this moment, the reserve included; a snapshot without the lock.</summary>
    public static ulong FreePages => PageAllocator.FreePageCount;

    /// <summary>Allocates one zeroed frame, never one of the <see cref="ReservedPages"/>.</summary>
    /// <returns>Its physical address, or 0 without memory or on a platform without user page tables.</returns>
    public static ulong AllocateFrame()
    {
        if (s_arch == null)
        {
            return 0;
        }

        void* page = PageAllocator.AllocPages(PageType.Unmanaged, 1, zero: true, aKeepFree: ReservedPages);
        return page == null ? 0 : PageAllocator.VirtualToPhysical((ulong)page);
    }

    /// <summary>Frees a frame <see cref="AllocateFrame"/> returned and no table maps any more; 0 does nothing.</summary>
    /// <param name="physical">The frame's physical address.</param>
    /// <exception cref="ArgumentException">The address is no such frame.</exception>
    public static void FreeFrame(ulong physical)
    {
        if (physical == 0)
        {
            return;
        }

        if ((physical & PageOffsetMask) != 0 || physical >= PhysicalEnd)
        {
            throw new ArgumentException("Not a frame.", nameof(physical));
        }

        byte* page = (byte*)(physical + AddressSpace.HhdmOffset);
        // The run's head only: an address inside a longer run would free all of it.
        if (PageAllocator.GetAllocation(page, out byte* start) != PageType.Unmanaged || start != page)
        {
            throw new ArgumentException("Not a frame.", nameof(physical));
        }

        PageAllocator.Free(page);
    }

    private static bool IsUserPage(ulong user)
    {
        return user >= UserStart && user < UserEnd && (user & PageOffsetMask) == 0;
    }

    private static bool IsUserRange(ulong start, int count)
    {
        return count > 0 && IsUserPage(start)
            && (ulong)count <= (UserEnd - start) / PageAllocator.PageSize;
    }

    // ZehOS patch tlb-shootdown, see PATCHES.md
    /// <summary>
    /// One bit per logical CPU, as many as <see cref="CpuSet"/> holds. Each
    /// word changes interlocked; a reader takes the words one by one, which
    /// is enough: a bit that turns on during the read belongs to a CPU that
    /// loads CR3 after the barrier before it.
    /// </summary>
    private struct ActiveMask
    {
        private long _w0;
        private long _w1;
        private long _w2;
        private long _w3;

        public readonly bool IsEmpty
            => (Volatile.Read(ref Unsafe.AsRef(in _w0)) | Volatile.Read(ref Unsafe.AsRef(in _w1))
                | Volatile.Read(ref Unsafe.AsRef(in _w2)) | Volatile.Read(ref Unsafe.AsRef(in _w3))) == 0;

        public void Set(uint cpu) => Interlocked.Or(ref Word(cpu), 1L << (int)(cpu & 63));

        public void Clear(uint cpu) => Interlocked.And(ref Word(cpu), ~(1L << (int)(cpu & 63)));

        public readonly bool Contains(uint cpu)
        {
            long word = (cpu >> 6) switch
            {
                0 => _w0,
                1 => _w1,
                2 => _w2,
                _ => _w3,
            };
            return cpu < CpuSet.Capacity && (word & (1L << (int)(cpu & 63))) != 0;
        }

        /// <summary>A copy, each word read with acquire semantics.</summary>
        public readonly ActiveMask Read()
        {
            ActiveMask copy = default;
            copy._w0 = Volatile.Read(ref Unsafe.AsRef(in _w0));
            copy._w1 = Volatile.Read(ref Unsafe.AsRef(in _w1));
            copy._w2 = Volatile.Read(ref Unsafe.AsRef(in _w2));
            copy._w3 = Volatile.Read(ref Unsafe.AsRef(in _w3));
            return copy;
        }

        [UnscopedRef]
        private ref long Word(uint cpu)
        {
            switch (cpu >> 6)
            {
                case 0: return ref _w0;
                case 1: return ref _w1;
                case 2: return ref _w2;
                default: return ref _w3;
            }
        }
    }
}
