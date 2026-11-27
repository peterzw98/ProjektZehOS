using System;
using System.Collections.Generic;
using System.Threading;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Optional neben <see cref="ILinuxFiles"/> (V16): Verweise auf offene Dateien, die in einer Unix-Socket-Nachricht unterwegs sind
/// (SCM_RIGHTS). Linux zählt sie in <c>struct file.f_count</c> mit; ohne das schlösse ein close des Senders die Datei, bevor der Empfänger
/// sie bekommt. Beide Methoden setzen voraus, dass der Aufrufer <see cref="ILinuxFiles.FileGate"/> hält.
/// </summary>
internal interface ILinuxInFlight
{
    object FileGate { get; }

    /// <summary>Ein Verweis mehr auf <paramref name="d"/> (die Nachricht hält die Datei offen).</summary>
    void HoldInFlight(ILinuxDescriptor d);

    /// <summary>Ein Verweis weniger; war es der letzte im Lauf, wird die Datei geschlossen (wie close der letzten Nummer).</summary>
    void ReleaseInFlight(ILinuxDescriptor d);
}

/// <summary>Dateien einer Nachricht samt dem Lauf, dessen Zähler sie hält; das Freigeben braucht dessen Dateisperre.</summary>
internal sealed class LinuxSocketFds
{
    public LinuxSocketFds(ILinuxDescriptor[] files, ILinuxInFlight owner)
    {
        Files = files;
        Owner = owner;
    }

    public ILinuxDescriptor[] Files { get; }

    public ILinuxInFlight Owner { get; }

    /// <summary>Gibt die unterwegs gehaltenen Verweise ab <paramref name="from"/> frei (verworfen oder nicht zugestellt). Nimmt die Dateisperre.</summary>
    public void Release(int from = 0)
    {
        lock (Owner.FileGate)
        {
            for (int i = from; i < Files.Length; i++)
            {
                Owner.ReleaseInFlight(Files[i]);
            }
        }
    }
}

/// <summary>
/// Ende eines Unix-Sockets (V16, AF_UNIX SOCK_STREAM und SOCK_SEQPACKET): Ladybird verbindet Browser und WebContent über ein
/// socketpair und reicht Dateien (Bildpuffer, weitere Sockets) mit SCM_RIGHTS weiter. Jede Richtung ist ein eigener Kanal mit
/// Nachrichten bis <see cref="Capacity"/> Bytes; eine Nachricht trägt Bytes und höchstens eine Liste Dateien.
/// <para>
/// Stream: Lesen fasst Nachrichten zusammen und hält nach der ersten Nachricht mit Dateien an (wie unix_stream_read_generic in Linux 6.12:
/// ein Lesen liefert höchstens eine Dateiliste, zusammen mit den Bytes ihrer Nachricht). Seqpacket: ein Lesen ist genau eine Nachricht; was nicht in den Puffer passt, ist verloren (MSG_TRUNC).
/// </para>
/// <para>
/// Nebenläufigkeit: <see cref="Link.Gate"/> ist eine Blattsperre, die beide Enden eines Paars teilen. Darin werden nur Kanäle bewegt;
/// Benutzerspeicher und Dateitabelle bearbeitet der Verteiler davor oder danach. Reihenfolge: FileGate vor Gate (close unter der
/// Dateisperre schließt ein Ende); Senden und Empfangen halten die Dateisperre nicht, während sie warten. Gewartet wird in Scheiben von
/// <see cref="LinuxPipe.SliceMs"/> wie bei der Pipe, damit ein Abbruch jeden Wartenden erreicht. Der Abbruch ist der des aufrufenden
/// Prozesses, nicht des erzeugenden: Über SCM_RIGHTS und vfork benutzen auch andere Prozesse das Ende, und der Erzeuger darf vorher enden.
/// </para>
/// </summary>
internal sealed partial class LinuxUnixSocket : ILinuxPollable, ILinuxInput, ILinuxStatusFlags, ILinuxEdgeSource
{
    public const int SockStream = 1, SockDgram = 2, SockSeqpacket = 5;

    /// <summary>Bytes je Richtung, wie Linux' Vorgabe für SO_SNDBUF (net.core.wmem_default).</summary>
    public const int Capacity = 212992;

    /// <summary>Linux net.unix.max_dgram_qlen: so viele Datagramme warten höchstens beim Empfänger, auch leere (sonst unbegrenzter Kernelspeicher).</summary>
    public const int MaxDgramQueue = 10;

