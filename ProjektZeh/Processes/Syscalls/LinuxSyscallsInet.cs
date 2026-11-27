using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V27: AF_INET nur auf Loopback (<see cref="LinuxUnixSocket"/>, Teil Inet): socket, bind, listen, connect, accept, getsockname,
/// getpeername, sendto/recvfrom und sendmsg/recvmsg für UDP, Socketoptionen. Adressen sind <c>struct sockaddr_in</c> (16 Bytes, Port und
/// Adresse in Netzreihenfolge). Andere Adressen als 0.0.0.0 und 127.0.0.0/8 gibt es nicht: bind -EADDRNOTAVAIL, connect und sendto
/// -ENETUNREACH (ein Linux ohne Netz meldet dasselbe). AF_INET6 bleibt -EAFNOSUPPORT.
/// </summary>
internal static partial class LinuxSyscalls
{
    private const int AfInet = LinuxUnixSocket.AfInet, AfUnspec = 0, IpprotoTcp = 6, IpprotoUdp = 17;
    private const int SockaddrInSize = 16, SoReuseAddr = 2, SoKeepAlive = 9, SoReusePort = 15, TcpNoDelay = 1;
    private const long InetEADDRNOTAVAIL = 99, InetENETUNREACH = 101, InetEINPROGRESS = 115, InetEISCONN = 106;

    private static long DoSocketInet(ILinuxProcess p, ILinuxFiles f, int kind, int flags, int protocol)
    {
        // inet_create: zuerst der Typ (ohne Eintrag -ESOCKTNOSUPPORT), dann das Protokoll des Typs.
        if (kind != LinuxUnixSocket.SockStream && kind != LinuxUnixSocket.SockDgram)
        {
            return -SockErr.ESOCKTNOSUPPORT;
        }

        int own = kind == LinuxUnixSocket.SockStream ? IpprotoTcp : IpprotoUdp;
        if (protocol != 0 && protocol != own)
        {
            return -SockErr.EPROTONOSUPPORT;
        }

        var s = LinuxUnixSocket.CreateInet(kind, (flags & SockNonblock) != 0, p.Pid);
        lock (f.FileGate)
        {
            int fd = f.AddDescriptor(s, 0);
            if (fd < 0)
            {
                s.Close();
                return fd;
            }

            MarkCloseOnExec(f, fd, (flags & SockCloexec) != 0);
            return fd;
        }
    }

    /// <summary>
    /// Liest eine Socketadresse wie move_addr_to_kernel: <paramref name="len"/> ist ein int (V27b: obere Bits des Registers zählen nicht),
    /// negativ oder länger als sockaddr_storage (128) -EINVAL, sonst werden <paramref name="len"/> Bytes gelesen (-EFAULT). Familie -1 bei
    /// weniger als 2 Bytes; Adresse und Port (Rechnerreihenfolge) nur ab 16 Bytes, sonst 0. Längen- und Familienprüfung macht der Aufrufer, weil
    /// bind, connect und sendto sie in verschiedener Reihenfolge prüfen.
    /// </summary>
    private static long ReadInetAddress(ILinuxProcess p, ulong addr, int len, out int family, out uint ip, out int port)
    {
        family = -1;
        ip = 0;
        port = 0;
        if (len < 0 || len > 128)
        {
            return -SockErr.EINVAL;
        }

        if (len == 0)
        {
            return 0;
        }

        Span<byte> raw = stackalloc byte[128];
        raw = raw.Slice(0, len);
        if (addr + (ulong)len < addr || !p.Memory.TryRead(addr, raw))
        {
            return -SockErr.EFAULT;
        }

        if (len >= 2)
        {
            family = BitConverter.ToUInt16(raw.Slice(0, 2));
        }

        if (len >= SockaddrInSize)
        {
            port = (raw[2] << 8) | raw[3];
            ip = ((uint)raw[4] << 24) | ((uint)raw[5] << 16) | ((uint)raw[6] << 8) | raw[7];
        }

        return 0;
    }

    /// <summary>Schreibt (<paramref name="ip"/>, <paramref name="port"/>) als sockaddr_in gekürzt auf *lenPtr und meldet 16 (move_addr_to_user).</summary>
    private static long WriteInetName(ILinuxProcess p, uint ip, int port, ulong addr, ulong lenPtr)
    {
        long c = CheckNameBuffer(p, addr, lenPtr, out int len);
        if (c < 0)
        {
            return c;
        }

        int n = FormatInet(ip, port, addr, Math.Min(len, SockaddrInSize), p);
        if (n < 0 || !p.Memory.TryWrite(lenPtr, BitConverter.GetBytes(SockaddrInSize)))
        {
            return -SockErr.EFAULT;
        }

        return 0;
    }

