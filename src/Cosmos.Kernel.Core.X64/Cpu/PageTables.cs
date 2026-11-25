// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Threading;
using Cosmos.Kernel.Core.Memory;

namespace Cosmos.Kernel.Core.X64.Cpu;

// ZehOS patch user-page-tables, see PATCHES.md

/// <summary>
/// User page tables in the 4-level format, with 4 KiB pages only. Every table
/// is reached through the HHDM. The user half is entries 0 to 255 of the PML4;
/// entries 256 to 511 are copies of the kernel's, complete and constant since
/// <see cref="DeviceMapper.ShareKernelHalf"/>, so everything below them is the
/// kernel's own and shared.
/// </summary>
internal sealed unsafe class PageTables : UserPageTableArch
{
    private const ulong FlagPresent = 1UL << 0;
    private const ulong FlagWritable = 1UL << 1;
    private const ulong FlagUser = 1UL << 2;
    private const ulong FlagNoExecute = 1UL << 63;

    /// <summary>Flags of an entry that points to a table of the user half; the page's own entry restricts.</summary>
    private const ulong TableFlags = FlagPresent | FlagWritable | FlagUser;

    /// <summary>Physical-address field of a table entry (bits 51:12).</summary>
    private const ulong AddrMask = 0x000F_FFFF_FFFF_F000;
    /// <summary>Mask of an address's offset inside its page.</summary>
    private const ulong PageOffsetMask = 0xFFF;
    /// <summary>Mask isolating a 9-bit page-table index.</summary>
    private const ulong TableIndexMask = 0x1FF;
    /// <summary>Entries in one page table.</summary>
    private const int TableEntries = 512;
    /// <summary>The first PML4 entry of the kernel half.</summary>
    internal const int KernelHalfStart = 256;
    /// <summary>Right shift extracting the PML4 index (bits 47:39).</summary>
    private const int Pml4Shift = 39;
    /// <summary>Right shift extracting the PT index (bits 20:12).</summary>
    private const int PtShift = 12;
    /// <summary>Address bits one level resolves.</summary>
    private const int LevelShift = 9;
    /// <summary>Levels of tables below the PML4: PDPT, PD and PT.</summary>
    private const int LevelsBelowRoot = 3;

    /// <summary>The <c>IA32_EFER</c> MSR.</summary>
    private const uint EferMsr = 0xC000_0080;
    /// <summary><c>EFER.NXE</c>: without it bit 63 of an entry is reserved.</summary>
    private const ulong EferNoExecute = 1UL << 11;

    /// <summary>The kernel's PML4. Written before the kernel half counts as shared.</summary>
    private static ulong* s_kernelRoot;

    public override bool ShareKernelHalf()
    {
        if ((Bridge.X64CpuNative.ReadMsr(EferMsr) & EferNoExecute) == 0)
        {
            return false;
        }

        ulong* root = DeviceMapper.ShareKernelHalf();
        if (root == null)
        {
            return false;
        }

        s_kernelRoot = root;
        return true;
    }

    public override ulong CreateRoot()
    {
        ulong* root = AllocateTable();
        if (root == null)
        {
            return 0;
        }

        // Without the user bit, whatever the bootloader set: one level that
        // lacks it closes the whole kernel half to ring 3.
        for (int i = KernelHalfStart; i < TableEntries; i++)
        {
            root[i] = s_kernelRoot[i] & ~FlagUser;
        }

        return Physical(root);
    }

    public override bool TryMap(ulong root, ulong user, ulong physical, bool writable, bool executable)
    {
        ulong* entry = FindEntry(root, user, create: true);
        if (entry == null || (*entry & FlagPresent) != 0)
        {
            return false;
        }

        Volatile.Write(ref *entry, physical | Access(writable, executable));
        return true;
    }

    public override bool TryProtect(ulong root, ulong user, bool writable, bool executable)
    {
        ulong* entry = FindEntry(root, user, create: false);
        if (entry == null || (*entry & FlagPresent) == 0)
        {
            return false;
        }

        Volatile.Write(ref *entry, (*entry & AddrMask) | Access(writable, executable));
        return true;
    }