    private bool DgramQueueFull(Channel ch) => IsDgram && ch.Records.Count >= MaxDgramQueue;

    /// <summary>SCM_MAX_FD: höchstens so viele Dateien je Nachricht.</summary>
    public const int MaxFds = 253;

    public const short PollRdHup = 0x2000;

    private const long EAGAIN = 11, EINTR = 4, EPIPE = 32, EMSGSIZE = 90, ENOTCONN = 107, ECONNRESET = 104;

    private readonly object _idle = new();
    private Link? _link;
    private bool _sideA;
    private int _closed;

    // sk_err wie bei Linux: ECONNRESET, wenn die Gegenseite mit ungelesenen Daten geschlossen hat. Einmal gemeldet (send, recv ohne Daten,
    // SO_ERROR), danach EPIPE beziehungsweise EOF. Nur unter Link.Gate.
    private long _pendingError;

    public LinuxUnixSocket(int type, bool nonBlocking, int creatorPid = 0)
    {
        if (type != SockStream && type != SockSeqpacket && type != SockDgram)
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        Type = type;
        NonBlocking = nonBlocking;
        CreatorPid = creatorPid;
    }

    /// <summary>SOCK_STREAM, SOCK_SEQPACKET oder SOCK_DGRAM.</summary>
    public int Type { get; }

    /// <summary>O_NONBLOCK; gehört wie bei Linux zur offenen Datei, dup und SCM_RIGHTS teilen es.</summary>
    public bool NonBlocking { get; set; }

    /// <summary>Flags für F_GETFL: O_RDWR, dazu O_NONBLOCK.</summary>
    public int StatusFlags => 2 | (NonBlocking ? LinuxPipeEnd.ONonblock : 0);

    /// <summary>PID des Erzeugers (SO_PEERCRED der Gegenseite bei socketpair, wie Linux).</summary>
    public int CreatorPid { get; }

    /// <summary>Gegenseite, solange verbunden (auch nach deren close bleibt sie eingetragen; die Kanäle melden das Ende).</summary>
    public LinuxUnixSocket? Peer { get; private set; }

    public bool IsConnected => Volatile.Read(ref _link) != null;

    /// <summary>getsockopt SO_ERROR: liefert den anstehenden Fehler (positiv) und löscht ihn wie Linux.</summary>
    public int TakeError()
    {
        Link? l = Volatile.Read(ref _link);
        if (l == null)
        {
            return TakeConnectError(); // V27: verweigertes nichtblockierendes connect (AF_INET)
        }

        lock (l.Gate)
        {
            int err = (int)_pendingError;
            _pendingError = 0;
            return err;
        }
    }

    private bool IsStream => Type == SockStream;

    // V23: SOCK_DGRAM (nur socketpair, verbunden) ist wie Seqpacket nachrichtenorientiert, kennt aber kein EOF beim close der Gegenseite
    // (Empfang wartet weiter) und meldet beim Senden dorthin ECONNREFUSED statt EPIPE, ohne ECONNRESET (Linux unix_release_sock/unix_dgram_sendmsg).
    private bool IsDgram => Type == SockDgram;

    /// <summary>Hat die Gegenseite geschlossen (nicht nur shutdown)? Nur für Datagramm-Sockets von Belang.</summary>
    private bool PeerClosed => Peer is { } p && Volatile.Read(ref p._closed) != 0;

    /// <summary>Nachrichtenorientiert (Seqpacket, Dgram): ein Lesen ist genau eine Nachricht.</summary>
    public bool IsMessage => Type != SockStream;

    /// <summary>Beim letzten close vor dem Schließen der Kanäle; ohne Umsetzung entfällt der Aufruf.</summary>
    partial void OnLastClose();

    /// <summary>poll eines lauschenden Sockets: setzt <paramref name="listening"/> und die Ereignisse; ohne Umsetzung entfällt der Aufruf.</summary>
    partial void ListenEvents(short events, ref short result, ref bool listening);

    /// <summary>socketpair: zwei verbundene Enden desselben Typs.</summary>
    public static (LinuxUnixSocket A, LinuxUnixSocket B) CreatePair(int type, bool nonBlocking, int creatorPid = 0)
    {
        var a = new LinuxUnixSocket(type, nonBlocking, creatorPid);
        var b = new LinuxUnixSocket(type, nonBlocking, creatorPid);
        Connect(a, b);
        return (a, b);
    }

