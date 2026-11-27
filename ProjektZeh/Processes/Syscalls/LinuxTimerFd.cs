using System;
using System.Threading;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// timerfd (V22b): ein Zeitgeber als Deskriptor. read liefert die Zahl der Abläufe seit dem letzten read (8 Byte) und setzt sie auf 0; poll und
/// epoll melden POLLIN, sobald mindestens ein Ablauf aussteht. Anlass: s3 pipe_epoll und Ereignisschleifen, die Zeitgeber über epoll warten lassen.
/// <para>
/// Es gibt keinen Zeitgeber-Interrupt dahinter: Abläufe werden bei jeder Abfrage aus der Uhr nachgerechnet (<see cref="Advance"/>). Das reicht,
/// weil jeder Weg zur Bereitschaft abfragt (read, poll, epoll) und Wartende höchstens bis zum nächsten Ablauf schlafen (<see cref="WaitMs"/>).
/// Die Uhr ist CLOCK_MONOTONIC oder CLOCK_REALTIME des Wirts in Nanosekunden; CLOCK_BOOTTIME ist hier die monotone Uhr.
/// </para>
/// <para>
/// Grenzen (LINUX_ABI.md, V22b): TFD_TIMER_CANCEL_ON_SET wird angenommen, meldet aber nie -ECANCELED (ZehOS stellt die Wanduhr nie um);
/// die Alarmuhren wecken kein schlafendes System; TFD_IOC_SET_TICKS fehlt.
/// </para>
/// <para>
/// Sperre: <see cref="_gate"/> ist eine Blattsperre wie bei <see cref="LinuxEventFd"/> (Dateisperre oder epoll-Sperre davor möglich, darin
/// keine andere Sperre). Die Uhr wird darin gelesen; sie nimmt keine Sperre.
/// </para>
/// </summary>
internal sealed class LinuxTimerFd : ILinuxPollable, ILinuxStatusFlags, ILinuxInput, ILinuxAnonFile, ILinuxEdgeSource
{
    public const int TfdTimerAbstime = 1, TfdTimerCancelOnSet = 2;

    private const long EAGAIN = 11, EINVAL = 22, EINTR = 4;

    private readonly object _gate = new();
    private readonly Func<long> _clock;

    // Nächster Ablauf in Nanosekunden der Uhr; 0 heißt nicht gestellt (Linux: it_value 0 entschärft).
    private long _next;
    private long _interval;
    private ulong _ticks;
    private int _closed;

    // V22: steigt bei jedem Nachrechnen mit neuen Abläufen (EPOLLET meldet dann erneut, wie Linux' timerfd_tmrproc mit EPOLLIN weckt).
    private long _seq;

    public LinuxTimerFd(Func<long> clock, bool nonBlocking)
    {
        _clock = clock;
        NonBlocking = nonBlocking;
    }

    public bool NonBlocking { get; set; }

    /// <summary>O_RDWR wie Linux (timerfd_create öffnet mit O_RDWR), dazu O_NONBLOCK.</summary>
    public int StatusFlags => 2 | (NonBlocking ? LinuxPipeEnd.ONonblock : 0);

    public long EdgeSeq
    {
        get
        {
            lock (_gate)
            {
                Advance();
                return _seq;
            }
        }
    }

    /// <summary>
    /// timerfd_settime: stellt den Zeitgeber neu und verwirft ausstehende Abläufe (Linux setzt ticks auf 0). <paramref name="valueNs"/> 0 entschärft;
    /// mit <see cref="TfdTimerAbstime"/> ist der Wert ein Zeitpunkt der Uhr, sonst eine Spanne ab jetzt. Ein Zeitpunkt in der Vergangenheit läuft
    /// sofort ab. Liefert den vorherigen Stand wie <see cref="Get"/>.
    /// </summary>
    public void Set(int flags, long intervalNs, long valueNs, out long oldIntervalNs, out long oldValueNs)
    {
        lock (_gate)
        {
            long now = _clock();
            GetLocked(now, out oldIntervalNs, out oldValueNs);
            _ticks = 0;
            _interval = intervalNs;
            if (valueNs == 0)
            {
                _next = 0;
            }
            else
            {
                long at = (flags & TfdTimerAbstime) != 0 ? valueNs : Saturate(now, valueNs);
                _next = at <= 0 ? 1 : at; // 0 ist „nicht gestellt“; ein Zeitpunkt vor dem Uhrbeginn läuft sofort ab
            }

            Monitor.PulseAll(_gate); // Wartende rechnen ihre Schlafdauer neu
        }
    }

    /// <summary>timerfd_gettime: Restzeit bis zum nächsten Ablauf (0, wenn nicht gestellt oder einmalig abgelaufen) und das Intervall.</summary>
    public void Get(out long intervalNs, out long valueNs)
    {
        lock (_gate)
        {
            GetLocked(_clock(), out intervalNs, out valueNs);
        }
    }

    public long Write(ReadOnlySpan<byte> data) => -EINVAL;

