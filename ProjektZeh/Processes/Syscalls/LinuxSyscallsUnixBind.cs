using System;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// V16 (Teil senior2): socket, bind, listen, connect, accept/accept4, getsockname, getpeername für AF_UNIX (Stream, Seqpacket) mit
/// abstrakten Namen und Pfadnamen. Namen liegen in <see cref="LinuxUnixNames"/> des Laufs (<see cref="ILinuxUnixNames"/>); ohne sie
/// (Ersatzprozesse) liefern bind und connect -EOPNOTSUPP. Die Dateisperre gilt nur für Pfadauflösung, Dateitabelle und Namensdatei,
/// nie während connect oder accept warten.
/// </summary>
internal static partial class LinuxSyscalls
{
    private const long UnixEOPNOTSUPP = 95, UnixECONNREFUSED = 111, UnixEPROTOTYPE = 91, UnixENOTCONN = 107, UnixESOCKTNOSUPPORT = 94;
    private const int SockTypeMask = 0xf;

    private static long DoSocket(ILinuxProcess p, ILinuxFiles f, int domain, int type, int protocol)
    {
        int kind = type & SockTypeMask;
        if ((type & ~(SockTypeMask | SockNonblock | SockCloexec)) != 0 || kind == 0)
        {
            return -SockErr.EINVAL; // Linux: unbekannte Flags oder Typ 0 vor der Familie
        }

        if (domain == AfInet)
        {
            return DoSocketInet(p, f, kind, type, protocol); // V27: nur Loopback
        }

        if (domain != AfUnix)
        {
            return -SockErr.EAFNOSUPPORT; // AF_INET6 und andere: kein Netz für Linux-Programme (LINUX_ABI.md)
        }

        if (protocol != 0 && protocol != AfUnix)
        {
            return -SockErr.EPROTONOSUPPORT;
        }

        if (kind != LinuxUnixSocket.SockStream && kind != LinuxUnixSocket.SockSeqpacket)
        {
            return -UnixESOCKTNOSUPPORT; // SOCK_DGRAM fehlt (LINUX_ABI.md)
        }

        var s = new LinuxUnixSocket(kind, (type & SockNonblock) != 0, p.Pid);
        lock (f.FileGate)
        {
            int fd = f.AddDescriptor(s, 0);
            if (fd < 0)
            {
                s.Close();
                return fd;
            }

            MarkCloseOnExec(f, fd, (type & SockCloexec) != 0);
            return fd;
        }
    }

    /// <summary>Liest eine sockaddr_un; die Länge prüft <see cref="LinuxUnixAddress.Parse"/> (2 bis 110, sonst -EINVAL).</summary>
    private static long ReadUnixAddress(ILinuxProcess p, ulong addr, ulong len, out LinuxUnixAddress address)
    {
        address = LinuxUnixAddress.Unnamed;
        if (len > LinuxUnixAddress.MaxLength || len < 2)
        {
            return -SockErr.EINVAL;
        }

        byte[] raw = new byte[(int)len];
        if (addr + len < addr || !p.Memory.TryRead(addr, raw))
        {
            return -SockErr.EFAULT;
        }

        return LinuxUnixAddress.Parse(raw, out address);
    }

    private static long DoBind(ILinuxProcess p, ILinuxFiles f, int fd, ulong addr, ulong len)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        if (s.IsInet)
        {
            return DoBindInet(p, s, addr, len);
        }

        long r = ReadUnixAddress(p, addr, len, out LinuxUnixAddress a);
        if (r < 0)
        {
            return r;
        }

        if (p is not ILinuxUnixNames named)
        {
            return -UnixEOPNOTSUPP;
        }

        if (!s.TryBeginBind())
        {
            return -SockErr.EINVAL; // schon gebunden (auch ein angenommenes Ende, das den Namen des Lauschenden trägt)
        }

