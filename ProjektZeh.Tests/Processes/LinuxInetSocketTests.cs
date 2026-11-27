using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V27: AF_INET nur auf Loopback (TCP über die Unix-Stream-Kanäle, UDP mit eigenem Eingang), Fehlernummern wie Linux 6.12.</summary>
[TestClass]
public sealed class LinuxInetSocketTests
{
    private const ulong Page = 4096;
    private const long Read = 0, Write = 1, Close = 3, Poll = 7;
    private const int Inet = 2, Stream = 1, Dgram = 2, Seqpacket = 5, SockNonblock = 0x800;
    private const long EAGAIN = 11, EINVAL = 22, EAFNOSUPPORT = 97, EPROTONOSUPPORT = 93, ESOCKTNOSUPPORT = 94, EADDRINUSE = 98,
        EADDRNOTAVAIL = 99, ENETUNREACH = 101, ENOTCONN = 107, ECONNREFUSED = 111, EINPROGRESS = 115, EOPNOTSUPP = 95, ENOPROTOOPT = 92,
        EDESTADDRREQ = 89, ECONNABORTED = 103, EISCONN = 106;

    private sealed class Pages : IUserPages
    {
        private readonly Dictionary<ulong, byte[]> _frames = new();
        private ulong _next = 0x100000;
        public ulong AllocateFrame() { _frames[_next] = new byte[Page]; _next += Page; return _next - Page; }
        public void FreeFrame(ulong frame) => _frames.Remove(frame);
        public Span<byte> Frame(ulong frame) => _frames[frame];
        public bool Map(ulong page, ulong frame, MapAccess access) => true;
        public bool Unmap(ulong page) => true;
        public void DestroyTable() { }
    }

    private sealed class Host : ILinuxHost
    {
        public long RealtimeNanos => 0;
        public long MonotonicNanos => 0;
        public bool TryFillRandom(Span<byte> dst) { dst.Fill(7); return true; }
        public void SleepNanos(long ns) { }
    }

    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    private readonly LinuxUserProcess _p;
    private readonly ulong _mem;

    private ulong Addr => _mem;
    private ulong Name => _mem + 0x200;
    private ulong Len => _mem + 0x300;
    private ulong Opt => _mem + 0x340;
    private ulong Addr2 => _mem + 0x400;
    private ulong Buf => _mem + 0x1000;
    private ulong Buf2 => _mem + 0x3000;

    public LinuxInetSocketTests()
    {
        var term = new TerminalDescriptor(new Lines());
        _p = new LinuxUserProcess(new Pages(), term, 1, 64 << 20, new Host(), new LinuxFileSystem(new MemoryFileStore(), term));
        _p.StartThread = _ => true;
        _mem = _p.MapAnonymous(16 * Page, 3);
        Assert.AreNotEqual(0UL, _mem);
    }

    private long Call(long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
        return LinuxSyscalls.Dispatch(ref a, _p);
    }

    private int Sock(int type) => (int)Call(LinuxSyscalls.Socket, Inet, (ulong)type, 0);

    /// <summary>sockaddr_in bei <paramref name="at"/> (Port und Adresse in Netzreihenfolge); liefert 16.</summary>
    private ulong PutIn(ulong at, uint ip, int port, ushort family = Inet)
    {
        var b = new byte[16];
        BitConverter.TryWriteBytes(b.AsSpan(0, 2), family);
        b[2] = (byte)(port >> 8);
        b[3] = (byte)port;
        b[4] = (byte)(ip >> 24);
        b[5] = (byte)(ip >> 16);
        b[6] = (byte)(ip >> 8);
        b[7] = (byte)ip;
        Assert.IsTrue(_p.TryWrite(at, b));
        return 16;
    }

    private long Bind(int fd, uint ip, int port) => Call(LinuxSyscalls.Bind, (ulong)fd, Addr, PutIn(Addr, ip, port));

    private long Connect(int fd, uint ip, int port) => Call(LinuxSyscalls.Connect, (ulong)fd, Addr2, PutIn(Addr2, ip, port));

