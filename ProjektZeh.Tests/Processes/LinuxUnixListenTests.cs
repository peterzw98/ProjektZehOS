using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V16 (Teil senior2): socket, bind, listen, connect, accept4, getsockname/getpeername mit abstrakten und Pfadnamen.</summary>
[TestClass]
public sealed class LinuxUnixListenTests
{
    private const ulong Page = 4096;
    private const long Read = 0, Write = 1, Close = 3, Poll = 7, Fcntl = 72, Unlink = 87, Open = 2;
    private const int Stream = 1, Seqpacket = 5, Dgram = 2, SockNonblock = 0x800, SockCloexec = 0x80000;
    private const long EAGAIN = 11, EINVAL = 22, EAFNOSUPPORT = 97, ESOCKTNOSUPPORT = 94, EADDRINUSE = 98, EISCONN = 106, ENOTCONN = 107,
        ECONNREFUSED = 111, ENOENT = 2, EPROTOTYPE = 91;

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

    // Bereiche je Aufruf getrennt, damit Aufrufe aus zwei Threads sich nicht überschreiben.
    private ulong Addr => _mem;
    private ulong Name => _mem + 0x200;
    private ulong Len => _mem + 0x300;
    private ulong Buf => _mem + 0x1000;
    private ulong Addr2 => _mem + 0x400;

    public LinuxUnixListenTests()
    {
        var term = new TerminalDescriptor(new Lines());
        _p = new LinuxUserProcess(new Pages(), term, 1, 64 << 20, new Host(), new LinuxFileSystem(new MemoryFileStore(), term));
        _p.StartThread = _ => true;
        _mem = _p.MapAnonymous(16 * Page, 3);
        Assert.AreNotEqual(0UL, _mem);
    }

