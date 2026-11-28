// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cosmos.Kernel.Core.Scheduler;

namespace Cosmos.Kernel.Core.CPU;

// ZehOS patch user-mode-probe, see PATCHES.md

/// <summary>
/// The registers of a program, as the <c>syscall</c> entry and the interrupt stub save them: the
/// layout of <c>ThreadContext</c> without the FPU area. Changing it requires changing UserMode.s
/// and Interrupts.s.
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
[StructLayout(LayoutKind.Sequential)]
public struct UserFrame
{
#pragma warning disable CS1591 // the registers
    public ulong R15, R14, R13, R12, R11, R10, R9, R8, Rdi, Rsi, Rbp, Rbx, Rdx, Rcx, Rax;
    public ulong Interrupt, CpuFlags, Cr2, Temp;
    public ulong Rip, Cs, Rflags, Rsp, Ss;
#pragma warning restore CS1591
}

/// <summary>
/// The architecture's half of <see cref="UserMode"/>. An abstract class rather than an interface:
/// its members run masked, and the first call through an interface dispatch cell may allocate.
/// </summary>
internal abstract unsafe class UserModeArch
{
    /// <summary>Whether this CPU has what ring 3 needs.</summary>
    public abstract bool IsAvailable { get; }

    /// <summary>A frame that starts a program.</summary>
    public abstract void InitializeFrame(UserFrame* frame, ulong rip, ulong rsp);

    /// <summary>Whether a return to ring 3 with this frame cannot fault in the kernel nor gain rights.</summary>
    public abstract bool IsValid(UserFrame* frame);

    /// <summary>Makes this CPU ready for a thread's ring 3. Masked; must not allocate.</summary>
    public abstract void Load(ulong kernelRsp, ulong fsBase);

    /// <summary>Goes to ring 3 and returns once the thread has left it.</summary>
    public abstract void Enter(UserFrame* frame, ulong* kernelRspSlot);
}

/// <summary>
/// Runs programs in ring 3 on kernel threads (experimental, x64 only).
/// <para>
/// The handlers run on the thread's kernel stack with interrupts enabled, and like any unmanaged
/// callback must not let an exception out. A thread leaves ring 3 on the way back to it, never
/// inside a handler, so a handler always runs to its end.
/// </para>
/// </summary>
[Experimental(Experimentals.SchedulerSeamDiagId)]
public static unsafe class UserMode
{
    /// <summary><see cref="Enter"/> did not run the program: not initialized, not a thread that can, or already inside.</summary>
    public const ulong EnterRefused = ulong.MaxValue;

    /// <summary><see cref="Enter"/> ended because the program's registers do not allow a return to ring 3.</summary>
    public const ulong InvalidReturn = ulong.MaxValue - 1;

    /// <summary><see cref="Enter"/> ended because <see cref="FaultHandler"/> refused a fault, or there is none.</summary>
    public const ulong Faulted = ulong.MaxValue - 2;

    private const ulong NoSuchCall = unchecked((ulong)-38);
    private const ulong UserEnd = 0x0000_8000_0000_0000;

    private static UserModeArch? s_arch;
    private static bool s_initialized;

    /// <summary>The system call handler: the result goes into the frame's <c>Rax</c>. Set it before the first <see cref="Enter"/>.</summary>
    public static delegate* unmanaged<UserFrame*, void> SyscallHandler;

    /// <summary>
    /// The fault handler: vector, error code and fault address (CR2 of a page fault, else 0).
    /// 1 runs the instruction again, 0 ends <see cref="Enter"/>.
    /// </summary>
    public static delegate* unmanaged<UserFrame*, ulong, ulong, ulong, int> FaultHandler;

    // ZehOS patch user-return-work, see PATCHES.md
    /// <summary>
    /// Runs on the way back to ring 3 after <see cref="RequestWork"/>, before the program goes on,
    /// with interrupts enabled on the thread's kernel stack like <see cref="SyscallHandler"/>. The
    /// frame holds every register of the program and may be changed. Set it before the first
    /// <see cref="RequestWork"/>; without it a request waits.
    /// </summary>
    public static delegate* unmanaged<UserFrame*, void> ReturnHandler;

    /// <summary>Whether ring 3 can be entered: after <see cref="Initialize"/>, never on ARM64.</summary>
    public static bool IsSupported => Volatile.Read(ref s_initialized);

