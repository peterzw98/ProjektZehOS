using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>
/// Ladybird V32: MAP_SHARED auf regulären Dateien (sqlite WAL, Datei <c>-shm</c>). Alle Abbildungen aller Prozesse eines Laufs teilen die
/// Frames einer Datei; read/write/pread/pwrite sehen sie; zurückgeschrieben wird bei msync, fsync, close, munmap und Prozessende.
/// </summary>
[TestClass]
public sealed class LinuxSharedFileTests
{
    private const ulong Page = 4096;
    private const long Read = 0, Write = 1, Close = 3, Lseek = 8, Mmap = 9, Mprotect = 10, Munmap = 11, Pread = 17, Pwrite = 18, Msync = 26,
        Fsync = 74, Ftruncate = 77, Openat = 257, Unlinkat = 263, Renameat = 264;
    private const int ProtR = 1, ProtRw = 3, MapShared = 1, MapFixed = 0x10;
    private const int ORdOnly = 0, ORdWr = 2, OCreat = 0x40, OTrunc = 0x200;
    private const long EACCES = 13, EINVAL = 22, ENOMEM = 12, ENODEV = 19;

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

    private readonly Frames _frames = new();
    private readonly LinuxOpenFiles _open = new();
    private readonly LinuxIds _ids = new(1);
    private readonly MemoryFileStore _disk = new();
    private readonly TerminalDescriptor _term = new(new Lines());
    private LinuxFileSystem? _fs;

    private LinuxFileSystem Fs => _fs ??= new LinuxFileSystem(_disk, _term);

    // Alle Prozesse eines Laufs teilen Dateisystem und Dateisperre wie im Kernel (posix_spawn).
    private (LinuxUserProcess P, Pages Pages, ulong Mem) NewProcess(int pid)
    {
        var pages = new Pages(_frames);
        var p = new LinuxUserProcess(pages, _term, pid, 64 << 20, null, Fs, _open, _ids);
        ulong mem = p.MapAnonymous(4 * Page, ProtRw);
        Assert.AreNotEqual(0UL, mem);
        return (p, pages, mem);
    }

    private static long Call(LinuxUserProcess p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
        return LinuxSyscalls.Dispatch(ref a, p);
    }

    private static int Open(LinuxUserProcess p, ulong mem, string path, int flags)
    {
        Assert.IsTrue(p.TryWrite(mem + 0x800, Encoding.ASCII.GetBytes(path + "\0")));
        long fd = Call(p, Openat, unchecked((ulong)-100L), mem + 0x800, (ulong)flags, 0x1a4);
        Assert.IsTrue(fd >= 3, $"open {path}: {fd}");
        return (int)fd;
    }

    private static ulong Map(LinuxUserProcess p, int fd, ulong len, int prot = ProtRw, ulong offset = 0)
    {
        long r = Call(p, Mmap, 0, len, (ulong)prot, MapShared, (ulong)fd, offset);
        Assert.IsTrue(r > 0, $"mmap {r}");
        return (ulong)r;
    }

    private static byte[] Get(LinuxUserProcess p, ulong at, int n)
    {
        var b = new byte[n];
        Assert.IsTrue(p.TryRead(at, b));
        return b;
    }

    private static void Put(LinuxUserProcess p, ulong at, string text) => Assert.IsTrue(p.TryWrite(at, Encoding.ASCII.GetBytes(text)));

    /// <summary>Liest am Dateisystem vorbei direkt aus dem Speicher: zeigt, was zurückgeschrieben ist.</summary>
    private string OnDisk(string path, ulong at, int n)
    {
        Assert.AreEqual(0, _disk.OpenFile(path, false, out ILinuxStoreFile? f));
        var b = new byte[n];
        long got = f!.ReadAt((long)at, b);
        _ = f.Close();
        return Encoding.ASCII.GetString(b, 0, (int)Math.Max(0, got));
    }