    /// <summary>Schreibt höchstens <paramref name="room"/> Bytes einer sockaddr_in nach <paramref name="addr"/>; -1 bei einem Schreibfehler.</summary>
    private static int FormatInet(uint ip, int port, ulong addr, int room, ILinuxProcess p)
    {
        Span<byte> b = stackalloc byte[SockaddrInSize];
        b.Clear();
        BitConverter.TryWriteBytes(b.Slice(0, 2), (ushort)AfInet);
        b[2] = (byte)(port >> 8);
        b[3] = (byte)port;
        b[4] = (byte)(ip >> 24);
        b[5] = (byte)(ip >> 16);
        b[6] = (byte)(ip >> 8);
        b[7] = (byte)ip;
        int n = Math.Clamp(room, 0, SockaddrInSize);
        return n > 0 && !p.Memory.TryWrite(addr, b.Slice(0, n)) ? -1 : n;
    }

    private static long DoBindInet(ILinuxProcess p, LinuxUnixSocket s, ulong addr, ulong len)
    {
        long r = ReadInetAddress(p, addr, (int)len, out int family, out uint ip, out int port);
        if (r < 0)
        {
            return r;
        }

        // inet_bind_sk: kürzer als sockaddr_in -EINVAL; __inet_bind: AF_UNSPEC nur mit INADDR_ANY.
        if ((int)len < SockaddrInSize)
        {
            return -SockErr.EINVAL;
        }

        if (family != AfInet && !(family == AfUnspec && ip == 0))
        {
            return -SockErr.EAFNOSUPPORT;
        }

        if (!LinuxUnixSocket.IsLocal(ip))
        {
            return -InetEADDRNOTAVAIL;
        }

        return p is ILinuxUnixNames named ? s.BindInet(named.UnixNames, ip, port) : -SockErr.EOPNOTSUPP;
    }

    /// <summary>Bindet automatisch an einen kurzlebigen Port (listen, connect, sendto eines ungebundenen Sockets wie inet_autobind).</summary>
    private static long AutobindInet(ILinuxProcess p, LinuxUnixSocket s, uint ip)
    {
        if (s.InetBound)
        {
            return 0;
        }

        if (p is not ILinuxUnixNames named)
        {
            return -SockErr.EOPNOTSUPP;
        }

        long r = s.BindInet(named.UnixNames, ip, 0);
        return r == -SockErr.EINVAL ? 0 : r; // ein gleichzeitiges bind hat gewonnen und ist fertig (BindInet unter _bindGate, V27b)
    }

    private static long DoListenInet(ILinuxProcess p, LinuxUnixSocket s, int backlog)
    {
        if (s.Type != LinuxUnixSocket.SockStream)
        {
            return -SockErr.EOPNOTSUPP; // UDP kennt kein listen
        }

        if (s.IsConnected)
        {
            return -SockErr.EINVAL;
        }

        long r = AutobindInet(p, s, 0);
        return r < 0 ? r : s.Listen(backlog);
    }

