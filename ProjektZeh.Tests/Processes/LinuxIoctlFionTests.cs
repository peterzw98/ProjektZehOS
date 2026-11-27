using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V33: ioctl FIONBIO (O_NONBLOCK) und FIONREAD/TIOCINQ auf Pipes, Unix-Sockets, eventfd und inotify.</summary>
[TestClass]
public sealed class LinuxIoctlFionTests
{
    private const ulong Page = 4096;
    private const long Read = 0, Write = 1, Fcntl = 72, Socketpair = 53, Eventfd2 = 290, Pipe2 = 293, InotifyInit1 = 294, AddWatch = 254, RmWatch = 255;
    private const ulong FIONREAD = 0x541B, TIOCINQ = 0x541B, FIONBIO = 0x5421;
    private const long EAGAIN = 11, EBADF = 9, EFAULT = 14, EINVAL = 22, ENOTTY = 25;
    private const int Stream = 1, Dgram = 2, Seqpacket = 5, Nonblock = 0x800;

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

    private ulong Buf => _mem;
    private ulong Arg => _mem + 0x800;
    private ulong Addr => _mem + 0xA00;
    private ulong Data => _mem + 0x1000;

    public LinuxIoctlFionTests()
    {
        var term = new TerminalDescriptor(new Lines());
        _p = new LinuxUserProcess(new Pages(), term, 1, 64 << 20, new Host(), new LinuxFileSystem(new MemoryFileStore(), term));
        _p.StartThread = _ => true;
        _mem = _p.MapAnonymous(4 * Page, 3);
        Assert.AreNotEqual(0UL, _mem);
    }

