using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V22b: timerfd_create/settime/gettime, read, poll und epoll auf einer verstellbaren Uhr.</summary>
[TestClass]
public sealed class LinuxTimerFdTests
{
    private const ulong Page = 4096;
    private const long Read = 0, Write = 1, Poll = 7, Fcntl = 72, Eventfd2 = 290;
    private const long TimerfdCreate = 283, TimerfdSettime = 286, TimerfdGettime = 287, EpollWait = 232, EpollCtl = 233, EpollCreate1 = 291;
    private const long EINVAL = 22, EBADF = 9, EFAULT = 14, EAGAIN = 11;
    private const int Cloexec = 0x80000, Nonblock = 0x800, Abstime = 1, Add = 1;
    private const uint In = 1, Et = 1u << 31;
    private const long Ms = 1_000_000;

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

    // Verstellbare Uhr: monoton ab 1 s, Wanduhr ab 1000 s.
    private sealed class Host : ILinuxHost
    {
        public long Now = 1_000_000_000;
        public long RealtimeNanos => Interlocked.Read(ref Now) + 999_000_000_000;
        public long MonotonicNanos => Interlocked.Read(ref Now);
        public bool TryFillRandom(Span<byte> dst) { dst.Fill(7); return true; }
        public void SleepNanos(long ns) { }
        public void Step(long ns) => Interlocked.Add(ref Now, ns);
    }

    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    private readonly Host _host = new();
    private readonly LinuxUserProcess _p;
    private readonly ulong _mem;

    private ulong Buf => _mem;
    private ulong Its => _mem + 0x400;
    private ulong Old => _mem + 0x500;

    public LinuxTimerFdTests()
    {
        var term = new TerminalDescriptor(new Lines());
        _p = new LinuxUserProcess(new Pages(), term, 1, 64 << 20, _host, new LinuxFileSystem(new MemoryFileStore(), term));
        _p.StartThread = _ => true;
        _mem = _p.MapAnonymous(4 * Page, 3);
        Assert.AreNotEqual(0UL, _mem);
    }

    private long Call(long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
        return LinuxSyscalls.Dispatch(ref a, _p);
    }

    private int Create(int clock = 1, int flags = Nonblock)
    {
        long fd = Call(TimerfdCreate, (ulong)clock, (ulong)flags);
        Assert.IsTrue(fd >= 0, "timerfd_create " + fd);
        return (int)fd;
    }

    private long Set(int fd, long intervalNs, long valueNs, int flags = 0, ulong old = 0, long valueSec = -1)
    {
        var b = new byte[32];
        BitConverter.TryWriteBytes(b.AsSpan(0, 8), intervalNs / 1_000_000_000);
        BitConverter.TryWriteBytes(b.AsSpan(8, 8), intervalNs % 1_000_000_000);
        BitConverter.TryWriteBytes(b.AsSpan(16, 8), valueSec >= 0 ? valueSec : valueNs / 1_000_000_000);
        BitConverter.TryWriteBytes(b.AsSpan(24, 8), valueSec >= 0 ? valueNs : valueNs % 1_000_000_000);
        Assert.IsTrue(_p.TryWrite(Its, b));
        return Call(TimerfdSettime, (ulong)fd, (ulong)flags, Its, old);
    }

    private (long Interval, long Value) ReadSpec(ulong at)
    {
        var b = new byte[32];
        Assert.IsTrue(_p.TryRead(at, b));
        return ((BitConverter.ToInt64(b, 0) * 1_000_000_000) + BitConverter.ToInt64(b, 8), (BitConverter.ToInt64(b, 16) * 1_000_000_000) + BitConverter.ToInt64(b, 24));
    }

    private (long Interval, long Value) Get(int fd)
    {
        Assert.AreEqual(0, Call(TimerfdGettime, (ulong)fd, Old));
        return ReadSpec(Old);
    }

    // Liefert die Abläufe oder -errno.
    private long Ticks(int fd)
    {
        long r = Call(Read, (ulong)fd, Buf, 8);
        if (r < 0)
        {
            return r;
        }

        Assert.AreEqual(8, r);
        var b = new byte[8];
        Assert.IsTrue(_p.TryRead(Buf, b));
        return (long)BitConverter.ToUInt64(b);
    }

