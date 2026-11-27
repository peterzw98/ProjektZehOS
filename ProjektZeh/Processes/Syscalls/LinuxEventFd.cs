using System;
using System.Collections.Generic;
using System.Threading;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V20: Deskriptor ohne Datei dahinter (bei Linux eine „anon_inode“: eventfd, inotify). Gehört der Deskriptortabelle wie ein Pipe-Ende:
/// dup, vfork und SCM_RIGHTS teilen ihn, erst die letzte Nummer im Lauf schließt ihn (<see cref="LinuxSyscalls.ReleaseIfUnused"/>,
/// <c>LinuxOpenFiles</c>). fstat meldet Modus 0600 ohne Dateityp wie Linux.
/// </summary>
internal interface ILinuxAnonFile : ILinuxDescriptor
{
    /// <summary>Beim letzten close; ein zweiter Aufruf ändert nichts.</summary>
    void Close();
}

/// <summary>
/// eventfd/eventfd2 (V20): ein 64-Bit-Zähler. write addiert, read liefert den Zähler und setzt ihn auf 0 (mit EFD_SEMAPHORE: liefert 1 und
/// zieht 1 ab). Anlass: libcurl weckt seine Ereignisschleife damit (curl_multi_wakeup, HAVE_EVENTFD), RequestServer von Ladybird.
/// <para>
/// Sperre: <see cref="_gate"/> ist eine Blattsperre wie die Pipe-Sperre (Dateisperre davor möglich, darin keine andere Sperre).
/// Wartende schlafen in Scheiben von <see cref="LinuxPipe.SliceMs"/>, damit ein Abbruch jeden erreicht.
/// </para>
/// </summary>
internal sealed class LinuxEventFd : ILinuxPollable, ILinuxStatusFlags, ILinuxInput, ILinuxAnonFile, ILinuxEdgeSource
{
    public const int EfdSemaphore = 1;

    /// <summary>Höchster Zählerstand: 2^64 - 2; ein write darüber wartet (oder -EAGAIN).</summary>
    public const ulong MaxCount = 0xFFFF_FFFF_FFFF_FFFE;

    private const long EAGAIN = 11, EINVAL = 22, EINTR = 4;

    private readonly object _gate = new();
    private readonly bool _semaphore;
    private ulong _count;
    private int _closed;

    // V22: steigt bei jedem write (EPOLLET meldet dann erneut, wie Linux' eventfd_write mit EPOLLIN weckt).
    private long _seq;

    public LinuxEventFd(uint initial, bool semaphore, bool nonBlocking)
    {
        _count = initial;
        _semaphore = semaphore;
        NonBlocking = nonBlocking;
    }

    public bool NonBlocking { get; set; }

    /// <summary>O_RDWR wie Linux, dazu O_NONBLOCK.</summary>
    public int StatusFlags => 2 | (NonBlocking ? LinuxPipeEnd.ONonblock : 0);

    public long EdgeSeq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    /// <summary>Zählerstand (Hosttests).</summary>
    public ulong Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public long Write(ReadOnlySpan<byte> data) => WriteAs(data, null);

    public long WriteAs(ReadOnlySpan<byte> data, ILinuxAbort? abort)
    {
        if (data.Length < 8)
        {
            return -EINVAL;
        }

        ulong add = BitConverter.ToUInt64(data.Slice(0, 8));
        if (add == ulong.MaxValue)
        {
            return -EINVAL;
        }

        lock (_gate)
        {
            // Linux (eventfd_write): wartet, bis der Zähler den Wert ohne Überlauf über MaxCount aufnimmt.
            while (MaxCount - _count < add)
            {
                if (NonBlocking)
                {
                    return -EAGAIN;
                }

                if (abort?.IsAborted == true)
                {
                    return -EINTR;
                }

                Monitor.Wait(_gate, LinuxPipe.SliceMs);
            }

            _count += add;
            _seq++;
            if (add != 0)
            {
                Monitor.PulseAll(_gate);
            }
        }

        return 8;
    }

