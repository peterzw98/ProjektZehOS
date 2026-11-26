// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Diagnostics.CodeAnalysis;
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
/// table on its CPU, every other switch loads that CPU's kernel root. There
/// is no TLB shootdown yet (N4): <see cref="TryProtect"/> and
/// <see cref="Unmap"/> invalidate the page on the calling CPU and refuse
/// while another CPU runs on the table.
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

    /// <summary>
    /// CPUs whose CR3 holds the table, and <see cref="Changing"/> while an
    /// entry is rewritten. Raised before the CR3 write, never while
    /// <see cref="Changing"/> is set, and lowered after the next CR3 write,
    /// interlocked: a full barrier each, which <see cref="TryBeginChange"/> pairs with.
    /// </summary>
    private int _active;

    /// <summary>
    /// Bit of <see cref="_active"/> that holds off every switch to the table
    /// while <see cref="TryProtect"/> or <see cref="Unmap"/> rewrites an
    /// entry: set only under <see cref="_lock"/>, and only while no other
    /// CPU holds the table, so no CPU can read the entry into its TLB before
    /// the change is complete. Held for a few instructions, interrupts masked.
    /// </summary>
    private const int Changing = 1 << 30;

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

    /// <summary>Changes the access of a mapped page.</summary>
    /// <param name="user">Page-aligned user address.</param>
    /// <param name="writable">Whether the program may write. Not together with <paramref name="executable"/>.</param>
    /// <param name="executable">Whether the program may execute.</param>
    /// <returns>False, with nothing changed, for a refused argument, a page not mapped, once disposed,
    /// or while another CPU runs on the table (no shootdown before N4).</returns>
    public bool TryProtect(ulong user, bool writable, bool executable)
    {
        if (!IsUserPage(user) || (writable && executable))
        {
            return false;
        }

        using (_lock.AcquireIrqSafe())
        {
            // Refused before the entry is touched: a write taken back later
            // would leave a window in which another CPU caches the new entry.
            if (_root == 0 || !TryBeginChange(out int here))
            {
                return false;
            }

            bool changed = s_arch!.TryProtect(_root, user, writable, executable);
            EndChange(here, user);
            return changed;
        }
    }

    /// <summary>Removes a page; its frame is the caller's afterwards.</summary>
    /// <param name="user">Page-aligned user address.</param>
    /// <returns>The frame's physical address; 0, with nothing changed, when nothing is mapped there,
    /// once disposed, or while another CPU runs on the table (no shootdown before N4).</returns>
    public ulong Unmap(ulong user)
    {
        if (!IsUserPage(user))
        {
            return 0;
        }

        using (_lock.AcquireIrqSafe())
        {
            // As in TryProtect: refused before the entry is touched.
            if (_root == 0 || !TryBeginChange(out int here))
            {
                return 0;
            }

            ulong frame = s_arch!.Unmap(_root, user);
            EndChange(here, user);
            return frame;
        }
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
        while (Volatile.Read(ref _active) != 0)
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

        // Counted before the write, uncounted after the next one: the check
        // in TryProtect never misses a CPU that may hold a translation.
        if (table is not null)
        {
            EnterActive(table);
        }

        s_arch!.LoadRoot(table is null ? state._kernelRoot : table._root);
        state._loadedTable = table;
        if (old is not null)
        {
            Interlocked.Decrement(ref old._active);
        }
    }

    /// <summary>
    /// Counts this CPU into <see cref="_active"/>, waiting out a change in
    /// progress: its holder masks interrupts, takes no further lock and
    /// finishes in a few instructions, so the wait is bounded even under a
    /// queue lock. Interrupts masked.
    /// </summary>
    private static void EnterActive(UserPageTable table)
    {
        while (true)
        {
            int value = Volatile.Read(ref table._active);
            if ((value & Changing) == 0
                && Interlocked.CompareExchange(ref table._active, value + 1, value) == value)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sets <see cref="Changing"/> when no CPU but this one holds the table;
    /// from then on none can load it until <see cref="EndChange"/>. Caller
    /// holds <see cref="_lock"/>, interrupts masked.
    /// </summary>
    /// <param name="here">1 when this CPU holds the table, else 0: the count <see cref="EndChange"/> restores.</param>
    /// <returns>False, with nothing set, while another CPU holds the table (no shootdown before N4).</returns>
    private bool TryBeginChange(out int here)
    {
        here = IsLoadedHere() ? 1 : 0;
        while (true)
        {
            // Changing is never set here: only the holder of _lock sets it.
            // A CPU that enters or leaves meanwhile fails the exchange, and
            // the retry sees the new count.
            int value = Volatile.Read(ref _active);
            if (value > here)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _active, value | Changing, value) == value)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Drops this CPU's copy of the changed entry and lets switches to the
    /// table go on. While <see cref="Changing"/> was set no other CPU could
    /// enter or leave, and this one did not switch: the count is
    /// <paramref name="here"/> still. The exchange is a full barrier, so a
    /// CPU that enters afterwards loads CR3 after the entry's write.
    /// </summary>
    private void EndChange(int here, ulong user)
    {
        InvalidateHere(user);
        Interlocked.Exchange(ref _active, here);
    }

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

    /// <summary>Allocates one zeroed frame.</summary>
    /// <returns>Its physical address, or 0 without memory or on a platform without user page tables.</returns>
    public static ulong AllocateFrame()
    {
        if (s_arch == null)
        {
            return 0;
        }

        void* page = PageAllocator.AllocPages(PageType.Unmanaged, 1, zero: true);
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
}
