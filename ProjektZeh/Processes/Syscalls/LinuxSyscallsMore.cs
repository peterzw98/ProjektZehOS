using System;
using ProjektZeh.Processes;
using System.Diagnostics;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Optionale Dienste des Wirts (Zeit, Zufall). <see cref="ILinuxProcess"/>-Implementierungen können sie zusätzlich anbieten;
/// ohne sie nimmt der Verteiler Stopwatch/DateTime für die Zeit und antwortet auf getrandom mit -ENOSYS (nie mit schwachem Zufall).
/// </summary>
internal interface ILinuxHost
{
    /// <summary>Nanosekunden seit 1970 (CLOCK_REALTIME).</summary>
    long RealtimeNanos { get; }

    /// <summary>Nanosekunden seit einem festen Anfang, nie rückwärts (CLOCK_MONOTONIC).</summary>
    long MonotonicNanos { get; }

    /// <summary>Füllt mit kryptographischem Zufall; false, wenn keine Quelle verfügbar ist.</summary>
    bool TryFillRandom(Span<byte> dst);

    /// <summary>Gibt die CPU für etwa so viele Nanosekunden ab (poll mit Frist ohne bereite Einträge).</summary>
    void SleepNanos(long ns);
}

/// <summary>
/// Optional neben <see cref="ILinuxHost"/> (V13, sysinfo): Arbeitsspeicher des Rechners. Eigene Schnittstelle, damit vorhandene
/// Hosts unverändert bleiben; ohne sie meldet sysinfo 0 Bytes.
/// </summary>
internal interface ILinuxSystemInfo
{
    /// <summary>Gesamter und freier physischer Speicher in Bytes; false, wenn unbekannt.</summary>
    bool TryGetMemory(out ulong totalBytes, out ulong freeBytes);
}

/// <summary>
/// N4-S2: CPUs des Laufs für sched_getaffinity, getcpu, /proc/stat und /proc/cpuinfo. Gezählt wird dicht ab 0: Ein Lauf auf den
/// Kernel-CPUs 1 bis 3 meldet die CPUs 0 bis 2 (Abweichung von Linux, das die echten Nummern nennt). musl leitet
/// sysconf(_SC_NPROCESSORS_CONF) und _ONLN aus der Maske ab, Programme indizieren mit getcpu Tabellen dieser Größe. Ohne die
/// Schnittstelle gilt eine CPU (Nummer 0).
/// </summary>
internal interface ILinuxCpus
{
    /// <summary>Zahl der CPUs des Laufs, 1 bis 64.</summary>
    int CpuCount { get; }

    /// <summary>Dichte Nummer der CPU, auf der der Aufrufer gerade läuft (0 bis <see cref="CpuCount"/> - 1); kann schon beim Lesen veraltet sein.</summary>
    int CurrentCpu { get; }
}

/// <summary>Zustand der Signale eines Prozesses (Handler, Maske, Alternativstack); zugestellt wird seit V18 (LinuxSignals.cs).</summary>
internal sealed class LinuxSignalState
{
    public readonly ulong[] Handler = new ulong[65];
    public readonly ulong[] Flags = new ulong[65];
    public readonly ulong[] Restorer = new ulong[65];
    public readonly ulong[] Mask = new ulong[65];
    /// <summary>Maske eines Prozesses ohne Threads (Testattrappen); <see cref="ProjektZeh.Processes.LinuxUserProcess"/> führt sie seit V38 je Thread (<see cref="ProjektZeh.Processes.LinuxThread.Blocked"/>).</summary>
    public ulong Blocked;
    public ulong AltSp, AltSize;
    public int AltFlags = 2; // SS_DISABLE
}

internal static partial class LinuxSyscalls
{
    public const long Poll = 7, RtSigaction = 13, RtSigprocmask = 14, SchedYield = 24, Msync = 26, Madvise = 28, Nanosleep = 35, Getuid = 102, Getgid = 104, Geteuid = 107, Getegid = 108, ClockNanosleep = 230, Uname = 63,
        Sigaltstack = 131, Futex = 202, ClockGettime = 228, Getrandom = 318, Pause = 34, Sysinfo = 99, RtSigpending = 127, Getrusage = 98, RtSigsuspend = 130;

    public const long EAGAIN = 11, EDEADLK = 35, ETIMEDOUT = 110;

    private const int ClockMax = 7;
    private const short PollIn = 1, PollOut = 4, PollNval = 0x20;

    // Scheibe, wenn poll auf mehrere Deskriptoren zugleich wartet (V13): so spät meldet sich höchstens der zweite.
    private const int PollMultiSliceMs = 10;
    private const ulong SigKillStopMask = (1UL << 8) | (1UL << 18); // Signal 9 und 19, Bit = Nummer - 1
    private static readonly long StartTicks = Stopwatch.GetTimestamp();