    public long ReadAs(Span<byte> dst, ILinuxAbort? abort)
    {
        if (dst.Length < 8)
        {
            return -EINVAL;
        }

        ulong value;
        lock (_gate)
        {
            while (_count == 0)
            {
                if (NonBlocking)
                {
                    return -EAGAIN;
                }

                if (abort?.IsAborted == true)
                {
                    return -EINTR;
                }

                Monitor.Wait(_gate, LinuxPipe.SliceMs);
            }

            value = _semaphore ? 1 : _count;
            _count -= value;
            Monitor.PulseAll(_gate); // wartende write haben jetzt Platz
        }

        BitConverter.TryWriteBytes(dst.Slice(0, 8), value);
        return 8;
    }

    public long ReadInput(Span<byte> dst) => ReadAs(dst, null);

    public bool InputReady => PollEvents(LinuxPipe.PollIn) != 0;

    public bool WaitInput(long timeoutMs) => WaitPoll(LinuxPipe.PollIn, timeoutMs, null);

    public short PollEvents(short events)
    {
        lock (_gate)
        {
            return Ready(events);
        }
    }

    public bool WaitPoll(short events, long timeoutMs, ILinuxAbort? abort)
    {
        // Frist nach der Uhr wie LinuxPipe.WaitPoll (Stopwatch ist im Gast belegt, PulseAll weckt vorzeitig).
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_gate)
        {
            while (Ready(events) == 0)
            {
                long left = timeoutMs < 0 ? LinuxPipe.SliceMs : timeoutMs - LinuxPipe.ElapsedMs(start);
                if (left <= 0 || abort?.IsAborted == true)
                {
                    return false;
                }

                Monitor.Wait(_gate, (int)Math.Min(left, LinuxPipe.SliceMs));
            }

            return true;
        }
    }

    // Linux (eventfd_poll): POLLIN bei Zähler > 0, POLLOUT solange noch 1 dazupasst; POLLERR nur beim Überlauf (2^64 - 1, hier unerreichbar).
    private short Ready(short events)
    {
        short r = 0;
        if (_count > 0)
        {
            r |= LinuxPipe.PollIn;
        }

        if (_count < MaxCount)
        {
            r |= LinuxPipe.PollOut;
        }

        return (short)(r & events);
    }

    public void Close() => Interlocked.Exchange(ref _closed, 1);
}

/// <summary>
/// inotify (V20): Ladybird beobachtet damit die Zeitzone (TimeZoneWatcherUnix über FileWatcherInotify). ZehOS meldet keine
/// Dateiänderungen: Watches werden angelegt, geführt und entfernt, ein Ereignis entsteht nur beim Entfernen (IN_IGNORED wie Linux).
/// read wartet deshalb sonst bis zum Abbruch; poll meldet nie POLLOUT. Sperre wie <see cref="LinuxEventFd"/>.
/// </summary>
internal sealed class LinuxInotify : ILinuxPollable, ILinuxStatusFlags, ILinuxInput, ILinuxAnonFile
{
    /// <summary>fs.inotify.max_user_watches ist bei Linux größer; je Instanz reicht das für Ladybird bei weitem.</summary>
    public const int MaxWatches = 8192;

    public const uint InIgnored = 0x8000;

    private const int EventHeader = 16; // struct inotify_event ohne Namen: wd, mask, cookie, len
    private const long EAGAIN = 11, EINVAL = 22, EINTR = 4, EEXIST = 17, ENOSPC = 28, EBADF = 9;

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _byKey = new();
    private readonly Dictionary<int, (string Key, uint Mask)> _watches = new();
    private readonly Queue<(int Wd, uint Mask)> _events = new();
    private int _nextWd = 1;
    private int _closed;

    public LinuxInotify(bool nonBlocking) => NonBlocking = nonBlocking;

    public bool NonBlocking { get; set; }

    public int StatusFlags => (NonBlocking ? LinuxPipeEnd.ONonblock : 0); // O_RDONLY wie Linux (inotify_init1 öffnet nur lesend)

    /// <summary>Anzahl der Watches (Hosttests).</summary>
    public int WatchCount
    {
        get
        {
            lock (_gate)
            {
                return _watches.Count;
            }
        }
    }