    /// <summary>
    /// Verbindet zwei noch unverbundene Enden gleichen Typs (socketpair, connect/accept). Vorher sieht niemand die Kanäle
    /// (unverbunden: ENOTCONN), deshalb reicht das Veröffentlichen mit <see cref="Volatile.Write{T}(ref T, T)"/>.
    /// </summary>
    public static void Connect(LinuxUnixSocket a, LinuxUnixSocket b)
    {
        if (a.Type != b.Type || a.IsConnected || b.IsConnected || ReferenceEquals(a, b))
        {
            throw new InvalidOperationException("Unix-Sockets nur einmal und paarweise gleichen Typs verbinden");
        }

        var link = new Link();
        a._sideA = true;
        b._sideA = false;
        a.Peer = b;
        b.Peer = a;
        Volatile.Write(ref a._link, link);
        Volatile.Write(ref b._link, link);
    }

    private Channel Outgoing(Link l) => _sideA ? l.AtoB : l.BtoA;

    private Channel Incoming(Link l) => _sideA ? l.BtoA : l.AtoB;

    private static bool Aborted(ILinuxAbort? abort) => LinuxInterrupt.Check(abort); // V18: auch ein wartendes Signal (-EINTR)

    /// <summary>
    /// Sendet <paramref name="data"/>; <paramref name="fds"/> reisen mit der ersten Nachricht (<paramref name="fdsTaken"/>: übernommen,
    /// sonst gibt der Aufrufer sie frei). Stream: blockierend alles, O_NONBLOCK so viel wie passt; Seqpacket: ganz oder gar nicht,
    /// länger als <see cref="Capacity"/> -EMSGSIZE. -EPIPE nach dem Ende der Gegenseite oder shutdown, -ENOTCONN unverbunden, -EINTR beim
    /// Abbruch; wurde schon etwas gesendet, zählt das.
    /// </summary>
    public long Send(ReadOnlySpan<byte> data, LinuxSocketFds? fds, bool nonBlocking, ILinuxAbort? abort, out bool fdsTaken)
    {
        fdsTaken = false;
        Link? l = Volatile.Read(ref _link);
        if (l == null)
        {
            return IsInetDgram ? SendDatagramToPeer(data) : -ENOTCONN; // V27: UDP hat keine Verbindung, nur eine Gegenstelle
        }

        if (!IsStream && data.Length > Capacity)
        {
            return -EMSGSIZE;
        }

        if (IsStream && data.IsEmpty)
        {
            return 0; // Linux sendet bei Länge 0 auf einem Stream nichts, auch keine Dateien
        }

        int done = 0;
        lock (l.Gate)
        {
            Channel ch = Outgoing(l);
            while (true)
            {
                if (done == 0 && _pendingError != 0)
                {
                    long err = _pendingError;
                    _pendingError = 0;
                    return -err;
                }

                if (ch.WriteClosed)
                {
                    return done > 0 ? done : -EPIPE; // eigenes SHUT_WR geht vor ECONNREFUSED (Linux: sk_shutdown & SEND_SHUTDOWN zuerst)
                }

                if (IsDgram && PeerClosed)
                {
                    return -ECONNREFUSED;
                }

                if (ch.ReadClosed)
                {
                    return done > 0 ? done : -EPIPE;
                }

                if (Aborted(abort))
                {
                    return done > 0 ? done : -EINTR;
                }

                int space = Capacity - ch.Bytes;
                int want = data.Length - done;
                if (IsStream ? space > 0 : space >= want && !DgramQueueFull(ch))
                {
                    int take = Math.Min(space, want);
                    ch.Records.Enqueue(new Record(data.Slice(done, take).ToArray(), fdsTaken ? null : fds));
                    fdsTaken = true;
                    ch.Bytes += take;
                    ch.Seq++;
                    done += take;
                    Monitor.PulseAll(l.Gate); // Daten für wartende Leser und poll
                    if (done == data.Length)
                    {
                        return done;
                    }

                    continue;
                }

                if (nonBlocking)
                {
                    return done > 0 ? done : -EAGAIN;
                }

                Monitor.Wait(l.Gate, LinuxPipe.SliceMs);
            }
        }
    }

