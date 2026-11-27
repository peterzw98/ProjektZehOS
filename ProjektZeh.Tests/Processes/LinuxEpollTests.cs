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

/// <summary>Ladybird V22: epoll_create1, epoll_ctl, epoll_wait/epoll_pwait/epoll_pwait2 auf Pipes, eventfd, Unix-Sockets und Terminal.</summary>
[TestClass]
public sealed class LinuxEpollTests
{
    private const ulong Page = 4096;
    private const long Read = 0, Write = 1, Close = 3, Open = 2, Fcntl = 72, Dup2 = 33, Socketpair = 53, Eventfd2 = 290, Pipe2 = 293;
    private const long EpollCreate = 213, EpollWait = 232, EpollCtl = 233, EpollPwait = 281, EpollCreate1 = 291, EpollPwait2 = 441;
    private const long EINVAL = 22, EBADF = 9, ENOENT = 2, EEXIST = 17, EFAULT = 14, EPERM = 1;
    private const int Cloexec = 0x80000, Nonblock = 0x800, Add = 1, Del = 2, Mod = 3;
    private const uint In = 1, Out = 4, Hup = 0x10, Et = 1u << 31, Oneshot = 1u << 30;

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
    private readonly MemoryFileStore _store = new();
    private readonly ulong _mem;

    private ulong Buf => _mem;
    private ulong Path => _mem + 0x800;
    private ulong Big => _mem + 0x1000;

    public LinuxEpollTests()
    {
        var term = new TerminalDescriptor(new Lines());
        _p = new LinuxUserProcess(new Pages(), term, 1, 64 << 20, new Host(), new LinuxFileSystem(_store, term));
        _p.StartThread = _ => true;
        _mem = _p.MapAnonymous(8 * Page, 3);
        Assert.AreNotEqual(0UL, _mem);
    }

    private long Call(long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
        return LinuxSyscalls.Dispatch(ref a, _p);
    }

    private ulong Str(string s)
    {
        byte[] b = Encoding.ASCII.GetBytes(s + "\0");
        Assert.IsTrue(_p.TryWrite(Path, b));
        return Path;
    }

    private (int R, int W) MakePipe(int flags = Nonblock)
    {
        Assert.AreEqual(0L, Call(Pipe2, Buf, (ulong)flags));
        var b = new byte[8];
        Assert.IsTrue(_p.TryRead(Buf, b));
        return (BitConverter.ToInt32(b, 0), BitConverter.ToInt32(b, 4));
    }

    private long Ctl(int ep, int op, int fd, uint events = In, ulong data = 0)
    {
        var ev = new byte[12];
        BitConverter.TryWriteBytes(ev.AsSpan(0, 4), events);
        BitConverter.TryWriteBytes(ev.AsSpan(4, 8), data);
        Assert.IsTrue(_p.TryWrite(Buf + 0x100, ev));
        return Call(EpollCtl, (ulong)ep, (ulong)op, (ulong)fd, Buf + 0x100);
    }

    // Ergebnisse landen bei Big; liefert (events, data) je Meldung.
    private long WaitEp(int ep, int max, int timeout, out (uint Events, ulong Data)[] got)
    {
        long n = Call(EpollWait, (ulong)ep, Big, (ulong)max, (ulong)(long)timeout);
        got = new (uint, ulong)[Math.Max(0, (int)n)];
        var b = new byte[12];
        for (int i = 0; i < got.Length; i++)
        {
            Assert.IsTrue(_p.TryRead(Big + (ulong)(i * 12), b));
            got[i] = (BitConverter.ToUInt32(b, 0), BitConverter.ToUInt64(b, 4));
        }

        return n;
    }

    private void Send(int fd, string s)
    {
        byte[] b = Encoding.ASCII.GetBytes(s);
        Assert.IsTrue(_p.TryWrite(Buf + 0x200, b));
        Assert.AreEqual((long)b.Length, Call(Write, (ulong)fd, Buf + 0x200, (ulong)b.Length));
    }

    private long Drain(int fd, int len = 64) => Call(Read, (ulong)fd, Buf + 0x300, (ulong)len);