    private static long DispatchMore(ref SyscallArgs a, ILinuxProcess p)
    {
        switch (a.Number)
        {
            case ClockGettime: return DoClockGettime(p, (int)a.A0, a.A1);
            case Getrandom: return DoGetrandom(p, a.A0, a.A1, (uint)a.A2);
            case Uname: return DoUname(p, a.A0);
            case Sysinfo: return DoSysinfo(p, a.A0);
            case Getrusage: return DoGetrusage(p, (int)a.A0, a.A1);
            case RtSigaction: return DoSigaction(p, (int)a.A0, a.A1, a.A2, a.A3);
            case RtSigpending: return DoSigpending(p, a.A0, a.A1);
            case RtSigprocmask: return DoSigprocmask(p, (int)a.A0, a.A1, a.A2, a.A3);
            case Sigaltstack: return DoSigaltstack(p, a.A0, a.A1);
            case SchedYield: return 0;
            case Poll: return DoPoll(p, a.A0, a.A1, (int)a.A2);
            case Ppoll: return DoPpoll(p, a.A0, a.A1, a.A2, a.A3, a.A4); // V19
            // V22: epoll_wait wartet und läuft deshalb wie poll ohne die Dateisperre; epoll_create und epoll_ctl darunter (DispatchSoft).
            case EpollWait: return DoEpollPwait(p, (int)a.A0, a.A1, (int)a.A2, (int)a.A3, 0, 0);
            case EpollPwait: return DoEpollPwait(p, (int)a.A0, a.A1, (int)a.A2, (int)a.A3, a.A4, a.A5);
            case EpollPwait2: return DoEpollPwait2(p, (int)a.A0, a.A1, (int)a.A2, a.A3, a.A4, a.A5);
            case CloseRange: return p is ILinuxFiles ranged ? DoCloseRange(p, ranged, (uint)a.A0, (uint)a.A1, (uint)a.A2) : -ENOSYS;
            case Madvise: return DoMadvise(p, a.A0, a.A1, (int)a.A2);
            case Msync: return DoMsync(p, a.A0, a.A1, (int)a.A2); // V32
            case Nanosleep: return DoNanosleep(p, a.A0, a.A1);
            case ClockNanosleep: return DoClockNanosleep(p, (int)a.A0, (int)a.A1, a.A2, a.A3);
            // Ein Benutzer ohne Konten: der Wert kommt aus dem Prozess und ist derselbe wie AT_UID/AT_GID im Startstack.
            case Getuid:
            case Geteuid: return (long)p.Uid;
            case Getgid:
            case Getegid: return (long)p.Gid;
            case Futex: return p is ILinuxThreads threads ? DoFutexThreads(p, threads.Futexes, ref a) : DoFutex(p, ref a);
            case Pause: return p is ILinuxThreads paused ? paused.Futexes.Sleep(-1) : -ENOSYS; // bis ein Signal mit Handler kommt (-EINTR, V18) oder der Prozess endet
            // rt_sigsuspend(mask, 8): Wartemaske setzen und schlafen, bis ein Signal mit Handler kommt (immer -EINTR). busybox ash wartet in wait
            // so auf SIGCHLD (wait_block_or_sig); ohne den Aufruf kehrte sigsuspend sofort mit -ENOSYS zurück, und wait drehte endlos.
            // Die Brücke stellt die alte Maske über den Signalrahmen wieder her (wie bei ppoll mit -EINTR, LinuxSyscallsStart.cs).
            case RtSigsuspend: return DoSigsuspend(p, a.A0, a.A1);
            case Wait4:
            case Waitid:
            case Getppid:
            case Kill: return DispatchWait(ref a, p); // V15: Kindprozesse (posix_spawn)
            case >= Socket and <= Getsockopt:
            case Accept4:
                return p is ILinuxFiles sockets ? DispatchSocket(ref a, p, sockets) : -ENOSYS; // V16: Unix-Sockets, ohne grobe Dateisperre
            default: return p is ILinuxFiles files ? DispatchFileGated(ref a, p, files) : -ENOSYS; // Dateiaufrufe nur mit Dateifähigkeit (V2)
        }
    }

    /// <summary>
    /// V11: Alle Datei- und Pfadaufrufe eines Prozesses laufen nacheinander unter <see cref="ILinuxFiles.FileGate"/>. Threads (V10)
    /// teilen Deskriptortabelle, Arbeitsverzeichnis und das <c>LinuxFileSystem</c> (dessen Auflösung Arbeitslisten im Objekt hält);
    /// ohne Sperre könnte ein Thread eine Datei schließen, die ein anderer gerade liest, oder dieselbe Nummer zweimal vergeben.
    /// Eine grobe Sperre reicht: Dateiaufrufe warten hier nie auf andere Threads, nur auf die Platte. Das Lesen vom Terminal
    /// (fd 0) läuft vorher in <c>Dispatch</c> ab und hält die Sperre nicht, sonst stünden alle Dateiaufrufe bis zur nächsten Eingabe.
    /// </summary>
    private static long DispatchFileGated(ref SyscallArgs a, ILinuxProcess p, ILinuxFiles files)
    {
        if (IsWaitingLock(ref a))
        {
            return DoFcntlLockWait(ref a, p, files); // V28b: wartet außerhalb der Sperre, sonst stünden alle Dateiaufrufe des Laufs
        }

        lock (files.FileGate)
        {
            return DispatchFile(ref a, p, files);
        }
    }

