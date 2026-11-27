using System;
using System.Threading;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Processes;

/// <summary>
/// V18: Register eines Threads im Ring 3 an der Grenze eines Aufrufs, plattformunabhängig (die Brücke kopiert sie aus und in den
/// <c>UserFrame</c> von Cosmos). Eigenschaften statt Felder: Im Kernel setzt sie nur die Brücke, ein Feld wäre dort CS0649.
/// </summary>
internal struct LinuxUserRegs
{
    public ulong R8 { get; set; }
    public ulong R9 { get; set; }
    public ulong R10 { get; set; }
    public ulong R11 { get; set; }
    public ulong R12 { get; set; }
    public ulong R13 { get; set; }
    public ulong R14 { get; set; }
    public ulong R15 { get; set; }
    public ulong Rdi { get; set; }
    public ulong Rsi { get; set; }
    public ulong Rbp { get; set; }
    public ulong Rbx { get; set; }
    public ulong Rdx { get; set; }
    public ulong Rax { get; set; }
    public ulong Rcx { get; set; }
    public ulong Rsp { get; set; }
    public ulong Rip { get; set; }
    public ulong Rflags { get; set; }

    /// <summary>Argumente eines syscall wie Linux x86-64 (rax; rdi, rsi, rdx, r10, r8, r9); Brücke und Tests nehmen dieselbe Zuordnung.</summary>
    public readonly SyscallArgs ToSyscallArgs() => new()
    {
        Number = unchecked((long)Rax), A0 = Rdi, A1 = Rsi, A2 = Rdx, A3 = R10, A4 = R8, A5 = R9,
    };
}

/// <summary>Ein Signal, das ein Thread jetzt anspringt: Handler und Flags im Augenblick der Zustellung, Maske davor, siginfo.</summary>
internal readonly record struct LinuxSignalDelivery(int Signal, ulong Handler, ulong Flags, ulong Restorer, ulong OldBlocked, int Code, int Pid, int Status)
{
    /// <summary>V24: gesetzt bei einem Signal aus einer Ausnahme im Ring 3; dann trägt siginfo si_addr statt si_pid, sigcontext err, trapno, cr2.</summary>
    public LinuxFault? Fault { get; init; }
}

/// <summary>
/// V24: Signal, si_code und si_addr einer Ausnahme im Ring 3 wie Linux x86-64 (<c>arch/x86/kernel/traps.c</c>, <c>arch/x86/mm/fault.c</c>),
/// dazu die Felder, die Linux in den sigcontext schreibt (trapno, err, cr2).
/// </summary>
internal readonly record struct LinuxFault(int Signal, int Code, ulong Address, ulong Vector, ulong Error, ulong Cr2)
{
    public const int SiKernel = 0x80, SegvMaperr = 1, SegvAccerr = 2, IllIllopn = 2, FpeIntdiv = 1, BusAdraln = 1, BusAdrerr = 2, TrapTrace = 2;

    /// <summary>V30-P3: Signal einer Dateiseite, die sich im Seitenfehler nicht lesen lässt.</summary>
    public const int SigBus = 7;
    private const int FpeFltdiv = 3, FpeFltovf = 4, FpeFltund = 5, FpeFltres = 6, FpeFltinv = 7;

    /// <summary>Signal der Ausnahme <paramref name="vector"/> (auch für den Wartestatus, wenn kein Handler sie nimmt).</summary>
    public static int SignalOf(ulong vector) => vector switch
    {
        0 or 9 or 16 or 19 => 8, // #DE, Koprozessor-Segmentüberlauf, #MF, #XM: SIGFPE
        6 => 4,                  // #UD: SIGILL
        1 or 3 => 5,             // #DB, #BP: SIGTRAP
        11 or 12 or 17 => 7,     // #NP, #SS, #AC: SIGBUS
        _ => 11,                 // #GP, #PF, #TS und der Rest: SIGSEGV
    };

    /// <summary>
    /// Beschreibt die Ausnahme. <paramref name="mapped"/>: Liegt die Fehleradresse eines Seitenfehlers in einem Bereich (auch PROT_NONE)?
    /// Dann SEGV_ACCERR, sonst SEGV_MAPERR. <paramref name="fpu"/> ist das fxsave-Abbild beim Fehler (für den si_code von #MF und #XM).
    /// <paramref name="busError"/> (V30-P3): Der Seitenfehler traf eine Dateiseite, die sich nicht lesen ließ (Lesefehler, ganz hinter dem
    /// Dateiende); Linux meldet dann SIGBUS mit BUS_ADRERR und der Fehleradresse (<c>mm_fault_error</c> bei VM_FAULT_SIGBUS).
    /// </summary>
    public static LinuxFault Of(ulong vector, ulong error, ulong address, ulong rip, bool mapped, ReadOnlySpan<byte> fpu, bool busError = false)
    {
        if (vector == 14 && busError)
        {
            return new LinuxFault(SigBus, BusAdrerr, address, vector, error, address);
        }

        int signal = SignalOf(vector);
        return vector switch
        {
            14 => new LinuxFault(signal, mapped ? SegvAccerr : SegvMaperr, address, vector, error, address),
            0 => new LinuxFault(signal, FpeIntdiv, rip, vector, error, 0),
            6 => new LinuxFault(signal, IllIllopn, rip, vector, error, 0),
            1 => new LinuxFault(signal, TrapTrace, rip, vector, error, 0),
            17 => new LinuxFault(signal, BusAdraln, 0, vector, error, 0),
            16 => new LinuxFault(signal, MathCode(Fx16(fpu, 2) & ~Fx16(fpu, 0) & 0x7F), rip, vector, error, 0), // FSW ohne maskierte Bits der FCW
            19 => new LinuxFault(signal, MathCode(Fx32(fpu, 24) & ~(Fx32(fpu, 24) >> 7) & 0x3F), rip, vector, error, 0), // MXCSR: Flags ohne Masken
            _ => new LinuxFault(signal, SiKernel, 0, vector, error, 0), // #GP, #BP, #SS, ...: force_sig, si_addr 0
        };
    }

    // Linux fpu__exception_code: Reihenfolge der Prüfung wie dort (ungültig vor Division durch null vor Überlauf ...).
    private static int MathCode(uint err)
    {
        if ((err & 0x001) != 0)
        {
            return FpeFltinv;
        }

        if ((err & 0x004) != 0)
        {
            return FpeFltdiv;
        }

        if ((err & 0x008) != 0)
        {
            return FpeFltovf;
        }

        if ((err & 0x012) != 0)
        {
            return FpeFltund;
        }

        return (err & 0x020) != 0 ? FpeFltres : 0;
    }

    private static uint Fx16(ReadOnlySpan<byte> fpu, int offset) => fpu.Length >= offset + 2 ? (uint)BitConverter.ToUInt16(fpu.Slice(offset, 2)) : 0;

    private static uint Fx32(ReadOnlySpan<byte> fpu, int offset) => fpu.Length >= offset + 4 ? BitConverter.ToUInt32(fpu.Slice(offset, 4)) : 0;
}

