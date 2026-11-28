// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Cosmos.Kernel.Core.Bridge;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.IO;
using Cosmos.Kernel.Core.Scheduler;
using Cosmos.Kernel.Core.X64.Bridge;

namespace Cosmos.Kernel.Core.X64.Cpu;

// ZehOS patch user-mode-probe, see PATCHES.md

/// <summary>Ring 3 on x64: the CPU's registers for it, and the check of every frame that returns there.</summary>
internal sealed unsafe partial class UserModeX64 : UserModeArch
{
    private const uint EferMsr = 0xC000_0080;
    private const uint StarMsr = 0xC000_0081;
    private const uint LstarMsr = 0xC000_0082;
    private const uint SfmaskMsr = 0xC000_0084;
    private const uint FsBaseMsr = 0xC000_0100;
    private const uint KernelGsBaseMsr = 0xC000_0102;
    private const uint SysenterCsMsr = 0x174;

    private const ulong EferSyscall = 1;
    private const ulong Cr4Umip = 1UL << 11;
    private const ulong Cr4FsGsBase = 1UL << 16;
    private const ulong Cr4Smep = 1UL << 20;

    private const ulong FlagTrap = 1UL << 8;
    private const ulong FlagInterrupt = 1UL << 9;
    private const ulong FlagDirection = 1UL << 10;
    private const ulong FlagNested = 1UL << 14;
    private const ulong FlagAlignment = 1UL << 18;
    private const ulong Fixed = 1UL << 1;

    // CF, PF, AF, ZF, SF, TF, IF, DF, OF, RF, AC, ID and the fixed bit. Not IOPL, NT, VM, VIF, VIP.
    private const ulong AllowedFlags = 0x25_0FD7;

    private const ulong UserRpl = 3;
    private const ulong UserCs = Gdt.UserCodeSelector | UserRpl;
    private const ulong UserSs = Gdt.UserDataSelector | UserRpl;
    private const ulong UserEnd = 0x0000_8000_0000_0000;

    private const ulong LastExceptionVector = 31;
    private const ulong PageFaultVector = 14;

    private static ulong s_syscallAddress;
    private static ulong s_leaveAddress;

    // ZehOS patch user-return-work, see PATCHES.md
    private static ulong s_workAddress;

    public override bool IsAvailable
    {
        get
        {
            // SYSCALL, CPUID 80000001h EDX 11.
            if ((uint)X86Base.CpuId(unchecked((int)0x8000_0000), 0).Eax < 0x8000_0001
                || (X86Base.CpuId(unchecked((int)0x8000_0001), 0).Edx & (1 << 11)) == 0)
            {
                return false;
            }

            // This CPU only: each CPU asks for its own when it is set up, masked and unable to log.
            ulong features = Cr4Features();
            if ((features & Cr4Smep) == 0)
            {
                Serial.WriteString("[UserMode] no SMEP: the kernel could execute a program's pages\n");
            }

            if ((features & Cr4Umip) == 0)
            {
                Serial.WriteString("[UserMode] no UMIP: programs can read the descriptor table addresses\n");
            }

            s_leaveAddress = LeaveAddress();
            s_workAddress = WorkAddress();
            s_syscallAddress = SyscallAddress();
            return true;
        }
    }

    public override void InitializeFrame(UserFrame* frame, ulong rip, ulong rsp)
    {
        frame->Rip = rip;
        frame->Rsp = rsp;
        frame->Cs = UserCs;
        frame->Ss = UserSs;
        frame->Rflags = FlagInterrupt | Fixed;
    }

    public override bool IsValid(UserFrame* frame)
    {
        // The selectors too: with them, the iretq that takes this frame cannot fault.
        return frame->Rip < UserEnd
            && frame->Rsp < UserEnd
            && frame->Cs == UserCs
            && frame->Ss == UserSs
            && (frame->Rflags & ~AllowedFlags) == 0
            && (frame->Rflags & FlagInterrupt) != 0;
    }

    public override void Load(ulong kernelRsp, ulong fsBase)
    {
        ref PerCpuBlock block = ref PerCpuBlock.Current;
        if (block.UserReady == 0)
        {
            SetUpCurrentCpu();
            block.UserReady = 1;
        }

        Gdt.CurrentTss->Rsp0 = kernelRsp;
        block.UserKernelRsp = (nuint)kernelRsp;

        // Always: ring 3 can change the base by loading the selector.
        WriteMsr(FsBaseMsr, fsBase);
    }

    public override void Enter(UserFrame* frame, ulong* kernelRspSlot)
    {
        EnterNative(frame, kernelRspSlot);
    }

    /// <summary>Whether the frame is ring 3's.</summary>
    internal static bool IsUser(ref IRQContext ctx) => (ctx.cs & UserRpl) != 0;

    /// <summary>Hands an exception of ring 3 to the program's handler. NMI, #DF and #MC are not the program's.</summary>
    internal static bool TryHandleFault(ref IRQContext ctx)
    {
        if (ctx.interrupt > LastExceptionVector || Gdt.IstOf((int)ctx.interrupt) != 0)
        {
            return false;
        }

        // Read masked: the slot is the CPU's, and the handler may be moved to another.
        ulong errorCode = X64InterruptController.HasErrorCode(ctx.interrupt) ? IdtNative.GetLastErrorCode() : 0;
        ulong address = ctx.interrupt == PageFaultVector ? ctx.fault_address : 0;
        UserMode.HandleFault((UserFrame*)Unsafe.AsPointer(ref ctx), ctx.interrupt, errorCode, address);
        return true;
    }

