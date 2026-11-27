using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V17: memfd_create, MAP_SHARED über Prozesse (dieselben Frames), Refzählung bei munmap, close und Prozessende, Siegel.</summary>
[TestClass]
public sealed class LinuxMemFileTests
{
    private const ulong Page = 4096;
    private const long Read = 0, Write = 1, Close = 3, Fstat = 5, Lseek = 8, Mmap = 9, Munmap = 11, Fcntl = 72, Ftruncate = 77, Madvise = 28,
        MemfdCreate = 319;
    private const int ProtRw = 3, ProtR = 1, MapShared = 1, MapPrivate = 2;
    private const int FAddSeals = 1033, FGetSeals = 1034, MfdCloexec = 1, MfdAllowSealing = 2, MfdHugetlb = 4;
    private const long EPERM = 1, EBUSY = 16, EINVAL = 22;

    /// <summary>Frames aller Prozesse wie der PageAllocator des Kernels: eine Quelle, Zählung der lebenden Frames.</summary>
    private sealed class Frames
    {
        public readonly Dictionary<ulong, byte[]> Live = new();
        public ulong Next = 0x100000;
    }

    private sealed class Pages(Frames store) : IUserPages
    {
        public readonly Dictionary<ulong, ulong> Mapped = new();
        public ulong AllocateFrame() { store.Live[store.Next] = new byte[Page]; store.Next += Page; return store.Next - Page; }
        public void FreeFrame(ulong frame) { Assert.IsTrue(store.Live.Remove(frame), "Frame doppelt freigegeben"); }
        public Span<byte> Frame(ulong frame) => store.Live[frame];
        public bool Map(ulong page, ulong frame, MapAccess access) { Mapped[page] = frame; return true; }
        public bool Unmap(ulong page) { Mapped.Remove(page); return true; }
        public void DestroyTable() => Mapped.Clear();
    }

    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    private readonly Frames _store = new();
    private readonly LinuxOpenFiles _open = new();
    private readonly LinuxIds _ids = new(1);

    private (LinuxUserProcess P, Pages Pages, ulong Mem) NewProcess(int pid)
    {
        var term = new TerminalDescriptor(new Lines());
        var pages = new Pages(_store);
        var p = new LinuxUserProcess(pages, term, pid, 64 << 20, null, new LinuxFileSystem(new MemoryFileStore(), term), _open, _ids);
        ulong mem = p.MapAnonymous(4 * Page, ProtRw);
        Assert.AreNotEqual(0UL, mem);
        return (p, pages, mem);
    }

    private static long Call(LinuxUserProcess p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
        return LinuxSyscalls.Dispatch(ref a, p);
    }

    private static int Memfd(LinuxUserProcess p, ulong mem, uint flags = 0)
    {
        Assert.IsTrue(p.TryWrite(mem, Encoding.ASCII.GetBytes("buf\0")));
        long fd = Call(p, MemfdCreate, mem, flags);
        Assert.IsTrue(fd >= 3, $"memfd_create {fd}");
        return (int)fd;
    }

    private static ulong MapSharedFd(LinuxUserProcess p, int fd, ulong len, int prot = ProtRw, ulong offset = 0)
    {
        long r = Call(p, Mmap, 0, len, (ulong)prot, MapShared, (ulong)fd, offset);
        Assert.IsTrue(r > 0, $"mmap {r}");
        return (ulong)r;
    }

    /// <summary>Gibt die Datei an den zweiten Prozess weiter wie SCM_RIGHTS (gleiche offene Datei, eigene Nummer).</summary>
    private static int Pass(LinuxUserProcess from, int fd, LinuxUserProcess to)
    {
        lock (from.FileGate)
        {
            ILinuxDescriptor d = from.GetDescriptor(fd)!;
            int n = to.AddDescriptor(d, 0);
            Assert.IsTrue(n >= 0);
            return n;
        }
    }

    private static byte[] Get(LinuxUserProcess p, ulong at, int n)
    {
        var b = new byte[n];
        Assert.IsTrue(p.TryRead(at, b));
        return b;
    }

