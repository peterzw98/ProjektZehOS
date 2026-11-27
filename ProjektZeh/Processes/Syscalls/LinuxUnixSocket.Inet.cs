using System;
using System.Collections.Generic;
using System.Threading;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V27: AF_INET nur auf Loopback (127.0.0.0/8), ohne Netzwerkkarte und ohne TCP/IP-Stapel. Ein TCP-Socket ist ein Unix-Stream-Socket mit
/// Familie AF_INET und Adresse (127.x.y.z, Port) statt eines Namens: connect, accept, Senden, Empfangen, shutdown, poll und epoll laufen über
/// dieselben Kanäle wie AF_UNIX, nur ohne SCM_RIGHTS. Ports vergibt <see cref="LinuxInetPorts"/> des Laufs. UDP (SOCK_DGRAM) bekommt einen
/// eigenen Eingang je Socket: sendto stellt das Datagramm direkt beim Socket mit dem Zielport an, ohne Empfänger verfällt es (Linux: ICMP
/// port unreachable, das ein unverbundenes Socket nicht sieht).
/// <para>
/// Sperren: Adresse, Port und Optionen liegen unter <c>_nameGate</c> (Blattsperre wie bei Unix-Namen); der UDP-Eingang unter
/// <see cref="_inboxGate"/>, ebenfalls eine Blattsperre. Zustellen nimmt nur die Eingangssperre des Ziels und danach <c>_idle</c> zum
/// Wecken von poll; die Porttabelle wird vorher und ohne Socket-Sperre befragt.
/// </para>
/// </summary>
internal sealed partial class LinuxUnixSocket
{
    public const int AfInet = 2;

    /// <summary>127.0.0.1 in Rechnerreihenfolge.</summary>
    public const uint Loopback = 0x7f000001;

    /// <summary>Größtes UDP-Datagramm über IPv4 (65535 minus IP- und UDP-Kopf).</summary>
    public const int MaxDatagram = 65507;

    private const long EDESTADDRREQ = 89;

    private readonly object _inboxGate = new();
    private readonly Queue<Datagram> _inbox = new();
    private int _inboxBytes;
    private long _inboxSeq;

    // Nur unter _nameGate: eigene Adresse und Port (0: ungebunden), Gegenstelle eines verbundenen UDP-Sockets.
    private uint _inetAddr;
    private int _inetPort;
    private uint _udpPeerAddr;
    private int _udpPeerPort;

    // Socketoptionen, die getsockopt wieder meldet; ohne Wirkung auf die Übertragung (Loopback ohne Nagle und ohne Keepalive-Pakete).
    private int _reuseAddr;
    private int _reusePort;
    private int _keepAlive;
    private int _noDelay;

    // Fehler eines unverbundenen Sockets für SO_ERROR (nichtblockierendes connect auf einen Port ohne Lauschenden: ECONNREFUSED).
    private long _connectError;

    /// <summary>AF_UNIX oder <see cref="AfInet"/>.</summary>
    public int Family { get; private set; } = LinuxUnixAddress.AfUnix;

    public bool IsInet => Family == AfInet;

    public bool IsInetDgram => IsInet && Type == SockDgram;

    /// <summary>socket(AF_INET, …): TCP als <see cref="SockStream"/>, UDP als <see cref="SockDgram"/>.</summary>
    public static LinuxUnixSocket CreateInet(int type, bool nonBlocking, int creatorPid)
    {
        return new LinuxUnixSocket(type, nonBlocking, creatorPid) { Family = AfInet };
    }

    /// <summary>Ist <paramref name="addr"/> (Rechnerreihenfolge) eine lokale Adresse: 0.0.0.0 oder 127.0.0.0/8.</summary>
    public static bool IsLocal(uint addr) => addr == 0 || (addr >> 24) == 127;

    /// <summary>Eigene Adresse für getsockname; ein verbundenes Socket auf 0.0.0.0 meldet die tatsächliche 127.0.0.1 wie Linux.</summary>
    public (uint Addr, int Port) InetName
    {
        get
        {
            lock (_nameGate)
            {
                return (_inetAddr == 0 && (IsConnected || _udpPeerPort != 0) ? Loopback : _inetAddr, _inetPort);
            }
        }
    }

