using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V22: Eine epoll-Instanz (epoll_create1). Hält die Interessenliste: je Eintrag Nummer, Deskriptor, Ereignismaske und die Benutzerdaten
/// aus <c>epoll_event.data</c>. Bereitschaft wird nicht wie bei Linux über Rückrufe der Deskriptoren gesammelt, sondern bei jedem
/// epoll_wait wie bei poll erfragt (<see cref="ILinuxPollable.PollEvents"/>); die Interessenliste ersetzt nur das Übergeben der Liste.
/// <para>
/// Grenzen gegenüber Linux (LINUX_ABI.md, V22): Ein Eintrag hängt an Nummer und Deskriptor; zeigt die Nummer nicht mehr auf denselben
/// Deskriptor (close, dup2 darüber), fällt der Eintrag weg, auch wenn ein dup derselben Datei noch offen ist (Linux behält ihn, bis die
/// letzte Nummer zu ist). EPOLLET meldet erneut bei neuen Ereignissen der Quelle (<see cref="ILinuxEdgeSource"/>: Pipe, eventfd,
/// Unix-Socket) oder beim Übergang zu bereit; andere Deskriptoren nur beim Übergang. Verschachteltes epoll, EPOLLEXCLUSIVE-Verteilung,
/// EPOLLWAKEUP und EPOLLPRI gibt es nicht.
/// </para>
/// <para>
/// Sperre: <see cref="_gate"/> schützt die Liste. Darin fragt epoll_wait die Bereitschaft ab und nimmt damit die Blattsperre des
/// Deskriptors (Pipe, eventfd, Socket-Link). Reihenfolge: FileGate des Prozesses vor <see cref="_gate"/> vor der Deskriptorsperre;
/// gewartet wird nie unter <see cref="_gate"/>. Ein Deskriptor nimmt nie eine epoll-Sperre, deshalb gibt es keinen Kreis.
/// </para>
/// </summary>
internal sealed class LinuxEpoll : ILinuxAnonFile
{
    public const uint EpollIn = 1, EpollPri = 2, EpollOut = 4, EpollErr = 8, EpollHup = 0x10, EpollRdHup = 0x2000, EpollExclusive = 1u << 28,
        EpollWakeup = 1u << 29, EpollOneshot = 1u << 30, EpollEt = 1u << 31;

    /// <summary>Größe von <c>struct epoll_event</c> auf x86-64 (gepackt: u32 events, u64 data).</summary>
    public const int EventSize = 12;

    /// <summary>Höchstzahl der Einträge je Instanz; darüber -ENOSPC wie bei erschöpftem max_user_watches.</summary>
    public const int MaxEntries = 4096;

    // Bits, die Bereitschaft beschreiben; der Rest sind Steuerbits des Eintrags.
    private const uint ReadyBits = EpollIn | EpollPri | EpollOut | EpollErr | EpollHup | EpollRdHup | 0x40 | 0x80 | 0x100 | 0x200;

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();
    private bool _closed;

