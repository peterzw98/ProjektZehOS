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

/// <summary>Ladybird V20 (weiche Lücken): eventfd/eventfd2, statfs/fstatfs, inotify_init1/add_watch/rm_watch.</summary>
[TestClass]
public sealed class LinuxSoftGapsTests
{
    private const ulong Page = 4096;
    private const long Read = 0, Write = 1, Close = 3, Poll = 7, Fstat = 5, Fcntl = 72, Mkdir = 83, Dup = 32, Open = 2;
    private const long Statfs = 137, Fstatfs = 138, InotifyInit = 253, AddWatch = 254, RmWatch = 255, Eventfd = 284, Eventfd2 = 290, InotifyInit1 = 294;
    private const long EAGAIN = 11, EINVAL = 22, EBADF = 9, ENOENT = 2, ENOTDIR = 20, EEXIST = 17, EFAULT = 14;
    private const int Cloexec = 0x80000, Nonblock = 0x800, Semaphore = 1;

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

    public LinuxSoftGapsTests()
    {
        var term = new TerminalDescriptor(new Lines());
        _p = new LinuxUserProcess(new Pages(), term, 1, 64 << 20, new Host(), new LinuxFileSystem(_store, term));
        _p.StartThread = _ => true;
        _mem = _p.MapAnonymous(8 * Page, 3);
        Assert.AreNotEqual(0UL, _mem);
    }