    /// <summary>
    /// Once, before the first <see cref="Enter"/>. Each CPU sets its own registers up on its first
    /// return to ring 3, masked, so a CPU that comes online later needs nothing.
    /// </summary>
    /// <returns>False where ring 3 is not supported.</returns>
    public static bool Initialize()
    {
        if (s_arch is null || !s_arch.IsAvailable)
        {
            return false;
        }

        Volatile.Write(ref s_initialized, true);
        return true;
    }

    /// <summary>Runs a program on the calling thread until it leaves.</summary>
    /// <param name="rip">Where it starts, in the lower canonical half.</param>
    /// <param name="rsp">Its stack pointer, in the lower canonical half.</param>
    /// <returns>The value of the request it left by, or one of the failures above.</returns>
    public static ulong Enter(ulong rip, ulong rsp)
    {
        SchedulerThread? thread = SchedulerManager.CurrentThreadIfRunning;
        if (!IsSupported || thread is null || thread.IsIdle || thread._userKernelRsp != 0 || !InternalCpu.InterruptsEnabled)
        {
            return EnterRefused;
        }

        UserFrame frame = default;
        s_arch!.InitializeFrame(&frame, rip, rsp);
        thread._userFailure = 0;

        // The collector does not move objects, and the thread is this one.
        s_arch.Enter(&frame, (ulong*)Unsafe.AsPointer(ref thread._userKernelRsp));
        thread._userKernelRsp = 0;

        // A request wins over a failure and is used up; one that is only being written stays
        // for the next Enter.
        if (Volatile.Read(ref thread._userLeaveState) == 2)
        {
            ulong value = thread._userLeaveValue;
            Volatile.Write(ref thread._userLeaveState, 0);
            return value;
        }

        return thread._userFailure;
    }

    /// <summary>Only in a handler: <see cref="Enter"/> returns <paramref name="exitValue"/> once the handler has.</summary>
    public static void LeaveCurrent(ulong exitValue)
    {
        SchedulerThread? thread = SchedulerManager.CurrentThreadIfRunning;
        if (thread is not null && thread._userKernelRsp != 0)
        {
            RequestLeave(thread, exitValue);
        }
    }

    /// <summary>
    /// From any thread: <see cref="Enter"/> of <paramref name="thread"/> returns
    /// <paramref name="exitValue"/>, at the latest with its next interrupt in ring 3. The first
    /// request counts until it is used up; one to a thread outside of <see cref="Enter"/> ends
    /// its next one. A thread in ring 3 on another CPU gets that interrupt right away
    /// (<see cref="Kick"/>), not only with its CPU's next tick.
    /// </summary>
    public static void RequestLeave(SchedulerThread thread, ulong exitValue)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (Interlocked.CompareExchange(ref thread._userLeaveState, 1, 0) == 0)
        {
            thread._userLeaveValue = exitValue;

            // ZehOS patch user-leave-kick, see PATCHES.md
            // Interlocked, not volatile: a full barrier, so the state is visible before Kick
            // reads where the thread runs. A thread that moves meanwhile reads it in Prepare on
            // its new CPU; without the barrier the store could still sit in this CPU's buffer.
            Interlocked.Exchange(ref thread._userLeaveState, 2);
            Kick(thread);
        }
    }

    // ZehOS patch user-leave-kick, see PATCHES.md
    /// <summary>
    /// From any thread: interrupts the CPU <paramref name="thread"/> runs on, when that is
    /// another CPU and the thread is inside <see cref="Enter"/>, so that its way back to ring 3
    /// passes <see cref="Prepare"/> now. Nothing otherwise: a thread that does not run passes it
    /// on its resume, the calling thread on its own way back. What the caller wrote before is
    /// visible to that pass. Allocation-free, no lock; any context, also masked.
    /// </summary>
    public static void Kick(SchedulerThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        // Pairs with the queue lock the resume takes: a write of the caller before this barrier
        // is seen either by the CPU read below or by the one the thread is resumed on.
        Interlocked.MemoryBarrier();
        if (Volatile.Read(ref thread._userKernelRsp) == 0 || thread.State != SchedulerThreadState.Running)
        {
            return;
        }

        // A snapshot: a thread that moved meanwhile costs one needless reschedule on its old
        // CPU, and its new one resumes it through Prepare.
        uint cpu = thread.CpuId;
        if (cpu == SchedulerManager.GetCurrentCpuId() || cpu >= SchedulerManager.CpuCount
            || Volatile.Read(ref PerCpuBlock.At(cpu).Online) == 0)
        {
            return;
        }

#if COSMOS_TEST_HOOKS
        if (Volatile.Read(ref TestSkipKick))
        {
            return;
        }
#endif
        using (InternalCpu.DisableInterruptsScope())
        {
            InterruptManager.SendReschedule(PerCpuBlock.At(cpu).HardwareId);
        }
    }

    // ZehOS patch user-return-work, see PATCHES.md
    /// <summary>
    /// From any thread: <see cref="ReturnHandler"/> runs once for <paramref name="thread"/>
    /// before its next return to ring 3, a return from an interrupt included, so also for a
    /// program that computes without calls. Requests before that run are one. Only a request
    /// that sets the mark interrupts the thread's CPU (<see cref="Kick"/>); a thread outside of
    /// <see cref="Enter"/> keeps it until its first interrupt or call there. Allocation-free, no
    /// lock; any context, also masked.
    /// </summary>
    public static void RequestWork(SchedulerThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        // Interlocked: a full barrier, so what the caller wrote before is visible to the handler
        // that clears the mark. A mark still set is not yet taken, its run reads that too.
        if (Interlocked.Exchange(ref thread._userWork, 1) == 0)
        {
#if COSMOS_TEST_HOOKS
            Interlocked.Increment(ref TestWorkKicks);
#endif
            Kick(thread);
        }
    }