    [TestMethod]
    public void MemfdLiestSchreibtUndKuerztWieTmpfs()
    {
        var (p, _, mem) = NewProcess(1);
        int fd = Memfd(p, mem, MfdCloexec);
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, 10000));
        Assert.IsTrue(p.TryWrite(mem + 0x100, Encoding.ASCII.GetBytes("hallo")));
        Assert.AreEqual(5000, Call(p, Lseek, (ulong)fd, 5000, 0));
        Assert.AreEqual(5, Call(p, Write, (ulong)fd, mem + 0x100, 5));
        Assert.AreEqual(4998, Call(p, Lseek, (ulong)fd, 4998, 0));
        Assert.AreEqual(9, Call(p, Read, (ulong)fd, mem + 0x200, 9));
        CollectionAssert.AreEqual(new byte[] { 0, 0, (byte)'h', (byte)'a', (byte)'l', (byte)'l', (byte)'o', 0, 0 }, Get(p, mem + 0x200, 9));
        Assert.AreEqual(0, Call(p, Fstat, (ulong)fd, mem + 0x400));
        Assert.AreEqual(10000L, BitConverter.ToInt64(Get(p, mem + 0x400 + 48, 8)));
        Assert.AreEqual(0x81ffu, BitConverter.ToUInt32(Get(p, mem + 0x400 + 24, 4)));

        // Kürzen in die Mitte des Worts und wieder wachsen: der abgeschnittene Teil liest sich als Nullen.
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, 5002));
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, 6000));
        Assert.AreEqual(5000, Call(p, Lseek, (ulong)fd, 5000, 0));
        Assert.AreEqual(5, Call(p, Read, (ulong)fd, mem + 0x200, 5));
        CollectionAssert.AreEqual(new byte[] { (byte)'h', (byte)'a', 0, 0, 0 }, Get(p, mem + 0x200, 5));
        Assert.AreEqual(1, Call(p, Fcntl, (ulong)fd, 1), "MFD_CLOEXEC setzt FD_CLOEXEC");
        Assert.AreEqual(-EINVAL, Call(p, MemfdCreate, mem, MfdHugetlb));
        Assert.AreEqual(0, Call(p, Close, (ulong)fd));
        p.Destroy();
        Assert.AreEqual(0, _store.Live.Count, "alle Frames frei");
    }

    [TestMethod]
    public void ZweiProzesseSehenDieselbenFramesBisZurLetztenFreigabe()
    {
        var (a, pagesA, memA) = NewProcess(1);
        var (b, pagesB, memB) = NewProcess(2);
        int fd = Memfd(a, memA);
        Assert.AreEqual(0, Call(a, Ftruncate, (ulong)fd, 3 * Page));
        ulong atA = MapSharedFd(a, fd, 3 * Page);
        int fdB = Pass(a, fd, b);
        ulong atB = MapSharedFd(b, fdB, 3 * Page);

        // Programm A schreibt über einen Seitenfehler, B liest über den Kernel und umgekehrt.
        Assert.IsTrue(a.HandlePageFault(atA + Page, 2));
        Assert.IsTrue(a.TryWrite(atA + Page + 8, Encoding.ASCII.GetBytes("von A")));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("von A"), Get(b, atB + Page + 8, 5));
        Assert.IsTrue(b.TryWrite(atB + 2 * Page, Encoding.ASCII.GetBytes("von B")));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("von B"), Get(a, atA + 2 * Page, 5));
        Assert.IsTrue(b.HandlePageFault(atB + 2 * Page, 0));
        Assert.AreEqual(pagesA.Mapped[atA + 2 * Page], pagesB.Mapped[atB + 2 * Page], "derselbe Frame in beiden Tabellen");

        // write über den Deskriptor landet im selben Frame.
        Assert.IsTrue(a.TryWrite(memA + 0x100, Encoding.ASCII.GetBytes("WR")));
        Assert.AreEqual((long)(Page + 8), Call(a, Lseek, (ulong)fd, Page + 8, 0));
        Assert.AreEqual(2, Call(a, Write, (ulong)fd, memA + 0x100, 2));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("WRn A"), Get(b, atB + Page + 8, 5));

        int baseline = 8; // je Prozess 4 anonyme Seiten, davon belegt: höchstens 4
        // A schließt und gibt die Abbildung auf; B hält Inhalt und Frames.
        Assert.AreEqual(0, Call(a, Close, (ulong)fd));
        Assert.AreEqual(0, Call(a, Munmap, atA, 3 * Page));
        Assert.AreEqual(0, a.SharedRegionCount);
        a.Destroy();
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("von B"), Get(b, atB + 2 * Page, 5));
        Assert.AreEqual(0, Call(b, Close, (ulong)fdB));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("WRn A"), Get(b, atB + Page + 8, 5), "Abbildung hält die Datei nach close");

        // Prozessende von B gibt die letzte Abbildung und damit alle Frames frei.
        b.Destroy();
        Assert.IsTrue(_store.Live.Count < baseline);
        Assert.AreEqual(0, _store.Live.Count);
    }

    [TestMethod]
    public void MunmapInDerMitteLaesstReststueckeMitRichtigemVersatz()
    {
        var (p, _, mem) = NewProcess(1);
        int fd = Memfd(p, mem);
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, 4 * Page));
        for (int i = 0; i < 4; i++)
        {
            Assert.IsTrue(p.TryWrite(mem + 0x100, new[] { (byte)('0' + i) }));
            Call(p, Lseek, (ulong)fd, (ulong)i * Page, 0);
            Assert.AreEqual(1, Call(p, Write, (ulong)fd, mem + 0x100, 1));
        }

        ulong at = MapSharedFd(p, fd, 3 * Page, ProtR, Page); // Dateiseiten 1..3
        Assert.AreEqual((byte)'1', Get(p, at, 1)[0]);
        Assert.AreEqual(0, Call(p, Munmap, at + Page, Page));
        Assert.AreEqual(2, p.SharedRegionCount);
        Assert.AreEqual((byte)'1', Get(p, at, 1)[0]);
        Assert.AreEqual((byte)'3', Get(p, at + 2 * Page, 1)[0]);
        Assert.IsFalse(p.TryRead(at + Page, new byte[1]));

        // MADV_DONTNEED auf geteilter Seite: Inhalt bleibt in der Datei.
        Assert.AreEqual(0, Call(p, Madvise, at + 2 * Page, Page, 4));
        Assert.AreEqual((byte)'3', Get(p, at + 2 * Page, 1)[0]);
        Assert.AreEqual(0, Call(p, Close, (ulong)fd));
        Assert.AreEqual(0, Call(p, Munmap, at, 3 * Page));
        Assert.AreEqual(0, p.SharedRegionCount);
        p.Destroy();
        Assert.AreEqual(0, _store.Live.Count);
    }

    [TestMethod]
    public void ZugriffHinterDemDateiendeScheitert()
    {
        var (p, _, mem) = NewProcess(1);
        int fd = Memfd(p, mem);
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, 100));
        ulong at = MapSharedFd(p, fd, 2 * Page);
        Assert.IsTrue(p.HandlePageFault(at, 2));
        Assert.IsFalse(p.HandlePageFault(at + Page, 0), "Linux: SIGBUS");
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, 2 * Page));
        Assert.IsTrue(p.HandlePageFault(at + Page, 0));
        p.Destroy();
        Assert.AreEqual(0, _store.Live.Count);
    }

    [TestMethod]
    public void SiegelWieLinux()
    {
        var (p, _, mem) = NewProcess(1);
        int plain = Memfd(p, mem);
        Assert.AreEqual(1, Call(p, Fcntl, (ulong)plain, FGetSeals), "ohne MFD_ALLOW_SEALING ist F_SEAL_SEAL gesetzt");
        Assert.AreEqual(-EPERM, Call(p, Fcntl, (ulong)plain, FAddSeals, 2));
        Assert.AreEqual(-EINVAL, Call(p, Fcntl, 1, FGetSeals), "Terminal hat keine Siegel");

        int fd = Memfd(p, mem, MfdAllowSealing);
        Assert.AreEqual(0, Call(p, Fcntl, (ulong)fd, FGetSeals));
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, Page));
        ulong at = MapSharedFd(p, fd, Page);
        Assert.AreEqual(-EBUSY, Call(p, Fcntl, (ulong)fd, FAddSeals, 8), "F_SEAL_WRITE mit beschreibbarer Abbildung");
        Assert.AreEqual(0, Call(p, Munmap, at, Page));
        Assert.AreEqual(0, Call(p, Fcntl, (ulong)fd, FAddSeals, 2 | 4 | 8));
        Assert.AreEqual(14, Call(p, Fcntl, (ulong)fd, FGetSeals));
        Assert.AreEqual(-EPERM, Call(p, Ftruncate, (ulong)fd, 10));
        Assert.AreEqual(-EPERM, Call(p, Write, (ulong)fd, mem, 1));
        Assert.AreEqual(-EPERM, Call(p, Mmap, 0, Page, ProtRw, MapShared, (ulong)fd, 0));
        ulong ro = MapSharedFd(p, fd, Page, ProtR);
        Assert.AreEqual(0, Get(p, ro, 1)[0]);

        // MAP_PRIVATE bleibt die Kopie wie bei anderen Dateien.
        long priv = Call(p, Mmap, 0, Page, ProtRw, MapPrivate, (ulong)fd, 0);
        Assert.IsTrue(priv > 0);
        Assert.AreEqual(0, p.SharedRegionCount - 1);
        p.Destroy();
        Assert.AreEqual(0, _store.Live.Count);
    }
}