    [TestMethod]
    public void CreateUndCtlFehlerWieLinux()
    {
        Assert.AreEqual(-EINVAL, Call(EpollCreate1, 1), "unbekanntes Flag");
        Assert.AreEqual(-EINVAL, Call(EpollCreate, 0), "size 0");
        int ep = (int)Call(EpollCreate1, Cloexec);
        Assert.IsTrue(ep >= 0);
        Assert.AreEqual(1, Call(Fcntl, (ulong)ep, 1), "EPOLL_CLOEXEC");
        int ep2 = (int)Call(EpollCreate, 5);
        Assert.IsTrue(ep2 >= 0);
        Assert.AreEqual(0, Call(Fcntl, (ulong)ep2, 1));
        var (r, w) = MakePipe();
        Assert.AreEqual(0, Ctl(ep, Add, r, In, 0x1111));
        Assert.AreEqual(-EEXIST, Ctl(ep, Add, r));
        Assert.AreEqual(-ENOENT, Ctl(ep, Del, w));
        Assert.AreEqual(-ENOENT, Ctl(ep, Mod, w));
        Assert.AreEqual(-EBADF, Ctl(ep, Add, 99));
        Assert.AreEqual(-EBADF, Ctl(99, Add, r));
        Assert.AreEqual(-EINVAL, Ctl(ep, Add, ep), "epoll auf sich selbst");
        Assert.AreEqual(-EINVAL, Ctl(ep, Add, ep2), "verschachtelt nicht unterstützt");
        Assert.AreEqual(-EINVAL, Ctl(r, Add, w), "epfd ist kein epoll");
        Assert.AreEqual(-EINVAL, Ctl(ep, 7, w), "unbekannte Operation");
        Assert.AreEqual(-EFAULT, Call(EpollCtl, (ulong)ep, Add, (ulong)w, 0), "Ereignis nicht lesbar");
        Assert.AreEqual(0, Call(EpollCtl, (ulong)ep, Del, (ulong)r, 0), "DEL ohne Ereignis");
        Assert.AreEqual(-EINVAL, Ctl(ep, Add, w, Out | (1u << 28) | Oneshot), "EPOLLEXCLUSIVE mit ONESHOT");
        Assert.AreEqual(-EINVAL, Call(EpollWait, (ulong)ep, Big, 0, 0), "maxevents 0");
        Assert.AreEqual(-EINVAL, Call(EpollWait, (ulong)r, Big, 4, 0), "kein epoll");
        Assert.AreEqual(-EBADF, Call(EpollWait, 99, Big, 4, 0));
        Assert.AreEqual(0, Call(EpollWait, (ulong)ep, 0x10, 4, 0), "Zielpuffer nur Adressbereich geprüft (Linux 6.12: 0)");
        Assert.AreEqual(-EFAULT, Call(EpollWait, (ulong)ep, 0x7FFF_FFFF_F000, 4, 0), "außerhalb des Benutzerbereichs");
        Assert.AreEqual(-EINVAL, Call(EpollPwait, (ulong)ep, Big, 4, 0, Buf, 4), "sigsetsize");
        Assert.AreEqual(-EINVAL, Call(Read, (ulong)ep, Buf, 8), "read -EINVAL");
        Assert.AreEqual(-EINVAL, Call(Write, (ulong)ep, Buf, 8), "write -EINVAL");
    }

    [TestMethod]
    public void RegulaereDateiIstEperm()
    {
        int ep = (int)Call(EpollCreate1, 0);
        byte[] path = Encoding.ASCII.GetBytes("/f.txt\0");
        Assert.IsTrue(_p.TryWrite(Path, path));
        int f = (int)Call(Open, Path, 0x42, 0x1a4); // O_CREAT|O_RDWR
        Assert.IsTrue(f >= 0, "open " + f);
        Assert.AreEqual(-EPERM, Ctl(ep, Add, f));
    }