    /// <summary>
    /// connect eines AF_INET-Sockets in Linux' Reihenfolge (V27b): Länge und Lesen, kürzer als sa_family -EINVAL, AF_UNSPEC trennt; dann der
    /// Zustand (verbunden oder lauschend -EISCONN, offenes nichtblockierendes connect), erst danach sockaddr_in-Länge und Familie.
    /// </summary>
    private static long DoConnectInet(ILinuxProcess p, LinuxUnixSocket s, ulong addr, ulong len)
    {
        int alen = (int)len;
        long r = ReadInetAddress(p, addr, alen, out int family, out uint ip, out int port);
        if (r < 0)
        {
            return r;
        }

        if (alen < 2)
        {
            return -SockErr.EINVAL;
        }

        if (family == AfUnspec)
        {
            return DisconnectInet(s);
        }

        if (s.IsInetDgram)
        {
            // inet_dgram_connect bindet vor ip4_datagram_connect, das erst dann Länge und Familie prüft.
            r = AutobindInet(p, s, 0);
            if (r < 0)
            {
                return r;
            }

            r = CheckConnectTarget(alen, family, ref ip);
            if (r < 0)
            {
                return r;
            }

            s.SetUdpPeer(ip, port);
            return 0;
        }

        if (s.IsConnected)
        {
            // Aufgebaut über einen vollen Rückstand (EINPROGRESS): das erste weitere connect meldet 0 (SS_CONNECTING -> SS_CONNECTED).
            return s.TakePendingConnect() == 0 ? 0 : -InetEISCONN;
        }

        if (s.IsListening)
        {
            return -InetEISCONN; // SS_UNCONNECTED, aber sk_state TCP_LISTEN
        }

        r = s.TakePendingConnect();
        if (r <= 0)
        {
            return r; // verweigertes nichtblockierendes connect: Fehler einmal, danach wieder unverbunden
        }

        r = CheckConnectTarget(alen, family, ref ip);
        if (r < 0)
        {
            return r;
        }

        if (p is not ILinuxUnixNames named)
        {
            return -SockErr.EOPNOTSUPP;
        }

        // Ein Lauschender auf 0.0.0.0 nimmt jede Loopback-Adresse an, einer auf 127.0.0.1 nur diese.
        var listener = named.UnixNames.Ports.Find(false, port) as LinuxUnixSocket;
        (uint lip, _) = listener?.InetName ?? (0, 0);
        if (port == 0 || listener is null || !listener.IsListening || (lip != 0 && lip != ip))
        {
            if (s.NonBlocking)
            {
                // Linux schickt das SYN über lo und meldet die Ablehnung später: EINPROGRESS, dann SO_ERROR und POLLERR.
                s.SetConnectError(UnixECONNREFUSED);
                return -InetEINPROGRESS;
            }

            return -UnixECONNREFUSED;
        }

        r = AutobindInet(p, s, 0);
        if (r < 0)
        {
            return r;
        }

        r = s.ConnectTo(listener, s.NonBlocking, p as ILinuxAbort, overfill: true);
        if (r == 1)
        {
            s.MarkConnectInProgress();
            return -InetEINPROGRESS; // V27b: Rückstand voll, nichtblockierend (Linux verwirft das SYN und wiederholt)
        }

        return r;
    }

    /// <summary>tcp_v4_connect/ip4_datagram_connect: kürzer als sockaddr_in -EINVAL, Familie nicht AF_INET -EAFNOSUPPORT, nur Loopback.</summary>
    private static long CheckConnectTarget(int alen, int family, ref uint ip)
    {
        if (alen < SockaddrInSize)
        {
            return -SockErr.EINVAL;
        }

        if (family != AfInet)
        {
            return -SockErr.EAFNOSUPPORT;
        }

        if (ip == 0)
        {
            ip = LinuxUnixSocket.Loopback; // Linux: connect auf INADDR_ANY geht an die eigene Adresse
        }

        return LinuxUnixSocket.IsLocal(ip) ? 0 : -InetENETUNREACH;
    }

    /// <summary>
    /// connect mit AF_UNSPEC: UDP löst die Gegenstelle (udp_disconnect), ein unverbundenes TCP-Socket vergisst ein verweigertes connect
    /// (tcp_disconnect in TCP_CLOSE). Abweichung: Ein lauschendes oder verbundenes TCP-Socket trennt Linux (Rückstand verwerfen, RST);
    /// hier -EOPNOTSUPP statt eines halben Abbaus (LINUX_ABI.md, V27b).
    /// </summary>
    private static long DisconnectInet(LinuxUnixSocket s)
    {
        if (s.IsInetDgram)
        {
            s.DisconnectUdp();
            return 0;
        }

        if (s.IsListening || s.IsConnected)
        {
            return -SockErr.EOPNOTSUPP;
        }

        s.ForgetConnectError();
        return 0;
    }

    private static long DoGetNameInet(ILinuxProcess p, LinuxUnixSocket s, ulong addr, ulong lenPtr, bool peer)
    {
        long c = CheckNameBuffer(p, addr, lenPtr, out _);
        if (c < 0)
        {
            return c;
        }

        (uint ip, int port) = peer ? s.InetPeerName : s.InetName;
        if (peer && port == 0)
        {
            return -UnixENOTCONN;
        }

        return WriteInetName(p, ip, port, addr, lenPtr);
    }

