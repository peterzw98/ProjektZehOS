using System;
using System.Collections.Generic;
using System.Threading;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V16 (Teil senior2): Namen, listen, connect und accept eines Unix-Sockets. Wie Linux (unix_stream_connect) legt connect sofort das
/// Gegenende an, verbindet es mit dem Verbindenden und stellt es in die Warteschlange des Lauschenden; accept nimmt es heraus. Der
/// Verbindende kann deshalb schon vor accept senden.
/// <para>
/// Sperren: <see cref="_nameGate"/> ist eine Blattsperre je Socket für Name, Lauschzustand und Warteschlange; darin kein Warten auf eine
/// andere Sperre außer <see cref="_idle"/> zum Wecken von poll. Die Verbindungssperre (<c>Link.Gate</c>) wird darin nie genommen;
/// <see cref="Connect(LinuxUnixSocket, LinuxUnixSocket)"/> braucht keine. Reihenfolge damit: FileGate, dann _nameGate des Lauschenden,
/// dann _idle. Benutzerspeicher und Namenstabelle bearbeitet der Verteiler davor oder danach.
/// </para>
/// </summary>
internal sealed partial class LinuxUnixSocket
{
    /// <summary>SOMAXCONN in Linux 6.12; größere Werte für listen werden gekürzt.</summary>
    public const int MaxBacklog = 4096;

    private const long EINVAL = 22, EISCONN = 106, EALREADY = 114, ECONNREFUSED = 111;

    private readonly object _nameGate = new();
    private readonly Queue<LinuxUnixSocket> _pending = new();
    private LinuxUnixAddress _name = LinuxUnixAddress.Unnamed;
    private LinuxUnixNames? _names;
    private bool _bound;
    private bool _listening;
    private int _backlog;

    // 0 frei, 1 connect läuft oder fertig: verhindert zwei gleichzeitige connect desselben Sockets (Linux: EALREADY/EISCONN).
    // Geschrieben und gelesen nur unter _nameGate.
    private int _connecting;

    /// <summary>Lauscht (listen); für SO_ACCEPTCONN.</summary>
    public bool IsListening
    {
        get
        {
            lock (_nameGate)
            {
                return _listening;
            }
        }
    }

    /// <summary>Eigener Name (getsockname); unbenannt, solange nicht gebunden. Angenommene Enden tragen den Namen des Lauschenden.</summary>
    public LinuxUnixAddress Name
    {
        get
        {
            lock (_nameGate)
            {
                return _name;
            }
        }
    }

    /// <summary>
    /// Reserviert das Binden: false, wenn das Socket schon gebunden ist oder ein bind läuft (Linux: EINVAL). Nach einem Fehlschlag der
    /// Namenstabelle gibt <see cref="CancelBind"/> frei, nach Erfolg setzt <see cref="CompleteBind"/> den Namen.
    /// </summary>
    public bool TryBeginBind()
    {
        lock (_nameGate)
        {
            if (_bound)
            {
                return false;
            }

            _bound = true;
            return true;
        }
    }

    public void CancelBind()
    {
        lock (_nameGate)
        {
            _bound = false;
        }
    }

    /// <summary>Setzt den gebundenen Namen und merkt die Tabelle, aus der das letzte close ihn wieder löst.</summary>
    public void CompleteBind(LinuxUnixAddress name, LinuxUnixNames names)
    {
        lock (_nameGate)
        {
            _name = name;
            _names = names;
        }
    }

    /// <summary>listen: -EINVAL verbunden, unbenannt (Linux bindet hier nicht automatisch) oder geschlossen; erneutes listen ändert nur den Rückstand.</summary>
    public long Listen(int backlog)
    {
        if (IsConnected)
        {
            return -EINVAL;
        }

        lock (_nameGate)
        {
            // _connecting unter _nameGate lesen: ConnectTo reserviert unter derselben Sperre, sonst könnte ein paralleles connect
            // zwischen Prüfung und _listening = true durchrutschen (Socket wäre dann lauschend und verbunden).
            // V27: AF_INET braucht einen Port statt eines Namens (der Verteiler bindet vorher automatisch wie inet_listen).
            if (_connecting != 0 || (IsInet ? _inetPort == 0 : _name.IsUnnamed) || Volatile.Read(ref _closed) != 0)
            {
                return -EINVAL;
            }

            // Linux: sk_max_ack_backlog = backlog (unsigned gekürzt auf somaxconn); negativ wird wie 0 behandelt.
            _backlog = backlog < 0 ? 0 : Math.Min(backlog, MaxBacklog);
            _listening = true;
            Monitor.PulseAll(_nameGate);
        }

        return 0;
    }