#if COSMOS_TEST_HOOKS
    // ZehOS patch user-return-work, see PATCHES.md
    // Only in the test build: how often RequestWork set the mark, and a switch that makes the
    // work ramp lose the program's FPU state and RBX, as a ramp without the saved image would.
    // The suite's counter-check shows with it that its cell sees such a loss.
    internal static int TestWorkKicks;
    internal static bool TestWorkLoseState;
#endif

#if COSMOS_TEST_HOOKS
    // ZehOS patch user-leave-kick, see PATCHES.md
    // Only in the test build (COSMOS_TEST_DEFINES=COSMOS_TEST_HOOKS): Kick sends nothing, so a
    // request waits for the target CPU's next tick as before. The suite's counter-check uses it
    // to show that its cell sees the interrupt.
    internal static bool TestSkipKick;
#endif

    /// <summary>
    /// Sets the calling thread's FS base in ring 3, from the next return there on. A program that
    /// loads the FS selector itself has the base back with that return too.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The address is not in the lower canonical half.</exception>
    public static void SetFsBase(ulong address)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(address, UserEnd);
        SchedulerManager.CurrentThreadIfRunning?._userFsBase = address;
    }

    internal static void RegisterArch(UserModeArch arch)
    {
        s_arch = arch;
    }

    /// <summary>
    /// The last step before every return to ring 3: masked, on the CPU that returns, for its
    /// current thread. Allocation-free.
    /// </summary>
    /// <returns>0 to return to ring 3, otherwise the kernel stack pointer to leave it with.</returns>
    internal static ulong Prepare(UserFrame* frame)
    {
        SchedulerThread? thread = SchedulerManager.CurrentCpuState?.CurrentThread;
        if (thread is null || thread._userKernelRsp == 0)
        {
            Panic.Halt("A return to ring 3 on a thread that did not enter it.");
            return 0;
        }

        if (Volatile.Read(ref thread._userLeaveState) == 2 || thread._userFailure != 0)
        {
            return thread._userKernelRsp;
        }

        if (!s_arch!.IsValid(frame))
        {
            thread._userFailure = InvalidReturn;
            return thread._userKernelRsp;
        }

        s_arch.Load(thread._userKernelRsp, thread._userFsBase);
        return 0;
    }

    // ZehOS patch user-return-work, see PATCHES.md
    /// <summary>
    /// For a return from an interrupt that <see cref="Prepare"/> let go to ring 3: when work is
    /// due, keeps the frame's ring 3 words at the thread, so that the architecture can turn the
    /// frame to its work ramp. Masked, allocation-free.
    /// </summary>
    internal static bool TakeReturnFrame(UserFrame* frame)
    {
        SchedulerThread? thread = SchedulerManager.CurrentCpuState?.CurrentThread;
        if (thread is null || ReturnHandler == null || Volatile.Read(ref thread._userWork) == 0)
        {
            return false;
        }

        // One return to ring 3 at a time per thread, and the ramp takes the words back masked
        // before anything can interrupt it: the fields are never overwritten while in use.
        thread._userWorkRip = frame->Rip;
        thread._userWorkCs = frame->Cs;
        thread._userWorkRflags = frame->Rflags;
        thread._userWorkRsp = frame->Rsp;
        thread._userWorkSs = frame->Ss;
        return true;
    }

    /// <summary>
    /// The handler for every request that is due, then <see cref="Prepare"/>. Called masked on
    /// the thread's kernel stack, below the frame; returns masked.
    /// </summary>
    private static ulong RunWork(UserFrame* frame)
    {
        delegate* unmanaged<UserFrame*, void> handler = ReturnHandler;
        SchedulerThread? thread = SchedulerManager.CurrentCpuState?.CurrentThread;
        if (handler != null && thread is not null)
        {
            // A request during the handler runs it again before the return; one after the last
            // check here comes with its kick, or on this CPU with the next interrupt.
            while (Interlocked.Exchange(ref thread._userWork, 0) != 0)
            {
                InternalCpu.EnableInterrupts();
                handler(frame);
                InternalCpu.DisableInterrupts();
            }
        }

        return Prepare(frame);
    }

    /// <summary>A fault in ring 3: called masked on the thread's kernel stack, returns masked.</summary>
    internal static void HandleFault(UserFrame* frame, ulong vector, ulong errorCode, ulong address)
    {
        int again = 0;
        delegate* unmanaged<UserFrame*, ulong, ulong, ulong, int> handler = FaultHandler;
        if (handler != null)
        {
            InternalCpu.EnableInterrupts();
            again = handler(frame, vector, errorCode, address);
            InternalCpu.DisableInterrupts();
        }

        if (again == 0 && SchedulerManager.CurrentCpuState?.CurrentThread is { } thread)
        {
            thread._userFailure = Faulted;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "__managed__user_prepare")]
    private static ulong PrepareEntry(UserFrame* frame) => Prepare(frame);

    [UnmanagedCallersOnly(EntryPoint = "__managed__user_syscall")]
    private static ulong SyscallEntry(UserFrame* frame)
    {
        delegate* unmanaged<UserFrame*, void> handler = SyscallHandler;
        if (handler == null)
        {
            frame->Rax = NoSuchCall;
        }
        else
        {
            InternalCpu.EnableInterrupts();
            handler(frame);
            InternalCpu.DisableInterrupts();
        }

        // ZehOS patch user-return-work, see PATCHES.md
        // Work requested during the call runs before its return, not only with the next interrupt.
        return RunWork(frame);
    }

    // ZehOS patch user-return-work, see PATCHES.md
    // The work ramp: masked, on the thread's kernel stack right below the frame and its FPU image,
    // the kernel's GS. The frame's ring 3 words come back first; the ramp then returns through
    // the syscall exit's iretq, with every register and the FPU image of the frame.
    [UnmanagedCallersOnly(EntryPoint = "__managed__user_work")]
    private static ulong WorkEntry(UserFrame* frame)
    {
        SchedulerThread? thread = SchedulerManager.CurrentCpuState?.CurrentThread;
        if (thread is null)
        {
            Panic.Halt("The work ramp on a CPU without a thread.");
            return 0;
        }

        frame->Rip = thread._userWorkRip;
        frame->Cs = thread._userWorkCs;
        frame->Rflags = thread._userWorkRflags;
        frame->Rsp = thread._userWorkRsp;
        frame->Ss = thread._userWorkSs;
        ulong result = RunWork(frame);
#if COSMOS_TEST_HOOKS
        if (Volatile.Read(ref TestWorkLoseState))
        {
            // The XMM registers of the fxsave64 image below the frame (bytes 160 to 415), and RBX.
            new Span<byte>((byte*)frame - 512 + 160, 256).Clear();
            frame->Rbx = 0;
        }
#endif
        return result;
    }
}