    /// <summary>Gegenstelle für getpeername und accept; Port 0, wenn unverbunden.</summary>
    public (uint Addr, int Port) InetPeerName
    {
        get
        {
            if (IsInetDgram)
            {
                lock (_nameGate)
                {
                    return (_udpPeerAddr, _udpPeerPort);
                }
            }

            LinuxUnixSocket? peer = IsConnected ? Peer : null;
            return peer is null ? (0, 0) : peer.InetName;
        }
    }

    public bool InetBound
    {
        get
        {
            lock (_nameGate)
            {
                return _inetPort != 0;
            }
        }
    }

    /// <summary>
    /// bind (und das automatische Binden): trägt den Port in die Porttabelle ein. -EINVAL schon gebunden, -EADDRINUSE belegt.
    /// <paramref name="port"/> 0 vergibt einen kurzlebigen Port.
    /// </summary>
    public long BindInet(LinuxUnixNames names, uint addr, int port)
    {
        lock (_nameGate)
        {
            if (_bound)
            {
                return -EINVAL;
            }

            _bound = true;
        }

        long r = names.Ports.Bind(IsInetDgram, port, this);
        lock (_nameGate)
        {
            if (r < 0)
            {
                _bound = false;
                return r;
            }

            _inetAddr = addr;
            _inetPort = (int)r;
            _names = names;
        }

        if (Volatile.Read(ref _closed) != 0)
        {
            names.Ports.Release(this); // ein gleichzeitiges letztes close hat _names noch nicht gesehen
        }

        return 0;
    }

    /// <summary>Das angenommene Ende übernimmt Familie und Adresse des Lauschenden (Linux: gleicher lokaler Port, Adresse der Verbindung).</summary>
    private void CopyInetFrom(LinuxUnixSocket listener)
    {
        Family = listener.Family;
        _inetAddr = listener._inetAddr == 0 ? Loopback : listener._inetAddr;
        _inetPort = listener._inetPort;
    }

    /// <summary>UDP connect: setzt die Gegenstelle für send/write und getpeername.</summary>
    public void SetUdpPeer(uint addr, int port)
    {
        lock (_nameGate)
        {
            _udpPeerAddr = addr;
            _udpPeerPort = port;
        }
    }

    /// <summary>Nichtblockierendes connect wurde verweigert: SO_ERROR meldet es einmal, poll meldet POLLERR|POLLHUP.</summary>
    public void SetConnectError(long error) => Interlocked.Exchange(ref _connectError, error);

    private int TakeConnectError() => (int)Interlocked.Exchange(ref _connectError, 0);

    private bool HasConnectError => Interlocked.Read(ref _connectError) != 0;

    public const int SoReuseAddr = 2, SoKeepAlive = 9, SoReusePort = 15, TcpNoDelay = 1;

    /// <summary>Wert einer gemerkten Option oder -1, wenn <paramref name="name"/> keine ist.</summary>
    public int GetOption(int name)
    {
        lock (_nameGate)
        {
            return name switch
            {
                SoReuseAddr => _reuseAddr,
                SoReusePort => _reusePort,
                SoKeepAlive => _keepAlive,
                -TcpNoDelay => _noDelay,
                _ => -1,
            };
        }
    }

    /// <summary>Merkt eine Option (Wert ungleich 0 wird 1); TCP_NODELAY als negativer Name, damit SOL_SOCKET und IPPROTO_TCP getrennt bleiben.</summary>
    public bool SetOption(int name, int value)
    {
        int v = value != 0 ? 1 : 0;
        lock (_nameGate)
        {
            switch (name)
            {
                case SoReuseAddr: _reuseAddr = v; return true;
                case SoReusePort: _reusePort = v; return true;
                case SoKeepAlive: _keepAlive = v; return true;
                case -TcpNoDelay: _noDelay = v; return true;
                default: return false;
            }
        }
    }

