using System;
using System.Collections.Generic;

namespace ProjektZeh.Processes.Syscalls;

/// <summary>
/// Unix-Sockets (V16, Ladybird LibIPC): socketpair, sendmsg/recvmsg mit SCM_RIGHTS, sendto/recvfrom, shutdown, get/setsockopt.
/// Nur mit <see cref="ILinuxFiles"/>. Der Verteiler hält FileGate nur für Tabelle und Dateiverweise, nie während Senden oder Empfangen
/// warten (wie read/write einer Pipe); Benutzerspeicher wird vorher gelesen und nachher geschrieben.
/// </summary>
internal static partial class LinuxSyscalls
{
    public const long Socket = 41, Connect = 42, Accept = 43, Sendto = 44, Recvfrom = 45, Sendmsg = 46, Recvmsg = 47, Shutdown = 48,
        Bind = 49, Listen = 50, Getsockname = 51, Getpeername = 52, Socketpair = 53, Setsockopt = 54, Getsockopt = 55, Accept4 = 288;

    private static class SockErr
    {
        public const long EBADF = 9, EFAULT = 14, EINVAL = 22, EMFILE = 24, ENOTSOCK = 88, EMSGSIZE = 90, ENOPROTOOPT = 92,
            EPROTONOSUPPORT = 93, ESOCKTNOSUPPORT = 94, EOPNOTSUPP = 95, EAFNOSUPPORT = 97, ENOBUFS = 105, EISCONN = 106;
    }

    private const int AfUnix = 1, SolSocket = 1, ScmRights = 1, ScmCredentials = 2;
    private const int SockNonblock = 0x800, SockCloexec = 0x80000;
    private const int MsgOob = 1, MsgPeek = 2, MsgCtrunc = 8, MsgTrunc = 0x20, MsgDontwait = 0x40, MsgWaitall = 0x100, MsgCmsgCloexec = 0x40000000;
    private const int SoType = 3, SoError = 4, SoSndbuf = 7, SoRcvbuf = 8, SoPasscred = 16, SoPeercred = 17, SoAcceptconn = 30, SoPasssec = 34,
        SoProtocol = 38, SoDomain = 39;

    // struct msghdr auf x86-64: name, namelen(+4), iov, iovlen, control, controllen, flags(+4).
    private const int MsgHdrSize = 56, MsgNamelenAt = 8, MsgIovAt = 16, MsgIovlenAt = 24, MsgControlAt = 32, MsgControllenAt = 40, MsgFlagsAt = 48;
    private const int CmsgHdr = 16;

    // Linux: net.core.optmem_max begrenzt die Hilfsdaten einer Nachricht.
    private const ulong MaxControl = 20480;

    private static long DispatchSocket(ref SyscallArgs a, ILinuxProcess p, ILinuxFiles f)
    {
        switch (a.Number)
        {
            case Socketpair: return DoSocketpair(p, f, (int)a.A0, (int)a.A1, (int)a.A2, a.A3);
            case Sendmsg: return DoSendmsg(p, f, (int)a.A0, a.A1, (int)a.A2);
            case Recvmsg: return DoRecvmsg(p, f, (int)a.A0, a.A1, (int)a.A2);
            case Sendto: return DoSendto(p, (int)a.A0, a.A1, a.A2, (int)a.A3, a.A4);
            case Recvfrom: return DoRecvfrom(p, (int)a.A0, a.A1, a.A2, (int)a.A3, a.A4, a.A5);
            case Shutdown: return DoShutdown(p, (int)a.A0, (int)a.A1);
            case Getsockopt: return DoGetsockopt(p, (int)a.A0, (int)a.A1, (int)a.A2, a.A3, a.A4);
            case Setsockopt: return DoSetsockopt(p, (int)a.A0, (int)a.A1, (int)a.A2);
            case Socket: return DoSocket(p, f, (int)a.A0, (int)a.A1, (int)a.A2);
            case Bind: return DoBind(p, f, (int)a.A0, a.A1, (uint)a.A2);
            case Listen: return DoListen(p, (int)a.A0, (int)a.A1);
            case Connect: return DoConnect(p, f, (int)a.A0, a.A1, (uint)a.A2);
            case Accept: return DoAccept(p, f, (int)a.A0, a.A1, a.A2, 0);
            case Accept4: return DoAccept(p, f, (int)a.A0, a.A1, a.A2, (int)a.A3);
            case Getsockname: return DoGetName(p, (int)a.A0, a.A1, a.A2, peer: false);
            case Getpeername: return DoGetName(p, (int)a.A0, a.A1, a.A2, peer: true);
            default: return -ENOSYS;
        }
    }