    /// <summary>
    /// Empfängt in <paramref name="dst"/>. Stream: verfügbare Bytes (mit <paramref name="waitAll"/> bis <paramref name="dst"/> voll, EOF oder
    /// Dateien); Seqpacket: eine Nachricht, <paramref name="fullLength"/> ist ihre ganze Länge. <paramref name="fds"/>: mitgekommene Dateien,
    /// deren Verweise jetzt der Aufrufer hält (bei <paramref name="peek"/> bleiben sie in der Nachricht, der Aufrufer gibt nichts frei).
    /// 0 am Ende (Gegenseite zu oder shutdown), -EAGAIN ohne Daten bei O_NONBLOCK, -EINTR beim Abbruch, -ENOTCONN unverbunden.
    /// </summary>
    public long Receive(Span<byte> dst, bool peek, bool waitAll, bool nonBlocking, ILinuxAbort? abort, out LinuxSocketFds? fds, out int fullLength)
    {
        fds = null;
        fullLength = 0;
        Link? l = Volatile.Read(ref _link);
        if (l == null)
        {
            return IsInetDgram ? ReceiveDatagram(dst, peek, nonBlocking, abort, out fullLength, out _, out _) : -ENOTCONN;
        }

        if (IsStream && dst.IsEmpty)
        {
            return 0;
        }

        int got = 0;
        lock (l.Gate)
        {
            Channel ch = Incoming(l);
            while (true)
            {
                if (ch.Records.Count > 0)
                {
                    bool stop = IsStream ? TakeStream(ch, dst, ref got, peek, ref fds) : TakeRecord(ch, dst, ref got, peek, out fds, out fullLength);
                    if (!peek)
                    {
                        Monitor.PulseAll(l.Gate); // Platz für wartende Sender
                    }

                    if (stop || !waitAll || peek || got == dst.Length)
                    {
                        return got;
                    }

                    continue;
                }

                if (got == 0 && _pendingError != 0)
                {
                    long err = _pendingError;
                    _pendingError = 0;
                    return -err;
                }

                if ((ch.WriteClosed || ch.ReadClosed) && !(IsDgram && PeerClosed && !ch.ReadClosed))
                {
                    return got;
                }

                if (Aborted(abort))
                {
                    return got > 0 ? got : -EINTR;
                }

                if (nonBlocking)
                {
                    return got > 0 ? got : -EAGAIN;
                }

                Monitor.Wait(l.Gate, LinuxPipe.SliceMs);
            }
        }
    }

    /// <summary>Stream: Bytes aus den vorderen Nachrichten; true, wenn nach einer Nachricht mit Dateien angehalten wurde. Nur unter Gate.</summary>
    private static bool TakeStream(Channel ch, Span<byte> dst, ref int got, bool peek, ref LinuxSocketFds? fds)
    {
        bool stopped = false;
        foreach (Record r in ch.Records)
        {
            if (got == dst.Length)
            {
                break;
            }

            LinuxSocketFds? carried = r.Fds;
            int n = Math.Min(dst.Length - got, r.Data.Length - r.Offset);
            r.Data.AsSpan(r.Offset, n).CopyTo(dst.Slice(got));
            got += n;
            if (!peek)
            {
                r.Offset += n;
                ch.Bytes -= n;
                r.Fds = null; // übergeben; der Rest der Nachricht trägt sie nicht mehr
            }

            if (carried != null)
            {
                fds = carried;
                stopped = true; // Linux: nach Dateien endet das Lesen, die nächste Liste kommt mit dem nächsten Aufruf
                break;
            }
        }

        if (!peek)
        {
            while (ch.Records.Count > 0 && ch.Records.Peek().Offset == ch.Records.Peek().Data.Length)
            {
                _ = ch.Records.Dequeue();
            }
        }

        return stopped;
    }

    /// <summary>Seqpacket: genau die vorderste Nachricht. Nur unter Gate.</summary>
    private static bool TakeRecord(Channel ch, Span<byte> dst, ref int got, bool peek, out LinuxSocketFds? fds, out int fullLength)
    {
        Record r = peek ? ch.Records.Peek() : ch.Records.Dequeue();
        fullLength = r.Data.Length;
        int n = Math.Min(dst.Length, r.Data.Length);
        r.Data.AsSpan(0, n).CopyTo(dst);
        got = n;
        fds = r.Fds;
        if (!peek)
        {
            ch.Bytes -= r.Data.Length;
        }

        return true;
    }