    private long Call(long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3 };
        return LinuxSyscalls.Dispatch(ref a, _p);
    }

    private int Sock(int type) => (int)Call(LinuxSyscalls.Socket, 1, (ulong)type, 0);

    /// <summary>sockaddr_un bei <paramref name="at"/>; liefert die Länge wie in unix.c (abstrakt: genau, Pfad: Familie + Name + Null).</summary>
    private ulong PutAddr(ulong at, string name, bool isAbstract)
    {
        byte[] n = Encoding.ASCII.GetBytes(name);
        var b = new byte[LinuxUnixAddress.MaxLength];
        b[0] = 1;
        n.CopyTo(b, isAbstract ? 3 : 2);
        int len = isAbstract ? 3 + n.Length : 3 + n.Length;
        Assert.IsTrue(_p.TryWrite(at, b.AsSpan(0, len)));
        return (ulong)len;
    }

    private long Bind(int fd, string name, bool isAbstract) => Call(LinuxSyscalls.Bind, (ulong)fd, Addr, PutAddr(Addr, name, isAbstract));

    private long Connect(int fd, string name, bool isAbstract, ulong at = 0)
    {
        ulong a = at == 0 ? Addr2 : at;
        return Call(LinuxSyscalls.Connect, (ulong)fd, a, PutAddr(a, name, isAbstract));
    }

    /// <summary>getsockname/getpeername/accept-Name: Rückgabe, gemeldete Länge und Name ohne Familie.</summary>
    private long GetName(long nr, int fd, out int len, out string name)
    {
        Assert.IsTrue(_p.TryWrite(Len, BitConverter.GetBytes(LinuxUnixAddress.MaxLength)));
        long r = Call(nr, (ulong)fd, Name, Len);
        var lb = new byte[4];
        Assert.IsTrue(_p.TryRead(Len, lb));
        len = BitConverter.ToInt32(lb);
        var nb = new byte[Math.Max(0, Math.Min(len, LinuxUnixAddress.MaxLength) - 2)];
        Assert.IsTrue(_p.TryRead(Name + 2, nb));
        name = Encoding.ASCII.GetString(nb).Trim('\0'); // abstrakt: führendes Nullbyte, Pfad: abschließendes
        return r;
    }

    private long WriteBytes(int fd, string s)
    {
        Assert.IsTrue(_p.TryWrite(Buf, Encoding.ASCII.GetBytes(s)));
        return Call(Write, (ulong)fd, Buf, (ulong)s.Length);
    }

    private string ReadBytes(int fd, out long r)
    {
        r = Call(Read, (ulong)fd, Buf + 0x800, 64);
        var b = new byte[Math.Max(0, (int)r)];
        Assert.IsTrue(_p.TryRead(Buf + 0x800, b));
        return Encoding.ASCII.GetString(b);
    }

    private short PollIn(int fd)
    {
        var pfd = new byte[8];
        BitConverter.TryWriteBytes(pfd.AsSpan(0, 4), fd);
        BitConverter.TryWriteBytes(pfd.AsSpan(4, 2), (short)1);
        Assert.IsTrue(_p.TryWrite(Buf + 0x700, pfd));
        _ = Call(Poll, Buf + 0x700, 1, 0);
        var rb = new byte[2];
        Assert.IsTrue(_p.TryRead(Buf + 0x706, rb));
        return BitConverter.ToInt16(rb);
    }

    [TestMethod]
    public void SocketPrueftFamilieTypUndFlags()
    {
        Assert.AreEqual(-EAFNOSUPPORT, Call(LinuxSyscalls.Socket, 2, Stream, 0));
        Assert.AreEqual(-ESOCKTNOSUPPORT, Call(LinuxSyscalls.Socket, 1, Dgram, 0));
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Socket, 1, Stream | 0x40, 0), "unbekanntes Flag");
        int s = Sock(Stream | SockCloexec | SockNonblock);
        Assert.IsTrue(s >= 0);
        Assert.AreEqual(1, Call(Fcntl, (ulong)s, 1 /* F_GETFD */));
        Assert.AreEqual(0x802, Call(Fcntl, (ulong)s, 3 /* F_GETFL */), "O_RDWR|O_NONBLOCK");
    }

    [TestMethod]
    public void AbstrakterNameVerbindetNimmtAnUndMeldetNamen()
    {
        int s = Sock(Stream | SockCloexec);
        int u = Sock(Stream);
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Listen, (ulong)u, 1), "listen unbenannt");
        Assert.AreEqual(0, Bind(s, "zehos-v16", true));
        Assert.AreEqual(-EADDRINUSE, Bind(u, "zehos-v16", true));
        Assert.AreEqual(-EINVAL, Bind(s, "anders", true), "schon gebunden");
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)s, 4));
        Assert.AreEqual(0, PollIn(s), "keine wartende Verbindung");

        int c = Sock(Stream | SockNonblock);
        Assert.AreEqual(0, Connect(c, "zehos-v16", true));
        Assert.AreEqual(-EISCONN, Connect(c, "zehos-v16", true));
        Assert.AreEqual(1, PollIn(s), "POLLIN: Verbindung wartet");
        Assert.AreEqual(2, WriteBytes(c, "hi"), "Senden schon vor accept");

        Assert.IsTrue(_p.TryWrite(Len, BitConverter.GetBytes(LinuxUnixAddress.MaxLength)));
        int x = (int)Call(LinuxSyscalls.Accept4, (ulong)s, Name, Len, SockCloexec);
        Assert.IsTrue(x >= 0);
        Assert.AreEqual(1, Call(Fcntl, (ulong)x, 1), "accept4 SOCK_CLOEXEC");
        var lb = new byte[4];
        Assert.IsTrue(_p.TryRead(Len, lb));
        Assert.AreEqual(2, BitConverter.ToInt32(lb), "Verbindender unbenannt");

        Assert.AreEqual("hi", ReadBytes(x, out _));
        Assert.AreEqual(2, WriteBytes(x, "zu"));
        Assert.AreEqual("zu", ReadBytes(c, out _));
        ReadBytes(c, out long r);
        Assert.AreEqual(-EAGAIN, r);

        Assert.AreEqual(0, GetName(LinuxSyscalls.Getsockname, s, out int len, out string name));
        Assert.AreEqual(12, len);
        Assert.AreEqual("zehos-v16", name);
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getpeername, c, out len, out name));
        Assert.AreEqual(12, len);
        Assert.AreEqual("zehos-v16", name);
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getsockname, x, out len, out _), "angenommenes Ende trägt den Namen des Lauschenden");
        Assert.AreEqual(12, len);
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getpeername, x, out len, out _));
        Assert.AreEqual(2, len);
        Assert.AreEqual(-ENOTCONN, GetName(LinuxSyscalls.Getpeername, s, out _, out _));

        Assert.AreEqual(0, Call(Fcntl, (ulong)s, 4 /* F_SETFL */, 0x800));
        Assert.AreEqual(-EAGAIN, Call(LinuxSyscalls.Accept, (ulong)s, 0, 0));
        Assert.AreEqual(-ECONNREFUSED, Connect(Sock(Stream), "fehlt", true));

        Assert.AreEqual(0, Call(Close, (ulong)x));
        ReadBytes(c, out r);
        Assert.AreEqual(0, r, "EOF nach close der Gegenseite");
        Assert.AreEqual(0, Call(Close, (ulong)s));
        Assert.AreEqual(0, Bind(Sock(Stream), "zehos-v16", true), "Name nach dem letzten close frei");
    }

    [TestMethod]
    public void PfadnameLegtDateiAnUndUnlinkLoestIhn()
    {
        int s = Sock(Stream);
        Assert.AreEqual(0, Bind(s, "sock", false));
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)s, 2));
        Assert.AreEqual(-EADDRINUSE, Bind(Sock(Stream), "sock", false));
        int c = Sock(Stream);
        Assert.AreEqual(0, Connect(c, "sock", false));
        int x = (int)Call(LinuxSyscalls.Accept, (ulong)s, 0, 0);
        Assert.IsTrue(x >= 0);
        Assert.AreEqual(2, WriteBytes(c, "pf"));
        Assert.AreEqual("pf", ReadBytes(x, out _));
        Assert.AreEqual(0, GetName(LinuxSyscalls.Getsockname, s, out int len, out string name));
        Assert.AreEqual(7, len);
        Assert.AreEqual("sock", name);

        Assert.AreEqual(-EPROTOTYPE, Connect(Sock(Seqpacket), "sock", false), "Pfad mit anderem Typ");
        PutAddr(Buf, "datei", false);
        Assert.IsTrue(_p.TryWrite(Buf + 0x600, Encoding.ASCII.GetBytes("datei\0")));
        long fd = Call(Open, Buf + 0x600, 0x41, 0x1a4);
        Assert.IsTrue(fd >= 0);
        Assert.AreEqual(-ECONNREFUSED, Connect(Sock(Stream), "datei", false), "gewöhnliche Datei");

        Assert.IsTrue(_p.TryWrite(Buf + 0x600, Encoding.ASCII.GetBytes("sock\0")));
        Assert.AreEqual(0, Call(Unlink, Buf + 0x600));
        Assert.AreEqual(-ENOENT, Connect(Sock(Stream), "sock", false));
    }

    [TestMethod]
    public void SeqpacketHaeltGrenzenUndAbstrakterNameSuchtNachTyp()
    {
        int s = Sock(Seqpacket);
        Assert.AreEqual(0, Bind(s, "seq", true));
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)s, 1));
        int c = Sock(Seqpacket);
        Assert.AreEqual(0, Connect(c, "seq", true));
        int x = (int)Call(LinuxSyscalls.Accept, (ulong)s, 0, 0);
        Assert.AreEqual(1, WriteBytes(c, "a"));
        Assert.AreEqual(2, WriteBytes(c, "bc"));
        Assert.AreEqual("a", ReadBytes(x, out _));
        Assert.AreEqual("bc", ReadBytes(x, out _));
        Assert.AreEqual(-ECONNREFUSED, Connect(Sock(Stream), "seq", true), "Linux sucht abstrakte Namen nach Typ");
    }

    [TestMethod]
    public void VolleWarteschlangeBlockiertBisAccept()
    {
        int s = Sock(Stream);
        Assert.AreEqual(0, Bind(s, "voll", true));
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)s, 0));
        Assert.AreEqual(0, Connect(Sock(Stream), "voll", true), "Linux: backlog 0 lässt eine wartende Verbindung zu");
        Assert.AreEqual(-EAGAIN, Connect(Sock(Stream | SockNonblock), "voll", true));

        int blocking = Sock(Stream);
        ulong at = _mem + 0x500;
        ulong len = PutAddr(at, "voll", true);
        var t = Task.Run(() =>
        {
            var a = new SyscallArgs { Number = LinuxSyscalls.Connect, A0 = (ulong)blocking, A1 = at, A2 = len };
            return LinuxSyscalls.Dispatch(ref a, _p);
        });
        Thread.Sleep(80);
        Assert.IsFalse(t.IsCompleted, "connect wartet auf Platz");
        Assert.IsTrue(Call(LinuxSyscalls.Accept, (ulong)s, 0, 0) >= 0);
        Assert.IsTrue(t.Wait(5000));
        Assert.AreEqual(0, t.Result);
    }

    [TestMethod]
    public void BlockierendesAcceptWachtBeimConnectAuf()
    {
        int s = Sock(Stream);
        Assert.AreEqual(0, Bind(s, "warte", true));
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)s, 1));
        var t = Task.Run(() =>
        {
            var a = new SyscallArgs { Number = LinuxSyscalls.Accept, A0 = (ulong)s };
            return LinuxSyscalls.Dispatch(ref a, _p);
        });
        Thread.Sleep(80);
        Assert.IsFalse(t.IsCompleted, "accept wartet");
        Assert.AreEqual(0, Connect(Sock(Stream), "warte", true));
        Assert.IsTrue(t.Wait(5000));
        Assert.IsTrue(t.Result >= 0);
    }

    [TestMethod]
    public void CloseDesLauschendenBeendetNichtAngenommeneVerbindungen()
    {
        int s = Sock(Stream);
        Assert.AreEqual(0, Bind(s, "weg", true));
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)s, 1));
        int c = Sock(Stream);
        Assert.AreEqual(0, Connect(c, "weg", true));
        Assert.AreEqual(0, Call(Close, (ulong)s));
        ReadBytes(c, out long r);
        Assert.AreEqual(0, r, "Gegenende geschlossen: EOF");
        Assert.AreEqual(-ECONNREFUSED, Connect(Sock(Stream), "weg", true));
    }

    [TestMethod]
    public void RecvfromMeldetDenNamenDesSenders()
    {
        int s = Sock(Stream);
        Assert.AreEqual(0, Bind(s, "srv", true));
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)s, 1));
        int c = Sock(Stream);
        Assert.AreEqual(0, Connect(c, "srv", true));
        int x = (int)Call(LinuxSyscalls.Accept, (ulong)s, 0, 0);
        Assert.AreEqual(1, WriteBytes(x, "q"));
        Assert.IsTrue(_p.TryWrite(Len, BitConverter.GetBytes(LinuxUnixAddress.MaxLength)));
        var a = new SyscallArgs { Number = LinuxSyscalls.Recvfrom, A0 = (ulong)c, A1 = Buf, A2 = 16, A3 = 0, A4 = Name, A5 = Len };
        Assert.AreEqual(1, LinuxSyscalls.Dispatch(ref a, _p));
        var lb = new byte[4];
        Assert.IsTrue(_p.TryRead(Len, lb));
        Assert.AreEqual(6, BitConverter.ToInt32(lb), "abstrakt \\0srv");
        Assert.AreEqual(1, WriteBytes(c, "r"));
        Assert.IsTrue(_p.TryWrite(Len, BitConverter.GetBytes(LinuxUnixAddress.MaxLength)));
        a = new SyscallArgs { Number = LinuxSyscalls.Recvfrom, A0 = (ulong)x, A1 = Buf, A2 = 16, A3 = 0, A4 = Name, A5 = Len };
        Assert.AreEqual(1, LinuxSyscalls.Dispatch(ref a, _p));
        Assert.IsTrue(_p.TryRead(Len, lb));
        Assert.AreEqual(0, BitConverter.ToInt32(lb), "unbenannter Sender: Länge 0");
    }

    [TestMethod]
    public void AngenommenesEndeIstGebundenUndVerbunden()
    {
        // Review V16 P3 (1): das angenommene Ende trägt den Namen des Lauschenden; bind darauf ist wie bei Linux -EINVAL.
        int s = Sock(Stream);
        Assert.AreEqual(0, Bind(s, "acc", true));
        Assert.AreEqual(0, Call(LinuxSyscalls.Listen, (ulong)s, 1));
        int c = Sock(Stream);
        Assert.AreEqual(0, Connect(c, "acc", true));
        int x = (int)Call(LinuxSyscalls.Accept, (ulong)s, 0, 0);
        Assert.IsTrue(x >= 0);
        Assert.AreEqual(-EINVAL, Bind(x, "neu", true), "bind auf angenommenes Ende");
        Assert.AreEqual(0, Bind(Sock(Stream), "neu", true), "fehlgeschlagenes bind belegt keinen Namen");
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Listen, (ulong)x, 1), "listen auf angenommenes Ende");
        Assert.AreEqual(-EISCONN, Connect(x, "acc", true), "connect auf angenommenes Ende");
        Assert.AreEqual(-EINVAL, Call(LinuxSyscalls.Listen, (ulong)c, 1), "listen auf verbundenes Ende");
    }

    [TestMethod]
    public void ListenGegenConnectDesselbenSocketsSchliesstSichAus()
    {
        // Review V16 P3 (4): listen und connect desselben Sockets nebenläufig; genau eines gewinnt, nie lauschend und verbunden zugleich.
        var names = new LinuxUnixNames();
        var listener = new LinuxUnixSocket(Stream, false, 1);
        Assert.IsTrue(listener.TryBeginBind());
        listener.CompleteBind(LinuxUnixAddress.Abstract(Encoding.ASCII.GetBytes("\0ziel")), names);
        Assert.AreEqual(0, listener.Listen(LinuxUnixSocket.MaxBacklog));
        for (int i = 0; i < 200; i++)
        {
            var u = new LinuxUnixSocket(Stream, false, 1);
            Assert.IsTrue(u.TryBeginBind());
            u.CompleteBind(LinuxUnixAddress.Abstract(Encoding.ASCII.GetBytes("\0r" + i)), names);
            long lr = 1, cr = 1;
            using var go = new ManualResetEventSlim();
            var t = Task.Run(() => { go.Wait(); cr = u.ConnectTo(listener, true, null); });
            go.Set();
            lr = u.Listen(1);
            t.Wait();
            Assert.IsFalse(u.IsListening && u.IsConnected, $"Runde {i}: listen {lr}, connect {cr}");
            Assert.IsTrue((lr == 0) != (cr == 0), $"Runde {i}: genau eines gewinnt (listen {lr}, connect {cr})");
            u.Close();
            while (listener.Accept(true, null, out LinuxUnixSocket? x) == 0)
            {
                x!.Close();
            }
        }

        listener.Close();
    }
}
