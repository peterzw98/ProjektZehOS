using System;
using System.Diagnostics;
using System.Threading;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Ein Deskriptor, dessen Bereitschaft poll selbst erfragt und auf die es warten kann (V13: Pipe-Enden). Das Terminal bleibt bei
/// <see cref="ILinuxInput"/>; für ein Objekt, das beides ist, nimmt poll diese Schnittstelle.
/// </summary>
internal interface ILinuxPollable : ILinuxDescriptor
{
    /// <summary>
    /// Bereite Ereignisse ohne Warten: die Teilmenge von <paramref name="events"/> aus POLLIN und POLLOUT, dazu immer POLLERR und
    /// POLLHUP, wenn sie zutreffen (wie Linux, auch ungefragt).
    /// </summary>
    short PollEvents(short events);

    /// <summary>
    /// Wartet höchstens <paramref name="timeoutMs"/> (negativ: ohne Frist), bis <see cref="PollEvents"/> etwas meldet oder das Programm
    /// abbricht; liefert, ob etwas bereit ist. Das Prüfen geschieht unter derselben Sperre wie das Wecken, damit kein Ereignis
    /// zwischen dem Durchsuchen in poll und dem Schlaf verloren geht.
    /// </summary>
    bool WaitPoll(short events, long timeoutMs);
}

/// <summary>
/// Ein Deskriptor mit Statusflags der offenen Datei (V16): fcntl F_GETFL/F_SETFL für Pipe-Enden und Unix-Sockets. O_NONBLOCK gehört wie
/// bei Linux zur offenen Datei; dup und SCM_RIGHTS teilen es.
/// </summary>
internal interface ILinuxStatusFlags : ILinuxDescriptor
{
    /// <summary>O_NONBLOCK.</summary>
    bool NonBlocking { get; set; }

    /// <summary>Flags für F_GETFL: Zugriffsart (O_RDONLY, O_WRONLY, O_RDWR), dazu O_NONBLOCK.</summary>
    int StatusFlags { get; }
}

/// <summary>
/// Pipe innerhalb eines Prozesses (V13, pipe/pipe2): ein Ringpuffer bis <see cref="Capacity"/> Bytes, ein Lese- und ein Schreibende.
/// LibCore (Ladybird) weckt seine Ereignisschleife über eine solche Pipe aus anderen Threads; deshalb blockieren Lesen und Schreiben
/// wie bei Linux und poll wartet auf beide Enden.
/// <para>
/// Nebenläufigkeit: <see cref="_gate"/> ist eine Blattsperre. Darin wird nur der Puffer bewegt; Benutzerspeicher kopiert der Verteiler
/// vorher oder nachher, ohne die Sperre, und nimmt keine andere Sperre darin. Wer die Sperre hält und warten muss, schläft mit
/// <see cref="Monitor.Wait(object, int)"/> in Scheiben von <see cref="SliceMs"/>; so erreicht ein Abbruch (exit_group, Zeitlimit der
/// Shell) jeden Wartenden, auch wenn ein Wecken verloren ginge. Sperrreihenfolge: FileGate des Prozesses vor <see cref="_gate"/>
/// (close unter der Dateisperre schließt ein Ende); Lesen und Schreiben einer Pipe halten die Dateisperre nicht.
/// </para>
/// </summary>
internal sealed class LinuxPipe
{
    /// <summary>Puffergröße wie bei Linux (16 Seiten).</summary>
    public const int Capacity = 65536;

    /// <summary>PIPE_BUF: Schreiben bis zu dieser Länge geschieht ganz oder gar nicht.</summary>
    public const int AtomicMax = 4096;

    /// <summary>Längster Schlaf am Stück; danach prüft der Wartende den Abbruch.</summary>
    public const int SliceMs = 50;

    public const short PollIn = 1, PollOut = 4, PollErr = 8, PollHup = 0x10;

    private const long EAGAIN = 11, EINTR = 4, EPIPE = 32;

    private readonly object _gate = new();
    private readonly ILinuxAbort? _abort;

    // Klein anfangen: Weckpipes tragen nur wenige Bytes, gewachsen wird bis Capacity.
    private byte[] _buffer = new byte[256];
    private int _head;
    private int _count;
    private int _readers = 1;
    private int _writers = 1;

    private LinuxPipe(ILinuxAbort? abort) => _abort = abort;

    /// <summary>Legt eine Pipe an. <paramref name="abort"/> (der Prozess) beendet Wartende beim Abbruch des Programms.</summary>
    public static (LinuxPipeReader Reader, LinuxPipeWriter Writer) Create(ILinuxAbort? abort, bool nonBlocking)
    {
        var pipe = new LinuxPipe(abort);
        return (new LinuxPipeReader(pipe) { NonBlocking = nonBlocking }, new LinuxPipeWriter(pipe) { NonBlocking = nonBlocking });
    }