    /// <summary>shutdown: 0 = SHUT_RD, 1 = SHUT_WR, 2 = beides. -ENOTCONN unverbunden; ungültiges <paramref name="how"/> prüft der Verteiler.</summary>
    public long Shutdown(int how)
    {
        Link? l = Volatile.Read(ref _link);
        if (l == null)
        {
            return -ENOTCONN;
        }

        lock (l.Gate)
        {
            if (how != 1)
            {
                Incoming(l).ReadClosed = true; // Gegenseite bekommt EPIPE; Gepuffertes bleibt lesbar, danach EOF
            }

            if (how != 0)
            {
                Outgoing(l).WriteClosed = true; // Gegenseite liest den Rest, dann EOF; eigenes Senden -EPIPE
                Outgoing(l).Seq++;
            }

            Monitor.PulseAll(l.Gate);
        }

        return 0;
    }

    /// <summary>
    /// Beim letzten Verweis (close der letzten Nummer, auch unterwegs gehaltene): beide Richtungen zu; Ungelesenes verfällt wie bei Linux,
    /// mitgeschickte Dateien werden freigegeben. Der Aufrufer hält die Dateisperre oder keine Sperre; ein zweiter Aufruf ändert nichts.
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        OnLastClose(); // V16 senior2 (LinuxUnixSocket.Listen.cs): Namen freigeben, Wartende eines Listeners wecken

        Link? l = Volatile.Read(ref _link);
        if (l == null)
        {
            return;
        }

        List<LinuxSocketFds>? dropped = null;
        lock (l.Gate)
        {
            Channel incoming = Incoming(l);
            Outgoing(l).WriteClosed = true;
            Outgoing(l).Seq++;
            incoming.ReadClosed = true;
            if (!IsDgram && incoming.Records.Count > 0 && Peer != null)
            {
                Peer._pendingError = ECONNRESET; // wie unix_release_sock: Ungelesenes verworfen, die Gegenseite erfährt es einmal
            }
            foreach (Record r in incoming.Records)
            {
                if (r.Fds != null)
                {
                    (dropped ??= new List<LinuxSocketFds>()).Add(r.Fds);
                }
            }

            incoming.Records.Clear();
            incoming.Bytes = 0;
            Monitor.PulseAll(l.Gate);
        }