    /// <summary>Das Socket hinter <paramref name="fd"/> oder -EBADF/-ENOTSOCK in <paramref name="error"/>.</summary>
    private static LinuxUnixSocket? SocketOf(ILinuxProcess p, int fd, out long error)
    {
        ILinuxDescriptor? d = p.GetDescriptor(fd);
        error = d is null ? -SockErr.EBADF : d is LinuxUnixSocket ? 0 : -SockErr.ENOTSOCK;
        return d as LinuxUnixSocket;
    }

    private static long DoSocketpair(ILinuxProcess p, ILinuxFiles f, int domain, int type, int protocol, ulong svPtr)
    {
        int kind = type & ~(SockNonblock | SockCloexec);
        if (domain != AfUnix)
        {
            return -SockErr.EAFNOSUPPORT;
        }

        if (kind != LinuxUnixSocket.SockStream && kind != LinuxUnixSocket.SockSeqpacket)
        {
            return -SockErr.ESOCKTNOSUPPORT; // SOCK_DGRAM fehlt (LINUX_ABI.md)
        }

        if (protocol != 0 && protocol != AfUnix)
        {
            return -SockErr.EPROTONOSUPPORT;
        }

        if (svPtr + 8 < svPtr || !p.Memory.CanWrite(svPtr, 8))
        {
            return -SockErr.EFAULT;
        }

        var (s0, s1) = LinuxUnixSocket.CreatePair(kind, (type & SockNonblock) != 0, p.Pid);
        bool cloexec = (type & SockCloexec) != 0;
        lock (f.FileGate)
        {
            int fd0 = f.AddDescriptor(s0, 0);
            if (fd0 < 0)
            {
                return fd0;
            }

            int fd1 = f.AddDescriptor(s1, 0);
            if (fd1 < 0)
            {
                f.RemoveDescriptor(fd0);
                _ = ReleaseIfUnused(p, s0);
                return fd1;
            }

            MarkCloseOnExec(f, fd0, cloexec);
            MarkCloseOnExec(f, fd1, cloexec);
            Span<byte> b = stackalloc byte[8];
            BitConverter.TryWriteBytes(b.Slice(0, 4), fd0);
            BitConverter.TryWriteBytes(b.Slice(4, 4), fd1);
            if (!p.Memory.TryWrite(svPtr, b))
            {
                f.RemoveDescriptor(fd0);
                f.RemoveDescriptor(fd1);
                _ = ReleaseIfUnused(p, s0);
                _ = ReleaseIfUnused(p, s1);
                return -SockErr.EFAULT;
            }

            return 0;
        }
    }

    /// <summary>Liest den iovec-Vektor; Summe höchstens <see cref="MaxRw"/> (Linux kürzt, hier wie writev -EINVAL).</summary>
    private static long ReadIov(ILinuxProcess p, ulong iov, ulong cnt, out ulong[] bases, out ulong[] lens, out ulong total)
    {
        bases = Array.Empty<ulong>();
        lens = Array.Empty<ulong>();
        total = 0;
        if (cnt > MaxIov)
        {
            return -SockErr.EMSGSIZE;
        }

        bases = new ulong[cnt];
        lens = new ulong[cnt];
        Span<byte> entry = stackalloc byte[16];
        for (ulong i = 0; i < cnt; i++)
        {
            if (iov + (i * 16) < iov || !p.Memory.TryRead(iov + (i * 16), entry))
            {
                return -SockErr.EFAULT;
            }

            bases[i] = BitConverter.ToUInt64(entry.Slice(0, 8));
            lens[i] = BitConverter.ToUInt64(entry.Slice(8, 8));
            if (lens[i] > (ulong)MaxRw || total + lens[i] > (ulong)MaxRw)
            {
                return -SockErr.EINVAL;
            }

            total += lens[i];
        }

        return 0;
    }