    public override ulong Unmap(ulong root, ulong user)
    {
        ulong* entry = FindEntry(root, user, create: false);
        if (entry == null || (*entry & FlagPresent) == 0)
        {
            return 0;
        }

        ulong physical = *entry & AddrMask;
        Volatile.Write(ref *entry, 0UL);
        return physical;
    }

    public override bool TryTranslate(ulong root, ulong user, bool write, out ulong physical)
    {
        physical = 0;
        ulong* entry = FindEntry(root, user, create: false);
        if (entry == null || (*entry & FlagPresent) == 0 || (write && (*entry & FlagWritable) == 0))
        {
            return false;
        }

        physical = (*entry & AddrMask) | (user & PageOffsetMask);
        return true;
    }

    public override void Destroy(ulong root)
    {
        ulong* pml4 = Table(root);
        for (int i = 0; i < KernelHalfStart; i++)
        {
            if ((pml4[i] & FlagPresent) != 0)
            {
                FreeTables(Table(pml4[i] & AddrMask), LevelsBelowRoot);
            }
        }

        PageAllocator.Free(pml4);
    }

    // ZehOS patch user-cr3, see PATCHES.md
    public override bool TryGetAccess(ulong root, ulong user, out bool writable, out bool executable)
    {
        ulong* entry = FindEntry(root, user, create: false);
        ulong value = entry == null ? 0 : *entry;
        writable = (value & FlagWritable) != 0;
        executable = (value & FlagNoExecute) == 0;
        return (value & FlagPresent) != 0;
    }

    public override ulong ReadRoot() => Bridge.X64CpuNative.ReadCr3();

    /// <summary>
    /// A CR3 write drops every translation but the global ones; the kernel
    /// half is the same in every root, so its global entries stay right.
    /// </summary>
    public override void LoadRoot(ulong root) => Bridge.X64CpuNative.WriteCr3(root);

    public override void InvalidatePage(ulong user) => Bridge.X64CpuNative.InvalidatePage(user);

    /// <summary>
    /// Frees a table and the tables below it. Recurses at most twice: the
    /// last level's entries are pages, which belong to the caller.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="levels">Levels of tables from this one on: 1 for a PT.</param>
    private static void FreeTables(ulong* table, int levels)
    {
        if (levels > 1)
        {
            for (int i = 0; i < TableEntries; i++)
            {
                if ((table[i] & FlagPresent) != 0)
                {
                    FreeTables(Table(table[i] & AddrMask), levels - 1);
                }
            }
        }

        PageAllocator.Free(table);
    }

    /// <summary>
    /// The page-table entry for <paramref name="user"/>, or null when a table
    /// on the way is missing and not to be created, or cannot be. A table
    /// created on a way that then fails stays, empty, until the root goes.
    /// </summary>
    private static ulong* FindEntry(ulong root, ulong user, bool create)
    {
        ulong* table = Table(root);
        for (int shift = Pml4Shift; shift > PtShift; shift -= LevelShift)
        {
            ulong* slot = table + ((user >> shift) & TableIndexMask);
            if ((*slot & FlagPresent) != 0)
            {
                table = Table(*slot & AddrMask);
                continue;
            }

            if (!create)
            {
                return null;
            }

            table = AllocateTable();
            if (table == null)
            {
                return null;
            }

            // Zeroed before it is linked, for a CPU that walks these tables.
            Volatile.Write(ref *slot, Physical(table) | TableFlags);
        }

        return table + ((user >> PtShift) & TableIndexMask);
    }

    private static ulong Access(bool writable, bool executable)
    {
        return FlagPresent | FlagUser | (writable ? FlagWritable : 0) | (executable ? 0 : FlagNoExecute);
    }

    private static ulong* AllocateTable()
    {
        return (ulong*)PageAllocator.AllocPages(PageType.PageDirectory, 1, zero: true);
    }

    private static ulong Physical(ulong* table)
    {
        return (ulong)table - AddressSpace.HhdmOffset;
    }

    private static ulong* Table(ulong physical)
    {
        return (ulong*)(physical + AddressSpace.HhdmOffset);
    }
}