        // Außerhalb von Gate (Reihenfolge FileGate vor Gate): Ein freigegebenes Socket kann selbst Dateien halten, die Kette endet bei
        // Dateien ohne weitere Verweise. Zyklen (ein Socket trägt sich selbst) bleiben offen: Linux räumt sie per Garbage Collector ab,
        // hier nicht (LINUX_ABI.md).
        if (dropped != null)
        {
            foreach (LinuxSocketFds f in dropped)
            {
                f.Release();
            }
        }
    }

    /// <summary>V22: Ereigniszähler der eingehenden Richtung; unverbunden und lauschend 0 (epoll meldet dann nur Übergänge).</summary>
    public long EdgeSeq
    {
        get
        {
            Link? l = Volatile.Read(ref _link);
            if (l == null)
            {
                return IsInetDgram ? DatagramSeq : 0;
            }

            lock (l.Gate)
            {
                return Incoming(l).Seq;
            }
        }
    }

    /// <summary>Bereitschaft wie Linux' unix_poll; unverbunden nur schreibbereit.</summary>
    public short PollEvents(short events)
    {
        // Ein lauschendes Socket (listen) beantwortet poll selbst (POLLIN bei wartender Verbindung); Teil von senior2.
        short listen = 0;
        bool listening = false;
        ListenEvents(events, ref listen, ref listening);
        if (listening)
        {
            return listen;
        }

        Link? l = Volatile.Read(ref _link);
        if (l == null)
        {
            return UnconnectedEvents(events); // schreibbereit; V27: UDP-Eingang, verweigertes connect
        }

        lock (l.Gate)
        {
            return EventsLocked(l, events);
        }
    }

    private short EventsLocked(Link l, short events)
    {
        Channel inc = Incoming(l);
        Channel outc = Outgoing(l);
        bool rcvShut = (inc.WriteClosed && !(IsDgram && PeerClosed)) || inc.ReadClosed;
        bool sndShut = outc.WriteClosed || outc.ReadClosed;
        short r = _pendingError != 0 ? LinuxPipe.PollErr : (short)0;
        if (inc.Records.Count > 0 || rcvShut)
        {
            r |= (short)(events & LinuxPipe.PollIn);
        }

        if (rcvShut)
        {
            r |= (short)(events & PollRdHup);
        }

        if (rcvShut && sndShut)
        {
            r |= LinuxPipe.PollHup; // wie Linux: beide Richtungen zu, auch ungefragt
        }

        // Linux: schreibbereit, solange höchstens ein Viertel des Sendepuffers belegt ist; nach dem Ende der Gegenseite auch (write: EPIPE).
        if (sndShut || (outc.Bytes * 4L <= Capacity && !DgramQueueFull(outc)))
        {
            r |= (short)(events & LinuxPipe.PollOut);
        }

        return r;
    }

    public bool WaitPoll(short events, long timeoutMs, ILinuxAbort? abort)
    {
        Link? l = Volatile.Read(ref _link);
        if (l == null)
        {
            // Unverbunden ändert sich hier nichts, was weckt (connect gehört dem Verbindenden); eine Scheibe schlafen, poll fragt neu.
            // Monitor.Wait statt Thread.Sleep, wie die übrigen Wartenden im Kernel.
            lock (_idle)
            {
                Monitor.Wait(_idle, (int)(timeoutMs < 0 ? LinuxPipe.SliceMs : Math.Min(LinuxPipe.SliceMs, timeoutMs)));
            }

            return PollEvents(events) != 0;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (l.Gate)
        {
            while (EventsLocked(l, events) == 0)
            {
                long left = timeoutMs < 0 ? LinuxPipe.SliceMs : timeoutMs - LinuxPipe.ElapsedMs(start);
                if (Aborted(abort) || left <= 0)
                {
                    return false;
                }

                Monitor.Wait(l.Gate, (int)Math.Min(LinuxPipe.SliceMs, left));
            }

            return true;
        }
    }

    // ILinuxDescriptor/ILinuxInput: write und read ohne Prozess (der Verteiler nimmt sonst Send/Receive mit dem Abbruch des Aufrufers).
    // Dateien in gelesenen Nachrichten verfallen wie bei Linux' read auf einem Unix-Socket.
    public long Write(ReadOnlySpan<byte> data) => Send(data, null, NonBlocking, null, out _);

    public long ReadInput(Span<byte> dst) => ReadDiscardingFds(dst, null);

    public long ReadAs(Span<byte> dst, ILinuxAbort? abort) => ReadDiscardingFds(dst, abort);

    public long WriteAs(ReadOnlySpan<byte> data, ILinuxAbort? abort) => Send(data, null, NonBlocking, abort, out _);

    /// <summary>read/readv: wie recv ohne Hilfsdaten; mitgekommene Dateien werden freigegeben.</summary>
    public long ReadDiscardingFds(Span<byte> dst, ILinuxAbort? abort)
    {
        long r = Receive(dst, peek: false, waitAll: false, NonBlocking, abort, out LinuxSocketFds? fds, out _);
        fds?.Release();
        return r;
    }

    public bool InputReady => PollEvents(LinuxPipe.PollIn) != 0;

    public bool WaitInput(long timeoutMs) => WaitPoll(LinuxPipe.PollIn, timeoutMs, null);

    /// <summary>Eine Nachricht im Kanal: Bytes ab <see cref="Offset"/> (Stream liest Teile) und die Dateien, bis sie übergeben sind.</summary>
    private sealed class Record
    {
        public Record(byte[] data, LinuxSocketFds? fds)
        {
            Data = data;
            Fds = fds;
        }

        public byte[] Data { get; }

        public int Offset { get; set; }

        public LinuxSocketFds? Fds { get; set; }
    }

    /// <summary>Eine Richtung. WriteClosed: der Sender sendet nicht mehr (close, SHUT_WR); ReadClosed: der Empfänger liest nicht mehr (close, SHUT_RD).</summary>
    private sealed class Channel
    {
        public Queue<Record> Records { get; } = new();

        public int Bytes { get; set; }

        public bool WriteClosed { get; set; }

        public bool ReadClosed { get; set; }

        /// <summary>V22: steigt mit jedem Datensatz und mit dem Ende des Senders (EPOLLET des Empfängers, <see cref="ILinuxEdgeSource"/>).</summary>
        public long Seq { get; set; }
    }

    /// <summary>Gemeinsamer Zustand eines verbundenen Paars: Sperre und beide Richtungen.</summary>
    private sealed class Link
    {
        public object Gate { get; } = new();

        public Channel AtoB { get; } = new();

        public Channel BtoA { get; } = new();
    }
}