/// <summary>
/// V18 Signalzustellung (LibCore und Ladybird setzen Handler für SIGCHLD, SIGINT, SIGPIPE). Anhängige Signale liegen je Prozess
/// (<see cref="_pendingSignals"/>, Bit = Nummer - 1) mit einer siginfo je Nummer; die Brücke holt nach jedem Aufruf mit
/// <see cref="TakeSignal"/> das niedrigste freigegebene mit Handler und baut den Rahmen (<see cref="LinuxSignalFrame"/>).
/// <para>
/// Die Maske gilt seit V38 je Thread wie bei Linux (<see cref="LinuxThread.Blocked"/>): Ein Signal an den Prozess nimmt der erste Thread, der
/// es nicht blockiert und aus einem Aufruf zurückkehrt; eins an einen Thread (tgkill, tkill) steht bei ihm (<see cref="LinuxThread.Pending"/>).
/// Abweichungen von Linux: Der Alternativstack gilt je Prozess, die siginfo je Signalnummer (nicht je Warteschlange). Echtzeitsignale
/// (32 bis 64) reihen sich nicht mehrfach ein. Zugestellt wird nur beim Rücksprung aus einem Aufruf, nicht in einer Rechenschleife ohne Aufruf
/// (Cosmos bietet keinen Haken am Rücksprung eines Interrupts in den Ring 3); wartende Aufrufe kehren dafür mit -EINTR zurück. Seit V24 zusätzlich
/// beim Rücksprung aus einer Ausnahme mit Handler (<see cref="TakeFaultSignal"/>, <see cref="LinuxSignalFrame.DeliverFault"/>).
/// </para>
/// </summary>
internal sealed partial class LinuxUserProcess : ILinuxSignalWake
{
    /// <summary>si_code: kill (SI_USER), tkill/tgkill (SI_TKILL); SIGCHLD trägt CLD_EXITED, CLD_KILLED oder CLD_DUMPED.</summary>
    public const int SiUser = 0, SiTkill = -6;

    public const ulong SaSiginfo = 0x4, SaOnstack = 0x08000000, SaRestart = 0x10000000, SaNodefer = 0x40000000, SaResethand = 0x80000000,
        SaRestorer = 0x04000000;

    private const ulong KillStopBits = (1UL << 8) | (1UL << 18);

    // siginfo der anhängigen Signale, unter _threadGate; Linux behält bei einem Standardsignal, das schon anhängig ist, die erste.
    private readonly int[] _infoCode = new int[65];
    private readonly int[] _infoPid = new int[65];
    private readonly int[] _infoStatus = new int[65];

    // Zahl der genommenen Signale (TakeSignal), nur wachsend: die Brücke liest sie vor einem Aufruf, AfterSyscall erkennt daran, ob während
    // des Aufrufs ein anderer Thread ein Signal nahm (dann ist ein -EINTR ohne eigenes Signal einer, den Linux still neu startet).
    private long _signalsTaken;

    public long SignalsTaken => Interlocked.Read(ref _signalsTaken);