    /// <summary>
    /// UDP sendto/sendmsg: mit Zieladresse (<paramref name="addr"/> ungleich 0) an diese, sonst an die Gegenstelle aus connect. Die Länge ist ein
    /// int wie bei Linux (V27b); sendmsg kürzt und nullt sie vorher selbst.
    /// </summary>
    private static long SendDatagramInet(ILinuxProcess p, LinuxUnixSocket s, ReadOnlySpan<byte> data, ulong addr, int addrLen)
    {
        if (addr == 0)
        {
            return s.Write(data);
        }

        long r = ReadInetAddress(p, addr, addrLen, out int family, out uint ip, out int port);
        if (r < 0)
        {
            return r;
        }

        // udp_sendmsg: kürzer als sockaddr_in -EINVAL (auch Länge 0 mit Adresse), Familie AF_INET oder AF_UNSPEC, Zielport 0 -EINVAL vor dem Routing.
        if (addrLen < SockaddrInSize)
        {
            return -SockErr.EINVAL;
        }

        if (family != AfInet && family != AfUnspec)
        {
            return -SockErr.EAFNOSUPPORT;
        }

        if (port == 0)
        {
            return -SockErr.EINVAL;
        }

        if (ip == 0)
        {
            ip = LinuxUnixSocket.Loopback;
        }

        if (!LinuxUnixSocket.IsLocal(ip))
        {
            return -InetENETUNREACH;
        }

        r = AutobindInet(p, s, 0);
        return r < 0 ? r : s.SendDatagram(data, ip, port);
    }

    private static long DoSendtoInetDgram(ILinuxProcess p, LinuxUnixSocket s, ulong buf, ulong len, ulong addr, ulong addrLen)
    {
        if (len > LinuxUnixSocket.MaxDatagram)
        {
            return -SockErr.EMSGSIZE;
        }

        byte[] data = new byte[(int)len];
        if (len > 0 && (buf + len < buf || !p.Memory.TryRead(buf, data)))
        {
            return -SockErr.EFAULT;
        }

        return SendDatagramInet(p, s, data, addr, (int)addrLen);
    }

    /// <summary>
    /// UDP recvfrom/recvmsg: ein Datagramm nach <paramref name="buf"/>, Absender als sockaddr_in nach <paramref name="name"/> mit Platz
    /// <paramref name="room"/>. Liefert die Anzahl (mit MSG_TRUNC die ganze Länge) und in <paramref name="truncated"/> MSG_TRUNC.
    /// </summary>
    private static long ReceiveDatagramInet(ILinuxProcess p, LinuxUnixSocket s, Span<byte> buf, int flags, ulong name, int room, out bool truncated)
    {
        truncated = false;
        long r = s.ReceiveDatagram(buf, (flags & MsgPeek) != 0, s.NonBlocking || (flags & MsgDontwait) != 0, p as ILinuxAbort,
            out int full, out uint ip, out int port);
        if (r < 0)
        {
            return r;
        }

        truncated = full > r;
        if (name != 0 && FormatInet(ip, port, name, Math.Min(room, SockaddrInSize), p) < 0)
        {
            return -SockErr.EFAULT;
        }

        return (flags & MsgTrunc) != 0 ? full : r;
    }

    private static long DoRecvfromInetDgram(ILinuxProcess p, LinuxUnixSocket s, ulong buf, ulong len, int flags, ulong addr, ulong addrLenPtr)
    {
        int n = (int)Math.Min(len, (ulong)LinuxUnixSocket.MaxDatagram);
        if (n > 0 && (buf + (ulong)n < buf || !p.Memory.CanWrite(buf, (ulong)n)))
        {
            return -SockErr.EFAULT;
        }

        int room = 0;
        if (addr != 0 && addrLenPtr != 0)
        {
            long c = CheckNameBuffer(p, addr, addrLenPtr, out room);
            if (c < 0)
            {
                return c;
            }
        }

        byte[] tmp = new byte[n];
        long r = ReceiveDatagramInet(p, s, tmp, flags, addr != 0 && addrLenPtr != 0 ? addr : 0, room, out _);
        if (r < 0)
        {
            return r;
        }

        int copied = (int)Math.Min(r, n);
        if (copied > 0 && !p.Memory.TryWrite(buf, tmp.AsSpan(0, copied)))
        {
            return -SockErr.EFAULT;
        }

        if (addr != 0 && addrLenPtr != 0 && !p.Memory.TryWrite(addrLenPtr, BitConverter.GetBytes(SockaddrInSize)))
        {
            return -SockErr.EFAULT;
        }

        return r;
    }