    [TestMethod]
    public void PegelFlankeOneshotUndDel()
    {
        int ep = (int)Call(EpollCreate1, 0);
        var (r, w) = MakePipe();
        Assert.AreEqual(0, Ctl(ep, Add, r, In, 0x1111));
        Assert.AreEqual(0, WaitEp(ep, 4, 0, out _), "leer");
        Send(w, "xy");
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out var got));
        Assert.AreEqual((In, 0x1111UL), got[0]);
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out _), "pegelgesteuert: noch einmal");
        Assert.AreEqual(2, Drain(r));
        Assert.AreEqual(0, WaitEp(ep, 4, 0, out _));

        Assert.AreEqual(0, Ctl(ep, Mod, r, In | Et, 0x2222));
        Send(w, "12");
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out got));
        Assert.AreEqual(0x2222UL, got[0].Data);
        Assert.AreEqual(0, WaitEp(ep, 4, 0, out _), "Flanke: ohne neues Ereignis stumm");
        Send(w, "3");
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out _), "Flanke: neue Daten, obwohl lesbar geblieben");
        Assert.AreEqual(1, Drain(r, 1));
        Assert.AreEqual(0, WaitEp(ep, 4, 0, out _), "Flanke: Lesen ist kein EPOLLIN-Ereignis");
        Assert.AreEqual(2, Drain(r));

        Assert.AreEqual(0, Ctl(ep, Mod, r, In | Oneshot, 0x3333));
        Send(w, "z");
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out _));
        Assert.AreEqual(0, WaitEp(ep, 4, 0, out _), "ONESHOT: danach stumm");
        Assert.AreEqual(0, Ctl(ep, Mod, r, In | Oneshot, 0x3333));
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out _), "nach MOD wieder scharf");
        Assert.AreEqual(-EEXIST, Ctl(ep, Add, r), "abgeschalteter Eintrag bleibt in der Liste");
        Assert.AreEqual(0, Ctl(ep, Del, r));
        Assert.AreEqual(0, WaitEp(ep, 4, 0, out _), "nach DEL");
    }

    [TestMethod]
    public void HupUndErrOhneAnfrage()
    {
        int ep = (int)Call(EpollCreate1, 0);
        var (r, w) = MakePipe();
        Assert.AreEqual(0, Ctl(ep, Add, r, 0, 0x44));
        Assert.AreEqual(0, Ctl(ep, Add, w, 0, 0x55));
        Assert.AreEqual(0, WaitEp(ep, 4, 0, out _), "ohne Ereignisse angefragt");
        Assert.AreEqual(0, Call(Close, (ulong)w));
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out var got), "geschlossene Nummer fällt aus der Liste, Leseende meldet HUP");
        Assert.AreEqual((Hup, 0x44UL), got[0]);
        var (r2, w2) = MakePipe();
        Assert.AreEqual(0, Ctl(ep, Add, w2, Out, 0x66));
        Assert.AreEqual(0, Call(Close, (ulong)r2));
        Assert.AreEqual(2, WaitEp(ep, 4, 0, out got));
        CollectionAssert.Contains(got, (8u | Out, 0x66UL), "ohne Leser EPOLLERR|EPOLLOUT wie Linux 6.12");
    }

    [TestMethod]
    public void GeschlosseneNummerUndNeueDateiGleicherNummer()
    {
        int ep = (int)Call(EpollCreate1, 0);
        var (r, w) = MakePipe();
        Assert.AreEqual(0, Ctl(ep, Add, r, In, 1));
        Assert.AreEqual(0, Call(Close, (ulong)r));
        Assert.AreEqual(0, Call(Close, (ulong)w));
        var (r2, w2) = MakePipe();
        Assert.AreEqual(r, r2, "Nummer wiederverwendet");
        Assert.AreEqual(0, Ctl(ep, Add, r2, In, 2), "alter Eintrag ist weg (Linux: eventpoll_release beim close)");
        Send(w2, "a");
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out var got));
        Assert.AreEqual(2UL, got[0].Data);
    }

    [TestMethod]
    public void MaxeventsReihumFair()
    {
        int ep = (int)Call(EpollCreate1, 0);
        var (r1, w1) = MakePipe();
        var (r2, w2) = MakePipe();
        Assert.AreEqual(0, Ctl(ep, Add, r1, In, 1));
        Assert.AreEqual(0, Ctl(ep, Add, r2, In, 2));
        Send(w1, "a");
        Send(w2, "b");
        Assert.AreEqual(1, WaitEp(ep, 1, 0, out var a));
        Assert.AreEqual(1, WaitEp(ep, 1, 0, out var b));
        Assert.AreNotEqual(a[0].Data, b[0].Data, "der zweite bereite Eintrag kommt beim nächsten Aufruf zuerst");
        Assert.AreEqual(2, WaitEp(ep, 8, 0, out _));
    }

    [TestMethod]
    public void EventfdUndSocketpairUndTerminal()
    {
        int ep = (int)Call(EpollCreate1, 0);
        int e = (int)Call(Eventfd2, 0, Nonblock);
        Assert.AreEqual(0, Ctl(ep, Add, e, In | Et, 5));
        Assert.AreEqual(0, WaitEp(ep, 4, 0, out _));
        Assert.IsTrue(_p.TryWrite(Buf, BitConverter.GetBytes(1UL)));
        Assert.AreEqual(8, Call(Write, (ulong)e, Buf, 8));
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out _));
        Assert.AreEqual(0, WaitEp(ep, 4, 0, out _), "Flanke");
        Assert.AreEqual(8, Call(Write, (ulong)e, Buf, 8));
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out _), "eventfd: neues write meldet erneut");
        Assert.AreEqual(0, Ctl(ep, Del, e));

        Assert.AreEqual(0, Call(Socketpair, 1, 1, 0, Buf + 0x400)); // AF_UNIX, SOCK_STREAM
        var sv = new byte[8];
        Assert.IsTrue(_p.TryRead(Buf + 0x400, sv));
        int s0 = BitConverter.ToInt32(sv, 0), s1 = BitConverter.ToInt32(sv, 4);
        Assert.AreEqual(0, Ctl(ep, Add, s0, In | Out, 7));
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out var got));
        Assert.AreEqual((Out, 7UL), got[0], "Socket schreibbar, nicht lesbar");
        Assert.AreEqual(0, Ctl(ep, Mod, s0, In | Et, 8));
        Send(s1, "hi");
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out got));
        Assert.AreEqual((In, 8UL), got[0]);
        Assert.AreEqual(0, WaitEp(ep, 4, 0, out _));
        Send(s1, "!");
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out _), "Socket: neue Daten melden erneut");
        Assert.AreEqual(0, Ctl(ep, Del, s0));

        Assert.AreEqual(0, Ctl(ep, Add, 1, Out, 9), "Terminal (fd 1)");
        Assert.AreEqual(1, WaitEp(ep, 4, 0, out got));
        Assert.AreEqual((Out, 9UL), got[0]);
    }

    [TestMethod]
    public void WaitWirdVonAnderemThreadGeweckt()
    {
        int ep = (int)Call(EpollCreate1, 0);
        var (r, w) = MakePipe(0);
        Assert.AreEqual(0, Ctl(ep, Add, r, In, 0x7777));
        var writer = Task.Run(async () =>
        {
            await Task.Delay(50);
            var a = new SyscallArgs { Number = Write, A0 = (ulong)w, A1 = Buf + 0x200, A2 = 1 };
            return LinuxSyscalls.Dispatch(ref a, _p);
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.AreEqual(1, WaitEp(ep, 4, 10000, out var got));
        Assert.IsTrue(sw.ElapsedMilliseconds < 5000, "geweckt, nicht abgelaufen: " + sw.ElapsedMilliseconds);
        Assert.AreEqual(0x7777UL, got[0].Data);
        Assert.AreEqual(1, writer.Result);
    }

    [TestMethod]
    public void FristLaeuftAbUndPwait2()
    {
        int ep = (int)Call(EpollCreate1, 0);
        var (r, _) = MakePipe();
        Assert.AreEqual(0, Ctl(ep, Add, r, In, 1));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.AreEqual(0, WaitEp(ep, 4, 120, out _));
        Assert.IsTrue(sw.ElapsedMilliseconds >= 100, "Frist abgewartet: " + sw.ElapsedMilliseconds);
        var ts = new byte[16];
        BitConverter.TryWriteBytes(ts.AsSpan(8), 30_000_000L);
        Assert.IsTrue(_p.TryWrite(Buf + 0x500, ts));
        sw.Restart();
        Assert.AreEqual(0, Call(EpollPwait2, (ulong)ep, Big, 4, Buf + 0x500, 0, 8));
        Assert.IsTrue(sw.ElapsedMilliseconds >= 25, "pwait2 30 ms: " + sw.ElapsedMilliseconds);
        BitConverter.TryWriteBytes(ts.AsSpan(8), 1_000_000_000L);
        Assert.IsTrue(_p.TryWrite(Buf + 0x500, ts));
        Assert.AreEqual(-EINVAL, Call(EpollPwait2, (ulong)ep, Big, 4, Buf + 0x500, 0, 8), "tv_nsec zu groß");
        Assert.AreEqual(0, Call(EpollPwait, (ulong)ep, Big, 4, 0, Buf, 8), "Maske 8 Bytes, Frist 0");
    }

    [TestMethod]
    public void CloseGibtEpollFrei()
    {
        int ep = (int)Call(EpollCreate1, 0);
        var (r, _) = MakePipe();
        Assert.AreEqual(0, Ctl(ep, Add, r, In, 1));
        var inst = (LinuxEpoll)_p.GetDescriptor(ep)!;
        Assert.AreEqual(1, inst.Count);
        Assert.AreEqual(40, Call(Dup2, (ulong)ep, 40));
        Assert.AreEqual(0, Call(Close, (ulong)ep));
        Assert.AreEqual(1, inst.Count, "dup hält die Instanz offen");
        Assert.AreEqual(0, Call(Close, 40));
        Assert.AreEqual(0, inst.Count, "letztes close leert die Liste");
    }
}