    /// <summary>Anzahl der Einträge (Hosttests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Linux: write auf epoll -EINVAL.</summary>
    public long Write(ReadOnlySpan<byte> data) => -LinuxSyscalls.EINVAL;

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _entries.Clear();
        }
    }

    /// <summary>
    /// epoll_ctl nach der Prüfung der Nummern: ADD (-EEXIST, -ENOSPC), MOD und DEL (-ENOENT). <paramref name="p"/> dient nur dazu, veraltete
    /// Einträge (Nummer zeigt nicht mehr auf den Deskriptor) vorher zu verwerfen.
    /// </summary>
    public long Control(ILinuxProcess p, int op, int fd, ILinuxDescriptor d, uint events, ulong data)
    {
        lock (_gate)
        {
            DropStale(p);
            int i = _entries.FindIndex(e => e.Fd == fd);
            switch (op)
            {
                case 1: // EPOLL_CTL_ADD
                    if (i >= 0)
                    {
                        return -17; // EEXIST
                    }

                    if (_entries.Count >= MaxEntries || _closed)
                    {
                        return -28; // ENOSPC
                    }

                    _entries.Add(new Entry(fd, d, Normalize(events), data));
                    return 0;
                case 2: // EPOLL_CTL_DEL
                    if (i < 0)
                    {
                        return -2; // ENOENT
                    }

                    _entries.RemoveAt(i);
                    return 0;
                case 3: // EPOLL_CTL_MOD
                    if (i < 0)
                    {
                        return -2;
                    }

                    // Linux: EPOLLEXCLUSIVE lässt sich nicht nachträglich setzen oder auf einem solchen Eintrag ändern.
                    if (((events | _entries[i].Events) & EpollExclusive) != 0)
                    {
                        return -LinuxSyscalls.EINVAL;
                    }

                    // MOD macht den Eintrag scharf (ONESHOT) und vergisst die letzte Meldung: Linux fragt danach neu ab und meldet sofort,
                    // was bereit ist, auch flankengesteuert.
                    _entries[i] = new Entry(fd, d, Normalize(events), data);
                    return 0;
                default:
                    return -LinuxSyscalls.EINVAL;
            }
        }
    }

    // EPOLLERR und EPOLLHUP gelten immer (Linux: do_epoll_ctl setzt sie für ADD und MOD).
    private static uint Normalize(uint events) => events | EpollErr | EpollHup;

    private void DropStale(ILinuxProcess p) => _entries.RemoveAll(e => !ReferenceEquals(p.GetDescriptor(e.Fd), e.Descriptor));

    /// <summary>
    /// Ein Durchgang von epoll_wait ohne Warten: schreibt bis zu <paramref name="max"/> Ereignisse nach <paramref name="dst"/> (je
    /// <see cref="EventSize"/> Bytes) und liefert ihre Anzahl. Ohne Meldung nennt <paramref name="waitFor"/> den ersten nicht bereiten
    /// Eintrag, auf den sich warten lässt, <paramref name="waitables"/> die Zahl solcher Einträge und <paramref name="blind"/>, ob ein
    /// Eintrag bereit, aber nicht meldbar ist (EPOLLET ohne neues Ereignis): auf den lässt sich nicht warten, ohne sofort zu erwachen.
    /// </summary>
    public int Scan(ILinuxProcess p, Span<byte> dst, int max, out object? waitFor, out uint waitEvents, out int waitables, out bool blind)
    {
        waitFor = null;
        waitEvents = 0;
        waitables = 0;
        blind = false;
        int n = 0;
        lock (_gate)
        {
            DropStale(p);
            List<Entry>? requeue = null;
            for (int i = 0; i < _entries.Count && n < max; i++)
            {
                Entry e = _entries[i];
                if (e.Disarmed)
                {
                    continue;
                }

                uint ready = Ready(e.Descriptor, e.Events) & e.Events & ReadyBits;
                long seq = e.Descriptor is ILinuxEdgeSource source ? source.EdgeSeq : 0;
                bool report = ready != 0;
                if ((e.Events & EpollEt) != 0)
                {
                    // Flanke: neu bereite Bits oder ein neues Ereignis der Quelle seit der letzten Meldung.
                    report = ready != 0 && ((ready & ~e.LastReady) != 0 || seq != e.LastSeq);
                    blind |= ready != 0 && !report;
                    e.LastReady = ready;
                    e.LastSeq = seq;
                }

                if (report)
                {
                    BitConverter.TryWriteBytes(dst.Slice(n * EventSize, 4), ready);
                    BitConverter.TryWriteBytes(dst.Slice((n * EventSize) + 4, 8), e.Data);
                    n++;
                    if ((e.Events & EpollOneshot) != 0)
                    {
                        e.Disarmed = true; // bis zum nächsten MOD stumm
                    }
                    else if ((e.Events & EpollEt) == 0)
                    {
                        // Pegelgesteuert ans Ende, wie Linux die Bereitliste wieder anhängt: Bei maxevents kleiner als die Zahl bereiter
                        // Einträge kommen beim nächsten Aufruf die anderen zuerst.
                        (requeue ??= new List<Entry>()).Add(e);
                    }
                }
                else if (ready == 0 && (e.Events & (EpollIn | EpollOut)) != 0 && e.Descriptor is ILinuxPollable or ILinuxInput)
                {
                    waitables++;
                    if (waitFor == null)
                    {
                        waitFor = e.Descriptor;
                        waitEvents = e.Events;
                    }
                }
            }

            if (requeue != null)
            {
                foreach (Entry e in requeue)
                {
                    _entries.Remove(e);
                    _entries.Add(e);
                }
            }
        }

        return n;
    }

    /// <summary>Bereitschaft eines Deskriptors wie in poll (<c>PollScan</c>): Pipes, Sockets, eventfd selbst, Eingaben über InputReady.</summary>
    private static uint Ready(ILinuxDescriptor d, uint events)
    {
        if (d is ILinuxPollable pollable)
        {
            return (ushort)pollable.PollEvents((short)(events & (EpollIn | EpollOut | EpollRdHup)));
        }

        uint r = events & EpollOut; // Terminal und andere Ausgaben schreiben immer
        if ((events & EpollIn) != 0 && d is ILinuxInput input && input.InputReady)
        {
            r |= EpollIn;
        }

        return r;
    }

    /// <summary>Ein Eintrag der Interessenliste; veränderlich nur unter <see cref="_gate"/>.</summary>
    private sealed class Entry
    {
        public Entry(int fd, ILinuxDescriptor d, uint events, ulong data)
        {
            Fd = fd;
            Descriptor = d;
            Events = events;
            Data = data;
            LastSeq = -1;
        }

        public int Fd { get; }

        public ILinuxDescriptor Descriptor { get; }

        public uint Events { get; }

        public ulong Data { get; }

        /// <summary>EPOLLONESHOT nach der Meldung.</summary>
        public bool Disarmed { get; set; }

        /// <summary>EPOLLET: Bereitschaft und Ereigniszähler beim letzten Durchgang.</summary>
        public uint LastReady { get; set; }

        public long LastSeq { get; set; }
    }
}