        LinuxUnixNames names = named.UnixNames;
        if (a.IsUnnamed)
        {
            byte[] auto = names.Autobind(s); // Länge 2: automatischer abstrakter Name wie unix_autobind
            if (auto.Length == 0)
            {
                s.CancelBind();
                return -105; // ENOSPC wäre Linux; alle 2^20 Namen belegt
            }

            s.CompleteBind(LinuxUnixAddress.Abstract(auto), names);
            return 0;
        }

        if (a.IsAbstract)
        {
            r = names.BindAbstract(a.Key, s);
        }
        else
        {
            lock (f.FileGate)
            {
                r = LinuxPath.Resolve(p, f, LinuxPath.AtFdcwd, a.Path, out string path);
                if (r == 0)
                {
                    r = names.BindPath(f.FileSystem, path, s);
                }
            }
        }

        if (r < 0)
        {
            s.CancelBind();
            return r;
        }

        s.CompleteBind(a, names);
        return 0;
    }

    private static long DoListen(ILinuxProcess p, int fd, int backlog)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        return s is null ? error : s.IsInet ? DoListenInet(p, s, backlog) : s.Listen(backlog);
    }

    private static long DoConnect(ILinuxProcess p, ILinuxFiles f, int fd, ulong addr, ulong len)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        if (s.IsInet)
        {
            return DoConnectInet(p, s, addr, len);
        }

        // unix_validate_addr: connect braucht mehr als die Familie (anders als bind mit Länge 2).
        if (len <= 2)
        {
            return -SockErr.EINVAL;
        }

        long r = ReadUnixAddress(p, addr, len, out LinuxUnixAddress a);
        if (r < 0)
        {
            return r;
        }

        if (p is not ILinuxUnixNames named)
        {
            return -UnixEOPNOTSUPP;
        }

        LinuxUnixSocket? target;
        if (a.IsAbstract)
        {
            // Linux sucht abstrakte Namen nach Typ: ein Socket anderen Typs gilt als nicht vorhanden.
            target = named.UnixNames.FindAbstract(a.Key) as LinuxUnixSocket;
            if (target is null || target.Type != s.Type)
            {
                return -UnixECONNREFUSED;
            }
        }
        else
        {
            object? owner;
            lock (f.FileGate)
            {
                r = LinuxPath.Resolve(p, f, LinuxPath.AtFdcwd, a.Path, out string path);
                owner = null;
                if (r == 0)
                {
                    r = named.UnixNames.FindPath(f.FileSystem, path, out owner);
                }
            }

            if (r < 0)
            {
                return r;
            }

            target = owner as LinuxUnixSocket;
            if (target is null)
            {
                return -UnixECONNREFUSED; // gewöhnliche Datei oder Socket geschlossen
            }

            if (target.Type != s.Type)
            {
                return -UnixEPROTOTYPE; // unix_find_bsd: Pfad gefunden, Typ verschieden
            }
        }

        return s.ConnectTo(target, s.NonBlocking, p as ILinuxAbort);
    }

    private static long DoAccept(ILinuxProcess p, ILinuxFiles f, int fd, ulong addr, ulong lenPtr, int flags)
    {
        if ((flags & ~(SockNonblock | SockCloexec)) != 0)
        {
            return -SockErr.EINVAL;
        }

        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        // Adresszeiger vorher prüfen: Linux verlöre sonst eine schon angenommene Verbindung (dort ebenso, hier vermieden).
        if (addr != 0)
        {
            long c = CheckNameBuffer(p, addr, lenPtr, out _);
            if (c < 0)
            {
                return c;
            }
        }

        long r = s.Accept(s.NonBlocking, p as ILinuxAbort, out LinuxUnixSocket? x);
        if (r < 0)
        {
            return r;
        }

        x!.NonBlocking = (flags & SockNonblock) != 0;
        int nfd;
        lock (f.FileGate)
        {
            nfd = f.AddDescriptor(x, 0);
            if (nfd >= 0)
            {
                MarkCloseOnExec(f, nfd, (flags & SockCloexec) != 0);
            }
        }

        if (nfd < 0)
        {
            LinuxUnixSocket.Discard(x);
            return nfd;
        }

        if (addr != 0 && x.IsInet)
        {
            (uint ip, int port) = x.InetPeerName;
            long w = WriteInetName(p, ip, port, addr, lenPtr);
            if (w < 0)
            {
                return w;
            }
        }
        else if (addr != 0)
        {
            LinuxUnixSocket? peer = x.Peer;
            long w = WriteName(p, peer?.Name ?? LinuxUnixAddress.Unnamed, addr, lenPtr);
            if (w < 0)
            {
                return w; // wie Linux: die Nummer bleibt eingetragen
            }
        }

        return nfd;
    }

    private static long DoGetName(ILinuxProcess p, int fd, ulong addr, ulong lenPtr, bool peer)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        if (s.IsInet)
        {
            return DoGetNameInet(p, s, addr, lenPtr, peer);
        }

        long c = CheckNameBuffer(p, addr, lenPtr, out _);
        if (c < 0)
        {
            return c;
        }

        LinuxUnixAddress name;
        if (peer)
        {
            LinuxUnixSocket? other = s.IsConnected ? s.Peer : null;
            if (other is null)
            {
                return -UnixENOTCONN;
            }

            name = other.Name;
        }
        else
        {
            name = s.Name;
        }

        return WriteName(p, name, addr, lenPtr);
    }

    /// <summary>Liest *lenPtr; -EFAULT ohne Zeiger, -EINVAL negativ.</summary>
    private static long CheckNameBuffer(ILinuxProcess p, ulong addr, ulong lenPtr, out int len)
    {
        len = 0;
        Span<byte> lb = stackalloc byte[4];
        if (lenPtr == 0 || !p.Memory.TryRead(lenPtr, lb))
        {
            return -SockErr.EFAULT;
        }

        len = BitConverter.ToInt32(lb);
        if (len < 0)
        {
            return -SockErr.EINVAL;
        }

        return len > 0 && (addr + (ulong)len < addr || !p.Memory.CanWrite(addr, (ulong)len)) ? -SockErr.EFAULT : 0;
    }

    /// <summary>Schreibt den Namen gekürzt auf *lenPtr und meldet in *lenPtr die volle Länge (wie move_addr_to_user).</summary>
    private static long WriteName(ILinuxProcess p, LinuxUnixAddress name, ulong addr, ulong lenPtr)
    {
        long c = CheckNameBuffer(p, addr, lenPtr, out int len);
        if (c < 0)
        {
            return c;
        }

        Span<byte> buf = stackalloc byte[LinuxUnixAddress.MaxLength + 1];
        int n = name.Format(buf.Slice(0, Math.Min(len, buf.Length)));
        if ((n > 0 && !p.Memory.TryWrite(addr, buf.Slice(0, n))) || !p.Memory.TryWrite(lenPtr, BitConverter.GetBytes(name.Length)))
        {
            return -SockErr.EFAULT;
        }

        return 0;
    }

    /// <summary>
    /// msg_name von recvmsg/recvfrom: Linux (unix_copy_addr) meldet den Namen des Senders, bei unbenanntem Sender Länge 0.
    /// <paramref name="room"/> ist der Platz des Programms; liefert die zu meldende Länge oder -EFAULT.
    /// </summary>
    private static long WriteSenderName(ILinuxProcess p, LinuxUnixSocket s, ulong addr, int room)
    {
        LinuxUnixAddress name = s.Peer?.Name ?? LinuxUnixAddress.Unnamed;
        if (name.IsUnnamed || addr == 0)
        {
            return 0;
        }

        Span<byte> buf = stackalloc byte[LinuxUnixAddress.MaxLength + 1];
        int n = name.Format(buf.Slice(0, Math.Clamp(room, 0, buf.Length)));
        if (n > 0 && !p.Memory.TryWrite(addr, buf.Slice(0, n)))
        {
            return -SockErr.EFAULT;
        }

        return name.Length;
    }
}