    private long Call(long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3 };
        return LinuxSyscalls.Dispatch(ref a, _p);
    }

    private (int A, int B) Pair(long nr, ulong a0, ulong a1 = 0, ulong a2 = 0)
    {
        Assert.AreEqual(0L, nr == Pipe2 ? Call(nr, Buf, a0) : Call(nr, a0, a1, a2, Buf));
        var b = new byte[8];
        Assert.IsTrue(_p.TryRead(Buf, b));
        return (BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4));
    }

    private (int A, int B) SocketPair(int type) => Pair(Socketpair, 1, (ulong)type);

    /// <summary>ioctl FIONBIO mit dem int <paramref name="on"/> im Benutzerspeicher.</summary>
    private long SetNonBlocking(int fd, int on)
    {
        Assert.IsTrue(_p.TryWrite(Arg, BitConverter.GetBytes(on)));
        return Call(LinuxSyscalls.Ioctl, (ulong)fd, FIONBIO, Arg);
    }

    /// <summary>ioctl FIONREAD; liefert die Rückgabe und bei 0 die gemeldete Anzahl in <paramref name="bytes"/> (sonst -1).</summary>
    private long Readable(int fd, out int bytes, ulong request = FIONREAD)
    {
        Assert.IsTrue(_p.TryWrite(Arg, BitConverter.GetBytes(-1)));
        long r = Call(LinuxSyscalls.Ioctl, (ulong)fd, request, Arg);
        var b = new byte[4];
        Assert.IsTrue(_p.TryRead(Arg, b));
        bytes = BitConverter.ToInt32(b, 0);
        return r;
    }

    private int ReadableOk(int fd, ulong request = FIONREAD)
    {
        Assert.AreEqual(0L, Readable(fd, out int n, request));
        return n;
    }

    private void Put(int fd, string s)
    {
        byte[] b = Encoding.ASCII.GetBytes(s);
        Assert.IsTrue(_p.TryWrite(Data, b));
        Assert.AreEqual((long)b.Length, Call(Write, (ulong)fd, Data, (ulong)b.Length));
    }

    private long Take(int fd, int len) => Call(Read, (ulong)fd, Data + 0x400, (ulong)len);

    private bool FlagNonBlocking(int fd) => (Call(Fcntl, (ulong)fd, 3) & Nonblock) != 0;

    [TestMethod]
    public void FionbioSetztUndLoeschtONonblockWieFSetfl()
    {
        var (r, w) = Pair(Pipe2, 0);
        Assert.IsFalse(FlagNonBlocking(r));
        Assert.AreEqual(0L, SetNonBlocking(r, 1));
        Assert.IsTrue(FlagNonBlocking(r), "F_GETFL sieht FIONBIO");
        Assert.IsFalse(FlagNonBlocking(w), "nur die offene Datei des Leseendes");
        Assert.AreEqual(-EAGAIN, Take(r, 8), "leere Pipe blockiert nicht mehr");
        Assert.AreEqual(0L, SetNonBlocking(r, 7), "jeder Wert ungleich 0 setzt");
        Assert.IsTrue(FlagNonBlocking(r));
        Assert.AreEqual(0L, SetNonBlocking(r, 0));
        Assert.IsFalse(FlagNonBlocking(r));

        var (s0, s1) = SocketPair(Stream);
        Assert.AreEqual(0L, SetNonBlocking(s0, 1));
        Assert.IsTrue(FlagNonBlocking(s0));
        Assert.IsFalse(FlagNonBlocking(s1));
        Assert.AreEqual(-EAGAIN, Take(s0, 8), "Socket ohne Daten: -EAGAIN statt Warten");

        int e = (int)Call(Eventfd2, 0, 0);
        Assert.AreEqual(0L, SetNonBlocking(e, 1));
        Assert.AreEqual(-EAGAIN, Take(e, 8), "eventfd mit Zähler 0");
        Assert.AreEqual(0L, SetNonBlocking(e, 0));
        Assert.IsFalse(FlagNonBlocking(e));
    }

    [TestMethod]
    public void FionbioFehlerwege()
    {
        var (s0, _) = SocketPair(Stream);
        Assert.AreEqual(-EBADF, SetNonBlocking(99, 1));
        Assert.AreEqual(-EFAULT, Call(LinuxSyscalls.Ioctl, (ulong)s0, FIONBIO, 8), "Zeiger außerhalb");
        Assert.IsFalse(FlagNonBlocking(s0), "nach -EFAULT unverändert");
        Assert.AreEqual(0L, SetNonBlocking(1, 1), "Terminal: wie F_SETFL still angenommen, kein -ENOTTY");
    }

    [TestMethod]
    public void FionreadPipeBeideEnden()
    {
        var (r, w) = Pair(Pipe2, Nonblock);
        Assert.AreEqual(0, ReadableOk(r));
        Put(w, "hallo");
        Put(w, "!!");
        Assert.AreEqual(7, ReadableOk(r));
        Assert.AreEqual(7, ReadableOk(w), "Linux pipe_ioctl: auch das Schreibende meldet den Füllstand");
        Assert.AreEqual(3L, Take(r, 3));
        Assert.AreEqual(4, ReadableOk(r, TIOCINQ));
        Assert.AreEqual(-EFAULT, Call(LinuxSyscalls.Ioctl, (ulong)r, FIONREAD, 8));
        Assert.AreEqual(4, ReadableOk(r), "-EFAULT nimmt nichts heraus");
    }

    [TestMethod]
    public void FionreadSocketStreamZaehltAlleBytes()
    {
        var (s0, s1) = SocketPair(Stream);
        Assert.AreEqual(0, ReadableOk(s0));
        Put(s1, "abc");
        Put(s1, "de");
        Assert.AreEqual(5, ReadableOk(s0));
        Assert.AreEqual(0, ReadableOk(s1), "nur die eigene Empfangsrichtung");
        Assert.AreEqual(2L, Take(s0, 2));
        Assert.AreEqual(3, ReadableOk(s0), "angebrochene Nachricht zählt mit dem Rest");
        Assert.AreEqual(3L, Take(s0, 8));
        Assert.AreEqual(0, ReadableOk(s0));
    }

    [TestMethod]
    public void FionreadSeqpacketSummeDgramErsteNachricht()
    {
        var (q0, q1) = SocketPair(Seqpacket);
        Put(q1, "abc");
        Put(q1, "defg");
        Assert.AreEqual(7, ReadableOk(q0), "Linux unix_inq_len: Seqpacket summiert");

        var (d0, d1) = SocketPair(Dgram);
        Assert.AreEqual(0, ReadableOk(d0));
        Put(d1, "abc");
        Put(d1, "defg");
        Assert.AreEqual(3, ReadableOk(d0), "Datagramm: nur das vorderste");
        Assert.AreEqual(3L, Take(d0, 64));
        Assert.AreEqual(4, ReadableOk(d0));
    }

    [TestMethod]
    public void FionreadUnverbundenNullLauschendEinval()
    {
        int s = (int)Call(LinuxSyscalls.Socket, 1, Stream, 0);
        Assert.IsTrue(s >= 0);
        Assert.AreEqual(0, ReadableOk(s), "unverbunden: leere Warteschlange");

        // abstrakter Name "\0fion": Familie AF_UNIX, Nullbyte, Name
        byte[] addr = { 1, 0, 0, (byte)'f', (byte)'i', (byte)'o', (byte)'n' };
        Assert.IsTrue(_p.TryWrite(Addr, addr));
        Assert.AreEqual(0L, Call(LinuxSyscalls.Bind, (ulong)s, Addr, (ulong)addr.Length));
        Assert.AreEqual(0L, Call(LinuxSyscalls.Listen, (ulong)s, 4));
        Assert.AreEqual(-EINVAL, Readable(s, out int n));
        Assert.AreEqual(-1, n, "nichts geschrieben");
    }

    [TestMethod]
    public void FionreadRegulaereDateiGroesseMinusPosition()
    {
        // O_CREAT|O_RDWR = 0x42, Modus 0644
        Assert.IsTrue(_p.TryWrite(Addr, Encoding.ASCII.GetBytes("/fion.txt\0")));
        long fd = Call(257, ulong.MaxValue - 99, Addr, 0x42, 0x1A4); // openat(AT_FDCWD, ...)
        Assert.IsTrue(fd >= 0, $"openat {fd}");
        Assert.AreEqual(0, ReadableOk((int)fd), "leere Datei");
        Put((int)fd, "0123456789");
        Assert.AreEqual(0, ReadableOk((int)fd), "Position am Ende");
        Assert.AreEqual(0L, Call(8, (ulong)fd, 0, 0)); // lseek SEEK_SET 0
        Assert.AreEqual(10, ReadableOk((int)fd));
        Assert.AreEqual(4L, Take((int)fd, 4));
        Assert.AreEqual(6, ReadableOk((int)fd), "Größe minus Position");
        Assert.AreEqual(-EFAULT, Call(LinuxSyscalls.Ioctl, (ulong)fd, FIONREAD, 8));
        Assert.AreEqual(0L, Call(3, (ulong)fd)); // close
        Assert.AreEqual(-EBADF, Readable((int)fd, out _));
    }

    [TestMethod]
    public void FionreadTimerfdEnottyWieLinux()
    {
        long t = Call(283, 1, 0); // timerfd_create(CLOCK_MONOTONIC)
        Assert.IsTrue(t >= 0);
        Assert.AreEqual(-ENOTTY, Readable((int)t, out _));
    }

    [TestMethod]
    public void FionreadEventfdEnottyInotifyEreignisse()
    {
        int e = (int)Call(Eventfd2, 1, 0);
        Assert.AreEqual(-ENOTTY, Readable(e, out _), "Linux: eventfd kennt kein ioctl");

        int n = (int)Call(InotifyInit1, 0);
        Assert.IsTrue(n >= 0);
        Assert.AreEqual(0, ReadableOk(n));
        Assert.IsTrue(_p.TryWrite(Addr, Encoding.ASCII.GetBytes("/\0")));
        long wd = Call(AddWatch, (ulong)n, Addr, 2);
        Assert.IsTrue(wd > 0);
        Assert.AreEqual(0L, Call(RmWatch, (ulong)n, (ulong)wd));
        Assert.AreEqual(16, ReadableOk(n), "IN_IGNORED ohne Namen");

        Assert.AreEqual(-EBADF, Readable(99, out _));
    }
}