    private long Call(long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3 };
        return LinuxSyscalls.Dispatch(ref a, _p);
    }

    private ulong Str(string s)
    {
        byte[] b = Encoding.ASCII.GetBytes(s + "\0");
        Assert.IsTrue(_p.TryWrite(Path, b));
        return Path;
    }

    private long Put(int fd, ulong value, ulong len = 8)
    {
        Assert.IsTrue(_p.TryWrite(Buf, BitConverter.GetBytes(value)));
        return Call(Write, (ulong)fd, Buf, len);
    }

    private long Take(int fd, out ulong value, ulong len = 8)
    {
        Assert.IsTrue(_p.TryWrite(Buf, new byte[8]));
        long r = Call(Read, (ulong)fd, Buf, len);
        var b = new byte[8];
        Assert.IsTrue(_p.TryRead(Buf, b));
        value = BitConverter.ToUInt64(b);
        return r;
    }

    private short PollFd(int fd, short events, int timeout = 0)
    {
        var pfd = new byte[8];
        BitConverter.TryWriteBytes(pfd.AsSpan(0, 4), fd);
        BitConverter.TryWriteBytes(pfd.AsSpan(4, 2), events);
        Assert.IsTrue(_p.TryWrite(Buf + 0x100, pfd));
        _ = Call(Poll, Buf + 0x100, 1, (ulong)timeout);
        var rb = new byte[2];
        Assert.IsTrue(_p.TryRead(Buf + 0x106, rb));
        return BitConverter.ToInt16(rb);
    }

    private long[] StatfsFields(long r)
    {
        Assert.AreEqual(0, r);
        var b = new byte[120];
        Assert.IsTrue(_p.TryRead(Big, b));
        var f = new long[15];
        for (int i = 0; i < 15; i++)
        {
            f[i] = BitConverter.ToInt64(b, i * 8);
        }

        return f;
    }

    [TestMethod]
    public void EventfdZaehltLiestUndSetztZurueck()
    {
        Assert.AreEqual(-EINVAL, Call(Eventfd2, 0, 0x40), "unbekanntes Flag");
        int e = (int)Call(Eventfd2, 3, Cloexec | Nonblock);
        Assert.IsTrue(e >= 0);
        Assert.AreEqual(1, Call(Fcntl, (ulong)e, 1), "EFD_CLOEXEC");
        Assert.AreEqual(0x802, Call(Fcntl, (ulong)e, 3), "O_RDWR|O_NONBLOCK");
        Assert.AreEqual(8, Put(e, 4));
        Assert.AreEqual(5, PollFd(e, 5), "POLLIN|POLLOUT");
        Assert.AreEqual(8, Take(e, out ulong v));
        Assert.AreEqual(7UL, v);
        Assert.AreEqual(-EAGAIN, Take(e, out _), "leer, nicht blockierend");
        Assert.AreEqual(4, PollFd(e, 5), "nur POLLOUT");
        Assert.AreEqual(-EINVAL, Take(e, out _, 7), "Puffer unter 8 Bytes");
        Assert.AreEqual(-EINVAL, Call(Read, (ulong)e, Buf, 0), "Länge 0 wie Linux");
        Assert.AreEqual(-EINVAL, Put(e, 1, 4), "write unter 8 Bytes");
        Assert.AreEqual(-EINVAL, Put(e, ulong.MaxValue), "2^64-1 verboten");
        Assert.AreEqual(8, Put(e, LinuxEventFd.MaxCount));
        Assert.AreEqual(-EAGAIN, Put(e, 1), "Überlauf: nicht blockierend -EAGAIN");
        Assert.AreEqual(1, PollFd(e, 5), "voll: nur POLLIN");
        Assert.AreEqual(8, Put(e, 0), "0 passt immer");
        Assert.AreEqual(8, Take(e, out v, 16), "längerer Puffer liefert 8");
        Assert.AreEqual(LinuxEventFd.MaxCount, v);
        Assert.AreEqual(0, Call(Close, (ulong)e));
        Assert.AreEqual(-EBADF, Take(e, out _));
    }

    [TestMethod]
    public void EventfdSemaphoreUndBlockierendesLesen()
    {
        int s = (int)Call(Eventfd2, 2, Semaphore | Nonblock);
        Assert.AreEqual(8, Take(s, out ulong v));
        Assert.AreEqual(1UL, v);
        Assert.AreEqual(8, Take(s, out v));
        Assert.AreEqual(1UL, v);
        Assert.AreEqual(-EAGAIN, Take(s, out _));

        int e = (int)Call(Eventfd, 0);
        Assert.IsTrue(e >= 0);
        Assert.AreEqual(0, Call(Fcntl, (ulong)e, 1), "eventfd ohne Flags");
        var ev = (LinuxEventFd)_p.GetDescriptor(e)!;
        var reader = Task.Run(() => { var b = new byte[8]; long r = ev.ReadAs(b, null); return (r, BitConverter.ToUInt64(b)); });
        Assert.IsFalse(reader.Wait(100), "blockiert ohne Zähler");
        Assert.AreEqual(8, Put(e, 9));
        Assert.IsTrue(reader.Wait(5000));
        Assert.AreEqual((8L, 9UL), reader.Result);
    }

    [TestMethod]
    public void EventfdTeiltZaehlerUeberDupUndSchliesstMitDerLetztenNummer()
    {
        int e = (int)Call(Eventfd2, 0, Nonblock);
        int d = (int)Call(Dup, (ulong)e);
        Assert.AreEqual(8, Put(d, 5));
        Assert.AreEqual(0, Call(Close, (ulong)e));
        Assert.AreEqual(8, Take(d, out ulong v));
        Assert.AreEqual(5UL, v);

        Assert.AreEqual(0, Call(Fstat, (ulong)d, Big));
        var b = new byte[4];
        Assert.IsTrue(_p.TryRead(Big + 24, b));
        Assert.AreEqual(0x180u, BitConverter.ToUInt32(b), "anon_inode: 0600 ohne Typ");
        Assert.AreEqual(0, Call(Fstatfs, (ulong)d, Big));
        Assert.AreEqual(LinuxSyscalls.AnonInodeFsMagic, StatfsFields(0)[0]);
    }

    [TestMethod]
    public void StatfsMeldetTmpfsMitGrenzenUndFehlerwege()
    {
        long[] f = StatfsFields(Call(Statfs, Str("/"), Big));
        Assert.AreEqual(LinuxSyscalls.TmpfsMagic, f[0]);
        Assert.AreEqual(4096, f[1], "f_bsize");
        Assert.AreEqual(_store.MaxTotalBytes / 4096, f[2], "f_blocks");
        Assert.IsTrue(f[3] > 0 && f[3] <= f[2], "f_bfree");
        Assert.AreEqual(f[3], f[4], "f_bavail");
        Assert.AreEqual(_store.MaxNodes, f[5], "f_files");
        Assert.AreEqual(255, f[8], "f_namelen");
        Assert.AreEqual(4096, f[9], "f_frsize");
        Assert.AreEqual(0x20, f[10], "ST_VALID");
        Assert.AreEqual(-ENOENT, Call(Statfs, Str("/fehlt"), Big));
        Assert.AreEqual(-EFAULT, Call(Statfs, Str("/"), 8));

        int fd = (int)Call(Open, Str("/"), 0x10000 /* O_DIRECTORY */);
        Assert.IsTrue(fd >= 0);
        Assert.AreEqual(LinuxSyscalls.TmpfsMagic, StatfsFields(Call(Fstatfs, (ulong)fd, Big))[0]);
        Assert.AreEqual(LinuxSyscalls.DevptsSuperMagic, StatfsFields(Call(Fstatfs, 1, Big))[0], "Terminal");
        Assert.AreEqual(-EBADF, Call(Fstatfs, 77, Big));
    }

    [TestMethod]
    public void StatfsDerPlatteMeldetKontingent()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Die Wurzel ist ein Pfad mit '/' wie im Kernel.");
        }

        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zehos-v20-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var term = new TerminalDescriptor(new Lines());
            var disk = DirectoryFileStore.Open(dir, new HostLinuxDiskLinks(), StringComparison.Ordinal).Value!;
            var fs = new LinuxFileSystem(disk, term);
            LinuxFsStats st = fs.FsStats;
            Assert.AreEqual(LinuxSyscalls.MsdosSuperMagic, st.Type);
            Assert.AreEqual(disk.QuotaBytes / 4096, st.Blocks);
            Assert.AreEqual(st.Blocks, st.FreeBlocks, "nichts gewachsen");
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void InotifyFuehrtWatchesOhneEreignis()
    {
        Assert.AreEqual(-EINVAL, Call(InotifyInit1, 1));
        int n = (int)Call(InotifyInit1, Cloexec | Nonblock);
        Assert.IsTrue(n >= 0);
        Assert.AreEqual(1, Call(Fcntl, (ulong)n, 1));
        Assert.AreEqual(0x800, Call(Fcntl, (ulong)n, 3), "O_RDONLY|O_NONBLOCK");
        Assert.AreEqual(0, Call(Mkdir, Str("/etc"), 0x1ed));

        Assert.AreEqual(-EINVAL, Call(AddWatch, (ulong)n, Str("/etc"), 0), "Maske 0");
        Assert.AreEqual(-EINVAL, Call(AddWatch, (ulong)n, Str("/etc"), 0x30000002), "MASK_ADD mit MASK_CREATE");
        Assert.AreEqual(-EBADF, Call(AddWatch, 77, Str("/etc"), 2));
        Assert.AreEqual(-EINVAL, Call(AddWatch, 1, Str("/etc"), 2), "kein inotify");
        Assert.AreEqual(-ENOENT, Call(AddWatch, (ulong)n, Str("/etc/localtime"), 2));

        long w1 = Call(AddWatch, (ulong)n, Str("/etc"), 0x2 | 0x100 /* MODIFY|CREATE */);
        Assert.AreEqual(1, w1);
        Assert.AreEqual(w1, Call(AddWatch, (ulong)n, Str("/etc/"), 0x200), "dieselbe Datei: derselbe Watch");
        Assert.AreEqual(-EEXIST, Call(AddWatch, (ulong)n, Str("/etc"), 0x10000002));
        int f = (int)Call(Open, Str("/etc/tz"), 0x41 /* O_CREAT|O_WRONLY */, 0x1a4);
        Assert.IsTrue(f >= 0);
        Assert.AreEqual(-ENOTDIR, Call(AddWatch, (ulong)n, Str("/etc/tz"), 0x01000002), "IN_ONLYDIR");
        long w2 = Call(AddWatch, (ulong)n, Str("/etc/tz"), 2);
        Assert.AreEqual(2, w2);
        Assert.AreEqual(1, Put(f, 0x41, 1), "Änderung erzeugt kein Ereignis");

        Assert.AreEqual(0, PollFd(n, 5), "nie POLLOUT, kein POLLIN");
        Assert.AreEqual(-EAGAIN, Take(n, out _, 64));
        Assert.AreEqual(-EBADF, Put(n, 1), "write: nur lesend offen");

        Assert.AreEqual(-EINVAL, Call(RmWatch, (ulong)n, 9));
        Assert.AreEqual(0, Call(RmWatch, (ulong)n, (ulong)w1));
        Assert.AreEqual(-EINVAL, Call(RmWatch, (ulong)n, (ulong)w1), "schon entfernt");
        Assert.AreEqual(1, PollFd(n, 5), "IN_IGNORED wartet");
        Assert.AreEqual(-EINVAL, Take(n, out _, 8), "Puffer kleiner als ein Ereignis");
        Assert.AreEqual(16, Call(Read, (ulong)n, Big, 64));
        var ev = new byte[16];
        Assert.IsTrue(_p.TryRead(Big, ev));
        Assert.AreEqual(1, BitConverter.ToInt32(ev, 0), "wd");
        Assert.AreEqual(0x8000u, BitConverter.ToUInt32(ev, 4), "IN_IGNORED");
        Assert.AreEqual(0u, BitConverter.ToUInt32(ev, 12), "len");
        Assert.AreEqual(3, Call(AddWatch, (ulong)n, Str("/etc"), 2), "neue Nummer, nicht wiederverwendet");

        int old = (int)Call(InotifyInit);
        Assert.IsTrue(old >= 0);
        Assert.AreEqual(0, Call(Fcntl, (ulong)old, 1), "inotify_init ohne CLOEXEC");
        Assert.AreEqual(0, Call(Close, (ulong)n));
    }

    [TestMethod]
    public void InotifyLesenBlockiertBisZumEntfernen()
    {
        int n = (int)Call(InotifyInit1, 0);
        Assert.AreEqual(1, Call(AddWatch, (ulong)n, Str("/"), 2));
        var ino = (LinuxInotify)_p.GetDescriptor(n)!;
        var reader = Task.Run(() => ino.ReadAs(new byte[64], null));
        Assert.IsFalse(reader.Wait(100), "ohne Ereignis blockiert read");
        Assert.AreEqual(0, Call(RmWatch, (ulong)n, 1));
        Assert.IsTrue(reader.Wait(5000));
        Assert.AreEqual(16, reader.Result);
        Assert.AreEqual(0, ino.WatchCount);
    }
}