/// <summary>
/// V22 epoll: epoll_create/epoll_create1 und epoll_ctl laufen unter der Dateisperre (<see cref="DispatchSoft"/>), epoll_wait,
/// epoll_pwait und epoll_pwait2 ohne sie (<see cref="DispatchMore"/>), weil sie warten. Anlass: LibCore von Ladybird wartet über
/// epoll, wenn es verfügbar ist; s3 pipe_epoll prüft den Umfang.
/// </summary>
internal static partial class LinuxSyscalls
{
    public const long EpollCreate = 213, EpollWait = 232, EpollCtl = 233, EpollPwait = 281, EpollCreate1 = 291, EpollPwait2 = 441;

    // maxevents-Obergrenze wie Linux (EP_MAX_EVENTS = INT_MAX / sizeof(struct epoll_event)).
    private const int EpollMaxEvents = int.MaxValue / LinuxEpoll.EventSize;

    // Größter Puffer, den epoll_wait auf einmal füllt: mehr Einträge als MaxEntries kann eine Instanz nicht melden.
    private const int EpollMaxBatch = LinuxEpoll.MaxEntries;

    // TASK_SIZE_MAX auf x86-64 mit 4 Ebenen: Ende des Benutzeradressraums (access_ok).
    private const ulong UserSpaceEnd = 0x0000_7FFF_FFFF_F000;

    /// <summary>epoll_create(size): size muss positiv sein, sonst wie epoll_create1(0).</summary>
    private static long DoEpollCreate(ILinuxFiles f, int size) => size <= 0 ? -EINVAL : DoEpollCreate1(f, 0);

    /// <summary>epoll_create1(flags): nur EPOLL_CLOEXEC (gleich O_CLOEXEC), sonst -EINVAL.</summary>
    private static long DoEpollCreate1(ILinuxFiles f, int flags)
    {
        if ((flags & ~SoftOCloexec) != 0)
        {
            return -EINVAL;
        }

        return AddAnon(f, new LinuxEpoll(), (flags & SoftOCloexec) != 0);
    }