    /// <summary>UDP recvmsg: ein Datagramm in die iovec-Bereiche, Absender nach msg_name (16 Bytes), MSG_TRUNC in msg_flags, keine Hilfsdaten.</summary>
    private static long RecvmsgInetDgram(ILinuxProcess p, LinuxUnixSocket s, ulong msgPtr, byte[] h, int flags, ulong[] bases, ulong[] lens, ulong total)
    {
        ulong namePtr = BitConverter.ToUInt64(h, 0);
        int room = namePtr != 0 ? Math.Max(0, BitConverter.ToInt32(h, MsgNamelenAt)) : 0;
        if (room > 0 && (namePtr + (ulong)room < namePtr || !p.Memory.CanWrite(namePtr, (ulong)Math.Min(room, SockaddrInSize))))
        {
            return -SockErr.EFAULT;
        }

        byte[] tmp = new byte[(int)Math.Min(total, (ulong)LinuxUnixSocket.MaxDatagram)];
        long r = ReceiveDatagramInet(p, s, tmp, flags, namePtr, room, out bool truncated);
        if (r < 0)
        {
            return r;
        }

        int copied = (int)Math.Min(r, tmp.Length);
        if (ScatterIov(p, bases, lens, tmp.AsSpan(0, copied)) < copied)
        {
            return -SockErr.EFAULT;
        }

        BitConverter.TryWriteBytes(h.AsSpan(MsgNamelenAt, 4), namePtr != 0 ? SockaddrInSize : 0);
        BitConverter.TryWriteBytes(h.AsSpan(MsgControllenAt, 8), 0UL);
        BitConverter.TryWriteBytes(h.AsSpan(MsgFlagsAt, 4), truncated ? MsgTrunc : 0);
        return p.Memory.TryWrite(msgPtr, h) ? r : -SockErr.EFAULT;
    }

    /// <summary>getsockopt eines AF_INET-Sockets; schreibt den Wert nach <paramref name="v"/> und liefert dessen Größe oder einen Fehler.</summary>
    private static long InetOption(LinuxUnixSocket s, int level, int name, Span<byte> v)
    {
        if (level == SolSocket)
        {
            switch (name)
            {
                case SoType: BitConverter.TryWriteBytes(v, s.Type); return 4;
                case SoError: BitConverter.TryWriteBytes(v, s.TakeError()); return 4;
                case SoSndbuf:
                case SoRcvbuf: BitConverter.TryWriteBytes(v, LinuxUnixSocket.Capacity); return 4;
                case SoAcceptconn: BitConverter.TryWriteBytes(v, s.IsListening ? 1 : 0); return 4;
                case SoProtocol: BitConverter.TryWriteBytes(v, s.Type == LinuxUnixSocket.SockStream ? IpprotoTcp : IpprotoUdp); return 4;
                case SoDomain: BitConverter.TryWriteBytes(v, AfInet); return 4;
                case SoReuseAddr:
                case SoReusePort:
                case SoKeepAlive: BitConverter.TryWriteBytes(v, s.GetOption(name)); return 4;
                default: return -SockErr.ENOPROTOOPT;
            }
        }

        if (level == IpprotoTcp && s.Type == LinuxUnixSocket.SockStream && name == TcpNoDelay)
        {
            BitConverter.TryWriteBytes(v, s.GetOption(-TcpNoDelay));
            return 4;
        }

        return -SockErr.ENOPROTOOPT;
    }

    /// <summary>
    /// setsockopt eines AF_INET-Sockets: SO_REUSEADDR, SO_REUSEPORT, SO_KEEPALIVE und TCP_NODELAY werden gemerkt (ohne Wirkung auf Loopback,
    /// siehe <see cref="LinuxInetPorts"/>), Puffergrößen still angenommen; alles andere -ENOPROTOOPT statt einer stillen Abweichung.
    /// </summary>
    private static long DoSetsockoptInet(ILinuxProcess p, LinuxUnixSocket s, int level, int name, ulong val, ulong len)
    {
        bool known = (level == SolSocket && name is SoReuseAddr or SoReusePort or SoKeepAlive or SoSndbuf or SoRcvbuf)
            || (level == IpprotoTcp && s.Type == LinuxUnixSocket.SockStream && name == TcpNoDelay);
        if (!known)
        {
            return -SockErr.ENOPROTOOPT;
        }

        if (len < 4)
        {
            return -SockErr.EINVAL;
        }

        Span<byte> b = stackalloc byte[4];
        if (!p.Memory.TryRead(val, b))
        {
            return -SockErr.EFAULT;
        }

        if (name is SoSndbuf or SoRcvbuf && level == SolSocket)
        {
            return 0;
        }

        _ = s.SetOption(level == IpprotoTcp ? -name : name, BitConverter.ToInt32(b));
        return 0;
    }
}