    /// <summary>Kopiert Bytes ab <paramref name="skip"/> der iovec-Bereiche in <paramref name="dst"/> (aus dem Benutzerspeicher).</summary>
    private static bool GatherIov(ILinuxProcess p, ulong[] bases, ulong[] lens, ulong skip, Span<byte> dst)
    {
        int at = 0;
        for (int i = 0; i < bases.Length && at < dst.Length; i++)
        {
            if (skip >= lens[i])
            {
                skip -= lens[i];
                continue;
            }

            int n = (int)Math.Min(lens[i] - skip, (ulong)(dst.Length - at));
            ulong src = bases[i] + skip;
            if (src < bases[i] || !p.Memory.TryRead(src, dst.Slice(at, n)))
            {
                return false;
            }

            at += n;
            skip = 0;
        }

        return true;
    }

    /// <summary>Verteilt <paramref name="src"/> auf die iovec-Bereiche; liefert die geschriebene Anzahl (weniger bei einem Fehler).</summary>
    private static int ScatterIov(ILinuxProcess p, ulong[] bases, ulong[] lens, ReadOnlySpan<byte> src)
    {
        int at = 0;
        for (int i = 0; i < bases.Length && at < src.Length; i++)
        {
            int n = (int)Math.Min(lens[i], (ulong)(src.Length - at));
            if (n == 0)
            {
                continue;
            }

            if (!p.Memory.TryWrite(bases[i], src.Slice(at, n)))
            {
                return at;
            }

            at += n;
        }

        return at;
    }

    /// <summary>
    /// Liest die Hilfsdaten von sendmsg und hält die Dateien aus SCM_RIGHTS (unter FileGate). Andere Ebenen als SOL_SOCKET übergeht Linux
    /// bei Unix-Sockets; SCM_CREDENTIALS wird angenommen und nicht weitergereicht (LINUX_ABI.md).
    /// </summary>
    private static long ReadRights(ILinuxProcess p, ILinuxFiles f, ulong control, ulong controlLen, out LinuxSocketFds? fds)
    {
        fds = null;
        if (control == 0 || controlLen == 0)
        {
            return 0;
        }

        if (controlLen > MaxControl)
        {
            return -SockErr.ENOBUFS;
        }

        byte[] c = new byte[(int)controlLen];
        if (control + controlLen < control || !p.Memory.TryRead(control, c))
        {
            return -SockErr.EFAULT;
        }

        var numbers = new List<int>();
        int at = 0;
        while (at + CmsgHdr <= c.Length)
        {
            ulong len = BitConverter.ToUInt64(c, at);
            int level = BitConverter.ToInt32(c, at + 8);
            int type = BitConverter.ToInt32(c, at + 12);
            if (len < CmsgHdr || len > (ulong)(c.Length - at))
            {
                return -SockErr.EINVAL;
            }

            if (level == SolSocket)
            {
                if (type == ScmRights)
                {
                    for (int o = at + CmsgHdr; o + 4 <= at + (int)len; o += 4)
                    {
                        numbers.Add(BitConverter.ToInt32(c, o));
                    }
                }
                else if (type != ScmCredentials)
                {
                    return -SockErr.EINVAL;
                }
            }

            at += (int)((len + 7) & ~7UL); // CMSG_ALIGN
        }

        if (numbers.Count == 0)
        {
            return 0;
        }

        if (numbers.Count > LinuxUnixSocket.MaxFds)
        {
            return -SockErr.EINVAL;
        }

        if (f is not ILinuxInFlight inFlight)
        {
            return -SockErr.EOPNOTSUPP; // ohne Zähler über Prozesse (Ersatzprozesse der Tests) keine Weitergabe
        }

        lock (f.FileGate)
        {
            var files = new ILinuxDescriptor[numbers.Count];
            for (int i = 0; i < files.Length; i++)
            {
                ILinuxDescriptor? d = p.GetDescriptor(numbers[i]);
                if (d is null)
                {
                    return -SockErr.EBADF;
                }

                files[i] = d;
            }

            foreach (ILinuxDescriptor d in files)
            {
                inFlight.HoldInFlight(d);
            }

            fds = new LinuxSocketFds(files, inFlight);
        }

        return 0;
    }