    /// <summary>Linux: timerfd hat kein write, der Aufruf liefert -EINVAL.</summary>
    public long WriteAs(ReadOnlySpan<byte> data, ILinuxAbort? abort) => -EINVAL;

    public long ReadAs(Span<byte> dst, ILinuxAbort? abort)
    {
        if (dst.Length < 8)
        {
            return -EINVAL;
        }

        ulong value;
        lock (_gate)
        {
            while (true)
            {
                long now = _clock();
                Advance(now);
                if (_ticks != 0)
                {
                    break;
                }

                if (NonBlocking)
                {
                    return -EAGAIN;
                }

                if (abort?.IsAborted == true)
                {
                    return -EINTR;
                }

                Monitor.Wait(_gate, WaitMs(now, LinuxPipe.SliceMs));
            }

            value = _ticks;
            _ticks = 0;
        }

        BitConverter.TryWriteBytes(dst.Slice(0, 8), value);
        return 8;
    }

    public long ReadInput(Span<byte> dst) => ReadAs(dst, null);

    public bool InputReady => PollEvents(LinuxPipe.PollIn) != 0;

    public bool WaitInput(long timeoutMs) => WaitPoll(LinuxPipe.PollIn, timeoutMs, null);

    /// <summary>Linux (timerfd_poll): nur EPOLLIN bei ausstehenden Abläufen, nie POLLOUT.</summary>
    public short PollEvents(short events)
    {
        lock (_gate)
        {
            Advance();
            return _ticks != 0 ? (short)(events & LinuxPipe.PollIn) : (short)0;
        }
    }

    public bool WaitPoll(short events, long timeoutMs, ILinuxAbort? abort)
    {
        // Frist nach der Uhr wie LinuxPipe.WaitPoll; geschlafen wird höchstens bis zum nächsten Ablauf, weil ihn niemand weckt.
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_gate)
        {
            while (true)
            {
                long now = _clock();
                Advance(now);
                if (_ticks != 0 && (events & LinuxPipe.PollIn) != 0)
                {
                    return true;
                }

                long left = timeoutMs < 0 ? LinuxPipe.SliceMs : timeoutMs - LinuxPipe.ElapsedMs(start);
                if (left <= 0 || abort?.IsAborted == true)
                {
                    return false;
                }

                Monitor.Wait(_gate, WaitMs(now, Math.Min(left, LinuxPipe.SliceMs)));
            }
        }
    }

    public void Close() => Interlocked.Exchange(ref _closed, 1);

    private void Advance() => Advance(_clock());

    // Rechnet Abläufe bis now nach (unter _gate). Periodisch: alle verpassten Abläufe zählen, wie Linux' hrtimer_forward die Überläufe zählt.
    private void Advance(long now)
    {
        if (_next == 0 || now < _next)
        {
            return;
        }

        if (_interval > 0)
        {
            ulong k = 1 + (ulong)((now - _next) / _interval);
            _ticks += k;
            _next = Saturate(_next, (long)Math.Min(k * (ulong)_interval, (ulong)long.MaxValue));
        }
        else
        {
            _ticks++;
            _next = 0;
        }

        _seq++;
        Monitor.PulseAll(_gate);
    }

    private void GetLocked(long now, out long intervalNs, out long valueNs)
    {
        Advance(now); // Linux (timerfd_gettime): ein abgelaufener periodischer Zeitgeber wird erst vorgerückt, dann die Restzeit gemeldet
        intervalNs = _interval;
        valueNs = _next == 0 ? 0 : Math.Max(1, _next - now);
    }

    // Millisekunden bis zum nächsten Ablauf, aufgerundet und höchstens cap; nicht gestellt: cap.
    private int WaitMs(long now, long cap)
    {
        if (_next == 0)
        {
            return (int)Math.Max(1, cap);
        }

        long ms = ((_next - now) + 999_999) / 1_000_000;
        return (int)Math.Clamp(ms, 1, Math.Max(1, cap));
    }

    private static long Saturate(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}

/// <summary>
/// V22b timerfd: timerfd_create, timerfd_settime und timerfd_gettime laufen unter der Dateisperre (<see cref="DispatchSoft"/>), weil sie nie
/// warten; read, poll und epoll_wait auf dem Deskriptor laufen wie bei eventfd ohne sie.
/// </summary>
internal static partial class LinuxSyscalls
{
    public const long TimerfdCreate = 283, TimerfdSettime = 286, TimerfdGettime = 287;

    private const int ClockRealtimeId = 0, ClockMonotonicId = 1, ClockBoottimeId = 7, ClockRealtimeAlarmId = 8, ClockBoottimeAlarmId = 9;

    // Größe von struct itimerspec auf x86-64: it_interval und it_value, je tv_sec und tv_nsec zu 8 Byte.
    private const int ItimerspecSize = 32;