    private static long DoClockGettime(ILinuxProcess p, int clock, ulong tsPtr)
    {
        if ((uint)clock > ClockMax)
        {
            return -EINVAL;
        }

        long ns;
        var host = p as ILinuxHost;
        // V29c: CLOCK_PROCESS_CPUTIME_ID (2) und CLOCK_THREAD_CPUTIME_ID (3) zählen die Rechenzeit, nicht die Wanduhr.
        if ((clock == 2 || clock == 3) && p is ILinuxCpuTime cpu)
        {
            long cpuNs = clock == 2 ? cpu.ProcessCpuNanos : cpu.CurrentThreadCpuNanos;
            Span<byte> t = stackalloc byte[16];
            BitConverter.TryWriteBytes(t.Slice(0, 8), cpuNs / 1_000_000_000L);
            BitConverter.TryWriteBytes(t.Slice(8, 8), cpuNs % 1_000_000_000L);
            return p.Memory.TryWrite(tsPtr, t) ? 0 : -EFAULT;
        }

        // Realtime und grobe Realtime-Uhr (0, 5) laufen auf Wanduhr; alle anderen (auch CPU-Zeit) auf der monotonen Uhr.
        if (clock == 0 || clock == 5)
        {
            ns = host != null ? host.RealtimeNanos : (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;
        }
        else
        {
            ns = host != null ? host.MonotonicNanos : (long)((Stopwatch.GetTimestamp() - StartTicks) * (1_000_000_000.0 / Stopwatch.Frequency));
        }

        Span<byte> b = stackalloc byte[16];
        BitConverter.TryWriteBytes(b.Slice(0, 8), ns / 1_000_000_000L);
        BitConverter.TryWriteBytes(b.Slice(8, 8), ns % 1_000_000_000L);
        return p.Memory.TryWrite(tsPtr, b) ? 0 : -EFAULT;
    }

    /// <summary>
    /// getrusage (V29c): ru_utime aus der CPU-Zeit (RUSAGE_SELF: Prozess, RUSAGE_THREAD: aufrufender Thread), ru_stime 0 (ZehOS trennt
    /// Kernel- und Benutzerzeit nicht), alle übrigen Zähler 0. RUSAGE_CHILDREN liefert 0 (beendete Kinder werden nicht aufsummiert).
    /// </summary>
    private static long DoGetrusage(ILinuxProcess p, int who, ulong ptr)
    {
        if (who != 0 && who != -1 && who != 1)
        {
            return -EINVAL;
        }

        long ns = p is ILinuxCpuTime cpu && who != -1 ? (who == 1 ? cpu.CurrentThreadCpuNanos : cpu.ProcessCpuNanos) : 0;
        byte[] buf = new byte[RusageSize];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 8), ns / 1_000_000_000L);
        BitConverter.TryWriteBytes(buf.AsSpan(8, 8), ns % 1_000_000_000L / 1000);
        return p.Memory.TryWrite(ptr, buf) ? 0 : -EFAULT;
    }

    private static long DoGetrandom(ILinuxProcess p, ulong buf, ulong len, uint flags)
    {
        if ((flags & ~7u) != 0 || (flags & 6u) == 6u) // GRND_RANDOM und GRND_INSECURE schließen sich aus
        {
            return -EINVAL;
        }

        if (p is not ILinuxHost host)
        {
            return -ENOSYS;
        }

        ulong count = len > (ulong)MaxRw ? (ulong)MaxRw : len;
        byte[] tmp = new byte[(int)Math.Min(count, (ulong)Chunk)];
        ulong done = 0;
        while (done < count)
        {
            if (Aborted(p))
            {
                return done > 0 ? (long)done : -EINTR;
            }

            int n = (int)Math.Min(count - done, (ulong)tmp.Length);
            var span = tmp.AsSpan(0, n);
            if (!host.TryFillRandom(span))
            {
                return done > 0 ? (long)done : -EAGAIN; // Quelle nicht bereit (wie GRND_NONBLOCK vor der Initialisierung)
            }

            if (buf + done < buf || !p.Memory.TryWrite(buf + done, span))
            {
                return done > 0 ? (long)done : -EFAULT;
            }

            done += (ulong)n;
        }

        return (long)done;
    }

    private static long DoUname(ILinuxProcess p, ulong buf)
    {
        // struct utsname: sechs Felder zu je 65 Bytes. Release als echte Linux-Version, weil Laufzeiten sie auswerten.
        byte[] b = new byte[6 * 65];
        PutField(b, 0, "Linux");
        PutField(b, 1, "zehos");
        PutField(b, 2, "6.1.0-zehos");
        PutField(b, 3, "#1 ZehOS");
        PutField(b, 4, "x86_64");
        PutField(b, 5, "(none)");
        return p.Memory.TryWrite(buf, b) ? 0 : -EFAULT;
    }

    /// <summary>
    /// sysinfo (V13): struct sysinfo von x86-64 (112 Bytes). Laufzeit in Sekunden aus der monotonen Uhr (wie Linux CLOCK_BOOTTIME ohne
    /// Ruhezustand), Speicher aus <see cref="ILinuxSystemInfo"/> in Bytes (mem_unit 1). Lastwerte, Swap, geteilter und Pufferspeicher
    /// sind 0; procs ist 1 (ein Prozess sieht nur sich).
    /// </summary>
    private static long DoSysinfo(ILinuxProcess p, ulong buf)
    {
        long ns = p is ILinuxHost host ? host.MonotonicNanos : (long)((Stopwatch.GetTimestamp() - StartTicks) * (1_000_000_000.0 / Stopwatch.Frequency));
        ulong total = 0, free = 0;
        if (p is ILinuxSystemInfo info && info.TryGetMemory(out ulong t, out ulong f))
        {
            total = t;
            free = Math.Min(f, t);
        }

        byte[] b = new byte[112];
        BitConverter.TryWriteBytes(b.AsSpan(0, 8), Math.Max(0, ns / 1_000_000_000L)); // uptime
        BitConverter.TryWriteBytes(b.AsSpan(32, 8), total); // totalram
        BitConverter.TryWriteBytes(b.AsSpan(40, 8), free); // freeram
        BitConverter.TryWriteBytes(b.AsSpan(80, 2), (ushort)1); // procs
        BitConverter.TryWriteBytes(b.AsSpan(104, 4), 1u); // mem_unit
        return p.Memory.TryWrite(buf, b) ? 0 : -EFAULT;
    }

    private static void PutField(byte[] b, int index, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            b[index * 65 + i] = (byte)text[i];
        }
    }

    private static LinuxSignalState Signals(ILinuxProcess p) => p.Signals;

    /// <summary>V38: Maske des aufrufenden Threads (Linux je Thread); ein Prozess ohne Threads hat nur die eine in <see cref="LinuxSignalState"/>.</summary>
    private static ref ulong BlockedOf(ILinuxProcess p)
    {
        if (p is ILinuxThreads threads)
        {
            return ref threads.CurrentThread.Blocked;
        }

        return ref p.Signals.Blocked;
    }

    private static long DoSigaction(ILinuxProcess p, int sig, ulong act, ulong oact, ulong sigsetsize)
    {
        if (sigsetsize != 8 || sig < 1 || sig > 64)
        {
            return -EINVAL;
        }

        if (act != 0 && (sig == 9 || sig == 19))
        {
            return -EINVAL; // SIGKILL und SIGSTOP lassen sich nicht abfangen
        }

        var st = Signals(p);
        Span<byte> b = stackalloc byte[32]; // kernel_sigaction: handler, flags, restorer, mask
        ulong newHandler = 0, newFlags = 0, newRestorer = 0, newMask = 0;
        if (act != 0)
        {
            if (!p.Memory.TryRead(act, b))
            {
                return -EFAULT;
            }

            newHandler = BitConverter.ToUInt64(b.Slice(0, 8));
            newFlags = BitConverter.ToUInt64(b.Slice(8, 8));
            newRestorer = BitConverter.ToUInt64(b.Slice(16, 8));
            newMask = BitConverter.ToUInt64(b.Slice(24, 8));
        }

        // P3-1 (Risiko-Review 2): Lesen der alten und Setzen der neuen Aktion unter der Sperre, unter der ein anderer Thread sie
        // zustellt (TakeSignal, SA_RESETHAND). Die alte schreibt erst danach in den Speicher des Programms, außerhalb der Sperre
        // (_threadGate steht zuunterst); scheitert das, gilt die neue trotzdem, wie bei Linux (do_sigaction vor copy_to_user).
        Span<ulong> action = stackalloc ulong[4];
        action[0] = newHandler;
        action[1] = newFlags;
        action[2] = newRestorer;
        action[3] = newMask & ~SigKillStopMask;
        if (p is ILinuxThreads threads)
        {
            threads.ExchangeSignalAction(sig, act != 0, action);
        }
        else
        {
            ExchangeAction(st, sig, act != 0, action);
        }

        if (oact != 0)
        {
            BitConverter.TryWriteBytes(b.Slice(0, 8), action[0]);
            BitConverter.TryWriteBytes(b.Slice(8, 8), action[1]);
            BitConverter.TryWriteBytes(b.Slice(16, 8), action[2]);
            BitConverter.TryWriteBytes(b.Slice(24, 8), action[3]);
            if (!p.Memory.TryWrite(oact, b))
            {
                return -EFAULT;
            }
        }

        if (act != 0)
        {
            // SIG_IGN verwirft ein anhängiges Signal (Linux do_sigaction), auch ein blockiertes.
            (p as ILinuxThreads)?.SignalStateChanged();
        }

        return 0;
    }

    /// <summary>Tauscht die Aktion eines Signals (Handler, Flags, Restorer, Maske); der Aufrufer hält die Sperre, falls es eine gibt.</summary>
    internal static void ExchangeAction(LinuxSignalState st, int sig, bool set, Span<ulong> action)
    {
        ulong handler = st.Handler[sig], flags = st.Flags[sig], restorer = st.Restorer[sig], mask = st.Mask[sig];
        if (set)
        {
            st.Handler[sig] = action[0];
            st.Flags[sig] = action[1];
            st.Restorer[sig] = action[2];
            st.Mask[sig] = action[3];
        }

        action[0] = handler;
        action[1] = flags;
        action[2] = restorer;
        action[3] = mask;
    }

    private static long DoSigprocmask(ILinuxProcess p, int how, ulong set, ulong oldset, ulong sigsetsize)
    {
        if (sigsetsize != 8 || (set != 0 && (uint)how > 2))
        {
            return -EINVAL;
        }

        ref ulong blocked = ref BlockedOf(p);
        Span<byte> b = stackalloc byte[8];
        ulong value = 0;
        if (set != 0)
        {
            if (!p.Memory.TryRead(set, b))
            {
                return -EFAULT;
            }

            value = BitConverter.ToUInt64(b);
        }

        if (oldset != 0)
        {
            BitConverter.TryWriteBytes(b, System.Threading.Volatile.Read(ref blocked));
            if (!p.Memory.TryWrite(oldset, b))
            {
                return -EFAULT;
            }
        }

        if (set != 0)
        {
            ulong old = System.Threading.Volatile.Read(ref blocked);
            ulong next = how == 0 ? old | value : how == 1 ? old & ~value : value;
            System.Threading.Volatile.Write(ref blocked, next & ~SigKillStopMask);

            // Ein freigegebenes anhängiges Signal (tgkill während der Blockade) folgt jetzt seiner Standardaktion.
            (p as ILinuxThreads)?.SignalStateChanged();
        }

        return 0;
    }

    /// <summary>
    /// rt_sigpending (V23): anhängige und blockierte Signale (Linux: pending &amp; blocked; ein freigegebenes wird ohnehin zugestellt). Es gibt
    /// nur eine Menge je Prozess (keine je Thread, LINUX_ABI.md); Echtzeitsignale gibt es nicht.
    /// </summary>
    private static long DoSigpending(ILinuxProcess p, ulong set, ulong sigsetsize)
    {
        if (sigsetsize > 128)
        {
            return -EINVAL; // Linux: größer als sizeof(sigset_t) -EINVAL; kleiner schneidet ab
        }

        if (p is not ILinuxThreads threads)
        {
            return -ENOSYS;
        }

        ulong value = threads.PendingSignals & System.Threading.Volatile.Read(ref BlockedOf(p));
        Span<byte> b = stackalloc byte[8];
        BitConverter.TryWriteBytes(b, value);
        int n = (int)Math.Min(sigsetsize, 8UL);
        if (!p.Memory.TryWrite(set, b.Slice(0, n)))
        {
            return -EFAULT;
        }

        return 0;
    }

    private static long DoSigaltstack(ILinuxProcess p, ulong ss, ulong old)
    {
        var st = Signals(p);
        Span<byte> b = stackalloc byte[24]; // stack_t: sp, flags (int, aufgefüllt), size
        ulong sp = 0, size = 0;
        int flags = 0;
        if (ss != 0)
        {
            if (!p.Memory.TryRead(ss, b))
            {
                return -EFAULT;
            }

            sp = BitConverter.ToUInt64(b.Slice(0, 8));
            flags = BitConverter.ToInt32(b.Slice(8, 4));
            size = BitConverter.ToUInt64(b.Slice(16, 8));
            int mode = flags & 0x7fffffff; // SS_AUTODISARM (Bit 31) ist erlaubt
            if (mode != 0 && mode != 2)
            {
                return -EINVAL;
            }

            if ((flags & 2) == 0 && size < 2048)
            {
                return -ENOMEM; // MINSIGSTKSZ
            }
        }

        if (old != 0)
        {
            b.Clear();
            BitConverter.TryWriteBytes(b.Slice(0, 8), st.AltSp);
            BitConverter.TryWriteBytes(b.Slice(8, 4), st.AltFlags);
            BitConverter.TryWriteBytes(b.Slice(16, 8), st.AltSize);
            if (!p.Memory.TryWrite(old, b))
            {
                return -EFAULT;
            }
        }

        if (ss != 0)
        {
            if ((flags & 2) != 0)
            {
                st.AltSp = 0;
                st.AltSize = 0;
                st.AltFlags = 2;
            }
            else
            {
                st.AltSp = sp;
                st.AltSize = size;
                st.AltFlags = flags & int.MinValue; // SS_AUTODISARM bleibt (V18: die Zustellung schaltet den Stack dann ab), Linux meldet es mit
            }
        }

        return 0;
    }

    private static long DoPoll(ILinuxProcess p, ulong fds, ulong nfds, long timeoutMs)
    {
        if (nfds > 1024)
        {
            return -EINVAL; // RLIMIT_NOFILE-Obergrenze
        }

        // Einträge vorab lesen, dann in einem Zug zurückschreiben: bei -EFAULT bleibt nichts halb geändert.
        byte[] b = new byte[(int)nfds * 8];
        if (nfds > 0 && !p.Memory.TryRead(fds, b))
        {
            return -EFAULT;
        }

        long ready = PollScan(p, b, (int)nfds, out object? waitFor, out short waitEvents, out int waitables);

        if (ready == 0 && timeoutMs != 0 && waitFor != null)
        {
            // Wartet poll auf Terminaleingabe (V3 stdin) oder eine Pipe (V13), wartet der Deskriptor selbst (in Scheiben, mit Abbruch),
            // auch ohne Frist. Bei einem Wartenden die ganze Frist; bei mehreren reihum in kurzen Scheiben auf den ersten, dann neu
            // durchsuchen, damit ein anderer (etwa die Weckpipe neben fd 0) höchstens eine Scheibe zu spät gemeldet wird. Die Frist läuft
            // nach der Stopwatch, nicht nach der Uhr des Prozesses (Ersatzprozesse in den Tests haben eine stehende) und nicht nach der
            // nominalen Scheibe: Eine Pipe kehrt bei jedem fremden Puls früh zurück, gezählte Scheiben verkürzten die Frist (V13-Review P2-1).
            // Die erste Scheibe bekommt die volle Frist: Die bis hierher verstrichene Zeit (Einlesen, JIT) gehört nicht zum Warten, und
            // ein abgerundeter Stopwatch-Wert verkürzte bei einem Wartenden die ganze Frist um 1 ms (V13-Delta-Review, Test flackerte).
            long start = Stopwatch.GetTimestamp();
            bool first = true;
            while (true)
            {
                long left = timeoutMs < 0 ? -1 : (first ? timeoutMs : Math.Max(0, timeoutMs - LinuxPipe.ElapsedMs(start)));
                first = false;
                long slice = waitables == 1 || left == 0 ? left : (left < 0 ? PollMultiSliceMs : Math.Min(PollMultiSliceMs, left));
                // V16: Pipes und Sockets warten mit dem Abbruch des Aufrufers (sie gehören oft einem anderen Prozess, der vorher enden darf).
                _ = waitFor is ILinuxPollable pollable ? pollable.WaitPoll(waitEvents, slice, p as ILinuxAbort) : ((ILinuxInput)waitFor).WaitInput(slice);
                ready = PollScan(p, b, (int)nfds, out _, out _, out _);
                if (ready > 0 || LinuxInterrupt.Check(p as ILinuxAbort) || waitables == 1 || (timeoutMs >= 0 && LinuxPipe.ElapsedMs(start) >= timeoutMs))
                {
                    break;
                }
            }
        }
        else if (ready == 0 && timeoutMs > 0 && waitFor == null && p is ILinuxHost host)
        {
            // Ohne bereite Einträge schläft poll die Frist ab (sonst drehen Warteschleifen, etwa poll(NULL, 0, ms)).
            // Negative Frist (unendlich) schläft nie: es kann niemand wecken, Ergebnis 0. Ohne Host entfällt das Schlafen.
            // V19: in Scheiben, damit ein Signal (auch ein vor ppoll anhängiges, das die Wartemaske freigibt) das Schlafen sofort beendet.
            // Gezählt wird die nominale Schlafzeit (SleepNanos kehrt nur beim Abbruch früher zurück; Ersatzhosts der Tests schlafen gar nicht).
            for (long slept = 0; slept < timeoutMs && p is not ILinuxSignalWake { SignalWaiting: true } && !LinuxInterrupt.Check(p as ILinuxAbort);)
            {
                long slice = Math.Min(timeoutMs - slept, PollMultiSliceMs);
                host.SleepNanos(slice * 1_000_000L);
                slept += slice;
            }
        }

        // V18: Ohne bereiten Eintrag unterbricht ein Signal poll wie unter Linux (-EINTR, auch mit SA_RESTART); revents bleiben dann unberührt.
        if (ready == 0 && p is ILinuxSignalWake { SignalWaiting: true })
        {
            return -EINTR;
        }

        if (nfds > 0 && !p.Memory.TryWrite(fds, b))
        {
            return -EFAULT;
        }

        return ready;
    }

    /// <summary>
    /// Füllt revents aller Einträge; zählt die bereiten. <paramref name="waitFor"/> ist der erste nicht bereite Deskriptor, auf den
    /// sich warten lässt (<see cref="ILinuxPollable"/> oder <see cref="ILinuxInput"/>), <paramref name="waitables"/> ihre Anzahl.
    /// </summary>
    private static long PollScan(ILinuxProcess p, byte[] b, int nfds, out object? waitFor, out short waitEvents, out int waitables)
    {
        waitFor = null;
        waitEvents = 0;
        waitables = 0;
        long ready = 0;
        for (int i = 0; i < nfds; i++)
        {
            int fd = BitConverter.ToInt32(b, i * 8);
            short events = BitConverter.ToInt16(b, i * 8 + 4);
            short revents = 0;
            if (fd >= 0)
            {
                ILinuxDescriptor? d = p.GetDescriptor(fd);
                if (d is null)
                {
                    revents = PollNval;
                }
                else if (d is ILinuxPollable pollable)
                {
                    // V13 Pipe: Lesen mit Bytes, Schreiben mit Platz; POLLHUP/POLLERR auch ungefragt.
                    revents = pollable.PollEvents(events);
                    if (revents == 0 && (events & (PollIn | PollOut)) != 0)
                    {
                        waitables++;
                        if (waitFor == null)
                        {
                            waitFor = pollable;
                            waitEvents = events;
                        }
                    }
                }
                else
                {
                    // Schreiben ist immer bereit; Lesen nur bei einer Eingabe mit Bytes, Eingabeende oder Abbruch (V3 stdin).
                    revents = (short)(events & PollOut);
                    if ((events & PollIn) != 0 && d is ILinuxInput input)
                    {
                        if (input.InputReady)
                        {
                            revents |= PollIn;
                        }
                        else
                        {
                            waitables++;
                            waitFor ??= input;
                        }
                    }
                }
            }

            BitConverter.TryWriteBytes(b.AsSpan(i * 8 + 6, 2), revents);
            if (revents != 0)
            {
                ready++;
            }
        }

        return ready;
    }

    /// <summary>
    /// msync (V32): Prüfungen wie Linux 6.12 (unbekannte Bits oder MS_ASYNC zusammen mit MS_SYNC -EINVAL, Adresse nicht seitenbündig
    /// -EINVAL, Lücke im Bereich -ENOMEM). Geteilte Dateiseiten gehen sofort zurück in die Datei, auch bei MS_ASYNC; MS_INVALIDATE hat
    /// nichts zu tun, weil alle Abbildungen dieselben Frames sehen.
    /// </summary>
    private static long DoMsync(ILinuxProcess p, ulong addr, ulong len, int flags)
    {
        const int MsAsync = 1, MsInvalidate = 2, MsSync = 4;
        if ((flags & ~(MsAsync | MsInvalidate | MsSync)) != 0 || (flags & (MsAsync | MsSync)) == (MsAsync | MsSync) || (addr & 0xfff) != 0)
        {
            return -EINVAL;
        }

        if (len > ulong.MaxValue - 0xfff)
        {
            return -ENOMEM;
        }

        ulong flen = (len + 0xfff) & ~0xfffUL;
        if (flen == 0)
        {
            return 0;
        }

        return p is ILinuxSharedMemory shared ? shared.SyncShared(addr, flen) : 0;
    }

    private static long DoMadvise(ILinuxProcess p, ulong addr, ulong len, int advice)
    {
        if ((addr & 0xfff) != 0 || len > (1UL << 47))
        {
            return -EINVAL;
        }

        switch (advice)
        {
            case 0: // NORMAL
            case 1: // RANDOM
            case 2: // SEQUENTIAL
            case 3: // WILLNEED
            case 10: // DONTFORK
            case 11: // DOFORK
            case 12: // MERGEABLE
            case 13: // UNMERGEABLE
            case 14: // HUGEPAGE
            case 15: // NOHUGEPAGE
            case 16: // DONTDUMP
            case 17: // DODUMP
            case 18: // WIPEONFORK
            case 19: // KEEPONFORK
            case 20: // COLD
            case 21: // PAGEOUT
                return 0; // nur Hinweise; es gibt weder fork noch Kernabzug noch Auslagerung
            case 4: // DONTNEED
            case 8: // FREE
                // Alles ist anonym und privat: Verwerfen heißt "beim nächsten Zugriff Nullen" (Allokatoren geben so Speicher zurück).
                if (len == 0)
                {
                    return 0;
                }

                return p.Discard(addr, (len + 0xfff) & ~0xfffUL) ? 0 : -ENOMEM;
            default:
                return -EINVAL; // REMOVE, DONTNEED_LOCKED, unbekannte Werte
        }
    }

    private const long NsPerSec = 1_000_000_000L;

    /// <summary>Liest ein timespec; false bei -EFAULT, sonst Nanosekunden ; Fehler als negatives Ergebnis.</summary>
    private static long ReadTimespec(ILinuxProcess p, ulong ptr, out long ns)
    {
        ns = 0;
        Span<byte> ts = stackalloc byte[16];
        if (!p.Memory.TryRead(ptr, ts))
        {
            return -EFAULT;
        }

        long sec = BitConverter.ToInt64(ts.Slice(0, 8)), nsec = BitConverter.ToInt64(ts.Slice(8, 8));
        if (sec < 0 || nsec < 0 || nsec >= NsPerSec)
        {
            return -EINVAL;
        }

        ns = sec > (long.MaxValue - nsec) / NsPerSec ? long.MaxValue : sec * NsPerSec + nsec; // Sättigung statt Überlauf
        return 0;
    }

    /// <summary>
    /// Schläft <paramref name="ns"/> relativ (V2c). Mit Threads (V10) schläft der Aufruf auf der futex-Tabelle und endet mit exit_group
    /// vorzeitig (-EINTR, <c>rem</c> bleibt dann unberührt); ohne Threads schläft der Host.
    /// </summary>
    private static long SleepFor(ILinuxProcess p, long ns)
    {
        if (ns <= 0)
        {
            return 0;
        }

        if (p is ILinuxThreads threads && p is ILinuxHost clock)
        {
            long now = clock.MonotonicNanos;
            return threads.Futexes.Sleep(ns > long.MaxValue - now ? long.MaxValue : now + ns);
        }

        (p as ILinuxHost)?.SleepNanos(ns);
        return 0;
    }

    private static long DoNanosleep(ILinuxProcess p, ulong req, ulong rem)
    {
        long r = ReadTimespec(p, req, out long ns);
        if (r != 0)
        {
            return r;
        }

        long begin = (p as ILinuxHost)?.MonotonicNanos ?? 0;
        long slept = SleepFor(p, ns);
        if (slept != 0)
        {
            return WriteRemaining(p, rem, slept, ns, begin);
        }

        // Ungestört bis zum Ende geschlafen: Rest 0.
        return rem != 0 && !p.Memory.TryWrite(rem, new byte[16]) ? -EFAULT : 0;
    }

    /// <summary>V18: Ein Signal unterbrach den Schlaf (-EINTR): Linux schreibt die Restzeit nach <c>rem</c>.</summary>
    private static long WriteRemaining(ILinuxProcess p, ulong rem, long result, long ns, long begin)
    {
        if (result != -EINTR || rem == 0 || p is not ILinuxHost host || p is not ILinuxSignalWake)
        {
            return result;
        }

        long left = Math.Max(0, ns - (host.MonotonicNanos - begin));
        Span<byte> ts = stackalloc byte[16];
        BitConverter.TryWriteBytes(ts.Slice(0, 8), left / 1_000_000_000);
        BitConverter.TryWriteBytes(ts.Slice(8, 8), left % 1_000_000_000);
        return p.Memory.TryWrite(rem, ts) ? result : -EFAULT;
    }

    private static long DoClockNanosleep(ILinuxProcess p, int clock, int flags, ulong req, ulong rem)
    {
        const int TimerAbstime = 1;
        // CPU-Zeituhren (2, 3) und unbekannte lehnt der Aufruf ab (-EINVAL); erlaubt sind REALTIME, MONOTONIC, BOOTTIME, TAI und die groben Uhren.
        if (clock is not (0 or 1 or 4 or 5 or 6 or 7 or 9 or 11) || (flags & ~TimerAbstime) != 0)
        {
            return -EINVAL;
        }

        long r = ReadTimespec(p, req, out long ns);
        if (r != 0)
        {
            return r;
        }

        if ((flags & TimerAbstime) != 0)
        {
            var host = p as ILinuxHost;
            long now = (clock == 0 || clock == 5 || clock == 11)
                ? (host != null ? host.RealtimeNanos : (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100)
                : (host != null ? host.MonotonicNanos : (long)((Stopwatch.GetTimestamp() - StartTicks) * (1_000_000_000.0 / Stopwatch.Frequency)));
            ns = ns > now ? ns - now : 0;
            rem = 0; // bei absoluter Frist wird rem nie geschrieben
        }

        long begin = (p as ILinuxHost)?.MonotonicNanos ?? 0;
        long slept = SleepFor(p, ns);
        if (slept != 0)
        {
            return WriteRemaining(p, rem, slept, ns, begin);
        }

        return rem != 0 && !p.Memory.TryWrite(rem, new byte[16]) ? -EFAULT : 0;
    }

    private static long DoFutex(ILinuxProcess p, ref SyscallArgs a)
    {
        const int Wait = 0, Wake = 1, Requeue = 3, CmpRequeue = 4, WaitBitset = 9, WakeBitset = 10;
        int op = (int)a.A1 & 127; // FUTEX_PRIVATE_FLAG (128) und FUTEX_CLOCK_REALTIME (256) ändern hier nichts
        int val = (int)a.A2;
        if ((a.A0 & 3) != 0)
        {
            return -EINVAL;
        }

        Span<byte> w = stackalloc byte[4];
        switch (op)
        {
            case Wait:
            case WaitBitset:
                if (op == WaitBitset && (uint)a.A5 == 0)
                {
                    return -EINVAL;
                }

                if (!p.Memory.TryRead(a.A0, w))
                {
                    return -EFAULT;
                }

                if (BitConverter.ToInt32(w) != val)
                {
                    return -EAGAIN;
                }

                if (a.A3 != 0)
                {
                    Span<byte> ts = stackalloc byte[16];
                    if (!p.Memory.TryRead(a.A3, ts))
                    {
                        return -EFAULT;
                    }

                    long sec = BitConverter.ToInt64(ts.Slice(0, 8)), nsec = BitConverter.ToInt64(ts.Slice(8, 8));
                    if (nsec < 0 || nsec >= 1_000_000_000L || (op == Wait && sec < 0))
                    {
                        return -EINVAL;
                    }

                    return -ETIMEDOUT; // Ein Thread, niemand weckt ihn: die Frist läuft ab (ohne tatsächlich zu schlafen)
                }

                return -EDEADLK; // Einzelthread ohne Frist würde ewig hängen; Linux hängt, wir melden es
            case Wake:
                return p.Memory.TryRead(a.A0, w) ? 0 : -EFAULT; // keine Wartenden; Linux prüft den Zeiger trotzdem
            case WakeBitset:
                if ((uint)a.A5 == 0)
                {
                    return -EINVAL;
                }

                return p.Memory.TryRead(a.A0, w) ? 0 : -EFAULT;
            case Requeue:
            case CmpRequeue:
                if ((a.A4 & 3) != 0)
                {
                    return -EINVAL;
                }

                Span<byte> w2 = stackalloc byte[4];
                if (!p.Memory.TryRead(a.A0, w) || !p.Memory.TryRead(a.A4, w2))
                {
                    return -EFAULT;
                }

                if (op == CmpRequeue)
                {
                    if (BitConverter.ToInt32(w) != (int)a.A5)
                    {
                        return -EAGAIN;
                    }
                }

                return 0;
            default:
                return -ENOSYS;
        }
    }
    /// <summary>
    /// futex mit Threads (V10): WAIT/WAKE (auch _BITSET), REQUEUE und CMP_REQUEUE über die futex-Tabelle des Prozesses. WAIT
    /// schläft wirklich, bis ein anderer Thread weckt, die Frist abläuft oder der Prozess endet (-EINTR). Fristen: FUTEX_WAIT relativ,
    /// FUTEX_WAIT_BITSET absolut auf CLOCK_MONOTONIC (mit FUTEX_CLOCK_REALTIME auf der Wanduhr).
    /// </summary>
    private static long DoFutexThreads(ILinuxProcess p, LinuxFutexTable table, ref SyscallArgs a)
    {
        const int Wait = 0, Wake = 1, Requeue = 3, CmpRequeue = 4, WaitBitset = 9, WakeBitset = 10, ClockRealtime = 256;
        int op = (int)a.A1 & 127;
        bool realtime = ((int)a.A1 & ClockRealtime) != 0;
        if ((a.A0 & 3) != 0)
        {
            return -EINVAL;
        }

        switch (op)
        {
            case Wait:
            case WaitBitset:
            {
                uint bitset = op == WaitBitset ? (uint)a.A5 : uint.MaxValue;
                if (bitset == 0)
                {
                    return -EINVAL;
                }

                long deadline = -1;
                if (a.A3 != 0)
                {
                    Span<byte> ts = stackalloc byte[16];
                    if (!p.Memory.TryRead(a.A3, ts))
                    {
                        return -EFAULT;
                    }

                    long sec = BitConverter.ToInt64(ts.Slice(0, 8)), nsec = BitConverter.ToInt64(ts.Slice(8, 8));
                    if (nsec < 0 || nsec >= 1_000_000_000L || sec < 0)
                    {
                        return -EINVAL;
                    }

                    var host = p as ILinuxHost;
                    long now = host?.MonotonicNanos ?? 0;
                    long ns = sec > long.MaxValue / 2_000_000_000L ? long.MaxValue / 2 : sec * 1_000_000_000L + nsec;
                    if (op == Wait)
                    {
                        deadline = now + ns; // relativ, auch mit FUTEX_CLOCK_REALTIME (Linux ab 4.5)
                    }
                    else
                    {
                        // Absolut: auf die monotone Uhr umrechnen, die die Tabelle misst.
                        long clockNow = realtime ? host?.RealtimeNanos ?? 0 : now;
                        deadline = now + Math.Max(0, ns - clockNow);
                    }
                }

                // V21: ohne FUTEX_PRIVATE_FLAG kann der Wecker ein anderer Prozess sein (MAP_SHARED nach fork); siehe LinuxFutexTable.Wait.
                return table.Wait(p.Memory, a.A0, (int)a.A2, bitset, deadline, shared: ((int)a.A1 & 128) == 0);
            }

            case Wake:
            case WakeBitset:
            {
                uint bitset = op == WakeBitset ? (uint)a.A5 : uint.MaxValue;
                if (bitset == 0)
                {
                    return -EINVAL;
                }

                return table.Wake(a.A0, (int)Math.Min((uint)a.A2, int.MaxValue), bitset);
            }

            case Requeue:
            case CmpRequeue:
                if ((a.A4 & 3) != 0 || (int)a.A2 < 0 || (int)a.A3 < 0)
                {
                    return -EINVAL;
                }

                return table.Requeue(p.Memory, a.A0, a.A4, (int)a.A2, (int)a.A3, op == CmpRequeue ? (int)a.A5 : null);
            default:
                return -ENOSYS;
        }
    }
}