    /// <summary>getsockname/getpeername: Rückgabe, gemeldete Länge, Adresse und Port.</summary>
    private long GetName(long nr, int fd, out int len, out uint ip, out int port)
    {
        Assert.IsTrue(_p.TryWrite(Len, BitConverter.GetBytes(16)));
        long r = Call(nr, (ulong)fd, Name, Len);
        ReadName(Name, Len, out len, out ip, out port);
        return r;
    }

    private void ReadName(ulong name, ulong lenPtr, out int len, out uint ip, out int port)
    {
        var lb = new byte[4];
        Assert.IsTrue(_p.TryRead(lenPtr, lb));
        len = BitConverter.ToInt32(lb);
        var b = new byte[16];
        Assert.IsTrue(_p.TryRead(name, b));
        port = (b[2] << 8) | b[3];
        ip = ((uint)b[4] << 24) | ((uint)b[5] << 16) | ((uint)b[6] << 8) | b[7];
    }

    private int PortOf(int fd)
    {
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getsockname, fd, out _, out _, out int port));
        return port;
    }

    private long WriteBytes(int fd, string s)
    {
        Assert.IsTrue(_p.TryWrite(Buf, Encoding.ASCII.GetBytes(s)));
        return Call(Write, (ulong)fd, Buf, (ulong)s.Length);
    }

    private string ReadBytes(int fd, out long r)
    {
        r = Call(Read, (ulong)fd, Buf2, 64);
        var b = new byte[Math.Max(0, (int)r)];
        Assert.IsTrue(_p.TryRead(Buf2, b));
        return Encoding.ASCII.GetString(b);
    }

    private short PollEvents(int fd, short events)
    {
        var pfd = new byte[8];
        BitConverter.TryWriteBytes(pfd.AsSpan(0, 4), fd);
        BitConverter.TryWriteBytes(pfd.AsSpan(4, 2), events);
        Assert.IsTrue(_p.TryWrite(Buf + 0x700, pfd));
        _ = Call(Poll, Buf + 0x700, 1, 0);
        var rb = new byte[2];
        Assert.IsTrue(_p.TryRead(Buf + 0x706, rb));
        return BitConverter.ToInt16(rb);
    }

    private long GetOpt(int fd, int level, int name, out int value)
    {
        Assert.IsTrue(_p.TryWrite(Len, BitConverter.GetBytes(4)));
        long r = Call(LinuxSyscalls.Getsockopt, (ulong)fd, (ulong)level, (ulong)name, Opt, Len);
        var b = new byte[4];
        Assert.IsTrue(_p.TryRead(Opt, b));
        value = BitConverter.ToInt32(b);
        return r;
    }

    private long SetOpt(int fd, int level, int name, int value)
    {
        Assert.IsTrue(_p.TryWrite(Opt, BitConverter.GetBytes(value)));
        return Call(LinuxSyscalls.Setsockopt, (ulong)fd, (ulong)level, (ulong)name, Opt, 4);
    }

    private int Listener(out int port)
    {
        int s = Sock(Stream);
        Assert.AreEqual(0, Bind(s, LinuxUnixSocket.Loopback, 0));
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)s, 8));
        port = PortOf(s);
        return s;
    }

    [TestMethod]
    public void SocketPrueftTypUndProtokoll()
    {
        Assert.IsTrue(Sock(Stream) >= 0);
        Assert.IsTrue(Sock(Dgram) >= 0);
        Assert.IsTrue(Call(LinuxSyscalls.Socket, Inet, Stream, 6) >= 0, "IPPROTO_TCP");
        Assert.IsTrue(Call(LinuxSyscalls.Socket, Inet, Dgram, 17) >= 0, "IPPROTO_UDP");
        Assert.AreEqual(-EPROTONOSUPPORT, Call(LinuxSyscalls.Socket, Inet, Stream, 17));
        Assert.AreEqual(-ESOCKTNOSUPPORT, Call(LinuxSyscalls.Socket, Inet, Seqpacket, 0));
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Socket, Inet, 99, 0), "Typ 99 hat Flagbits: EINVAL vor der Familie");
        Assert.AreEqual(-EAFNOSUPPORT, Call(LinuxSyscalls.Socket, 99, Stream, 0));
        Assert.AreEqual(-EAFNOSUPPORT, Call(LinuxSyscalls.Socket, 10, Stream, 0), "AF_INET6 fehlt");
    }

    [TestMethod]
    public void BindVergibtPortUndPrueftAdresse()
    {
        int s = Sock(Stream);
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getsockname, s, out int len, out uint ip, out int port));
        Assert.AreEqual(16, len);
        Assert.AreEqual(0u, ip);
        Assert.AreEqual(0, port);
        Assert.AreEqual(0, Bind(s, LinuxUnixSocket.Loopback, 0));
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getsockname, s, out len, out ip, out port));
        Assert.AreEqual(16, len);
        Assert.AreEqual(LinuxUnixSocket.Loopback, ip);
        Assert.IsTrue(port >= LinuxInetPorts.EphemeralFirst && port <= LinuxInetPorts.EphemeralLast);
        Assert.AreEqual(-EINVAL, Bind(s, LinuxUnixSocket.Loopback, 0), "schon gebunden");

        int dup = Sock(Stream);
        Assert.AreEqual(-EADDRINUSE, Bind(dup, LinuxUnixSocket.Loopback, port));
        Assert.AreEqual(-EADDRINUSE, Bind(dup, 0, port), "0.0.0.0 und 127.0.0.1 teilen den Port");
        Assert.AreEqual(-EADDRNOTAVAIL, Bind(dup, 0xc0000201, 0), "192.0.2.1 ist nicht lokal");
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Bind, (ulong)dup, Addr, 8), "zu kurz");
        Assert.AreEqual(-EAFNOSUPPORT, Call(LinuxSyscalls.Bind, (ulong)dup, Addr, PutIn(Addr, LinuxUnixSocket.Loopback, 0, 1)));
        Assert.AreEqual(0, Call(LinuxSyscalls.Bind, (ulong)dup, Addr, PutIn(Addr, 0, 0, 0)), "AF_UNSPEC mit INADDR_ANY wie Linux");

        int u = Sock(Dgram);
        Assert.AreEqual(0, Bind(u, LinuxUnixSocket.Loopback, port), "UDP hat eigene Ports");

        Assert.AreEqual(0, Call(Close, (ulong)s));
        Assert.AreEqual(0, Bind(Sock(Stream), LinuxUnixSocket.Loopback, port), "nach close frei (kein TIME_WAIT)");
    }

    [TestMethod]
    public void ConnectAcceptEchoUndNamen()
    {
        int ls = Listener(out int port);
        Assert.AreEqual(0, GetOpt(ls, 1, 30 /* SO_ACCEPTCONN */, out int acc));
        Assert.AreEqual(1, acc);

        int c = Sock(Stream);
        Assert.AreEqual(-ENOTCONN, GetName(LinuxSyscalls.Getpeername, c, out _, out _, out _));
        Assert.AreEqual(0, Connect(c, LinuxUnixSocket.Loopback, port));
        int cport = PortOf(c);
        Assert.AreNotEqual(0, cport);
        Assert.AreNotEqual(port, cport);

        Assert.IsTrue(_p.TryWrite(Len, BitConverter.GetBytes(16)));
        int a = (int)Call(LinuxSyscalls.Accept, (ulong)ls, Name, Len);
        Assert.IsTrue(a >= 0);
        ReadName(Name, Len, out int len, out uint ip, out int pport);
        Assert.AreEqual(16, len);
        Assert.AreEqual(LinuxUnixSocket.Loopback, ip);
        Assert.AreEqual(cport, pport, "accept meldet den Port des Clients");
        Assert.AreEqual(port, PortOf(a), "angenommenes Ende trägt den Serverport");
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getpeername, c, out _, out ip, out pport));
        Assert.AreEqual((LinuxUnixSocket.Loopback, port), (ip, pport));

        Assert.AreEqual(5, WriteBytes(c, "hallo"));
        Assert.AreEqual("hallo", ReadBytes(a, out _));
        Assert.AreEqual(1, WriteBytes(a, "!"));
        Assert.AreEqual("!", ReadBytes(c, out _));
        Assert.AreEqual(0, Call(LinuxSyscalls.Shutdown, (ulong)c, 1));
        _ = ReadBytes(a, out long eof);
        Assert.AreEqual(0, eof, "EOF nach SHUT_WR");
        Assert.AreEqual(0, Call(Close, (ulong)a));
        _ = ReadBytes(c, out eof);
        Assert.AreEqual(0, eof);
    }

    [TestMethod]
    public void ListenOhneBindVergibtPort()
    {
        int s = Sock(Stream);
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)s, 1));
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getsockname, s, out _, out uint ip, out int port));
        Assert.AreEqual(0u, ip, "0.0.0.0 wie inet_autobind");
        Assert.AreNotEqual(0, port);
        int c = Sock(Stream);
        Assert.AreEqual(0, Connect(c, 0 /* INADDR_ANY = lokal */, port));
        Assert.AreEqual(-EOPNOTSUPP, Call(LinuxSyscalls.Listen, (ulong)Sock(Dgram), 1));
    }

    [TestMethod]
    public void ConnectOhneLauschendenWirdVerweigert()
    {
        int d = Sock(Stream);
        Assert.AreEqual(0, Bind(d, LinuxUnixSocket.Loopback, 0));
        int dead = PortOf(d);
        Assert.AreEqual(-ECONNREFUSED, Connect(Sock(Stream), LinuxUnixSocket.Loopback, dead), "gebunden, aber nicht lauschend");
        Assert.AreEqual(0, Call(Close, (ulong)d));
        Assert.AreEqual(-ECONNREFUSED, Connect(Sock(Stream), LinuxUnixSocket.Loopback, dead));
        Assert.AreEqual(-ENETUNREACH, Connect(Sock(Stream), 0xc0000201, dead));

        int nb = Sock(Stream | SockNonblock);
        Assert.AreEqual(-EINPROGRESS, Connect(nb, LinuxUnixSocket.Loopback, dead));
        short ev = PollEvents(nb, 4 /* POLLOUT */);
        Assert.AreEqual(4 | 8 | 0x10, ev, "POLLOUT|POLLERR|POLLHUP");
        Assert.AreEqual(0, GetOpt(nb, 1, 4 /* SO_ERROR */, out int err));
        Assert.AreEqual((int)ECONNREFUSED, err);
        Assert.AreEqual(0, GetOpt(nb, 1, 4, out err));
        Assert.AreEqual(0, err, "SO_ERROR einmal");
    }

    [TestMethod]
    public void NichtblockierendesConnectUndAccept()
    {
        int ls = Listener(out int port);
        int c = Sock(Stream | SockNonblock);
        Assert.AreEqual(0, Connect(c, LinuxUnixSocket.Loopback, port));
        Assert.AreEqual(4, PollEvents(c, 4) & 4);
        Assert.AreEqual(0, GetOpt(c, 1, 4, out int err));
        Assert.AreEqual(0, err);
        Assert.AreEqual(1, PollEvents(ls, 1), "lauschendes Socket mit wartender Verbindung: POLLIN");
        Assert.IsTrue(Call(LinuxSyscalls.Accept, (ulong)ls, 0, 0) >= 0);
    }

    [TestMethod]
    public void OptionenWerdenGemerkt()
    {
        int s = Sock(Stream);
        Assert.AreEqual(0, GetOpt(s, 1, 2 /* SO_REUSEADDR */, out int v));
        Assert.AreEqual(0, v);
        Assert.AreEqual(0, SetOpt(s, 1, 2, 5));
        Assert.AreEqual(0, GetOpt(s, 1, 2, out v));
        Assert.AreEqual(1, v);
        Assert.AreEqual(0, SetOpt(s, 6 /* IPPROTO_TCP */, 1 /* TCP_NODELAY */, 1));
        Assert.AreEqual(0, GetOpt(s, 6, 1, out v));
        Assert.AreEqual(1, v);
        Assert.AreEqual(0, GetOpt(s, 1, 39 /* SO_DOMAIN */, out v));
        Assert.AreEqual(Inet, v);
        Assert.AreEqual(0, GetOpt(s, 1, 38 /* SO_PROTOCOL */, out v));
        Assert.AreEqual(6, v);
        Assert.AreEqual(-ENOPROTOOPT, SetOpt(s, 0 /* IPPROTO_IP */, 1, 1));
        Assert.AreEqual(-ENOPROTOOPT, SetOpt(Sock(Dgram), 6, 1, 1), "TCP_NODELAY nur bei TCP");
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Setsockopt, (ulong)s, 1, 2, Opt, 2), "optlen < 4");
    }

    [TestMethod]
    public void UdpSendtoRecvfromMitAbsender()
    {
        int u1 = Sock(Dgram), u2 = Sock(Dgram);
        Assert.AreEqual(0, Bind(u2, LinuxUnixSocket.Loopback, 0));
        int p2 = PortOf(u2);
        Assert.AreEqual(0, PollEvents(u2, 1));
        Assert.IsTrue(_p.TryWrite(Buf, Encoding.ASCII.GetBytes("udp")));
        Assert.AreEqual(3, Call(LinuxSyscalls.Sendto, (ulong)u1, Buf, 3, 0, Addr2, PutIn(Addr2, LinuxUnixSocket.Loopback, p2)));
        int p1 = PortOf(u1);
        Assert.AreNotEqual(0, p1, "sendto bindet automatisch");
        Assert.AreEqual(1, PollEvents(u2, 1));

        Assert.IsTrue(_p.TryWrite(Len, BitConverter.GetBytes(16)));
        Assert.AreEqual(3, Call(LinuxSyscalls.Recvfrom, (ulong)u2, Buf2, 64, 0, Name, Len));
        ReadName(Name, Len, out int len, out uint ip, out int port);
        Assert.AreEqual((16, LinuxUnixSocket.Loopback, p1), (len, ip, port));
        Assert.AreEqual(-EAGAIN, Call(LinuxSyscalls.Recvfrom, (ulong)u2, Buf2, 64, 0x40 /* MSG_DONTWAIT */, 0, 0));

        // Ohne Empfänger verfällt das Datagramm; die Länge zählt trotzdem.
        Assert.AreEqual(3, Call(LinuxSyscalls.Sendto, (ulong)u1, Buf, 3, 0, Addr2, PutIn(Addr2, LinuxUnixSocket.Loopback, 9)));
        Assert.AreEqual(-EDESTADDRREQ, WriteBytes(u1, "x"), "unverbunden ohne Ziel");

        // Verbunden: write/read, Kürzen mit MSG_TRUNC.
        Assert.AreEqual(0, Connect(u1, LinuxUnixSocket.Loopback, p2));
        Assert.AreEqual(5, WriteBytes(u1, "abcde"));
        Assert.AreEqual(5, Call(LinuxSyscalls.Recvfrom, (ulong)u2, Buf2, 2, 0x20 /* MSG_TRUNC */, 0, 0), "MSG_TRUNC liefert die ganze Länge");
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getpeername, u1, out _, out ip, out port));
        Assert.AreEqual((LinuxUnixSocket.Loopback, p2), (ip, port));
    }

    [TestMethod]
    public void UdpBlockierendesRecvWirdGeweckt()
    {
        int u1 = Sock(Dgram), u2 = Sock(Dgram);
        Assert.AreEqual(0, Bind(u2, LinuxUnixSocket.Loopback, 0));
        int p2 = PortOf(u2);
        Task<long> rx = Task.Run(() =>
        {
            var a = new SyscallArgs { Number = LinuxSyscalls.Recvfrom, A0 = (ulong)u2, A1 = Buf2, A2 = 64 };
            return LinuxSyscalls.Dispatch(ref a, _p);
        });
        Assert.IsFalse(rx.Wait(50));
        Assert.IsTrue(_p.TryWrite(Buf, Encoding.ASCII.GetBytes("z")));
        Assert.AreEqual(1, Call(LinuxSyscalls.Sendto, (ulong)u1, Buf, 1, 0, Addr2, PutIn(Addr2, LinuxUnixSocket.Loopback, p2)));
        Assert.IsTrue(rx.Wait(5000));
        Assert.AreEqual(1, rx.Result);
    }

    // V27b (Review-P3 zu V27)

    [TestMethod]
    public void NichtblockierendVerweigertZweitesConnectMeldetFehler()
    {
        int d = Sock(Stream);
        Assert.AreEqual(0, Bind(d, LinuxUnixSocket.Loopback, 0));
        int dead = PortOf(d);

        // __inet_stream_connect: SS_CONNECTING mit TCP_CLOSE -> sock_error, danach wieder SS_UNCONNECTED.
        int nb = Sock(Stream | SockNonblock);
        Assert.AreEqual(-EINPROGRESS, Connect(nb, LinuxUnixSocket.Loopback, dead));
        Assert.AreEqual(-ECONNREFUSED, Connect(nb, LinuxUnixSocket.Loopback, dead), "zweites connect liefert den Fehler");
        Assert.AreEqual(0, GetOpt(nb, 1, 4, out int err));
        Assert.AreEqual(0, err, "Fehler vom zweiten connect verbraucht");
        Assert.AreEqual(-EINPROGRESS, Connect(nb, LinuxUnixSocket.Loopback, dead), "drittes connect ist ein neuer Versuch");

        // Fehler schon mit SO_ERROR abgeholt: ECONNABORTED.
        int nb2 = Sock(Stream | SockNonblock);
        Assert.AreEqual(-EINPROGRESS, Connect(nb2, LinuxUnixSocket.Loopback, dead));
        Assert.AreEqual(0, GetOpt(nb2, 1, 4, out err));
        Assert.AreEqual((int)ECONNREFUSED, err);
        Assert.AreEqual(-ECONNABORTED, Connect(nb2, LinuxUnixSocket.Loopback, dead));

        // Inzwischen lauscht jemand: das zweite connect meldet trotzdem erst den alten Fehler.
        int nb3 = Sock(Stream | SockNonblock);
        Assert.AreEqual(-EINPROGRESS, Connect(nb3, LinuxUnixSocket.Loopback, dead));
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)d, 4));
        Assert.AreEqual(-ECONNREFUSED, Connect(nb3, LinuxUnixSocket.Loopback, dead));
        Assert.AreEqual(0, Connect(nb3, LinuxUnixSocket.Loopback, dead));
    }

    [TestMethod]
    public void NichtblockierendBeiVollemRueckstandEinprogress()
    {
        int ls = Sock(Stream);
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)ls, 0));
        int port = PortOf(ls);
        int c1 = Sock(Stream | SockNonblock);
        Assert.AreEqual(0, Connect(c1, LinuxUnixSocket.Loopback, port), "Rückstand 0 nimmt eine Verbindung");
        int c2 = Sock(Stream | SockNonblock);
        Assert.AreEqual(-EINPROGRESS, Connect(c2, LinuxUnixSocket.Loopback, port), "voll: EINPROGRESS statt EAGAIN");
        Assert.AreEqual(4, PollEvents(c2, 4) & 4);
        Assert.AreEqual(0, Connect(c2, LinuxUnixSocket.Loopback, port), "zweites connect nach Aufbau: 0");
        Assert.AreEqual(-EISCONN, Connect(c2, LinuxUnixSocket.Loopback, port));
        Assert.IsTrue(Call(LinuxSyscalls.Accept, (ulong)ls, 0, 0) >= 0);
        int a2 = (int)Call(LinuxSyscalls.Accept, (ulong)ls, 0, 0);
        Assert.IsTrue(a2 >= 0);
        Assert.AreEqual(2, WriteBytes(c2, "ok"));
        Assert.AreEqual("ok", ReadBytes(a2, out _));
    }

    [TestMethod]
    public void ConnectAufLauschendesOderVerbundenesIstEisconn()
    {
        int ls = Listener(out int port);
        Assert.AreEqual(-EISCONN, Connect(ls, LinuxUnixSocket.Loopback, port), "lauschend: EISCONN");
        int c = Sock(Stream);
        Assert.AreEqual(0, Connect(c, LinuxUnixSocket.Loopback, port));
        Assert.AreEqual(-EISCONN, Call(LinuxSyscalls.Connect, (ulong)c, Addr2, 8), "Zustand vor der Adresslänge");
        int a = (int)Call(LinuxSyscalls.Accept, (ulong)ls, 0, 0);
        Assert.AreEqual(-EISCONN, Connect(a, LinuxUnixSocket.Loopback, port), "angenommenes Ende");
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Connect, (ulong)Sock(Stream), Addr2, 8), "unverbunden: zu kurz");
    }

    [TestMethod]
    public void ConnectAfUnspecLoestUdpGegenstelle()
    {
        int u1 = Sock(Dgram), u2 = Sock(Dgram);
        Assert.AreEqual(0, Bind(u2, LinuxUnixSocket.Loopback, 0));
        int p2 = PortOf(u2);
        Assert.AreEqual(0, Connect(u1, LinuxUnixSocket.Loopback, p2));
        Assert.AreNotEqual(0, PortOf(u1));
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Connect, (ulong)u1, Addr2, 1), "kürzer als sa_family");
        _ = PutIn(Addr2, 0, 0, 0);
        Assert.AreEqual(0, Call(LinuxSyscalls.Connect, (ulong)u1, Addr2, 2), "AF_UNSPEC, Länge 2");
        Assert.AreEqual(-ENOTCONN, GetName(LinuxSyscalls.Getpeername, u1, out _, out _, out _));
        Assert.AreEqual(-EDESTADDRREQ, WriteBytes(u1, "x"));
        Assert.AreEqual(0, PortOf(u1), "automatisch gebundener Port wird frei (udp_disconnect ohne BINDPORT_LOCK)");

        // Mit Port gebunden: Port und Adresse bleiben (SOCK_BINDPORT_LOCK, SOCK_BINDADDR_LOCK).
        int u3 = Sock(Dgram);
        Assert.AreEqual(0, Bind(u3, LinuxUnixSocket.Loopback, 40001));
        Assert.AreEqual(0, Connect(u3, LinuxUnixSocket.Loopback, p2));
        Assert.AreEqual(0, Call(LinuxSyscalls.Connect, (ulong)u3, Addr2, PutIn(Addr2, 0, 0, 0)));
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getsockname, u3, out _, out uint ip, out int port));
        Assert.AreEqual((LinuxUnixSocket.Loopback, 40001), (ip, port));

        // Mit Port 0 gebunden: der Port geht, die Adresse bleibt.
        Assert.AreEqual(0, Connect(u2, LinuxUnixSocket.Loopback, 9));
        Assert.AreEqual(0, Call(LinuxSyscalls.Connect, (ulong)u2, Addr2, PutIn(Addr2, 0, 0, 0)));
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getsockname, u2, out _, out ip, out port));
        Assert.AreEqual((LinuxUnixSocket.Loopback, 0), (ip, port));

        // TCP unverbunden: 0; lauschend/verbunden ist eine Abweichung (LINUX_ABI).
        Assert.AreEqual(0, Call(LinuxSyscalls.Connect, (ulong)Sock(Stream), Addr2, PutIn(Addr2, 0, 0, 0)));
        int ls = Listener(out _);
        Assert.AreEqual(-EOPNOTSUPP, Call(LinuxSyscalls.Connect, (ulong)ls, Addr2, PutIn(Addr2, 0, 0, 0)));
    }

    [TestMethod]
    public void AdresslaengeIstInt()
    {
        int u1 = Sock(Dgram), u2 = Sock(Dgram);
        Assert.AreEqual(0, Bind(u2, LinuxUnixSocket.Loopback, 0));
        int p2 = PortOf(u2);
        Assert.IsTrue(_p.TryWrite(Buf, Encoding.ASCII.GetBytes("udp")));
        ulong len = PutIn(Addr2, LinuxUnixSocket.Loopback, p2);
        Assert.AreEqual(3, Call(LinuxSyscalls.Sendto, (ulong)u1, Buf, 3, 0, Addr2, len | 0xffff_ffff_0000_0000UL), "obere Bits zählen nicht");
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Sendto, (ulong)u1, Buf, 3, 0, Addr2, 0xffff_ffffUL), "-1");
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Sendto, (ulong)u1, Buf, 3, 0, Addr2, 0), "Adresse mit Länge 0");
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Sendto, (ulong)u1, Buf, 3, 0, Addr2, 8));
        Assert.AreEqual(0, Call(LinuxSyscalls.Connect, (ulong)u1, Addr2, len | 0x1_0000_0000UL));
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Connect, (ulong)u1, Addr2, 0xffff_fff0UL));

        // sendmsg: msg_namelen über 128 wird gekürzt (__copy_msghdr), 0 heißt ohne Adresse.
        ulong hdr = Buf + 0x800, iov = Buf + 0x900;
        var io = new byte[16];
        BitConverter.TryWriteBytes(io.AsSpan(0, 8), Buf);
        BitConverter.TryWriteBytes(io.AsSpan(8, 8), 3UL);
        Assert.IsTrue(_p.TryWrite(iov, io));
        var h = new byte[56];
        BitConverter.TryWriteBytes(h.AsSpan(0, 8), Addr2);
        BitConverter.TryWriteBytes(h.AsSpan(8, 4), 200);
        BitConverter.TryWriteBytes(h.AsSpan(16, 8), iov);
        BitConverter.TryWriteBytes(h.AsSpan(24, 8), 1UL);
        Assert.IsTrue(_p.TryWrite(hdr, h));
        Assert.AreEqual(3, Call(LinuxSyscalls.Sendmsg, (ulong)u1, hdr, 0));
        BitConverter.TryWriteBytes(h.AsSpan(8, 4), -1);
        Assert.IsTrue(_p.TryWrite(hdr, h));
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Sendmsg, (ulong)u1, hdr, 0));
        Assert.AreEqual(3, Call(LinuxSyscalls.Recvfrom, (ulong)u2, Buf2, 64, 0x40, 0, 0));
        Assert.AreEqual(3, Call(LinuxSyscalls.Recvfrom, (ulong)u2, Buf2, 64, 0x40, 0, 0));
        Assert.AreEqual(-EAGAIN, Call(LinuxSyscalls.Recvfrom, (ulong)u2, Buf2, 64, 0x40, 0, 0));
    }

    [TestMethod]
    public void UdpVerbundenNimmtNurVonGegenstelle()
    {
        int u1 = Sock(Dgram), u2 = Sock(Dgram), u3 = Sock(Dgram);
        Assert.AreEqual(0, Bind(u1, LinuxUnixSocket.Loopback, 0));
        Assert.AreEqual(0, Bind(u2, LinuxUnixSocket.Loopback, 0));
        int p1 = PortOf(u1), p2 = PortOf(u2);
        Assert.AreEqual(0, Connect(u2, LinuxUnixSocket.Loopback, p1));
        Assert.IsTrue(_p.TryWrite(Buf, Encoding.ASCII.GetBytes("abc")));
        Assert.AreEqual(3, Call(LinuxSyscalls.Sendto, (ulong)u3, Buf, 3, 0, Addr2, PutIn(Addr2, LinuxUnixSocket.Loopback, p2)), "fremder Absender");
        Assert.AreEqual(0, PollEvents(u2, 1), "verworfen");
        Assert.AreEqual(2, Call(LinuxSyscalls.Sendto, (ulong)u1, Buf, 2, 0, Addr2, PutIn(Addr2, LinuxUnixSocket.Loopback, p2)));
        Assert.AreEqual(2, Call(LinuxSyscalls.Recvfrom, (ulong)u2, Buf2, 64, 0x40, 0, 0));
        Assert.AreEqual(-EAGAIN, Call(LinuxSyscalls.Recvfrom, (ulong)u2, Buf2, 64, 0x40, 0, 0));
    }

    [TestMethod]
    public void PortFreigabeNurFuerEigentuemer()
    {
        var ports = new LinuxInetPorts();
        object a = new(), b = new();
        Assert.AreEqual(5000, ports.Bind(false, 5000, a));
        ports.Release(false, 5000, b);
        Assert.AreSame(a, ports.Find(false, 5000));
        ports.Release(true, 5000, a);
        Assert.AreSame(a, ports.Find(false, 5000), "UDP-Tabelle getrennt");
        ports.Release(false, 5000, a);
        Assert.IsNull(ports.Find(false, 5000));
    }

    [TestMethod]
    public void ListenNebenBindLiefertNieEinval()
    {
        for (int i = 0; i < 200; i++)
        {
            int s = Sock(Stream);
            _ = PutIn(Addr + 0x40, LinuxUnixSocket.Loopback, 0);
            Task<long> b = Task.Run(() =>
            {
                var a = new SyscallArgs { Number = LinuxSyscalls.Bind, A0 = (ulong)s, A1 = Addr + 0x40, A2 = 16 };
                return LinuxSyscalls.Dispatch(ref a, _p);
            });
            long l = Call(LinuxSyscalls.Listen, (ulong)s, 1);
            Assert.IsTrue(b.Wait(5000));
            Assert.AreEqual(0, l, $"Lauf {i}");
            Assert.AreEqual(0, Call(Close, (ulong)s));
        }
    }
}