    /// <summary>
    /// timerfd_create(clockid, flags) wie Linux: Flags außer TFD_CLOEXEC und TFD_NONBLOCK oder eine andere Uhr als REALTIME, MONOTONIC, BOOTTIME
    /// und deren Alarmformen -EINVAL; die Alarmuhren ohne CAP_WAKE_ALARM (hier: nicht uid 0) -EPERM.
    /// </summary>
    private static long DoTimerfdCreate(ILinuxProcess p, ILinuxFiles f, int clock, int flags)
    {
        if ((flags & ~(SoftOCloexec | SoftONonblock)) != 0)
        {
            return -EINVAL;
        }

        if (clock != ClockRealtimeId && clock != ClockMonotonicId && clock != ClockBoottimeId && clock != ClockRealtimeAlarmId
            && clock != ClockBoottimeAlarmId)
        {
            return -EINVAL;
        }

        if ((clock == ClockRealtimeAlarmId || clock == ClockBoottimeAlarmId) && p.Uid != 0)
        {
            return -EPERM;
        }

        bool realtime = clock == ClockRealtimeId || clock == ClockRealtimeAlarmId;
        return AddAnon(f, new LinuxTimerFd(TimerClock(p, realtime), (flags & SoftONonblock) != 0), (flags & SoftOCloexec) != 0);
    }

    /// <summary>
    /// timerfd_settime(fd, flags, new, old) in der Reihenfolge von Linux: new lesen (-EFAULT), Flags außer TFD_TIMER_ABSTIME und
    /// TFD_TIMER_CANCEL_ON_SET oder ein ungültiger timespec -EINVAL, dann fd (-EBADF, kein timerfd -EINVAL); old wird erst nach dem Stellen
    /// geschrieben (-EFAULT, der Zeitgeber bleibt gestellt).
    /// </summary>
    private static long DoTimerfdSettime(ILinuxProcess p, int fd, int flags, ulong newPtr, ulong oldPtr)
    {
        Span<byte> its = stackalloc byte[ItimerspecSize];
        if (!p.Memory.TryRead(newPtr, its))
        {
            return -EFAULT;
        }

        if ((flags & ~(LinuxTimerFd.TfdTimerAbstime | LinuxTimerFd.TfdTimerCancelOnSet)) != 0)
        {
            return -EINVAL;
        }

        if (!TimespecNanos(its, out long intervalNs) || !TimespecNanos(its[16..], out long valueNs))
        {
            return -EINVAL;
        }

        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        if (d is not LinuxTimerFd timer)
        {
            return -EINVAL;
        }

        timer.Set(flags, intervalNs, valueNs, out long oldInterval, out long oldValue);
        return oldPtr == 0 || WriteItimerspec(p, oldPtr, oldInterval, oldValue) ? 0 : -EFAULT;
    }

    /// <summary>timerfd_gettime(fd, cur): fd (-EBADF, kein timerfd -EINVAL), dann cur schreiben (-EFAULT).</summary>
    private static long DoTimerfdGettime(ILinuxProcess p, int fd, ulong curPtr)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        if (d is null)
        {
            return -EBADF;
        }

        if (d is not LinuxTimerFd timer)
        {
            return -EINVAL;
        }

        timer.Get(out long interval, out long value);
        return WriteItimerspec(p, curPtr, interval, value) ? 0 : -EFAULT;
    }

    // Uhr des Zeitgebers wie clock_gettime (DoClockGettime): Wirt, sonst Stopwatch und DateTime (Ersatzprozesse der Tests).
    private static Func<long> TimerClock(ILinuxProcess p, bool realtime)
    {
        if (p is ILinuxHost host)
        {
            return realtime ? () => host.RealtimeNanos : () => host.MonotonicNanos;
        }

        return realtime
            ? () => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100
            : () => (long)((System.Diagnostics.Stopwatch.GetTimestamp() - StartTicks) * (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
    }

    // timespec64_valid: tv_sec >= 0, 0 <= tv_nsec < 10^9; sehr große Werte werden wie KTIME_MAX begrenzt.
    private static bool TimespecNanos(ReadOnlySpan<byte> ts, out long ns)
    {
        long sec = BitConverter.ToInt64(ts);
        long nsec = BitConverter.ToInt64(ts[8..]);
        ns = 0;
        if (sec < 0 || nsec < 0 || nsec >= 1_000_000_000)
        {
            return false;
        }

        ns = sec >= long.MaxValue / 1_000_000_000L ? long.MaxValue : (sec * 1_000_000_000L) + nsec;
        return true;
    }

    private static bool WriteItimerspec(ILinuxProcess p, ulong ptr, long intervalNs, long valueNs)
    {
        Span<byte> its = stackalloc byte[ItimerspecSize];
        BitConverter.TryWriteBytes(its, intervalNs / 1_000_000_000L);
        BitConverter.TryWriteBytes(its[8..], intervalNs % 1_000_000_000L);
        BitConverter.TryWriteBytes(its[16..], valueNs / 1_000_000_000L);
        BitConverter.TryWriteBytes(its[24..], valueNs % 1_000_000_000L);
        return p.Memory.TryWrite(ptr, its);
    }
}