    /// <summary>
    /// connect zu <paramref name="listener"/>: legt das Gegenende an (Erzeuger-PID des Lauschenden, wie copy_peercred), verbindet und
    /// stellt es an. -EISCONN schon verbunden, -EALREADY bei gleichzeitigem connect, -EINVAL lauschend, -ECONNREFUSED nicht lauschend oder
    /// geschlossen; Warteschlange voll: -EAGAIN mit <paramref name="nonBlocking"/>, sonst warten (Abbruch -EINTR). Typgleichheit prüft der
    /// Verteiler (die Fehlernummer hängt an der Namensart).
    /// </summary>
    public long ConnectTo(LinuxUnixSocket listener, bool nonBlocking, ILinuxAbort? abort)
    {
        if (IsConnected)
        {
            return -EISCONN;
        }

        lock (_nameGate)
        {
            // Lauschzustand und Reservierung zusammen unter _nameGate, passend zu Listen (Rennen listen gegen connect).
            if (_listening)
            {
                return -EINVAL;
            }

            if (_connecting != 0)
            {
                return IsConnected ? -EISCONN : -EALREADY;
            }

            _connecting = 1;
        }

        long r = listener.Enqueue(this, nonBlocking, abort);
        if (r < 0)
        {
            lock (_nameGate)
            {
                _connecting = 0;
            }
        }

        return r;
    }

    /// <summary>Am Lauschenden: wartet auf Platz, verbindet <paramref name="client"/> mit einem neuen Ende und stellt es an.</summary>
    private long Enqueue(LinuxUnixSocket client, bool nonBlocking, ILinuxAbort? abort)
    {
        lock (_nameGate)
        {
            while (true)
            {
                if (!_listening || Volatile.Read(ref _closed) != 0)
                {
                    return -ECONNREFUSED;
                }

                // Linux (unix_recvq_full_lockless): voll erst, wenn mehr als backlog Verbindungen warten.
                if (_pending.Count <= _backlog)
                {
                    break;
                }

                if (nonBlocking)
                {
                    return -EAGAIN;
                }

                if (Aborted(abort))
                {
                    return -EINTR;
                }

                Monitor.Wait(_nameGate, LinuxPipe.SliceMs);
            }

            var server = new LinuxUnixSocket(Type, false, CreatorPid);
            server._name = _name; // das angenommene Ende meldet den Namen des Lauschenden (getsockname, getpeername der Gegenseite)
            if (IsInet)
            {
                server.CopyInetFrom(this); // V27: gleicher Port, Adresse 127.0.0.1 bei einem Lauschenden auf 0.0.0.0
            }

            server._bound = true; // Linux: bind auf ein angenommenes Ende -EINVAL (unix_bind: schon ein Name), wie auf ein gebundenes
            server._connecting = 1; // verbunden: listen -EINVAL, connect -EISCONN
            Connect(client, server);
            _pending.Enqueue(server);
            Monitor.PulseAll(_nameGate); // wartende accept
        }

        WakeIdle(); // poll eines Lauschenden wartet auf _idle
        return 0;
    }

    /// <summary>
    /// accept: nimmt die älteste wartende Verbindung. -EINVAL nicht lauschend (auch nach close während des Wartens), -EAGAIN leer mit
    /// <paramref name="nonBlocking"/>, -EINTR beim Abbruch des Aufrufers.
    /// </summary>
    public long Accept(bool nonBlocking, ILinuxAbort? abort, out LinuxUnixSocket? accepted)
    {
        accepted = null;
        bool took = false;
        lock (_nameGate)
        {
            while (true)
            {
                if (!_listening || Volatile.Read(ref _closed) != 0)
                {
                    return -EINVAL;
                }

                if (_pending.Count > 0)
                {
                    accepted = _pending.Dequeue();
                    took = true;
                    Monitor.PulseAll(_nameGate); // Platz für wartende connect
                    break;
                }

                if (nonBlocking)
                {
                    return -EAGAIN;
                }

                if (Aborted(abort))
                {
                    return -EINTR;
                }

                Monitor.Wait(_nameGate, LinuxPipe.SliceMs);
            }
        }

        return took ? 0 : -EINVAL;
    }

    /// <summary>Ein angenommenes Ende, das nicht in die Dateitabelle kam (EMFILE): schließen wie Linux, die Gegenseite sieht EOF.</summary>
    public static void Discard(LinuxUnixSocket accepted) => accepted.Close();

    private void WakeIdle()
    {
        lock (_idle)
        {
            Monitor.PulseAll(_idle);
        }
    }

    partial void OnLastClose()
    {
        LinuxUnixSocket[]? drop = null;
        LinuxUnixNames? names;
        lock (_nameGate)
        {
            names = _names;
            _names = null;
            if (_listening)
            {
                _listening = false;
                drop = _pending.ToArray();
                _pending.Clear();
            }

            Monitor.PulseAll(_nameGate); // wartende accept und connect sehen das Ende
        }

        names?.Release(this);

        // Außerhalb von _nameGate: Close nimmt Link.Gate. Nicht angenommene Verbindungen enden wie bei Linux (Verbindender: EOF/ECONNRESET).
        foreach (LinuxUnixSocket s in drop ?? [])
        {
            s.Close();
        }

        WakeIdle();
    }

    partial void ListenEvents(short events, ref short result, ref bool listening)
    {
        lock (_nameGate)
        {
            if (!_listening)
            {
                return;
            }

            listening = true;
            result = _pending.Count > 0 ? (short)(events & LinuxPipe.PollIn) : (short)0;
        }
    }
}
