// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Diagnostics.CodeAnalysis;
using System.Threading;
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
}

/// <summary>
/// The page tables of one user address space: an empty lower half of its own,
/// and the kernel's upper half, whose top-level entries every space shares.
/// The table owns its tables and not the pages mapped in it: the caller frees
/// those (<see cref="FreeFrame"/>), before or after <see cref="Dispose"/>.
/// <para>
/// No CPU runs on such a table yet. That is why no member invalidates a TLB
/// entry: switching to one has to add that for <see cref="TryProtect"/> and
/// <see cref="Unmap"/>, on every CPU that may hold the translation.
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
    /// <returns>False, with nothing changed, for a refused argument, a page not mapped, or once disposed.</returns>
    public bool TryProtect(ulong user, bool writable, bool executable)
    {
        if (!IsUserPage(user) || (writable && executable))
        {
            return false;
        }

        using (_lock.AcquireIrqSafe())
        {
            return _root != 0 && s_arch!.TryProtect(_root, user, writable, executable);
        }
    }

    /// <summary>Removes a page; its frame is the caller's afterwards.</summary>
    /// <param name="user">Page-aligned user address.</param>
    /// <returns>The frame's physical address, or 0 when nothing is mapped there.</returns>
    public ulong Unmap(ulong user)
    {
        if (!IsUserPage(user))
        {
            return 0;
        }

        using (_lock.AcquireIrqSafe())
        {
            return _root == 0 ? 0 : s_arch!.Unmap(_root, user);
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
    /// Frees the tables, not the pages mapped in them. The caller makes sure
    /// no CPU runs on the table. Further calls do nothing.
    /// </summary>
    public void Dispose()
    {
        ulong root;
        using (_lock.AcquireIrqSafe())
        {
            root = _root;
            _root = 0;
        }

        // Outside the lock: nothing reaches the tables any more, and a large
        // space has too many to free with interrupts masked.
        if (root != 0)
        {
            s_arch!.Destroy(root);
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