    private long PollIn(int fd)
    {
        var b = new byte[8];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), fd);
        BitConverter.TryWriteBytes(b.AsSpan(4, 2), (short)(1 | 4)); // POLLIN | POLLOUT
        Assert.IsTrue(_p.TryWrite(Buf + 0x100, b));
        long n = Call(Poll, Buf + 0x100, 1, 0);
        Assert.IsTrue(_p.TryRead(Buf + 0x100, b));
        return n == 1 ? BitConverter.ToInt16(b, 6) : n;
    }

    [TestMethod]
    public void CreateUndFehlerWieLinux()
    {
        Assert.AreEqual(-EINVAL, Call(TimerfdCreate, 9999, 0), "unbekannte Uhr");
        Assert.AreEqual(-EINVAL, Call(TimerfdCreate, 2, 0), "CLOCK_PROCESS_CPUTIME_ID");
        Assert.AreEqual(-EINVAL, Call(TimerfdCreate, 1, 1), "unbekanntes Flag");
        int tf = Create(1, Cloexec | Nonblock);
        Assert.AreEqual(1, Call(Fcntl, (ulong)tf, 1), "TFD_CLOEXEC");
        Assert.AreEqual(2 | Nonblock, Call(Fcntl, (ulong)tf, 3), "O_RDWR|O_NONBLOCK");
        Assert.IsTrue(Call(TimerfdCreate, 0, 0) >= 0, "CLOCK_REALTIME");
        Assert.IsTrue(Call(TimerfdCreate, 7, 0) >= 0, "CLOCK_BOOTTIME");

        Assert.AreEqual(-EAGAIN, Ticks(tf), "nicht gestellt");
        Assert.AreEqual(-EINVAL, Call(Read, (ulong)tf, Buf, 4), "weniger als 8 Byte");
        Assert.AreEqual(-EINVAL, Call(Write, (ulong)tf, Buf, 8), "kein write");
        Assert.AreEqual(-EINVAL, Set(tf, 0, 1_000_000_000, valueSec: 0), "tv_nsec zu groß");
        Assert.AreEqual(0, Set(tf, 0, 0, valueSec: long.MaxValue), "sehr große Sekunden begrenzt wie KTIME_MAX");
        Assert.AreEqual(-EINVAL, Set(tf, 0, 5, flags: 4), "unbekanntes settime-Flag");
        Assert.AreEqual(-EBADF, Set(99, 0, 5));
        int ef = (int)Call(Eventfd2, 0, 0);
        Assert.AreEqual(-EINVAL, Set(ef, 0, 5), "kein timerfd");
        Assert.AreEqual(-EINVAL, Call(TimerfdGettime, (ulong)ef, Old));
        Assert.AreEqual(-EBADF, Call(TimerfdGettime, 99, Old));
        Assert.AreEqual(-EFAULT, Call(TimerfdSettime, (ulong)tf, 0, 0x10, 0), "new nicht lesbar");
        Assert.AreEqual(-EFAULT, Call(TimerfdGettime, (ulong)tf, 0x10), "cur nicht schreibbar");
    }

    [TestMethod]
    public void EinmaligLaeuftNachFristAb()
    {
        int tf = Create();
        Assert.AreEqual(0, Set(tf, 0, 20 * Ms));
        Assert.AreEqual((0L, 20 * Ms), Get(tf));
        _host.Step(10 * Ms);
        Assert.AreEqual(-EAGAIN, Ticks(tf));
        Assert.AreEqual(0, PollIn(tf), "noch nicht bereit, nie POLLOUT");
        Assert.AreEqual((0L, 10 * Ms), Get(tf));
        _host.Step(10 * Ms);
        Assert.AreEqual(1, PollIn(tf), "POLLIN");
        Assert.AreEqual(1, Ticks(tf));
        Assert.AreEqual(-EAGAIN, Ticks(tf), "gelesen setzt zurück");
        Assert.AreEqual((0L, 0L), Get(tf), "einmalig: danach abgelaufen");
        _host.Step(100 * Ms);
        Assert.AreEqual(-EAGAIN, Ticks(tf), "einmalig: kein zweiter Ablauf");
    }

    [TestMethod]
    public void PeriodischZaehltVerpassteAblaeufe()
    {
        int tf = Create();
        Assert.AreEqual(0, Set(tf, 10 * Ms, 10 * Ms));
        _host.Step(35 * Ms);
        Assert.AreEqual((10 * Ms, 5 * Ms), Get(tf), "gettime rückt vor: nächster Ablauf bei 40 ms");
        Assert.AreEqual(3, Ticks(tf), "10, 20, 30 ms");
        _host.Step(5 * Ms);
        Assert.AreEqual(1, Ticks(tf));
        Assert.AreEqual(0, Set(tf, 0, 0, old: Old), "entschärfen");
        Assert.AreEqual((10 * Ms, 10 * Ms), ReadSpec(Old), "alter Stand: Intervall und Restzeit");
        _host.Step(50 * Ms);
        Assert.AreEqual(-EAGAIN, Ticks(tf));
        Assert.AreEqual((0L, 0L), Get(tf));
    }

    [TestMethod]
    public void SettimeVerwirftAusstehendeUndAbsoluteZeit()
    {
        int tf = Create();
        Assert.AreEqual(0, Set(tf, 0, 5 * Ms));
        _host.Step(6 * Ms);
        Assert.AreEqual(0, Set(tf, 0, 50 * Ms), "neu stellen");
        Assert.AreEqual(-EAGAIN, Ticks(tf), "ausstehender Ablauf verworfen wie Linux");

        // Absolut in der Vergangenheit: sofort abgelaufen.
        Assert.AreEqual(0, Set(tf, 0, 1, flags: Abstime));
        Assert.AreEqual(1, Ticks(tf));

        // Absolut auf der Wanduhr.
        int rt = Create(0);
        long wall = _host.RealtimeNanos;
        Assert.AreEqual(0, Set(rt, 0, wall + (30 * Ms), flags: Abstime | 2 /* CANCEL_ON_SET angenommen */));
        Assert.AreEqual((0L, 30 * Ms), Get(rt));
        _host.Step(30 * Ms);
        Assert.AreEqual(1, Ticks(rt));
    }

    [TestMethod]
    public void EpollMeldetAblaufPegelUndFlanke()
    {
        int ep = (int)Call(EpollCreate1, 0);
        int tf = Create();
        var ev = new byte[12];
        BitConverter.TryWriteBytes(ev.AsSpan(0, 4), In | Et);
        BitConverter.TryWriteBytes(ev.AsSpan(4, 8), 0x6666UL);
        Assert.IsTrue(_p.TryWrite(Buf + 0x200, ev));
        Assert.AreEqual(0, Call(EpollCtl, (ulong)ep, Add, (ulong)tf, Buf + 0x200));
        Assert.AreEqual(0, Set(tf, 10 * Ms, 10 * Ms));
        Assert.AreEqual(0, Call(EpollWait, (ulong)ep, Buf + 0x300, 4, 0));
        _host.Step(10 * Ms);
        Assert.AreEqual(1, Call(EpollWait, (ulong)ep, Buf + 0x300, 4, 0));
        Assert.IsTrue(_p.TryRead(Buf + 0x300, ev));
        Assert.AreEqual(In, BitConverter.ToUInt32(ev, 0));
        Assert.AreEqual(0x6666UL, BitConverter.ToUInt64(ev, 4));
        Assert.AreEqual(0, Call(EpollWait, (ulong)ep, Buf + 0x300, 4, 0), "Flanke: ohne neuen Ablauf stumm");
        _host.Step(10 * Ms);
        Assert.AreEqual(1, Call(EpollWait, (ulong)ep, Buf + 0x300, 4, 0), "Flanke: neuer Ablauf, obwohl ungelesen");
        Assert.AreEqual(2, Ticks(tf));
    }

    [TestMethod]
    public void BlockierendesReadWartetBisZumAblauf()
    {
        int tf = Create(1, 0);
        Assert.AreEqual(0, Set(tf, 0, 40 * Ms));
        Task stepper = Task.Run(() =>
        {
            Thread.Sleep(60);
            _host.Step(40 * Ms);
        });
        Assert.AreEqual(1, Ticks(tf), "read wartet, ohne dass jemand weckt");
        stepper.Wait();
    }

    [TestMethod]
    public void EchteUhrWartetMitEpoll()
    {
        // Ohne verstellte Uhr: der Zeitgeber selbst über Stopwatch, gewartet mit Frist wie s3 pipe_epoll.
        long Clock() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
        var timer = new LinuxTimerFd(Clock, nonBlocking: true);
        timer.Set(0, 0, 20 * Ms, out _, out _);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.IsTrue(timer.WaitPoll(1, 2000, null));
        Assert.IsTrue(sw.ElapsedMilliseconds >= 15, "mindestens 15 ms, war " + sw.ElapsedMilliseconds);
        Assert.IsTrue(sw.ElapsedMilliseconds < 1000, "kein Warten bis zur Frist, war " + sw.ElapsedMilliseconds);
        Assert.IsFalse(timer.WaitPoll(4, 30, null), "POLLOUT nie");
    }
}