    /// <summary>
    /// UDP: sendet <paramref name="data"/> an (<paramref name="toAddr"/>, <paramref name="toPort"/>). Das Socket muss gebunden sein (der
    /// Verteiler bindet vorher automatisch). Ohne Empfänger oder bei vollem Eingang verfällt das Datagramm still; die Länge zählt trotzdem.
    /// </summary>
    public long SendDatagram(ReadOnlySpan<byte> data, uint toAddr, int toPort)
    {
        if (data.Length > MaxDatagram)
        {
            return -EMSGSIZE;
        }

        LinuxUnixNames? names;
        uint from;
        int fromPort;
        lock (_nameGate)
        {
            names = _names;
            from = _inetAddr == 0 ? Loopback : _inetAddr;
            fromPort = _inetPort;
        }

        if (names is null || fromPort == 0)
        {
            return -EDESTADDRREQ;
        }

        if (names.Ports.Find(true, toPort) is LinuxUnixSocket target && target.AcceptsDatagramFor(toAddr))
        {
            target.DeliverDatagram(data.ToArray(), from, fromPort);
        }

        return data.Length;
    }

    /// <summary>send/write eines verbundenen UDP-Sockets; unverbunden -EDESTADDRREQ wie Linux.</summary>
    private long SendDatagramToPeer(ReadOnlySpan<byte> data)
    {
        uint addr;
        int port;
        lock (_nameGate)
        {
            addr = _udpPeerAddr;
            port = _udpPeerPort;
        }

        return port == 0 ? -EDESTADDRREQ : SendDatagram(data, addr, port);
    }

    private bool AcceptsDatagramFor(uint toAddr)
    {
        lock (_nameGate)
        {
            return _inetAddr == 0 || _inetAddr == toAddr;
        }
    }

    private void DeliverDatagram(byte[] data, uint fromAddr, int fromPort)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        lock (_inboxGate)
        {
            // Linux zählt den Eingang gegen SO_RCVBUF und verwirft, was nicht mehr passt.
            if (_inboxBytes + data.Length > Capacity)
            {
                return;
            }

            _inbox.Enqueue(new Datagram(data, fromAddr, fromPort));
            _inboxBytes += data.Length;
            _inboxSeq++;
            Monitor.PulseAll(_inboxGate);
        }

        WakeIdle(); // poll eines unverbundenen Sockets wartet auf _idle
    }

    /// <summary>
    /// UDP: ein Datagramm; was nicht in <paramref name="dst"/> passt, ist verloren (<paramref name="fullLength"/> ist die ganze Länge).
    /// -EAGAIN leer mit <paramref name="nonBlocking"/>, -EINTR beim Abbruch des Aufrufers.
    /// </summary>
    public long ReceiveDatagram(Span<byte> dst, bool peek, bool nonBlocking, ILinuxAbort? abort, out int fullLength, out uint fromAddr, out int fromPort)
    {
        fullLength = 0;
        fromAddr = 0;
        fromPort = 0;
        lock (_inboxGate)
        {
            while (true)
            {
                if (_inbox.Count > 0)
                {
                    Datagram d = peek ? _inbox.Peek() : _inbox.Dequeue();
                    if (!peek)
                    {
                        _inboxBytes -= d.Data.Length;
                    }

                    int n = Math.Min(dst.Length, d.Data.Length);
                    d.Data.AsSpan(0, n).CopyTo(dst);
                    fullLength = d.Data.Length;
                    fromAddr = d.Addr;
                    fromPort = d.Port;
                    return n;
                }

                if (Aborted(abort))
                {
                    return -EINTR;
                }

                if (nonBlocking)
                {
                    return -EAGAIN;
                }

                Monitor.Wait(_inboxGate, LinuxPipe.SliceMs);
            }
        }
    }

    private bool DatagramWaiting
    {
        get
        {
            lock (_inboxGate)
            {
                return _inbox.Count > 0;
            }
        }
    }

    private long DatagramSeq
    {
        get
        {
            lock (_inboxGate)
            {
                return _inboxSeq;
            }
        }
    }

    /// <summary>poll eines unverbundenen Sockets: UDP lesbar mit Datagramm; nach verweigertem connect POLLERR|POLLHUP wie Linux.</summary>
    private short UnconnectedEvents(short events)
    {
        short r = (short)(events & LinuxPipe.PollOut);
        if (IsInetDgram && DatagramWaiting)
        {
            r |= (short)(events & LinuxPipe.PollIn);
        }

        if (HasConnectError)
        {
            r |= LinuxPipe.PollErr | LinuxPipe.PollHup;
        }

        return r;
    }

    private sealed class Datagram
    {
        public Datagram(byte[] data, uint addr, int port)
        {
            Data = data;
            Addr = addr;
            Port = port;
        }

        public byte[] Data { get; }

        public uint Addr { get; }

        public int Port { get; }
    }
}