    /// <summary>Unter _threadGate: als anhängig eintragen, beim Prozess oder (V38, tgkill) bei <paramref name="target"/>.</summary>
    private void Queue(int signal, int code, int senderPid, int status, LinuxThread? target)
    {
        ulong bit = 1UL << (signal - 1);
        if (((target?.Pending ?? _pendingSignals) & bit) == 0)
        {
            _infoCode[signal] = code;
            _infoPid[signal] = senderPid;
            _infoStatus[signal] = status;
        }

        if (target != null)
        {
            Volatile.Write(ref target.Pending, target.Pending | bit);
        }
        else
        {
            Volatile.Write(ref _pendingSignals, _pendingSignals | bit);
        }
    }

    /// <summary>
    /// true, solange ein anhängiges Signal für den aufrufenden Thread nicht blockiert ist; anhängig und frei ist nur eins mit Handler
    /// (Standardaktion "beenden" endet sofort, ignorierte fallen weg). Fragen nur Threads dieses Prozesses in ihren Warteschleifen (V38: ein
    /// Thread, der das Signal blockiert, wartet weiter, ein anderer nimmt es). Nur flüchtige Lesezugriffe.
    /// </summary>
    public bool SignalWaiting
    {
        get
        {
            LinuxThread me = CurrentThread;
            return ((Volatile.Read(ref _pendingSignals) | Volatile.Read(ref me.Pending)) & ~Volatile.Read(ref me.Blocked)) != 0;
        }
    }