    /// <summary>Anzahl ungelesener Bytes (Tests).</summary>
    public int Buffered
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    private bool Aborted => _abort is { IsAborted: true };

    /// <summary>Liest verfügbare Bytes; 0 am Ende (kein Schreibende mehr offen), -EAGAIN ohne Daten bei O_NONBLOCK, -EINTR beim Abbruch.</summary>
    public long Read(Span<byte> dst, bool nonBlocking)
    {
        if (dst.IsEmpty)
        {
            return 0;
        }

        lock (_gate)
        {
            while (true)
            {
                if (_count > 0)
                {
                    int n = Math.Min(dst.Length, _count);
                    int first = Math.Min(n, _buffer.Length - _head);
                    _buffer.AsSpan(_head, first).CopyTo(dst);
                    _buffer.AsSpan(0, n - first).CopyTo(dst.Slice(first));
                    _head = (_head + n) % _buffer.Length;
                    _count -= n;
                    if (_count == 0)
                    {
                        _head = 0;
                    }

                    Monitor.PulseAll(_gate); // Platz für wartende Schreiber
                    return n;
                }

                if (_writers == 0)
                {
                    return 0;
                }

                if (Aborted)
                {
                    return -EINTR;
                }

                if (nonBlocking)
                {
                    return -EAGAIN;
                }

                Monitor.Wait(_gate, SliceMs);
            }
        }
    }

    /// <summary>
    /// Schreibt alles (blockierend) oder so viel wie passt (O_NONBLOCK). Bis <see cref="AtomicMax"/> Bytes nur ganz: ohne Platz dafür
    /// wartet der Aufruf oder liefert -EAGAIN. -EPIPE ohne Leseende (SIGPIPE wird nicht zugestellt, die Signale sind in S1 nur
    /// verwaltet), -EINTR beim Abbruch; wurde schon etwas geschrieben, zählt das.
    /// </summary>
    public long Write(ReadOnlySpan<byte> src, bool nonBlocking)
    {
        if (src.IsEmpty)
        {
            return 0;
        }

        bool atomic = src.Length <= AtomicMax;
        int done = 0;
        lock (_gate)
        {
            while (true)
            {
                if (_readers == 0)
                {
                    return done > 0 ? done : -EPIPE;
                }

                if (Aborted)
                {
                    return done > 0 ? done : -EINTR;
                }

                int space = Capacity - _count;
                if (atomic ? space >= src.Length : space > 0)
                {
                    int take = Math.Min(space, src.Length - done);
                    Append(src.Slice(done, take));
                    done += take;
                    Monitor.PulseAll(_gate); // Daten für wartende Leser und poll
                    if (done == src.Length)
                    {
                        return done;
                    }

                    continue;
                }

                if (nonBlocking)
                {
                    return done > 0 ? done : -EAGAIN;
                }

                Monitor.Wait(_gate, SliceMs);
            }
        }
    }

    /// <summary>Hängt an den Ring an; vergrößert ihn vorher bis höchstens <see cref="Capacity"/>. Nur unter <see cref="_gate"/>.</summary>
    private void Append(ReadOnlySpan<byte> src)
    {
        int need = _count + src.Length;
        if (need > _buffer.Length)
        {
            int size = _buffer.Length;
            while (size < need)
            {
                size *= 2;
            }

            var grown = new byte[Math.Min(size, Capacity)];
            int first = Math.Min(_count, _buffer.Length - _head);
            _buffer.AsSpan(_head, first).CopyTo(grown);
            _buffer.AsSpan(0, _count - first).CopyTo(grown.AsSpan(first));
            _buffer = grown;
            _head = 0;
        }

        int tail = (_head + _count) % _buffer.Length;
        int part = Math.Min(src.Length, _buffer.Length - tail);
        src.Slice(0, part).CopyTo(_buffer.AsSpan(tail));
        src.Slice(part).CopyTo(_buffer.AsSpan(0));
        _count += src.Length;
    }

    /// <summary>Bereitschaft eines Endes; nur unter <see cref="_gate"/>.</summary>
    private short EventsLocked(bool reader, short events)
    {
        short r = 0;
        if (reader)
        {
            if (_count > 0)
            {
                r |= (short)(events & PollIn);
            }

            if (_writers == 0)
            {
                r |= PollHup; // Linux: Ende der Daten, auch ungefragt; POLLIN nur mit Bytes
            }
        }
        else
        {
            if (_readers == 0)
            {
                r |= PollErr;
            }
            else if (Capacity - _count >= AtomicMax)
            {
                r |= (short)(events & PollOut); // wie Linux: bereit, solange eine Seite frei ist
            }
        }

        return r;
    }

