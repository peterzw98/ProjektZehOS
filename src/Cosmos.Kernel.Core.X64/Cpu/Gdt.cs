// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Runtime.InteropServices;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.IO;
using Cosmos.Kernel.Core.Memory;
using Cosmos.Kernel.Core.X64.Bridge;

namespace Cosmos.Kernel.Core.X64.Cpu;

// ZehOS patch user-gdt, see PATCHES.md
/// <summary>
/// One GDT and one TSS per CPU, with a stack of their own for #DF, NMI and #MC (IST 1 to 3) and
/// one for RSP0. The kernel keeps the bootloader's selectors (0x28 code, 0x30 data), which the
/// IDT and every saved context carry. The boot CPU allocates every CPU's share in
/// <see cref="Prepare"/>, so an application processor loads its own without allocating. Until it
/// does, it runs on the bootloader's GDT.
/// </summary>
/// <remarks>
/// A handler on an IST stack must not leave its frame behind as a thread's saved context: the
/// collector scans from a thread's stack pointer to the end of that thread's own stack. No stop
/// does so through these vectors: <see cref="CpuStop"/> uses a maskable vector without IST, and
/// the panic stop's NMI (<see cref="PanicStop"/>) halts its CPU without saving anything. Any
/// other exception on them halts the system, as long as the CPU exception hook does not give up
/// a thread for vectors 2, 8 and 18.
/// </remarks>
internal static unsafe class Gdt
{
    /// <summary>Selector of kernel code, the bootloader's value.</summary>
    internal const ushort KernelCodeSelector = 0x28;

    /// <summary>Selector of kernel data, the bootloader's value.</summary>
    internal const ushort KernelDataSelector = 0x30;

    /// <summary>Base of the SYSRET selectors (STAR[63:48]): a null descriptor, user data and code follow it.</summary>
    internal const ushort UserBaseSelector = 0x38;

    /// <summary>Selector of user data, without RPL.</summary>
    internal const ushort UserDataSelector = 0x40;

    /// <summary>Selector of user code (64-bit), without RPL.</summary>
    internal const ushort UserCodeSelector = 0x48;

    /// <summary>Selector of the TSS, whose descriptor takes two entries.</summary>
    internal const ushort TssSelector = 0x50;

    private const int EntryCount = 12;
    private const int TableSize = EntryCount * 8;

    // Present, 64-bit, flat; the accessed bit is set so the CPU never writes the entry.
    private const ulong KernelCode = 0x00AF_9B00_0000_FFFF;
    private const ulong KernelData = 0x00CF_9300_0000_FFFF;
    private const ulong UserData = 0x00CF_F300_0000_FFFF;
    private const ulong UserCode = 0x00AF_FB00_0000_FFFF;

    // Present, DPL 0, available 64-bit TSS.
    private const ulong TssAccess = 0x89;

    // The busy bit ltr sets in the descriptor's type.
    private const ulong TssBusy = 1UL << 41;

    // The managed exception path (hook, report, stack walk) runs on these.
    private const ulong StackPages = 4;
    private const ulong StackSize = StackPages * PageAllocator.PageSize;
    private const int IstCount = 3;

    // One page for GDT and TSS, then the IST stacks and the RSP0 stack. There are no
    // guard pages: an overflow of one of these stacks is not caught.
    private const ulong PagesPerCpu = 1 + (IstCount + 1) * StackPages;
    private const ulong BytesPerCpu = PagesPerCpu * PageAllocator.PageSize;

    // The TSS's place in the first page, behind the GDT; 16-byte aligned, within one page.
    private const int TssOffset = 128;

    // Unmanaged pages, written by the boot CPU before another CPU starts; each CPU then
    // touches only its own part.
    private static byte* s_base;
    private static uint s_cpuCount;

    /// <summary>The IST number of a vector's gate, 0 for the interrupted stack.</summary>
    internal static ushort IstOf(int vector)
    {
        return vector switch
        {
            8 => 1,  // #DF
            2 => 2,  // NMI
            18 => 3, // #MC
            _ => 0
        };
    }

    /// <summary>Allocates and fills GDT, TSS and stacks for every CPU; the boot CPU, before any other starts.</summary>
    internal static void Prepare()
    {
        if (s_base != null)
        {
            return;
        }

        uint cpuCount = Smp.CpuCount;
        byte* memory = (byte*)PageAllocator.AllocPages(PageType.Unmanaged, PagesPerCpu * cpuCount, zero: true);
        if (memory == null)
        {
            Panic.Halt("No memory for the GDT, TSS and interrupt stacks of the CPUs.");
        }

        for (uint cpu = 0; cpu < cpuCount; cpu++)
        {
            Fill(memory + cpu * BytesPerCpu);
        }

        s_cpuCount = cpuCount;
        s_base = memory;
    }