    private static long DoSendmsg(ILinuxProcess p, ILinuxFiles f, int fd, ulong msgPtr, int flags)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        byte[] h = new byte[MsgHdrSize];
        if (msgPtr + MsgHdrSize < msgPtr || !p.Memory.TryRead(msgPtr, h))
        {
            return -SockErr.EFAULT;
        }

        if ((flags & MsgOob) != 0)
        {
            return -SockErr.EOPNOTSUPP;
        }

        // Stream mit Zieladresse: verbunden -EISCONN, sonst -EOPNOTSUPP (wie unix_stream_sendmsg); Seqpacket übergeht sie.
        if (s.Type == LinuxUnixSocket.SockStream && BitConverter.ToUInt32(h, MsgNamelenAt) != 0)
        {
            return s.IsConnected ? -SockErr.EISCONN : -SockErr.EOPNOTSUPP;
        }

        long r = ReadIov(p, BitConverter.ToUInt64(h, MsgIovAt), BitConverter.ToUInt64(h, MsgIovlenAt), out ulong[] bases, out ulong[] lens, out ulong total);
        if (r < 0)
        {
            return r;
        }

        r = ReadRights(p, f, BitConverter.ToUInt64(h, MsgControlAt), BitConverter.ToUInt64(h, MsgControllenAt), out LinuxSocketFds? fds);
        if (r < 0)
        {
            return r;
        }

        r = SendIov(p, s, bases, lens, total, fds, (flags & MsgDontwait) != 0, out bool taken);
        if (fds != null && !taken)
        {
            fds.Release(); // nicht gesendet: die Nachricht hält die Dateien nicht mehr
        }