    /// <summary>
    /// Legt einen Watch für <paramref name="key"/> (dieselbe Datei ergibt denselben Schlüssel) an oder ändert ihn. Wie Linux: IN_MASK_CREATE auf
    /// vorhandenem -EEXIST, IN_MASK_ADD verknüpft die Masken, sonst ersetzt die neue; liefert die Watch-Nummer.
    /// </summary>
    public long AddWatch(string key, uint mask, bool maskAdd, bool maskCreate)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out int wd))
            {
                if (maskCreate)
                {
                    return -EEXIST;
                }

                (string k, uint old) = _watches[wd];
                _watches[wd] = (k, maskAdd ? old | mask : mask);
                return wd;
            }

            if (_watches.Count >= MaxWatches)
            {
                return -ENOSPC;
            }

            // Linux vergibt Nummern zyklisch ab 1 (idr_alloc_cyclic); nach dem Umlauf überspringen wir belegte.
            while (_watches.ContainsKey(_nextWd) || _nextWd <= 0)
            {
                _nextWd = _nextWd <= 0 ? 1 : _nextWd + 1;
            }

            wd = _nextWd++;
            _watches[wd] = (key, mask);
            _byKey[key] = wd;
            return wd;
        }
    }

    /// <summary>inotify_rm_watch: -EINVAL ohne diesen Watch; sonst entfernt und IN_IGNORED angehängt.</summary>
    public long RemoveWatch(int wd)
    {
        lock (_gate)
        {
            if (!_watches.Remove(wd, out (string Key, uint Mask) w))
            {
                return -EINVAL;
            }

            _byKey.Remove(w.Key);
            _events.Enqueue((wd, InIgnored));
            Monitor.PulseAll(_gate);
        }

        return 0;
    }

    /// <summary>Linux 6.12: inotify ist nur lesend offen, write liefert -EBADF (vor der fehlenden write-Methode geprüft).</summary>
    public long Write(ReadOnlySpan<byte> data) => -EBADF;

    public long WriteAs(ReadOnlySpan<byte> data, ILinuxAbort? abort) => -EBADF;

    /// <summary>Ganze Ereignisse, so viele passen; passt das erste nicht, -EINVAL wie Linux.</summary>
    public long ReadAs(Span<byte> dst, ILinuxAbort? abort)
    {
        lock (_gate)
        {
            while (_events.Count == 0)
            {
                if (NonBlocking)
                {
                    return -EAGAIN;
                }

                if (abort?.IsAborted == true)
                {
                    return -EINTR;
                }

                Monitor.Wait(_gate, LinuxPipe.SliceMs);
            }

            if (dst.Length < EventHeader)
            {
                return -EINVAL;
            }

            int n = 0;
            while (_events.Count > 0 && dst.Length - n >= EventHeader)
            {
                (int wd, uint mask) = _events.Dequeue();
                Span<byte> e = dst.Slice(n, EventHeader);
                e.Clear();
                BitConverter.TryWriteBytes(e.Slice(0, 4), wd);
                BitConverter.TryWriteBytes(e.Slice(4, 4), mask);
                n += EventHeader;
            }

            return n;
        }
    }

    public long ReadInput(Span<byte> dst) => ReadAs(dst, null);

    public bool InputReady => PollEvents(LinuxPipe.PollIn) != 0;

    public bool WaitInput(long timeoutMs) => WaitPoll(LinuxPipe.PollIn, timeoutMs, null);

    public short PollEvents(short events)
    {
        lock (_gate)
        {
            return _events.Count > 0 ? (short)(events & LinuxPipe.PollIn) : (short)0;
        }
    }

    public bool WaitPoll(short events, long timeoutMs, ILinuxAbort? abort)
    {
        // Frist nach der Uhr wie LinuxPipe.WaitPoll (Stopwatch ist im Gast belegt, PulseAll weckt vorzeitig).
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_gate)
        {
            while ((_events.Count > 0 ? events & LinuxPipe.PollIn : 0) == 0)
            {
                long left = timeoutMs < 0 ? LinuxPipe.SliceMs : timeoutMs - LinuxPipe.ElapsedMs(start);
                if (left <= 0 || abort?.IsAborted == true)
                {
                    return false;
                }

                Monitor.Wait(_gate, (int)Math.Min(left, LinuxPipe.SliceMs));
            }

            return true;
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            lock (_gate)
            {
                _watches.Clear();
                _byKey.Clear();
                _events.Clear();
            }
        }
    }
}