    /// <summary>
    /// epoll_ctl(epfd, op, fd, event) in der Reihenfolge von Linux' do_epoll_ctl: erst das Ereignis lesen (außer DEL, -EFAULT), dann
    /// beide Nummern (-EBADF), dann ob sich der Zieldeskriptor abfragen lässt (reguläre Dateien -EPERM), dann epfd ist epoll und nicht das Ziel
    /// (-EINVAL), dann die Operation.
    /// </summary>
    private static long DoEpollCtl(ILinuxProcess p, int epfd, int op, int fd, ulong eventPtr)
    {
        uint events = 0;
        ulong data = 0;
        if (op != 2)
        {
            Span<byte> ev = stackalloc byte[LinuxEpoll.EventSize];
            if (!p.Memory.TryRead(eventPtr, ev))
            {
                return -EFAULT;
            }

            events = BitConverter.ToUInt32(ev);
            data = BitConverter.ToUInt64(ev[4..]);
        }

        ILinuxDescriptor? ed = p.GetDescriptor(epfd);
        ILinuxDescriptor? td = p.GetDescriptor(fd);
        if (ed is null || td is null)
        {
            return -EBADF;
        }

        if (td is ILinuxFile)
        {
            return -EPERM; // Linux: reguläre Dateien und Verzeichnisse haben kein poll
        }

        // Verschachteltes epoll unterstützt ZehOS nicht (Linux erlaubt es); auch dafür -EINVAL wie für epoll auf sich selbst.
        if (ed is not LinuxEpoll ep || ReferenceEquals(ed, td) || td is LinuxEpoll)
        {
            return -EINVAL;
        }

        // EPOLLEXCLUSIVE nur mit ADD und nur zusammen mit Ereignis- und Wakeup-Bits (Linux: ep_op_has_event, EPOLLEXCLUSIVE_OK_BITS).
        if ((events & LinuxEpoll.EpollExclusive) != 0 && op == 1
            && (events & ~(LinuxEpoll.EpollIn | LinuxEpoll.EpollOut | LinuxEpoll.EpollErr | LinuxEpoll.EpollHup | LinuxEpoll.EpollWakeup
                | LinuxEpoll.EpollEt | LinuxEpoll.EpollExclusive | 0x40 | 0x100)) != 0)
        {
            return -EINVAL;
        }

        return ep.Control(p, op, fd, td, events, data);
    }

    /// <summary>epoll_pwait(epfd, events, maxevents, timeout, sigmask, sigsetsize); epoll_wait ist dasselbe ohne Maske.</summary>
    private static long DoEpollPwait(ILinuxProcess p, int epfd, ulong events, int maxEvents, int timeoutMs, ulong maskPtr, ulong maskSize)
        => EpollWithMask(p, epfd, events, maxEvents, timeoutMs < 0 ? -1 : timeoutMs, maskPtr, maskSize);

    /// <summary>epoll_pwait2: Frist als timespec (NULL: ohne Frist), sonst wie epoll_pwait; Aufrunden wie ppoll.</summary>
    private static long DoEpollPwait2(ILinuxProcess p, int epfd, ulong events, int maxEvents, ulong timeoutPtr, ulong maskPtr, ulong maskSize)
    {
        long timeoutMs = -1;
        if (timeoutPtr != 0)
        {
            Span<byte> ts = stackalloc byte[16];
            if (!p.Memory.TryRead(timeoutPtr, ts))
            {
                return -EFAULT;
            }

            long sec = BitConverter.ToInt64(ts);
            long nsec = BitConverter.ToInt64(ts[8..]);
            if (sec < 0 || nsec < 0 || nsec >= 1_000_000_000)
            {
                return -EINVAL;
            }

            timeoutMs = sec >= PpollMaxMs / 1000 ? PpollMaxMs : (sec * 1000) + ((nsec + 999_999) / 1_000_000);
        }

        return EpollWithMask(p, epfd, events, maxEvents, timeoutMs, maskPtr, maskSize);
    }

    // Wartemaske wie ppoll (DoPpoll): gilt nur während des Wartens; bei -EINTR bleibt sie, bis die Brücke die Handler angesprungen hat.
    private static long EpollWithMask(ILinuxProcess p, int epfd, ulong events, int maxEvents, long timeoutMs, ulong maskPtr, ulong maskSize)
    {
        LinuxThread? thread = null;
        if (maskPtr != 0)
        {
            if (maskSize != 8)
            {
                return -EINVAL;
            }

            Span<byte> m = stackalloc byte[8];
            if (!p.Memory.TryRead(maskPtr, m))
            {
                return -EFAULT;
            }

            if (p is LinuxUserProcess up)
            {
                thread = up.CurrentThread;
                up.BeginWaitMask(thread, BitConverter.ToUInt64(m));
            }
        }

        long r = DoEpollWait(p, epfd, events, maxEvents, timeoutMs);
        if (thread != null && r != -EINTR)
        {
            ((LinuxUserProcess)p).EndWaitMask(thread);
        }

        return r;
    }