        return r;
    }

    /// <summary>Sendet die iovec-Bytes in Stücken (Stream) oder als eine Nachricht (Seqpacket); die Dateien reisen mit dem ersten Stück.</summary>
    private static long SendIov(ILinuxProcess p, LinuxUnixSocket s, ulong[] bases, ulong[] lens, ulong total, LinuxSocketFds? fds, bool dontWait, out bool taken)
    {
        taken = false;
        bool nonBlocking = s.NonBlocking || dontWait;
        var abort = p as ILinuxAbort;
        if (s.Type == LinuxUnixSocket.SockSeqpacket)
        {
            if (total > LinuxUnixSocket.Capacity)
            {
                return -SockErr.EMSGSIZE;
            }

            byte[] msg = new byte[(int)total];
            if (!GatherIov(p, bases, lens, 0, msg))
            {
                return -SockErr.EFAULT;
            }

            return s.Send(msg, fds, nonBlocking, abort, out taken);
        }

        // Stream: in Stücken bis 64 KiB, damit ein großes write nicht den ganzen Puffer auf einmal belegt.
        ulong done = 0;
        byte[] chunk = new byte[(int)Math.Min(total, 65536UL)];
        while (done < total)
        {
            int n = (int)Math.Min(total - done, (ulong)chunk.Length);
            if (!GatherIov(p, bases, lens, done, chunk.AsSpan(0, n)))
            {
                return done > 0 ? (long)done : -SockErr.EFAULT;
            }

            long w = s.Send(chunk.AsSpan(0, n), taken ? null : fds, nonBlocking, abort, out bool now);
            taken |= now;
            if (w < 0)
            {
                return done > 0 ? (long)done : w;
            }

            done += (ulong)w;
            if (w < n)
            {
                break;
            }
        }

        return (long)done;
    }

    private static long DoRecvmsg(ILinuxProcess p, ILinuxFiles f, int fd, ulong msgPtr, int flags)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        byte[] h = new byte[MsgHdrSize];
        if (msgPtr + MsgHdrSize < msgPtr || !p.Memory.TryRead(msgPtr, h) || !p.Memory.CanWrite(msgPtr, MsgHdrSize))
        {
            return -SockErr.EFAULT;
        }

        if ((flags & MsgOob) != 0)
        {
            return -SockErr.EOPNOTSUPP;
        }

        long r = ReadIov(p, BitConverter.ToUInt64(h, MsgIovAt), BitConverter.ToUInt64(h, MsgIovlenAt), out ulong[] bases, out ulong[] lens, out ulong total);
        if (r < 0)
        {
            return r;
        }

        // Erst prüfen, dann warten: sonst ginge eine Nachricht an einen ungültigen Zeiger verloren (wie read, V13-Review P2-2).
        for (int i = 0; i < bases.Length; i++)
        {
            if (lens[i] > 0 && (bases[i] + lens[i] < bases[i] || !p.Memory.CanWrite(bases[i], lens[i])))
            {
                return -SockErr.EFAULT;
            }
        }

        ulong control = BitConverter.ToUInt64(h, MsgControlAt);
        ulong controlLen = control == 0 ? 0 : BitConverter.ToUInt64(h, MsgControllenAt);
        bool peek = (flags & MsgPeek) != 0;
        byte[] tmp = new byte[(int)Math.Min(total, (ulong)LinuxUnixSocket.Capacity)];
        r = s.Receive(tmp, peek, (flags & MsgWaitall) != 0, s.NonBlocking || (flags & MsgDontwait) != 0, p as ILinuxAbort, out LinuxSocketFds? fds, out int full);
        if (r < 0)
        {
            return r;
        }

        int outFlags = 0;
        if (s.Type == LinuxUnixSocket.SockSeqpacket && full > r)
        {
            outFlags |= MsgTrunc;
        }

        if (ScatterIov(p, bases, lens, tmp.AsSpan(0, (int)r)) < r)
        {
            if (fds != null && !peek)
            {
                fds.Release();
            }

            return -SockErr.EFAULT;
        }

        ulong controlOut = 0;
        if (fds != null)
        {
            long c = DeliverRights(p, f, fds, control, controlLen, peek, (flags & MsgCmsgCloexec) != 0, ref outFlags, out controlOut);
            if (c < 0)
            {
                return c;
            }
        }

        // msg_namelen: Name des Senders (unix_copy_addr), 0 bei unbenanntem Sender wie nach socketpair.
        ulong namePtr = BitConverter.ToUInt64(h, 0);
        if (namePtr != 0)
        {
            long nl = WriteSenderName(p, s, namePtr, BitConverter.ToInt32(h, MsgNamelenAt));
            if (nl < 0)
            {
                return nl;
            }

            BitConverter.TryWriteBytes(h.AsSpan(MsgNamelenAt, 4), (int)nl);
        }

        BitConverter.TryWriteBytes(h.AsSpan(MsgControllenAt, 8), controlOut);
        BitConverter.TryWriteBytes(h.AsSpan(MsgFlagsAt, 4), outFlags);
        if (!p.Memory.TryWrite(msgPtr, h))
        {
            return -SockErr.EFAULT;
        }

        return s.Type == LinuxUnixSocket.SockSeqpacket && (flags & MsgTrunc) != 0 ? full : r;
    }

    /// <summary>
    /// Trägt empfangene Dateien als neue Nummern ein und schreibt eine SCM_RIGHTS-Nachricht nach <paramref name="control"/>. Was nicht
    /// passt (Puffer zu klein, Tabelle voll), verfällt mit MSG_CTRUNC wie bei Linux. Ohne <paramref name="peek"/> gehen die unterwegs gehaltenen
    /// Verweise auf die neuen Nummern über; mit peek bekommt das Programm zusätzliche Nummern und die Nachricht behält ihre Dateien.
    /// </summary>
    private static long DeliverRights(ILinuxProcess p, ILinuxFiles f, LinuxSocketFds fds, ulong control, ulong controlLen, bool peek, bool cloexec,
        ref int outFlags, out ulong controlOut)
    {
        controlOut = 0;
        int room = controlLen >= CmsgHdr ? (int)Math.Min((controlLen - CmsgHdr) / 4, (ulong)fds.Files.Length) : 0;
        var installed = new List<int>();
        lock (f.FileGate)
        {
            for (int i = 0; i < room; i++)
            {
                int nfd = f.AddDescriptor(fds.Files[i], 0);
                if (nfd < 0)
                {
                    break; // -EMFILE: Linux kürzt und meldet MSG_CTRUNC
                }

                MarkCloseOnExec(f, nfd, cloexec);
                installed.Add(nfd);
            }

            if (installed.Count < fds.Files.Length)
            {
                outFlags |= MsgCtrunc;
            }

            if (installed.Count > 0)
            {
                byte[] c = new byte[CmsgHdr + (installed.Count * 4)];
                BitConverter.TryWriteBytes(c.AsSpan(0, 8), (ulong)c.Length);
                BitConverter.TryWriteBytes(c.AsSpan(8, 4), SolSocket);
                BitConverter.TryWriteBytes(c.AsSpan(12, 4), ScmRights);
                for (int i = 0; i < installed.Count; i++)
                {
                    BitConverter.TryWriteBytes(c.AsSpan(CmsgHdr + (i * 4), 4), installed[i]);
                }

                if (!p.Memory.TryWrite(control, c))
                {
                    // Zurücknehmen: Nummern frei, die Verweise der Nachricht gibt das Freigeben unten ab.
                    foreach (int nfd in installed)
                    {
                        f.RemoveDescriptor(nfd);
                    }

                    installed.Clear();
                    if (!peek)
                    {
                        fds.Release();
                    }

                    return -SockErr.EFAULT;
                }

                controlOut = Math.Min((ulong)((c.Length + 7) & ~7), controlLen); // CMSG_SPACE, höchstens der Puffer
            }

            if (!peek)
            {
                fds.Release(); // die neuen Nummern zählen schon; was nicht eingetragen wurde, schließt hier
            }
        }

        return 0;
    }

    private static long DoSendto(ILinuxProcess p, int fd, ulong buf, ulong len, int flags, ulong addr)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        if ((flags & MsgOob) != 0)
        {
            return -SockErr.EOPNOTSUPP;
        }

        if (addr != 0 && s.Type == LinuxUnixSocket.SockStream)
        {
            return s.IsConnected ? -SockErr.EISCONN : -SockErr.EOPNOTSUPP;
        }

        ulong n = Math.Min(len, (ulong)MaxRw);
        return SendIov(p, s, [buf], [n], n, null, (flags & MsgDontwait) != 0, out _);
    }

    private static long DoRecvfrom(ILinuxProcess p, int fd, ulong buf, ulong len, int flags, ulong addr, ulong addrLenPtr)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        if ((flags & MsgOob) != 0)
        {
            return -SockErr.EOPNOTSUPP;
        }

        int n = (int)Math.Min(len, (ulong)LinuxUnixSocket.Capacity);
        if (n > 0 && (buf + (ulong)n < buf || !p.Memory.CanWrite(buf, (ulong)n)))
        {
            return -SockErr.EFAULT;
        }

        bool peek = (flags & MsgPeek) != 0;
        byte[] tmp = new byte[n];
        long r = s.Receive(tmp, peek, (flags & MsgWaitall) != 0, s.NonBlocking || (flags & MsgDontwait) != 0, p as ILinuxAbort, out LinuxSocketFds? fds, out int full);
        if (fds != null && !peek)
        {
            fds.Release(); // ohne Hilfsdatenpuffer verfallen mitgeschickte Dateien (Linux: MSG_CTRUNC, hier nicht sichtbar)
        }

        if (r < 0)
        {
            return r;
        }

        if (r > 0 && !p.Memory.TryWrite(buf, tmp.AsSpan(0, (int)r)))
        {
            return -SockErr.EFAULT;
        }

        if (addr != 0 && addrLenPtr != 0)
        {
            Span<byte> lb = stackalloc byte[4];
            if (!p.Memory.TryRead(addrLenPtr, lb))
            {
                return -SockErr.EFAULT;
            }

            long nl = WriteSenderName(p, s, addr, BitConverter.ToInt32(lb));
            if (nl < 0 || !p.Memory.TryWrite(addrLenPtr, BitConverter.GetBytes((int)nl)))
            {
                return -SockErr.EFAULT;
            }
        }

        return s.Type == LinuxUnixSocket.SockSeqpacket && (flags & MsgTrunc) != 0 ? full : r;
    }

    private static long DoShutdown(ILinuxProcess p, int fd, int how)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        return how is < 0 or > 2 ? -SockErr.EINVAL : s.Shutdown(how);
    }

    private static long DoGetsockopt(ILinuxProcess p, int fd, int level, int name, ulong val, ulong lenPtr)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        Span<byte> lb = stackalloc byte[4];
        if (!p.Memory.TryRead(lenPtr, lb))
        {
            return -SockErr.EFAULT;
        }

        int len = BitConverter.ToInt32(lb);
        if (len < 0)
        {
            return -SockErr.EINVAL;
        }

        if (level != SolSocket)
        {
            return -SockErr.EOPNOTSUPP; // Unix-Sockets haben keine Optionen eigener Ebene
        }

        Span<byte> v = stackalloc byte[12];
        int size = 4;
        switch (name)
        {
            case SoType: BitConverter.TryWriteBytes(v, s.Type); break;
            case SoError: BitConverter.TryWriteBytes(v, s.TakeError()); break;
            case SoSndbuf:
            case SoRcvbuf: BitConverter.TryWriteBytes(v, LinuxUnixSocket.Capacity); break;
            case SoAcceptconn: BitConverter.TryWriteBytes(v, s.IsListening ? 1 : 0); break;
            case SoPasscred:
            case SoProtocol: BitConverter.TryWriteBytes(v, 0); break;
            case SoDomain: BitConverter.TryWriteBytes(v, AfUnix); break;
            case SoPeercred:
                // struct ucred {pid, uid, gid}: bei socketpair der Erzeuger, wie Linux.
                if (!s.IsConnected)
                {
                    return -107; // ENOTCONN
                }

                size = 12;
                BitConverter.TryWriteBytes(v.Slice(0, 4), s.Peer!.CreatorPid);
                BitConverter.TryWriteBytes(v.Slice(4, 4), (int)p.Uid);
                BitConverter.TryWriteBytes(v.Slice(8, 4), (int)p.Gid);
                break;
            default:
                return -SockErr.ENOPROTOOPT;
        }

        int n = Math.Min(len, size);
        if ((n > 0 && !p.Memory.TryWrite(val, v.Slice(0, n))) || !p.Memory.TryWrite(lenPtr, BitConverter.GetBytes(n)))
        {
            return -SockErr.EFAULT;
        }

        return 0;
    }

    /// <summary>Puffergrößen und SO_PASSCRED/SO_PASSSEC still annehmen (Puffer sind fest); alles andere -ENOPROTOOPT statt einer stillen Abweichung.</summary>
    private static long DoSetsockopt(ILinuxProcess p, int fd, int level, int name)
    {
        LinuxUnixSocket? s = SocketOf(p, fd, out long error);
        if (s is null)
        {
            return error;
        }

        if (level != SolSocket)
        {
            return -SockErr.EOPNOTSUPP;
        }

        return name is SoSndbuf or SoRcvbuf or SoPasscred or SoPasssec ? 0 : -SockErr.ENOPROTOOPT;
    }
}