    public short PollEvents(bool reader, short events)
    {
        lock (_gate)
        {
            return EventsLocked(reader, events);
        }
    }

    public bool WaitPoll(bool reader, short events, long timeoutMs)
    {
        // Die Frist läuft nach der Uhr, nicht nach Scheiben: PulseAll weckt bei jedem Lesen oder Schreiben eines anderen Threads vorzeitig,
        // ein Zählen der nominalen Scheibe verkürzte die Frist dann beliebig (V13-Review P2-1: 1500 ms wurden 71 ms).
        long start = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            while (EventsLocked(reader, events) == 0)
            {
                long left = timeoutMs < 0 ? SliceMs : timeoutMs - ElapsedMs(start);
                if (Aborted || left <= 0)
                {
                    return false;
                }

                Monitor.Wait(_gate, (int)Math.Min(SliceMs, left));
            }

            return true;
        }
    }

    /// <summary>Vergangene Millisekunden seit <paramref name="start"/> (Stopwatch-Ticks, im Gast an den TSC gebunden).</summary>
    internal static long ElapsedMs(long start) => (long)((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);

    /// <summary>Das letzte Verweis auf ein Ende ist zu: Leser bekommen EOF, Schreiber EPIPE; Wartende wachen auf.</summary>
    public void CloseEnd(bool reader)
    {
        lock (_gate)
        {
            if (reader)
            {
                _readers--;
                if (_readers == 0)
                {
                    // Ungelesenes verwerfen wie Linux; der Puffer wird nicht mehr gebraucht.
                    _buffer = new byte[1];
                    _head = 0;
                    _count = 0;
                }
            }
            else
            {
                _writers--;
            }

            Monitor.PulseAll(_gate);
        }
    }
}

/// <summary>Gemeinsames eines Pipe-Endes: Statusflags (O_NONBLOCK) und einmaliges Schließen.</summary>
internal abstract class LinuxPipeEnd : ILinuxPollable, ILinuxStatusFlags
{
    public const int ONonblock = 0x800;

    private int _closed;

    protected LinuxPipeEnd(LinuxPipe pipe) => Pipe = pipe;

    public LinuxPipe Pipe { get; }

    /// <summary>O_NONBLOCK; gehört wie bei Linux zur offenen Datei, dup teilt es.</summary>
    public bool NonBlocking { get; set; }

    public abstract bool IsReader { get; }

    /// <summary>Flags für F_GETFL: O_RDONLY oder O_WRONLY, dazu O_NONBLOCK.</summary>
    public int StatusFlags => (IsReader ? 0 : 1) | (NonBlocking ? ONonblock : 0);

    public abstract long Write(ReadOnlySpan<byte> data);

    public short PollEvents(short events) => Pipe.PollEvents(IsReader, events);

    public bool WaitPoll(short events, long timeoutMs) => Pipe.WaitPoll(IsReader, events, timeoutMs);

    /// <summary>Beim letzten close (keine Nummer zeigt mehr darauf); ein zweiter Aufruf ändert nichts.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Pipe.CloseEnd(IsReader);
        }
    }
}

/// <summary>Leseende: liest über <see cref="ILinuxInput"/>, damit read und readv ohne Dateisperre blockieren dürfen wie fd 0.</summary>
internal sealed class LinuxPipeReader : LinuxPipeEnd, ILinuxInput
{
    public LinuxPipeReader(LinuxPipe pipe)
        : base(pipe)
    {
    }

    public override bool IsReader => true;

    public override long Write(ReadOnlySpan<byte> data) => -9; // EBADF: nur zum Lesen offen

    public long ReadInput(Span<byte> dst) => Pipe.Read(dst, NonBlocking);

    public bool InputReady => PollEvents(LinuxPipe.PollIn) != 0;

    public bool WaitInput(long timeoutMs) => WaitPoll(LinuxPipe.PollIn, timeoutMs);
}

/// <summary>Schreibende; read darauf liefert -EBADF.</summary>
internal sealed class LinuxPipeWriter : LinuxPipeEnd
{
    public LinuxPipeWriter(LinuxPipe pipe)
        : base(pipe)
    {
    }

    public override bool IsReader => false;

    public override long Write(ReadOnlySpan<byte> data) => Pipe.Write(data, NonBlocking);
}