    /// <summary>
    /// epoll_wait wie Linux' do_epoll_wait: maxevents außerhalb 1..EP_MAX_EVENTS -EINVAL, Zielpuffer außerhalb des Benutzerbereichs -EFAULT, epfd
    /// -EBADF oder kein epoll -EINVAL. Gewartet wird wie in poll (<see cref="DoPoll"/>): auf den ersten nicht bereiten Eintrag, bei einem die
    /// ganze Frist in Scheiben, bei mehreren in kurzen Scheiben mit neuem Durchgang. Die Scheibe ist auch bei einem Eintrag begrenzt, damit ein
    /// epoll_ctl aus einem anderen Thread (neuer Eintrag, der schon bereit ist) spätestens nach <see cref="LinuxPipe.SliceMs"/> gesehen wird.
    /// Ohne Eintrag, auf den sich warten lässt, schläft der Aufruf in Scheiben (Host) und fragt neu ab. Ein Signal ohne bereites Ereignis
    /// liefert -EINTR (auch mit SA_RESTART, wie Linux).
    /// </summary>
    private static long DoEpollWait(ILinuxProcess p, int epfd, ulong events, int maxEvents, long timeoutMs)
    {
        if (maxEvents <= 0 || maxEvents > EpollMaxEvents)
        {
            return -EINVAL;
        }

        // Linux prüft den Zielpuffer nur mit access_ok (Adressbereich des Benutzers), nicht ob er abgebildet ist: Ohne Ereignis liefert
        // epoll_wait auf einen ungültigen, aber niedrigen Zeiger 0 (gegen Linux 6.12 geprüft); -EFAULT erst beim Zurückschreiben.
        int max = Math.Min(maxEvents, EpollMaxBatch);
        ulong bytes = (ulong)maxEvents * LinuxEpoll.EventSize;
        if (events + bytes < events || events + bytes > UserSpaceEnd)
        {
            return -EFAULT;
        }

        ILinuxDescriptor? d = p.GetDescriptor(epfd);
        if (d is null)
        {
            return -EBADF;
        }

        if (d is not LinuxEpoll ep)
        {
            return -EINVAL;
        }

        byte[] buf = new byte[max * LinuxEpoll.EventSize];
        long start = Stopwatch.GetTimestamp();
        bool first = true;
        int n;
        while (true)
        {
            n = ep.Scan(p, buf, max, out object? waitFor, out uint waitEvents, out int waitables, out bool blind);
            if (n > 0 || timeoutMs == 0 || LinuxInterrupt.Check(p as ILinuxAbort) || p is ILinuxSignalWake { SignalWaiting: true })
            {
                break;
            }

            // Die erste Scheibe ohne abgezogene Zeit wie in poll (die bis hierher verstrichene gehört nicht zum Warten).
            long left = timeoutMs < 0 ? -1 : (first ? timeoutMs : timeoutMs - LinuxPipe.ElapsedMs(start));
            first = false;
            if (timeoutMs >= 0 && left <= 0)
            {
                break;
            }

            long cap = waitables == 1 && !blind ? LinuxPipe.SliceMs : PollMultiSliceMs;
            long slice = left < 0 ? cap : Math.Min(cap, left);
            if (waitFor is ILinuxPollable pollable)
            {
                _ = pollable.WaitPoll((short)(waitEvents & (LinuxEpoll.EpollIn | LinuxEpoll.EpollOut)), slice, p as ILinuxAbort);
            }
            else if (waitFor is ILinuxInput input)
            {
                _ = input.WaitInput(slice);
            }
            else if (p is ILinuxHost host)
            {
                host.SleepNanos(slice * 1_000_000L);
            }
            else
            {
                break; // ohne Host lässt sich nicht schlafen (Ersatzprozesse der Tests): nicht drehen
            }
        }

        if (n == 0 && p is ILinuxSignalWake { SignalWaiting: true })
        {
            return -EINTR;
        }

        if (n > 0 && !p.Memory.TryWrite(events, buf.AsSpan(0, n * LinuxEpoll.EventSize)))
        {
            return -EFAULT;
        }

        return n;
    }
}