    /// <summary>
    /// Checks the frame the stub is about to resume, the interrupted one or the context of a staged
    /// switch, when it is ring 3's. One that must not go there is turned into a return to the code
    /// after <see cref="UserMode.Enter"/>'s way out, in ring 0.
    /// </summary>
    /// <param name="ctx">The interrupted frame.</param>
    /// <param name="fromUser">Whether it was ring 3's on the entry.</param>
    internal static void PrepareReturn(ref IRQContext ctx, bool fromUser)
    {
        if (ctx.interrupt <= LastExceptionVector && Gdt.IstOf((int)ctx.interrupt) != 0)
        {
            // Back to exactly what was interrupted, even with a switch staged: that belongs to
            // the handler this one interrupted, whose exit checks it. The stub's own mark
            // decides its swapgs, so the frame's origin must not change.
            return;
        }

        IRQContext* frame = (IRQContext*)Unsafe.AsPointer(ref ctx);
        nuint staged = ContextSwitchNative.GetContextSwitchSp();
        if (staged != 0)
        {
            // A context no handler of a program can change: its CS is its origin.
            frame = (IRQContext*)(staged + ThreadContext.FpuAreaSize);
            fromUser = IsUser(ref *frame);
        }

        if (!fromUser)
        {
            return;
        }

        ulong leaveRsp = UserMode.Prepare((UserFrame*)frame);
        if (leaveRsp != 0)
        {
            frame->rip = s_leaveAddress;
            frame->cs = Gdt.KernelCodeSelector;
            frame->rflags = Fixed;
            frame->rsp = leaveRsp;
            frame->ss = Gdt.KernelDataSelector;
        }
        else if (UserMode.TakeReturnFrame((UserFrame*)frame))
        {
            // ZehOS patch user-return-work, see PATCHES.md
            // Work is due: to the ramp in ring 0, masked, on the stack right below the frame's
            // FPU image, the lowest thing of the thread's kernel stack that still lives. CS is the
            // kernel's, so the stub's exit does no swapgs; the ramp's own exit does.
            frame->rip = s_workAddress;
            frame->cs = Gdt.KernelCodeSelector;
            frame->rflags = Fixed;
            frame->rsp = (ulong)frame - ThreadContext.FpuAreaSize;
            frame->ss = Gdt.KernelDataSelector;
        }
    }

    private static void SetUpCurrentCpu()
    {
        // FSGSBASE off: ring 3 could otherwise set a GS base that looks like the kernel's to the
        // NMI entry. Nothing sets a program's GS base: it is 0.
        // SMEP and UMIP by this CPU's CPUID: a bit it lacks would fault.
        ChangeCr4(Cr4Features(), Cr4FsGsBase);
        ClearFs();
        WriteMsr(KernelGsBaseMsr, 0);

        WriteMsr(SysenterCsMsr, 0);
        WriteMsr(StarMsr, ((ulong)Gdt.UserBaseSelector << 48) | ((ulong)Gdt.KernelCodeSelector << 32));
        WriteMsr(LstarMsr, s_syscallAddress);
        WriteMsr(SfmaskMsr, FlagTrap | FlagInterrupt | FlagDirection | FlagNested | FlagAlignment);
        WriteMsr(EferMsr, X64CpuNative.ReadMsr(EferMsr) | EferSyscall);
    }

    [LibraryImport("*", EntryPoint = "_native_x64_user_enter")]
    private static partial void EnterNative(UserFrame* frame, ulong* kernelRspSlot);

    [LibraryImport("*", EntryPoint = "_native_x64_user_syscall_address")]
    [SuppressGCTransition]
    private static partial ulong SyscallAddress();

    [LibraryImport("*", EntryPoint = "_native_x64_user_leave_address")]
    [SuppressGCTransition]
    private static partial ulong LeaveAddress();

    // ZehOS patch user-return-work, see PATCHES.md
    [LibraryImport("*", EntryPoint = "_native_x64_user_work_address")]
    [SuppressGCTransition]
    private static partial ulong WorkAddress();

    [LibraryImport("*", EntryPoint = "_native_x64_user_wrmsr")]
    [SuppressGCTransition]
    private static partial void WriteMsr(uint index, ulong value);

    /// <summary>The bits of <see cref="Cr4Smep"/> and <see cref="Cr4Umip"/> the calling CPU has.</summary>
    [LibraryImport("*", EntryPoint = "_native_x64_user_cr4_features")]
    [SuppressGCTransition]
    private static partial ulong Cr4Features();

    [LibraryImport("*", EntryPoint = "_native_x64_user_change_cr4")]
    [SuppressGCTransition]
    private static partial ulong ChangeCr4(ulong set, ulong clear);

    [LibraryImport("*", EntryPoint = "_native_x64_user_clear_fs")]
    [SuppressGCTransition]
    private static partial void ClearFs();
}