    /// <summary>
    /// Nimmt das niedrigste anhängige, freigegebene Signal mit Handler und stellt die Maske für den Handler ein (sa_mask, ohne SA_NODEFER
    /// das Signal selbst); SA_RESETHAND setzt den Handler auf SIG_DFL zurück. false, wenn keins wartet.
    /// </summary>
    public bool TakeSignal(out LinuxSignalDelivery delivery)
    {
        delivery = default;
        LinuxThread me = CurrentThread;
        lock (_threadGate)
        {
            ulong ready = (_pendingSignals | me.Pending) & ~me.Blocked;
            for (int sig = 1; sig <= 64 && ready != 0; sig++)
            {
                ulong bit = 1UL << (sig - 1);
                if ((ready & bit) == 0 || Signals.Handler[sig] <= 1)
                {
                    continue;
                }

                // Erst das an diesen Thread gerichtete (Linux dequeue_signal: task->pending vor shared_pending).
                if ((me.Pending & bit) != 0)
                {
                    Volatile.Write(ref me.Pending, me.Pending & ~bit);
                }
                else
                {
                    Volatile.Write(ref _pendingSignals, _pendingSignals & ~bit);
                }

                Interlocked.Increment(ref _signalsTaken);
                ulong flags = Signals.Flags[sig];
                delivery = new LinuxSignalDelivery(sig, Signals.Handler[sig], flags, Signals.Restorer[sig], me.Blocked,
                    _infoCode[sig], _infoPid[sig], _infoStatus[sig]);
                ulong add = Signals.Mask[sig] | ((flags & SaNodefer) != 0 ? 0 : bit);
                Volatile.Write(ref me.Blocked, (me.Blocked | add) & ~KillStopBits);
                if ((flags & SaResethand) != 0)
                {
                    Signals.Handler[sig] = 0;
                    Signals.Flags[sig] = flags & ~SaSiginfo;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// V24: Signal einer Ausnahme im Ring 3 (Linux force_sig_fault): Es wird sofort zugestellt, nicht eingereiht, und geht anhängigen Signalen
    /// vor. Ist es blockiert oder ignoriert oder hat es keinen Handler, endet der Prozess damit (Linux setzt dann SIG_DFL und gibt es frei);
    /// false heißt genau das. Sonst wie <see cref="TakeSignal"/>: Maske für den Handler, SA_RESETHAND. Der Zähler <see cref="SignalsTaken"/>
    /// bleibt: Das Signal stammt nicht aus der Warteschlange und kann keinen Aufruf eines anderen Threads unterbrochen haben.
    /// </summary>
    public bool TakeFaultSignal(in LinuxFault fault, out LinuxSignalDelivery delivery)
    {
        delivery = default;
        int sig = fault.Signal;
        ulong bit = 1UL << (sig - 1);
        LinuxThread me = CurrentThread; // der Thread der Ausnahme
        lock (_threadGate)
        {
            if (Signals.Handler[sig] <= 1 || (me.Blocked & bit) != 0)
            {
                return false;
            }

            ulong flags = Signals.Flags[sig];
            delivery = new LinuxSignalDelivery(sig, Signals.Handler[sig], flags, Signals.Restorer[sig], me.Blocked, fault.Code, 0, 0)
            {
                Fault = fault,
            };
            ulong add = Signals.Mask[sig] | ((flags & SaNodefer) != 0 ? 0 : bit);
            Volatile.Write(ref me.Blocked, (me.Blocked | add) & ~KillStopBits);
            if ((flags & SaResethand) != 0)
            {
                Signals.Handler[sig] = 0;
                Signals.Flags[sig] = flags & ~SaSiginfo;
            }
        }

        return true;
    }

    /// <summary>Ein Signal an den eigenen Prozess (SIGPIPE nach -EPIPE), wie kill auf sich selbst.</summary>
    public void RaiseSignal(int signal) => _ = DeliverSignal(signal, fromOutside: false, SiUser, Pid);

    /// <summary>
    /// Der Rahmen passt nicht auf den Stack oder rt_sigreturn findet keinen: Linux erzwingt SIGSEGV (force_sigsegv); hier endet der Prozess
    /// mit SIGSEGV, auch wenn ein Handler gesetzt ist (der Stack, auf dem er liefe, ist ja kaputt).
    /// </summary>
    public void ForceSegv() => Terminate(11);

    /// <summary>Nichtkanonischer Stapelzeiger nach rt_sigreturn: Linux lädt ihn, der erste Stapelzugriff ist #SS und endet mit SIGBUS.</summary>
    public void ForceBus() => Terminate(7);

    /// <summary>
    /// V19 ppoll: <paramref name="mask"/> gilt ab jetzt für <paramref name="thread"/> (SIGKILL und SIGSTOP bleiben frei), die bisherige merkt
    /// er sich. Ein schon anhängiges, jetzt freigegebenes Signal mit Standardaktion "beenden" wirkt sofort wie nach sigprocmask; eins mit
    /// Handler lässt das folgende Warten mit -EINTR enden. Seit V38 je Thread wie bei Linux; das Zählen der Threads in einer Wartemaske
    /// (V19, als die Maske je Prozess galt) entfällt.
    /// </summary>
    public void BeginWaitMask(LinuxThread thread, ulong mask)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (thread.RestoreBlocked)
        {
            LeaveWaitMask(thread, restore: false); // nicht beendete Wartemaske desselben Threads (darf nicht vorkommen) nicht doppelt zählen
        }

        lock (_threadGate)
        {
            thread.SavedBlocked = thread.Blocked;
            thread.RestoreBlocked = true;
            Volatile.Write(ref thread.Blocked, mask & ~KillStopBits);
        }

        SignalStateChanged();
    }

    /// <summary>V19: Ende der Wartemaske ohne Signalrahmen (ppoll mit Ergebnis oder ohne Handler): die alte Maske gilt wieder.</summary>
    public void EndWaitMask(LinuxThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        LeaveWaitMask(thread, restore: true);
    }

    /// <summary>
    /// V19: <paramref name="thread"/> verlässt seine Wartemaske. Mit <paramref name="restore"/> gilt seine Maske von vorher wieder; ohne
    /// (ein Signalrahmen trägt sie, rt_sigreturn stellt sie her) bleibt die Wartemaske bis dahin. false, wenn der Thread keine Wartemaske hatte.
    /// </summary>
    internal bool LeaveWaitMask(LinuxThread thread, bool restore)
    {
        if (!thread.RestoreBlocked)
        {
            return false;
        }

        thread.RestoreBlocked = false;
        if (restore)
        {
            lock (_threadGate)
            {
                Volatile.Write(ref thread.Blocked, thread.SavedBlocked & ~KillStopBits);
            }

            SignalStateChanged();
        }

        return true;
    }

    /// <summary>rt_sigreturn: Maske des aufrufenden Threads aus dem Rahmen; ein jetzt freigegebenes Signal folgt seiner Aktion wie nach sigprocmask.</summary>
    public void RestoreBlocked(ulong blocked)
    {
        LinuxThread me = CurrentThread;
        lock (_threadGate)
        {
            Volatile.Write(ref me.Blocked, blocked & ~KillStopBits);
        }

        SignalStateChanged();
    }

    /// <summary>
    /// V38, unter _threadGate: Bits, die jeder lebende Thread blockiert (Linux: ein Signal an den Prozess bleibt anhängig, bis ein Thread es
    /// freigibt; <c>wants_signal</c> sucht einen Thread, der es nicht blockiert). Ohne lebenden Thread zählt der Hauptthread.
    /// </summary>
    private ulong BlockedEverywhereLocked()
    {
        ulong all = ulong.MaxValue;
        bool any = false;
        foreach (LinuxThread t in _threads)
        {
            if (!t.Exited)
            {
                all &= t.Blocked;
                any = true;
            }
        }

        return any ? all : MainThread.Blocked;
    }
}

/// <summary>
/// V18: Signalrahmen wie Linux x86-64 (<c>arch/x86/kernel/signal_64.c</c>, <c>struct rt_sigframe</c>): an RSP die Rücksprungadresse
/// (sa_restorer, musl und glibc rufen damit rt_sigreturn), dann <c>ucontext</c> (uc_flags, uc_link, uc_stack, sigcontext, uc_sigmask),
/// dann <c>siginfo</c>; darüber, auf 64 Byte ausgerichtet, das fxsave-Abbild (fpstate). Der Handler beginnt mit RSP ≡ 8 (mod 16) wie nach
/// einem call, rdi = Signal, rsi = &amp;siginfo, rdx = &amp;ucontext, und frischem FPU-Zustand.
/// </summary>
internal static class LinuxSignalFrame
{
    public const long RtSigreturn = 15, EPIPE = 32;

    // Rahmen: pretcode 8 + ucontext 304 + siginfo 128.
    public const int FrameSize = 440, FpuSize = 512;
    private const int UcOffset = 8, UcStack = UcOffset + 16, McOffset = UcOffset + 40, SigmaskOffset = UcOffset + 296, InfoOffset = 312;
    private const int RedZone = 128;

    // sigcontext_64: r8..r15, rdi, rsi, rbp, rbx, rdx, rax, rcx, rsp, rip, eflags (je 8), cs/gs/fs/ss (je 2), err, trapno, oldmask, cr2, fpstate.
    private const int McRsp = 120, McRip = 128, McFlags = 136, McCs = 144, McSs = 150, McErr = 152, McTrapno = 160, McOldmask = 168, McCr2 = 176,
        McFpstate = 184;

    // Flags, die ein Handler über den Rahmen ändern darf (Linux FIX_EFLAGS ohne TF und RF): CF, PF, AF, ZF, SF, DF, OF, AC.
    private const ulong FixFlags = 0x1 | 0x4 | 0x10 | 0x40 | 0x80 | 0x400 | 0x800 | 0x40000;
    private const ulong FlagsClearedForHandler = 0x400 | 0x100 | 0x10000; // DF, TF, RF wie Linux setup_rt_frame

    private const int SsOnstack = 1, SsDisable = 2;
    private const uint SsAutodisarm = 0x80000000;

    // Mit fxsave64 gesichertes MXCSR: Bits außerhalb der Maske lösen bei fxrstor im Ring 0 #GP aus, also nie ungeprüft übernehmen.
    private const int FxMxcsr = 24, FxMxcsrMask = 28;
    private const uint DefaultMxcsrMask = 0xFFBF;

    /// <summary>
    /// Nach einem Aufruf (rax hält schon das Ergebnis): SIGPIPE nach -EPIPE, dann alle wartenden Signale mit Handler, jedes mit eigenem Rahmen
    /// über dem vorigen (Linux stellt in der Schleife vor dem Rücksprung ebenso alle zu). <paramref name="number"/> -1: kein eigener Aufruf
    /// (Rücksprung nach vfork), dann kein Neustart und kein SIGPIPE. <paramref name="fpu"/> ist das fxsave-Abbild des Threads.
    /// <paramref name="call"/> sind die Argumente des Aufrufs (<see cref="LinuxUserRegs.ToSyscallArgs"/> vor dem Aufruf),
    /// <paramref name="takenBefore"/> der Stand von <see cref="LinuxUserProcess.SignalsTaken"/> davor.
    /// </summary>
    public static void AfterSyscall(LinuxUserProcess p, ref LinuxUserRegs r, Span<byte> fpu, in SyscallArgs call, long result, long takenBefore)
    {
        long number = call.Number;
        // Der Prozess endet (exit_group, kill, Abbruch): der Thread verlässt den Ring 3, kein Rahmen in einen Speicher, der gerade freigegeben wird.
        if (p.IsAborted)
        {
            return;
        }

        if (result == -LinuxSignalFrame.EPIPE && RaisesSigpipe(number, call))
        {
            p.RaiseSignal(13);
        }

        bool interrupted = result == -LinuxSyscalls.EINTR && number >= 0;
        bool restart = interrupted && Restartable(number, call.A1, call.A3);
        bool first = true;

        // V19 ppoll mit -EINTR: Die Handler laufen unter der Wartemaske, der erste Rahmen trägt die Maske von vorher (rt_sigreturn stellt
        // sie her, Linux restore_saved_sigmask über den Rahmen). Ohne genommenes Signal gilt sie nach der Schleife sofort wieder.
        LinuxThread thread = p.CurrentThread;
        bool saved = thread.RestoreBlocked;
        for (int n = 0; n < 64 && p.TakeSignal(out LinuxSignalDelivery d); n++)
        {
            if (first && saved)
            {
                d = d with { OldBlocked = thread.SavedBlocked };
                _ = p.LeaveWaitMask(thread, restore: false); // der Rahmen trägt die Maske, rt_sigreturn stellt sie her
                saved = false;
            }

            if (first && interrupted)
            {
                if (restart && (d.Flags & LinuxUserProcess.SaRestart) != 0)
                {
                    Rewind(ref r, number);
                }

                restart = false;
            }

            first = false;
            if (!Setup(p, ref r, fpu, d))
            {
                p.ForceSegv();
                return;
            }
        }

        if (saved)
        {
            _ = p.LeaveWaitMask(thread, restore: true);
        }

        // Ein anderer Thread nahm das Signal, das diesen Aufruf unterbrach: Linux startet ihn dann still neu (ERESTARTSYS ohne Handler).
        // Nur dann: Ein -EINTR ohne Signal (abgehängtes Terminal, abgebrochener Partner) bliebe sonst bestehen, und jeder Neustart
        // gäbe sofort wieder -EINTR, eine Endlosschleife zwischen Ring 3 und Brücke.
        if (restart && first && p.SignalsTaken != takenBefore)
        {
            Rewind(ref r, number);
        }
    }

    /// <summary>
    /// V24: Ausnahme im Ring 3 mit Handler. true: Die Register zeigen auf den Handler (Rahmen wie <see cref="Setup"/>, siginfo mit si_addr),
    /// danach folgen wie nach einem Aufruf weitere wartende Signale; der Befehl läuft nicht noch einmal, sondern der Handler. false: Der Prozess
    /// endet mit dem Signal (kein Handler, blockiert, ignoriert, Prozess endet schon) oder, wenn der Rahmen nicht auf den Stapel passt, mit
    /// SIGSEGV (<see cref="LinuxUserProcess.ForceSegv"/>, wie Linux force_sigsegv). Register und FPU bleiben dann unverändert.
    /// </summary>
    public static bool DeliverFault(LinuxUserProcess p, ref LinuxUserRegs r, Span<byte> fpu, in LinuxFault fault)
    {
        if (p.IsAborted || !p.TakeFaultSignal(fault, out LinuxSignalDelivery d))
        {
            return false;
        }

        LinuxUserRegs regs = r;
        Span<byte> image = stackalloc byte[FpuSize];
        fpu.CopyTo(image);
        if (!Setup(p, ref regs, image, d))
        {
            p.ForceSegv();
            return false;
        }

        r = regs;
        image.CopyTo(fpu);
        AfterSyscall(p, ref r, fpu, new SyscallArgs { Number = -1 }, 0, 0);
        return true;
    }

    /// <summary>
    /// rt_sigreturn: Register, Maske, Alternativstack und FPU-Zustand aus dem Rahmen an RSP - 8 (das ret des Handlers nahm pretcode).
    /// false, wenn der Rahmen nicht lesbar ist (dann SIGSEGV). rax enthält danach den gesicherten Wert, nicht ein Ergebnis.
    /// </summary>
    public static bool Return(LinuxUserProcess p, ref LinuxUserRegs r, Span<byte> fpu)
    {
        ulong frame = r.Rsp - 8;
        Span<byte> f = stackalloc byte[FrameSize];
        if (frame > r.Rsp || !p.Memory.TryRead(frame, f))
        {
            return false;
        }

        Span<byte> mc = f.Slice(McOffset, 256);
        ulong fpstate = U64(mc, McFpstate);
        Span<byte> image = stackalloc byte[FpuSize];
        if (fpstate != 0 && !p.Memory.TryRead(fpstate, image))
        {
            return false;
        }

        r.R8 = U64(mc, 0);
        r.R9 = U64(mc, 8);
        r.R10 = U64(mc, 16);
        r.R11 = U64(mc, 24);
        r.R12 = U64(mc, 32);
        r.R13 = U64(mc, 40);
        r.R14 = U64(mc, 48);
        r.R15 = U64(mc, 56);
        r.Rdi = U64(mc, 64);
        r.Rsi = U64(mc, 72);
        r.Rbp = U64(mc, 80);
        r.Rbx = U64(mc, 88);
        r.Rdx = U64(mc, 96);
        r.Rax = U64(mc, 104);
        r.Rcx = U64(mc, 112);
        r.Rsp = U64(mc, McRsp);
        r.Rip = U64(mc, McRip);
        r.Rflags = (r.Rflags & ~FixFlags) | (U64(mc, McFlags) & FixFlags);

        // FPU: gesichertes Abbild mit geprüftem MXCSR, ohne fpstate der Grundzustand (Linux: fpu__clear_user_states).
        uint mask = BitConverter.ToUInt32(fpu.Slice(FxMxcsrMask, 4));
        if (mask == 0)
        {
            mask = DefaultMxcsrMask;
        }

        if (fpstate != 0)
        {
            image.CopyTo(fpu);
            uint mxcsr = BitConverter.ToUInt32(fpu.Slice(FxMxcsr, 4)) & mask;
            BitConverter.TryWriteBytes(fpu.Slice(FxMxcsr, 4), mxcsr);
            BitConverter.TryWriteBytes(fpu.Slice(FxMxcsrMask, 4), mask);
        }
        else
        {
            InitialFpu(fpu, mask);
        }

        RestoreAltStack(p, f.Slice(UcStack, 24), r.Rsp);
        p.RestoreBlocked(U64(f, SigmaskOffset));
        return true;
    }

    /// <summary>
    /// Nach <see cref="Return"/>: true, wenn der Handler einen nichtkanonischen RSP in den Rahmen schrieb (Bits 63 bis 47 nicht gleich).
    /// Die Brücke beendet den Prozess dann wie Linux mit SIGBUS. Ein kanonischer RSP in der Kernelhälfte geht weiter an UserMode.Prepare,
    /// das jede Rückkehr mit RSP ab 0x0000_8000_0000_0000 ablehnt (Ergebnis SIGSEGV, wie der Seitenfehler unter Linux).
    /// </summary>
    public static bool NonCanonicalStack(in LinuxUserRegs r)
    {
        ulong top = r.Rsp >> 47;
        return top != 0 && top != 0x1FFFF;
    }

    /// <summary>Baut den Rahmen für <paramref name="d"/> und lenkt die Register auf den Handler; false, wenn der Stack den Rahmen nicht aufnimmt.</summary>
    public static bool Setup(LinuxUserProcess p, ref LinuxUserRegs r, Span<byte> fpu, in LinuxSignalDelivery d)
    {
        if ((d.Flags & LinuxUserProcess.SaRestorer) == 0 || d.Restorer == 0)
        {
            return false; // x86-64 kennt keinen Rücksprung ohne sa_restorer (Linux: SIGSEGV)
        }

        LinuxSignalState st = p.Signals;
        bool altEnabled = (st.AltFlags & SsDisable) == 0 && st.AltSize != 0;
        bool onAlt = altEnabled && OnStack(st.AltSp, st.AltSize, r.Rsp);
        ulong savedAltSp = st.AltSp, savedAltSize = st.AltSize;
        int savedAltFlags = (altEnabled ? st.AltFlags & ~SsDisable & ~SsOnstack : SsDisable) | (onAlt ? SsOnstack : 0);

        ulong sp;
        bool useAlt = (d.Flags & LinuxUserProcess.SaOnstack) != 0 && altEnabled && !onAlt;
        if (useAlt)
        {
            sp = st.AltSp + st.AltSize;
        }
        else
        {
            sp = r.Rsp - RedZone;
            if (sp > r.Rsp)
            {
                return false;
            }
        }

        ulong fpAddr = (sp - FpuSize) & ~63UL;
        ulong frame = ((fpAddr - FrameSize) & ~15UL) - 8;
        if (fpAddr > sp || frame > fpAddr)
        {
            return false;
        }

        // Auf dem Alternativstack muss der ganze Rahmen darauf liegen (Linux: sonst SIGSEGV statt stiller Überschreibung darunter).
        if ((useAlt || onAlt) && !OnStack(st.AltSp, st.AltSize, frame))
        {
            return false;
        }

        // fpstate: das Abbild des unterbrochenen Codes; den Softwarebereich (464..511) leer, er trägt bei xsave die Magie, die hier fehlt.
        Span<byte> image = stackalloc byte[FpuSize];
        fpu.CopyTo(image);
        image.Slice(464).Clear();
        Span<byte> f = stackalloc byte[FrameSize];
        f.Clear();
        Put(f, 0, d.Restorer);
        Put(f, UcOffset, 2); // UC_SIGCONTEXT_SS
        Put(f, UcStack, savedAltSp);
        BitConverter.TryWriteBytes(f.Slice(UcStack + 8, 4), savedAltFlags);
        Put(f, UcStack + 16, savedAltSize);

        Span<byte> mc = f.Slice(McOffset, 256);
        Put(mc, 0, r.R8);
        Put(mc, 8, r.R9);
        Put(mc, 16, r.R10);
        Put(mc, 24, r.R11);
        Put(mc, 32, r.R12);
        Put(mc, 40, r.R13);
        Put(mc, 48, r.R14);
        Put(mc, 56, r.R15);
        Put(mc, 64, r.Rdi);
        Put(mc, 72, r.Rsi);
        Put(mc, 80, r.Rbp);
        Put(mc, 88, r.Rbx);
        Put(mc, 96, r.Rdx);
        Put(mc, 104, r.Rax);
        Put(mc, 112, r.Rcx);
        Put(mc, McRsp, r.Rsp);
        Put(mc, McRip, r.Rip);
        Put(mc, McFlags, r.Rflags);
        BitConverter.TryWriteBytes(mc.Slice(McCs, 2), (ushort)0x33); // Linux' Selektoren; rt_sigreturn übernimmt sie nicht
        BitConverter.TryWriteBytes(mc.Slice(McSs, 2), (ushort)0x2b);
        Put(mc, McOldmask, d.OldBlocked);
        if (d.Fault is LinuxFault fault)
        {
            Put(mc, McErr, fault.Error);
            Put(mc, McTrapno, fault.Vector);
            Put(mc, McCr2, fault.Cr2);
        }

        Put(mc, McFpstate, fpAddr);
        Put(f, SigmaskOffset, d.OldBlocked);

        // siginfo: si_signo, si_errno, si_code; ab 16 si_pid, si_uid, bei SIGCHLD si_status (utime/stime 0). Aus einer Ausnahme (V24) steht
        // ab 16 stattdessen si_addr (_sigfault).
        Span<byte> info = f.Slice(InfoOffset, 128);
        BitConverter.TryWriteBytes(info.Slice(0, 4), d.Signal);
        BitConverter.TryWriteBytes(info.Slice(8, 4), d.Code);
        if (d.Fault is LinuxFault sigfault)
        {
            Put(info, 16, sigfault.Address);
        }
        else
        {
            BitConverter.TryWriteBytes(info.Slice(16, 4), d.Pid);
            BitConverter.TryWriteBytes(info.Slice(20, 4), (uint)p.Uid);
            BitConverter.TryWriteBytes(info.Slice(24, 4), d.Status);
        }

        if (!p.Memory.TryWrite(fpAddr, image) || !p.Memory.TryWrite(frame, f))
        {
            return false;
        }

        // SS_AUTODISARM: Der Alternativstack ist bis rt_sigreturn abgeschaltet (uc_stack bringt ihn zurück).
        if (useAlt && ((uint)st.AltFlags & SsAutodisarm) != 0)
        {
            st.AltSp = 0;
            st.AltSize = 0;
            st.AltFlags = SsDisable;
        }

        r.Rdi = (ulong)d.Signal;
        r.Rsi = frame + InfoOffset;
        r.Rdx = frame + UcOffset;
        r.Rax = 0;
        r.Rsp = frame;
        r.Rip = d.Handler;
        r.Rflags &= ~FlagsClearedForHandler;
        uint mask = BitConverter.ToUInt32(fpu.Slice(FxMxcsrMask, 4));
        InitialFpu(fpu, mask == 0 ? DefaultMxcsrMask : mask);
        return true;
    }

    /// <summary>
    /// Aufrufe, die Linux nach einem Handler mit SA_RESTART neu startet (-ERESTARTSYS). poll, nanosleep, pause und futex mit Frist
    /// liefern auch mit SA_RESTART -EINTR (Linux: ERESTARTNOHAND, restart_block).
    /// </summary>
    public static bool Restartable(long number, ulong a2, ulong a3) => number switch
    {
        0 or 1 or 17 or 18 or 19 or 20 => true,          // read, write, pread64, pwrite64, readv, writev
        42 or 43 or 44 or 45 or 46 or 47 or 288 => true, // connect, accept, sendto, recvfrom, sendmsg, recvmsg, accept4
        61 or 247 => true,                               // wait4, waitid
        202 => FutexWaitsForever(a2, a3),
        _ => false,
    };

    // futex(uaddr, op, val, timeout): rsi = op (A1), r10 = timeout (A3).
    private static bool FutexWaitsForever(ulong op, ulong timeout) => ((op & 127) is 0 or 9) && timeout == 0;

    // write, writev, sendto/sendmsg ohne MSG_NOSIGNAL (0x4000): flags stehen bei sendto(fd, buf, len, flags) in r10 (A3),
    // bei sendmsg(fd, msg, flags) in rdx (A2).
    private static bool RaisesSigpipe(long number, in SyscallArgs call) => number switch
    {
        1 or 18 or 20 => true,
        44 => (call.A3 & 0x4000) == 0,
        46 => (call.A2 & 0x4000) == 0,
        _ => false,
    };

    private static void Rewind(ref LinuxUserRegs r, long number)
    {
        r.Rip -= 2; // syscall ist 0F 05
        r.Rax = (ulong)number;
    }

    private static void RestoreAltStack(LinuxUserProcess p, ReadOnlySpan<byte> ss, ulong rsp)
    {
        LinuxSignalState st = p.Signals;
        // Linux restore_altstack: wie sigaltstack, also nicht, während der Thread noch auf dem eingestellten Alternativstack läuft (EPERM).
        if ((st.AltFlags & SsDisable) == 0 && st.AltSize != 0 && OnStack(st.AltSp, st.AltSize, rsp))
        {
            return;
        }

        ulong sp = U64(ss, 0), size = U64(ss, 16);
        int flags = BitConverter.ToInt32(ss.Slice(8, 4));
        int mode = flags & ~unchecked((int)SsAutodisarm);
        if (mode is not (0 or SsOnstack or SsDisable))
        {
            return;
        }

        if (mode == SsDisable)
        {
            st.AltSp = 0;
            st.AltSize = 0;
            st.AltFlags = SsDisable;
        }
        else if (size >= 2048)
        {
            st.AltSp = sp;
            st.AltSize = size;
            st.AltFlags = flags & unchecked((int)SsAutodisarm);
        }
    }

    // Linux on_sig_stack: sp > ss_sp und sp - ss_sp <= ss_size.
    private static bool OnStack(ulong altSp, ulong altSize, ulong sp) => sp > altSp && sp - altSp <= altSize;

    private static void InitialFpu(Span<byte> fpu, uint mxcsrMask)
    {
        fpu.Clear();
        BitConverter.TryWriteBytes(fpu.Slice(0, 2), (ushort)0x037F);
        BitConverter.TryWriteBytes(fpu.Slice(FxMxcsr, 4), 0x1F80u);
        BitConverter.TryWriteBytes(fpu.Slice(FxMxcsrMask, 4), mxcsrMask);
    }

    private static ulong U64(ReadOnlySpan<byte> b, int offset) => BitConverter.ToUInt64(b.Slice(offset, 8));

    private static void Put(Span<byte> b, int offset, ulong value) => BitConverter.TryWriteBytes(b.Slice(offset, 8), value);
}
