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

/// <summary>Zustand der Signale eines Prozesses (S1: nur Verwaltung, keine Zustellung).</summary>
internal sealed class LinuxSignalState
{
    public readonly ulong[] Handler = new ulong[65];
    public readonly ulong[] Flags = new ulong[65];
    public readonly ulong[] Restorer = new ulong[65];
    public readonly ulong[] Mask = new ulong[65];
    public ulong Blocked;
    public ulong AltSp, AltSize;
    public int AltFlags = 2; // SS_DISABLE
}

internal static partial class LinuxSyscalls
{
    public const long Poll = 7, RtSigaction = 13, RtSigprocmask = 14, SchedYield = 24, Madvise = 28, Nanosleep = 35, Getuid = 102, Getgid = 104, Geteuid = 107, Getegid = 108, ClockNanosleep = 230, Uname = 63,
        Sigaltstack = 131, Futex = 202, ClockGettime = 228, Getrandom = 318, Pause = 34;

    public const long EAGAIN = 11, EDEADLK = 35, ETIMEDOUT = 110;

    private const int ClockMax = 7;
    private const ulong SigKillStopMask = (1UL << 8) | (1UL << 18); // Signal 9 und 19, Bit = Nummer - 1
    private static readonly long StartTicks = Stopwatch.GetTimestamp();

    private static long DispatchMore(ref SyscallArgs a, ILinuxProcess p)
    {
        switch (a.Number)
        {
            case ClockGettime: return DoClockGettime(p, (int)a.A0, a.A1);
            case Getrandom: return DoGetrandom(p, a.A0, a.A1, (uint)a.A2);
            case Uname: return DoUname(p, a.A0);
            case RtSigaction: return DoSigaction(p, (int)a.A0, a.A1, a.A2, a.A3);
            case RtSigprocmask: return DoSigprocmask(p, (int)a.A0, a.A1, a.A2, a.A3);
            case Sigaltstack: return DoSigaltstack(p, a.A0, a.A1);
            case SchedYield: return 0;
            case Poll: return DoPoll(p, a.A0, a.A1, (int)a.A2);
            case Madvise: return DoMadvise(p, a.A0, a.A1, (int)a.A2);
            case Nanosleep: return DoNanosleep(p, a.A0, a.A1);
            case ClockNanosleep: return DoClockNanosleep(p, (int)a.A0, (int)a.A1, a.A2, a.A3);
            // Ein Benutzer ohne Konten: der Wert kommt aus dem Prozess und ist derselbe wie AT_UID/AT_GID im Startstack.
            case Getuid:
            case Geteuid: return (long)p.Uid;
            case Getgid:
            case Getegid: return (long)p.Gid;
            case Futex: return p is ILinuxThreads threads ? DoFutexThreads(p, threads.Futexes, ref a) : DoFutex(p, ref a);
            case Pause: return p is ILinuxThreads paused ? paused.Futexes.Sleep(-1) : -ENOSYS; // ohne Signalzustellung schläft pause bis zum Ende
            default: return p is ILinuxFiles files ? DispatchFile(ref a, p, files) : -ENOSYS; // Dateiaufrufe nur mit Dateifähigkeit (V2)
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

    private static void PutField(byte[] b, int index, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            b[index * 65 + i] = (byte)text[i];
        }
    }

    private static LinuxSignalState Signals(ILinuxProcess p) => p.Signals;

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

        if (oact != 0)
        {
            BitConverter.TryWriteBytes(b.Slice(0, 8), st.Handler[sig]);
            BitConverter.TryWriteBytes(b.Slice(8, 8), st.Flags[sig]);
            BitConverter.TryWriteBytes(b.Slice(16, 8), st.Restorer[sig]);
            BitConverter.TryWriteBytes(b.Slice(24, 8), st.Mask[sig]);
            if (!p.Memory.TryWrite(oact, b))
            {
                return -EFAULT;
            }
        }

        if (act != 0)
        {
            st.Handler[sig] = newHandler;
            st.Flags[sig] = newFlags;
            st.Restorer[sig] = newRestorer;
            st.Mask[sig] = newMask & ~SigKillStopMask;
        }

        return 0;
    }

    private static long DoSigprocmask(ILinuxProcess p, int how, ulong set, ulong oldset, ulong sigsetsize)
    {
        if (sigsetsize != 8 || (set != 0 && (uint)how > 2))
        {
            return -EINVAL;
        }

        var st = Signals(p);
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
            BitConverter.TryWriteBytes(b, st.Blocked);
            if (!p.Memory.TryWrite(oldset, b))
            {
                return -EFAULT;
            }
        }

        if (set != 0)
        {
            ulong next = how == 0 ? st.Blocked | value : how == 1 ? st.Blocked & ~value : value;
            st.Blocked = next & ~SigKillStopMask;
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
                st.AltFlags = 0;
            }
        }

        return 0;
    }

    private static long DoPoll(ILinuxProcess p, ulong fds, ulong nfds, long timeoutMs)
    {
        const short PollIn = 1, PollOut = 4, PollNval = 0x20;
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

        long ready = PollScan(p, b, (int)nfds, PollIn, PollOut, PollNval, out ILinuxInput? waitFor);

        // Wartet poll auf Terminaleingabe (V3 stdin), wartet die Eingabe selbst (in Scheiben, mit Abbruch), auch ohne Frist.
        if (ready == 0 && timeoutMs != 0 && waitFor != null && waitFor.WaitInput(timeoutMs))
        {
            ready = PollScan(p, b, (int)nfds, PollIn, PollOut, PollNval, out _);
        }
        else if (ready == 0 && timeoutMs > 0 && waitFor == null && p is ILinuxHost host)
        {
            // Ohne bereite Einträge schläft poll die Frist ab (sonst drehen Warteschleifen, etwa poll(NULL, 0, ms)).
            // Negative Frist (unendlich) schläft nie: es kann niemand wecken, Ergebnis 0. Ohne Host entfällt das Schlafen.
            host.SleepNanos(timeoutMs * 1_000_000L);
        }

        if (nfds > 0 && !p.Memory.TryWrite(fds, b))
        {
            return -EFAULT;
        }

        return ready;
    }

    /// <summary>Setzt revents aller Einträge in <paramref name="b"/>; liefert die Zahl bereiter Einträge und eine Eingabe, auf die poll warten kann.</summary>
    private static long PollScan(ILinuxProcess p, byte[] b, int nfds, short pollIn, short pollOut, short pollNval, out ILinuxInput? waitFor)
    {
        waitFor = null;
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
                    revents = pollNval;
                }
                else
                {
                    // Schreiben ist immer bereit; Lesen nur bei einer Eingabe mit Bytes, Eingabeende oder Abbruch (V3 stdin).
                    revents = (short)(events & pollOut);
                    if ((events & pollIn) != 0 && d is ILinuxInput input)
                    {
                        if (input.InputReady)
                        {
                            revents |= pollIn;
                        }
                        else
                        {
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

        long slept = SleepFor(p, ns);
        if (slept != 0)
        {
            return slept;
        }

        // Ungestört bis zum Ende geschlafen (keine Signalzustellung in S1): Rest 0.
        return rem != 0 && !p.Memory.TryWrite(rem, new byte[16]) ? -EFAULT : 0;
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

        long slept = SleepFor(p, ns);
        if (slept != 0)
        {
            return slept;
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

                return table.Wait(p.Memory, a.A0, (int)a.A2, bitset, deadline);
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