    // Wie sqlite (unixShmMap): Datei anlegen, je Seite ein Byte ans Ende schreiben, dann 32 KiB geteilt abbilden.
    private static int ShmFile(LinuxUserProcess p, ulong mem, string path, int pagesCount)
    {
        int fd = Open(p, mem, path, ORdWr | OCreat);
        Put(p, mem + 0x100, "\0");
        for (int i = 1; i <= pagesCount; i++)
        {
            Assert.AreEqual(1, Call(p, Pwrite, (ulong)fd, mem + 0x100, 1, (ulong)i * Page - 1));
        }

        return fd;
    }

    [TestMethod]
    public void ZweiProzesseTeilenDieSeitenEinerDatei()
    {
        var (a, pagesA, memA) = NewProcess(1);
        var (b, pagesB, memB) = NewProcess(2);
        int fdA = ShmFile(a, memA, "db-shm", 8);
        ulong atA = Map(a, fdA, 8 * Page);
        int fdB = Open(b, memB, "db-shm", ORdWr);
        ulong atB = Map(b, fdB, 8 * Page);

        // A schreibt über einen Seitenfehler, B liest; B schreibt über den Kernel, A liest; derselbe Frame in beiden Tabellen.
        Assert.IsTrue(a.HandlePageFault(atA + Page, 2));
        Put(a, atA + Page + 8, "von A");
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("von A"), Get(b, atB + Page + 8, 5));
        Put(b, atB + 7 * Page, "von B");
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("von B"), Get(a, atA + 7 * Page, 5));
        Assert.IsTrue(b.HandlePageFault(atB + Page, 0));
        Assert.AreEqual(pagesA.Mapped[atA + Page], pagesB.Mapped[atB + Page], "derselbe Frame");

        // Eine zweite Abbildung mit Versatz im selben Prozess sieht dieselben Seiten.
        ulong second = Map(a, fdA, Page, ProtRw, 7 * Page);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("von B"), Get(a, second, 5));

        Assert.AreEqual(0, Call(a, Munmap, atA, 8 * Page));
        Assert.AreEqual(0, Call(a, Munmap, second, Page));
        Assert.AreEqual(0, Call(a, Close, (ulong)fdA));
        a.Destroy();
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("von A"), Get(b, atB + Page + 8, 5), "B hält die Seiten");
        Assert.AreEqual(0, Call(b, Close, (ulong)fdB));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("von B"), Get(b, atB + 7 * Page, 5), "Abbildung hält die Datei nach close");
        b.Destroy();
        Assert.AreEqual(0, _frames.Live.Count, "alle Frames frei");
        Assert.AreEqual("von A", OnDisk("db-shm", Page + 8, 5), "Prozessende schreibt zurück");
        Assert.AreEqual("von B", OnDisk("db-shm", 7 * Page, 5));
    }

    [TestMethod]
    public void PreadUndPwriteSehenDieAbbildung()
    {
        var (p, _, mem) = NewProcess(1);
        int fd = ShmFile(p, mem, "x-shm", 2);
        ulong at = Map(p, fd, 2 * Page);
        Put(p, at + 100, "abbild");
        Assert.AreEqual(6, Call(p, Pread, (ulong)fd, mem + 0x200, 6, 100));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("abbild"), Get(p, mem + 0x200, 6), "pread sieht die Abbildung vor dem Zurückschreiben");

        // pwrite (zweiter Deskriptor) landet in Datei und Frame; ein späteres Zurückschreiben überschreibt es nicht mit altem Stand.
        int fd2 = Open(p, mem, "x-shm", ORdWr);
        Put(p, mem + 0x300, "PW");
        Assert.AreEqual(2, Call(p, Pwrite, (ulong)fd2, mem + 0x300, 2, 102));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("abPWld"), Get(p, at + 100, 6));
        Assert.AreEqual(0, Call(p, Msync, at, 2 * Page, 4));
        Assert.AreEqual("abPWld", OnDisk("x-shm", 100, 6));

        // read mit Dateiposition und write auf eine noch nicht berührte Seite: Die Abbildung liest sie danach aus der Datei.
        Assert.AreEqual(100L, Call(p, Lseek, (ulong)fd, 100, 0));
        Assert.AreEqual(6, Call(p, Read, (ulong)fd, mem + 0x200, 6));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("abPWld"), Get(p, mem + 0x200, 6));
        Assert.AreEqual((long)Page + 4, Call(p, Lseek, (ulong)fd2, Page + 4, 0));
        Assert.AreEqual(2, Call(p, Write, (ulong)fd2, mem + 0x300, 2));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("PW"), Get(p, at + Page + 4, 2));
        p.Destroy();
        Assert.AreEqual(0, _frames.Live.Count);
    }

    [TestMethod]
    public void MsyncFsyncUndMunmapSchreibenZurueck()
    {
        var (p, _, mem) = NewProcess(1);
        int fd = ShmFile(p, mem, "y-shm", 3);
        ulong at = Map(p, fd, 3 * Page);
        Put(p, at + 10, "eins");
        Put(p, at + Page + 10, "zwei");
        Put(p, at + 2 * Page + 10, "drei");
        Assert.AreEqual("\0\0\0\0", OnDisk("y-shm", 10, 4), "vor msync nur in den Frames");

        // msync nur über Seite 0; Prüfungen wie Linux.
        Assert.AreEqual(-EINVAL, Call(p, Msync, at + 1, Page, 4));
        Assert.AreEqual(-EINVAL, Call(p, Msync, at, Page, 5), "MS_ASYNC und MS_SYNC zusammen");
        Assert.AreEqual(-EINVAL, Call(p, Msync, at, Page, 8));
        Assert.AreEqual(-ENOMEM, Call(p, Msync, at, 64 * Page, 4), "Lücke im Bereich");
        Assert.AreEqual(0, Call(p, Msync, at, 100, 1));
        Assert.AreEqual("eins", OnDisk("y-shm", 10, 4));
        Assert.AreEqual("\0\0\0\0", OnDisk("y-shm", Page + 10, 4));

        Assert.AreEqual(0, Call(p, Fsync, (ulong)fd));
        Assert.AreEqual("zwei", OnDisk("y-shm", Page + 10, 4), "fsync schreibt die Frames");

        Put(p, at + 2 * Page + 10, "DREI");
        Assert.AreEqual(0, Call(p, Munmap, at + 2 * Page, Page));
        Assert.AreEqual("DREI", OnDisk("y-shm", 2 * Page + 10, 4), "munmap schreibt den wegfallenden Teil");
        Assert.AreEqual(3 * Page, (ulong)Call(p, Lseek, (ulong)fd, 0, 2), "Zurückschreiben verlängert die Datei nicht");
        Assert.AreEqual(0, Call(p, Close, (ulong)fd));
        p.Destroy();
        Assert.AreEqual(0, _frames.Live.Count);
    }

    [TestMethod]
    public void FtruncateWachsenUndKuerzen()
    {
        var (p, _, mem) = NewProcess(1);
        int fd = ShmFile(p, mem, "z-shm", 1);
        ulong at = Map(p, fd, 2 * Page);
        Assert.IsTrue(p.HandlePageFault(at, 2));
        Assert.IsFalse(p.HandlePageFault(at + Page, 0, out bool bus), "hinter dem Dateiende: SIGBUS");
        Assert.IsTrue(bus);
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, 2 * Page));
        Assert.IsTrue(p.HandlePageFault(at + Page, 2), "nach dem Wachsen abbildbar");
        CollectionAssert.AreEqual(new byte[4], Get(p, at + Page, 4));

        // Kürzen mitten in Seite 0: Der Rest der Seite liest sich nach erneutem Wachsen als Nullen, auch über read.
        Put(p, at + 50, "abcdef");
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, 52));
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, Page));
        Assert.AreEqual(6, Call(p, Pread, (ulong)fd, mem + 0x200, 6, 50));
        CollectionAssert.AreEqual(new byte[] { (byte)'a', (byte)'b', 0, 0, 0, 0 }, Get(p, mem + 0x200, 6));
        p.Destroy();
        Assert.AreEqual(0, _frames.Live.Count);
        Assert.AreEqual(4096L, _disk.Lookup("z-shm", out LinuxStoreNode node) == 0 ? node.Size : -1);
    }

    [TestMethod]
    public void LesenderDeskriptorWieLinux()
    {
        var (p, _, mem) = NewProcess(1);
        int rw = ShmFile(p, mem, "r-shm", 2);
        int ro = Open(p, mem, "r-shm", ORdOnly);
        Assert.AreEqual(-EACCES, Call(p, Mmap, 0, Page, ProtRw, MapShared, (ulong)ro, 0), "geteilt beschreibbar braucht O_RDWR");

        // Die lesende Abbildung legt die Ablage an (mit der nur lesenden Datei); die spätere schreibbare ersetzt die Rückhaltdatei, sonst
        // scheiterte das Zurückschreiben unten.
        ulong reader = Map(p, ro, 2 * Page, ProtR);
        Assert.IsTrue(p.HandlePageFault(reader, 0));
        Assert.AreEqual(-EACCES, Call(p, Mprotect, reader, Page, ProtRw), "ohne Schreibrecht des Deskriptors");
        ulong writer = Map(p, rw, 2 * Page);
        Put(p, writer + 5, "neu");
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("neu"), Get(p, reader + 5, 3));
        Assert.AreEqual(0, Call(p, Msync, writer, Page, 4));
        Assert.AreEqual("neu", OnDisk("r-shm", 5, 3));
        Assert.AreEqual(0, Call(p, Mprotect, writer, Page, ProtR));
        Assert.AreEqual(0, Call(p, Mprotect, writer, Page, ProtRw));

        // MAP_FIXED bleibt beim alten Weg: beschreibbar geteilt -ENODEV.
        Assert.AreEqual(-ENODEV, Call(p, Mmap, writer, Page, ProtRw, MapShared | MapFixed, (ulong)rw, 0));
        p.Destroy();
        Assert.AreEqual(0, _frames.Live.Count);
    }

    [TestMethod]
    public void NeueDateiGleichenNamensBekommtNeueSeiten()
    {
        var (p, _, mem) = NewProcess(1);
        int fd = ShmFile(p, mem, "n-shm", 1);
        ulong old = Map(p, fd, Page);
        Put(p, old, "alt");

        Assert.IsTrue(p.TryWrite(mem + 0x800, Encoding.ASCII.GetBytes("n-shm\0")));
        Assert.AreEqual(0, Call(p, Unlinkat, unchecked((ulong)-100L), mem + 0x800, 0));
        int fresh = Open(p, mem, "n-shm", ORdWr | OCreat | OTrunc);
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fresh, Page));
        ulong now = Map(p, fresh, Page);
        CollectionAssert.AreEqual(new byte[3], Get(p, now, 3), "nicht die Seiten der gelöschten Datei");
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("alt"), Get(p, old, 3), "die alte Abbildung bleibt");
        p.Destroy();
        Assert.AreEqual(0, _frames.Live.Count);
    }

    [TestMethod]
    public void OTruncVerwirftDieFrames()
    {
        var (p, _, mem) = NewProcess(1);
        int fd = ShmFile(p, mem, "t-shm", 1);
        ulong at = Map(p, fd, Page);
        Put(p, at, "inhalt");
        int again = Open(p, mem, "t-shm", ORdWr | OTrunc);
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)again, Page));
        Assert.AreEqual(6, Call(p, Pread, (ulong)again, mem + 0x200, 6, 0));
        CollectionAssert.AreEqual(new byte[6], Get(p, mem + 0x200, 6));
        Assert.AreEqual(0, Call(p, Munmap, at, Page));
        Assert.AreEqual("\0\0\0\0\0\0", OnDisk("t-shm", 0, 6), "kein alter Inhalt zurückgeschrieben");
        p.Destroy();
        Assert.AreEqual(0, _frames.Live.Count);
    }
    private static void Rename(LinuxUserProcess p, ulong mem, string from, string to)
    {
        Assert.IsTrue(p.TryWrite(mem + 0x800, Encoding.ASCII.GetBytes(from + "\0")));
        Assert.IsTrue(p.TryWrite(mem + 0xa00, Encoding.ASCII.GetBytes(to + "\0")));
        Assert.AreEqual(0, Call(p, Renameat, unchecked((ulong)-100L), mem + 0x800, unchecked((ulong)-100L), mem + 0xa00));
    }

    [TestMethod]
    public void RenameAlterDeskriptorSchreibtDurchDieAblage()
    {
        // V32-Review P2 (a): map, rename, write über den alten fd, munmap, read. Vorher fand der alte fd die Ablage nicht mehr, und munmap
        // schrieb den alten Frame über das write.
        var (p, _, mem) = NewProcess(1);
        int fd = ShmFile(p, mem, "a-shm", 1);
        ulong at = Map(p, fd, Page);
        Put(p, at, "abbild");
        Rename(p, mem, "a-shm", "b-shm");
        Put(p, mem + 0x300, "WRITE!");
        Assert.AreEqual(6, Call(p, Pwrite, (ulong)fd, mem + 0x300, 6, 0));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("WRITE!"), Get(p, at, 6), "die Abbildung sieht das write");

        // Ein neuer Deskriptor unter dem neuen Namen teilt dieselbe Ablage.
        int fresh = Open(p, mem, "b-shm", ORdWr);
        ulong other = Map(p, fresh, Page);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("WRITE!"), Get(p, other, 6));
        Assert.AreEqual(0, Call(p, Munmap, at, Page));
        Assert.AreEqual(0, Call(p, Munmap, other, Page));
        Assert.AreEqual(0, Call(p, Close, (ulong)fd));
        Assert.AreEqual(6, Call(p, Pread, (ulong)fresh, mem + 0x200, 6, 0));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("WRITE!"), Get(p, mem + 0x200, 6));
        Assert.AreEqual("WRITE!", OnDisk("b-shm", 0, 6), "munmap überschreibt das write nicht");
        p.Destroy();
        Assert.AreEqual(0, _frames.Live.Count);
    }

    [TestMethod]
    public void UnlinkUndNeuAlterDeskriptorTrifftNurDieAlteDatei()
    {
        // V32-Review P2 (b): Gleicher Name, gleicher Pfad-Hash; der alte fd darf die Frames der neuen Datei nicht verändern.
        var (p, _, mem) = NewProcess(1);
        int fd = ShmFile(p, mem, "u-shm", 1);
        ulong old = Map(p, fd, Page);
        Put(p, old, "alt");
        Assert.IsTrue(p.TryWrite(mem + 0x800, Encoding.ASCII.GetBytes("u-shm\0")));
        Assert.AreEqual(0, Call(p, Unlinkat, unchecked((ulong)-100L), mem + 0x800, 0));
        int fresh = Open(p, mem, "u-shm", ORdWr | OCreat);
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fresh, Page));
        ulong now = Map(p, fresh, Page);
        Put(p, now + 100, "neu");

        Put(p, mem + 0x300, "XYZ");
        Assert.AreEqual(3, Call(p, Pwrite, (ulong)fd, mem + 0x300, 3, 100));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("XYZ"), Get(p, old + 100, 3), "alte Abbildung sieht das write des alten fd");
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("neu"), Get(p, now + 100, 3), "neue Datei unberührt");
        Assert.AreEqual(0, Call(p, Ftruncate, (ulong)fd, 0));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("neu"), Get(p, now + 100, 3), "ftruncate des alten fd kürzt die neue Ablage nicht");
        Assert.AreEqual(0, Call(p, Munmap, old, Page));
        Assert.AreEqual(0, Call(p, Close, (ulong)fd));
        Assert.AreEqual(0, Call(p, Munmap, now, Page));
        Assert.AreEqual("neu", OnDisk("u-shm", 100, 3));
        Assert.AreEqual(0, Call(p, Close, (ulong)fresh));
        p.Destroy();
        Assert.AreEqual(0, _frames.Live.Count);
    }

    [TestMethod]
    public void NurGeschriebeneSeitenGehenZurueck()
    {
        // V32-P2: Eine nur lesend abgebildete Seite gleicht der Datei; munmap schreibt sie nicht zurück und überschreibt so nichts, was an
        // der Ablage vorbei in die Datei kam (hier direkt in den Speicher, wie über einen Hartlink).
        var (p, _, mem) = NewProcess(1);
        int fd = ShmFile(p, mem, "d-shm", 2);
        ulong writer = Map(p, fd, Page, ProtRw, 0);
        ulong reader = Map(p, fd, Page, ProtR, Page);
        Assert.IsTrue(p.HandlePageFault(reader, 0));
        Put(p, writer + 8, "map");
        Assert.AreEqual(0, _disk.OpenFile("d-shm", true, out ILinuxStoreFile? direct));
        Assert.AreEqual(5, direct!.WriteAt((long)Page + 8, Encoding.ASCII.GetBytes("extra")));
        _ = direct.Close();

        Assert.AreEqual(0, Call(p, Munmap, writer, Page));
        Assert.AreEqual(0, Call(p, Munmap, reader, Page));
        Assert.AreEqual(0, Call(p, Close, (ulong)fd));
        Assert.AreEqual("map", OnDisk("d-shm", 8, 3));
        Assert.AreEqual("extra", OnDisk("d-shm", Page + 8, 5), "lesende Seite nicht zurückgeschrieben");

        // mprotect auf PROT_WRITE macht eine schon abgebildete Seite zur geschriebenen.
        fd = Open(p, mem, "d-shm", ORdWr);
        ulong at = Map(p, fd, Page, ProtR, Page);
        Assert.IsTrue(p.HandlePageFault(at, 0));
        Assert.AreEqual(0, Call(p, Mprotect, at, Page, ProtRw));
        Put(p, at + 8, "PROTW");
        Assert.AreEqual(0, Call(p, Msync, at, Page, 4));
        Assert.AreEqual("PROTW", OnDisk("d-shm", Page + 8, 5));
        p.Destroy();
        Assert.AreEqual(0, _frames.Live.Count);
    }

    [TestMethod]
    public void RenameEinesVerzeichnissesNimmtDieAblageMit()
    {
        var (p, _, mem) = NewProcess(1);
        Assert.IsTrue(p.TryWrite(mem + 0x800, Encoding.ASCII.GetBytes("dir\0")));
        Assert.AreEqual(0, Call(p, 258, unchecked((ulong)-100L), mem + 0x800, 0x1ed)); // mkdirat
        int fd = ShmFile(p, mem, "dir/w-shm", 1);
        ulong at = Map(p, fd, Page);
        Put(p, at, "frame");
        Rename(p, mem, "dir", "neu");
        int fresh = Open(p, mem, "neu/w-shm", ORdWr);
        Assert.AreEqual(5, Call(p, Pread, (ulong)fresh, mem + 0x200, 5, 0));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("frame"), Get(p, mem + 0x200, 5), "neuer Name, dieselbe Ablage");
        p.Destroy();
        Assert.AreEqual(0, _frames.Live.Count);
        Assert.AreEqual("frame", OnDisk("neu/w-shm", 0, 5));
    }
}