    /// <summary>The calling CPU's TSS; null before <see cref="Prepare"/>.</summary>
    internal static TaskStateSegment* CurrentTss =>
        s_base == null ? null : (TaskStateSegment*)(s_base + PerCpuBlock.CurrentIndex * BytesPerCpu + TssOffset);

    /// <summary>
    /// Loads the calling CPU's GDT and TSS, with interrupts masked and before the IDT whose gates
    /// name its stacks. Allocation-free.
    /// </summary>
    internal static void LoadOnCurrentCpu()
    {
        uint cpu = PerCpuBlock.CurrentIndex;
        if (s_base == null || cpu >= s_cpuCount)
        {
            Panic.Halt("The GDT of this CPU was not built on the boot CPU before it loaded it.");
        }

        GdtPointer pointer = new()
        {
            Limit = TableSize - 1,
            Base = (ulong)(s_base + cpu * BytesPerCpu)
        };

        // ltr marks the descriptor busy and faults on a busy one; a second load starts clean.
        ((ulong*)pointer.Base)[TssSelector / 8] &= ~TssBusy;

        GdtNative.LoadGdt(&pointer, TssSelector);
        Report(cpu);
    }

    private static void Fill(byte* block)
    {
        TaskStateSegment* tss = (TaskStateSegment*)(block + TssOffset);
        ulong stacks = (ulong)block + PageAllocator.PageSize;
        tss->Ist1 = stacks + StackSize;
        tss->Ist2 = stacks + 2 * StackSize;
        tss->Ist3 = stacks + 3 * StackSize;

        // Not a thread's and not the boot stack, whose frames are live: until Ring 3 sets it
        // per thread, nothing uses it.
        tss->Rsp0 = stacks + 4 * StackSize;

        // Behind the limit: no I/O permission bitmap, every port is denied to Ring 3.
        tss->IoMapBase = (ushort)sizeof(TaskStateSegment);

        ulong tssBase = (ulong)tss;
        ulong limit = (ulong)sizeof(TaskStateSegment) - 1;
        ulong* table = (ulong*)block;
        table[KernelCodeSelector / 8] = KernelCode;
        table[KernelDataSelector / 8] = KernelData;
        table[UserDataSelector / 8] = UserData;
        table[UserCodeSelector / 8] = UserCode;
        table[TssSelector / 8] = limit
            | ((tssBase & 0xFF_FFFF) << 16)
            | (TssAccess << 40)
            | (((tssBase >> 24) & 0xFF) << 56);
        table[TssSelector / 8 + 1] = tssBase >> 32;
    }

    // Read back from the CPU, not from what was meant to be loaded.
    private static void Report(uint cpu)
    {
        ulong selectors = GdtNative.GetSelectors();
        GdtPointer loaded = default;
        GdtNative.StoreGdt(&loaded);

        ulong* entry = (ulong*)(loaded.Base + ((selectors >> 32) & 0xFFF8));
        ulong tssBase = ((entry[0] >> 16) & 0xFF_FFFF) | ((entry[0] >> 56) << 24) | (entry[1] << 32);
        TaskStateSegment* tss = (TaskStateSegment*)tssBase;

        Serial.WriteString("[GDT] CPU ");
        Serial.WriteNumber(cpu);
        WriteField(" CS=0x", selectors & 0xFFFF);
        WriteField(" SS=0x", (selectors >> 16) & 0xFFFF);
        WriteField(" TR=0x", (selectors >> 32) & 0xFFFF);
        WriteField(" GDT=0x", loaded.Base);
        WriteField(" TSS=0x", tssBase);
        WriteField(" RSP0=0x", tss->Rsp0);
        WriteField(" IST1=0x", tss->Ist1);
        WriteField(" IST2=0x", tss->Ist2);
        WriteField(" IST3=0x", tss->Ist3);
        WriteField(" IOPB=0x", tss->IoMapBase);
        Serial.WriteString("\n");
    }

    private static void WriteField(string name, ulong value)
    {
        Serial.WriteString(name);
        Serial.WriteHex(value);
    }
}

/// <summary>The 64-bit TSS (SDM 3A, 8.7), 104 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 104)]
internal struct TaskStateSegment
{
    public uint Reserved0;
    public ulong Rsp0;
    public ulong Rsp1;
    public ulong Rsp2;
    public ulong Reserved1;
    public ulong Ist1;
    public ulong Ist2;
    public ulong Ist3;
    public ulong Ist4;
    public ulong Ist5;
    public ulong Ist6;
    public ulong Ist7;
    public ulong Reserved2;
    public ushort Reserved3;
    public ushort IoMapBase;
}

/// <summary>The GDTR operand.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 10)]
internal struct GdtPointer
{
    public ushort Limit;
    public ulong Base;
}
