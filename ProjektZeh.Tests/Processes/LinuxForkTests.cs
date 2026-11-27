using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V21: fork, clone ohne CLONE_VM und clone3 als Prozess mit kopiertem Adressraum (eifrig, ohne COW).</summary>
[TestClass]
public sealed class LinuxForkTests
{
    private const ulong Page = 4096;
    private const long Write = 1, Close = 3, Mmap = 9, Munmap = 11, Getpid = 39, Getppid = 110, MemfdCreate = 319, Wait4 = 61;
    private const int ProtRw = 3, MapShared = 1, MapPrivate = 2, MapAnonymous = 0x20;
    private const ulong SigChld = 17;
    private const long EAGAIN = 11, ENOMEM = 12, EINVAL = 22, ENOSYS = 38, E2BIG = 7;

    /// <summary>Frames aller Prozesse wie der PageAllocator des Kernels: eine Quelle, Zählung der lebenden Frames.</summary>
    private sealed class Frames
    {
        public readonly Dictionary<ulong, byte[]> Live = new();
        public ulong Next = 0x100000;
        public int Limit = int.MaxValue; // physischer Speicher des Rechners
    }

    private sealed class Pages(Frames store) : IUserPages
    {
        public readonly Dictionary<ulong, ulong> Mapped = new();
        public bool Destroyed;
        public ulong AllocateFrame() { if (store.Live.Count >= store.Limit) { return 0; } store.Live[store.Next] = new byte[Page]; store.Next += Page; return store.Next - Page; }
        public void FreeFrame(ulong frame) { Assert.IsTrue(store.Live.Remove(frame), "Frame doppelt freigegeben"); }
        public Span<byte> Frame(ulong frame) => store.Live[frame];
        public bool Map(ulong page, ulong frame, MapAccess access) { Mapped[page] = frame; return true; }
        public bool Unmap(ulong page) { Mapped.Remove(page); return true; }
        public void DestroyTable() { Mapped.Clear(); Destroyed = true; }
    }

    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    private readonly Frames _store = new();
    private readonly List<LinuxUserProcess> _children = new();
    private readonly List<ulong> _stacks = new();
    private bool _refuseStart;

    private LinuxUserProcess NewParent(ulong maxBytes = 64 << 20)
    {
        var term = new TerminalDescriptor(new Lines());
        var p = new LinuxUserProcess(new Pages(_store), term, 1, maxBytes, null, new LinuxFileSystem(new MemoryFileStore(), term));
        Wire(p);
        return p;
    }

    /// <summary>Spielt die Brücke nach: neue Seiten aus derselben Quelle, StartFork merkt sich das Kind (kein Ring 3).</summary>
    private void Wire(LinuxUserProcess p)
    {
        p.CreatePages = () => new Pages(_store);
        p.StartFork = (child, stack) =>
        {
            if (_refuseStart)
            {
                return false;
            }

            Wire(child);
            _children.Add(child);
            _stacks.Add(stack);
            return true;
        };
    }

    private static long Call(LinuxUserProcess p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
        return LinuxSyscalls.Dispatch(ref a, p);
    }

    private static ulong MapAnon(LinuxUserProcess p, ulong len, int flags)
    {
        long r = Call(p, Mmap, 0, len, ProtRw, (ulong)(flags | MapAnonymous), unchecked((ulong)-1), 0);
        Assert.IsTrue(r > 0, $"mmap {r}");
        return (ulong)r;
    }

    private static byte Get(LinuxUserProcess p, ulong at)
    {
        var b = new byte[1];
        Assert.IsTrue(p.TryRead(at, b));
        return b[0];
    }

    private static void Put(LinuxUserProcess p, ulong at, byte value) => Assert.IsTrue(p.TryWrite(at, new[] { value }));

    [TestMethod]
    public void ForkMitUngelesenenDateiseitenLiestImKind()
    {
        // V30-P3: Seiten einer privaten Dateiabbildung, die die Eltern vor fork nicht berührt haben, liest das Kind selbst aus der Datei,
        // auch nachdem die Eltern close und unlink gerufen haben. Schreiben bleibt im jeweiligen Prozess; die Datei schließt erst, wenn
        // Kind und Eltern ihre Ausschnitte abgegeben haben.
        var term = new TerminalDescriptor(new Lines());
        var store = new MemoryFileStore();
        var parent = new LinuxUserProcess(new Pages(_store), term, 1, 64 << 20, null, new LinuxFileSystem(store, term));
        Wire(parent);
        ulong buf = MapAnon(parent, 3 * Page, MapPrivate);
        byte[] content = new byte[2 * Page];
        for (int i = 0; i < content.Length; i++)
        {
            content[i] = (byte)((i * 13) + (i >> 12));
        }

        Assert.IsTrue(parent.TryWrite(buf, "/f\0"u8));
        Assert.IsTrue(parent.TryWrite(buf + Page, content));
        long fd = Call(parent, 2, buf, (ulong)(LinuxFileSystem.ORdWr | LinuxFileSystem.OCreat), 0x1A4);
        Assert.IsTrue(fd >= 0, "open " + fd);
        Assert.AreEqual((long)content.Length, Call(parent, Write, (ulong)fd, buf + Page, (ulong)content.Length));
        long m = Call(parent, Mmap, 0, 2 * Page, ProtRw, MapPrivate, (ulong)fd, 0);
        Assert.IsTrue(m > 0, "mmap " + m);
        ulong at = (ulong)m;
        Put(parent, at + 1, 0xAA); // Seite 0 vor fork gelesen und geändert, Seite 1 nicht
        Assert.AreEqual(0L, Call(parent, Close, (ulong)fd));
        Assert.AreEqual(0L, Call(parent, 87, buf), "unlink");

        Assert.IsTrue(Call(parent, LinuxSyscalls.Fork) > 0);
        LinuxUserProcess child = _children[0];
        Assert.AreEqual(1, child.FileExtentCount, "Kind erbt den Ausschnitt");
        Assert.AreEqual(content[Page + 5], Get(child, at + Page + 5), "ungelesene Seite im Kind aus der Datei");
        Assert.AreEqual(0xAA, Get(child, at + 1), "kopierte Seite der Eltern");
        Put(child, at + Page + 6, 0x11);
        Assert.AreEqual(content[Page + 6], Get(parent, at + Page + 6), "Schreiben des Kinds bleibt im Kind");
        long bytesOpen = store.TotalBytes;
        child.Destroy();
        Assert.AreEqual(bytesOpen, store.TotalBytes, "Eltern halten die Datei noch");
        Assert.AreEqual(content[Page + 7], Get(parent, at + Page + 7));
        Assert.AreEqual(0L, Call(parent, Munmap, at, 2 * Page));
        Assert.AreEqual(0L, store.TotalBytes, "letzter Ausschnitt schließt die entfernte Datei");
        parent.Destroy();
    }

    [TestMethod]
    public void ForkTrenntPrivatenSpeicherUndTeiltGeteilten()
    {
        LinuxUserProcess parent = NewParent();
        ulong priv = MapAnon(parent, 8 * Page, MapPrivate);
        ulong shared = MapAnon(parent, Page, MapShared);
        for (ulong i = 0; i < 8; i++)
        {
            Put(parent, priv + (i * Page), (byte)(i + 1));
        }

        Put(parent, shared, 1);
        Assert.IsTrue(parent.TrySetBrk(parent.Brk + Page) || parent.Brk == 0);

        long pid = Call(parent, LinuxSyscalls.Fork);
        Assert.AreEqual(1, _children.Count);
        LinuxUserProcess child = _children[0];
        Assert.AreEqual(child.Pid, pid);
        Assert.AreNotEqual(parent.Pid, child.Pid);
        Assert.AreEqual(0UL, _stacks[0], "fork übergibt keinen Stack");
        Assert.AreSame(parent, child.Parent);
        Assert.AreEqual(child.Pid, Call(child, Getpid));
        Assert.AreEqual(parent.Pid, Call(child, Getppid));
        Assert.AreEqual(child.Pid, child.MainThread.Tid, "Hauptthread des Kinds trägt die PID");
        Assert.AreEqual(parent.Brk, child.Brk);
        Assert.IsFalse(child.InVfork);

        for (ulong i = 0; i < 8; i++)
        {
            Assert.AreEqual((byte)(i + 1), Get(child, priv + (i * Page)), "Inhalt kopiert");
        }

        Put(child, priv, 99);
        Put(parent, priv + Page, 77);
        Put(child, shared, 2);
        Assert.AreEqual(1, Get(parent, priv), "privat: Schreiben im Kind bleibt im Kind");
        Assert.AreEqual(2, Get(child, priv + Page), "privat: Schreiben der Eltern nach fork bleibt bei den Eltern");
        Assert.AreEqual(2, Get(parent, shared), "MAP_SHARED|MAP_ANONYMOUS bleibt geteilt");
        Put(parent, shared, 3);
        Assert.AreEqual(3, Get(child, shared));

        // Eine neue Seite der Eltern nach fork erscheint nicht im Kind (das sieht sie genullt).
        Put(parent, priv + (7 * Page) + 5, 42);
        Assert.AreEqual(0, Get(child, priv + (7 * Page) + 5));
    }

    [TestMethod]
    public void KindEndeGibtSeineFramesFreiGeteilteBleiben()
    {
        LinuxUserProcess parent = NewParent();
        ulong priv = MapAnon(parent, 4 * Page, MapPrivate);
        ulong shared = MapAnon(parent, 2 * Page, MapShared);
        for (ulong i = 0; i < 4; i++)
        {
            Put(parent, priv + (i * Page), 1);
        }

        Put(parent, shared, 5);
        int before = _store.Live.Count;
        Assert.IsTrue(Call(parent, LinuxSyscalls.Fork) > 0);
        LinuxUserProcess child = _children[0];
        Assert.IsTrue(_store.Live.Count > before, "das Kind hält eigene Frames");
        Put(child, shared + Page, 6); // neue geteilte Seite aus dem Kind

        child.Exit(17);
        child.ReportEnd(child.WaitStatusOf(9));
        child.Destroy();
        Assert.AreEqual(before + 1, _store.Live.Count, "nur die im Kind angelegte geteilte Seite bleibt (sie gehört der Datei)");
        Assert.AreEqual(6, Get(parent, shared + Page));
        Assert.AreEqual(5, Get(parent, shared));

        // wait4 sieht den Exitcode.
        ulong status = MapAnon(parent, Page, MapPrivate);
        Assert.AreEqual(child.Pid, Call(parent, Wait4, unchecked((ulong)-1), status, 0, 0));
        var raw = new byte[4];
        Assert.IsTrue(parent.TryRead(status, raw));
        Assert.AreEqual(17 << 8, BitConverter.ToInt32(raw));
        Assert.IsFalse(parent.IdInUse(child.Pid), "geerntet: PID frei");

        parent.Destroy();
        Assert.AreEqual(0, _store.Live.Count, "nach dem Ende beider Prozesse ist jeder Frame zurück");
    }

    [TestMethod]
    public void KindErbtDeskriptorenMitZaehlung()
    {
        LinuxUserProcess parent = NewParent();
        ulong mem = MapAnon(parent, Page, MapPrivate);
        Assert.IsTrue(parent.TryWrite(mem, Encoding.ASCII.GetBytes("buf\0")));
        long fd = Call(parent, MemfdCreate, mem, 0);
        Assert.IsTrue(fd >= 3);
        Assert.IsTrue(Call(parent, LinuxSyscalls.Fork) > 0);
        LinuxUserProcess child = _children[0];
        Assert.AreSame(parent.GetDescriptor((int)fd), child.GetDescriptor((int)fd), "dieselbe offene Datei");
        Assert.AreEqual(4, Call(child, Write, (ulong)fd, mem, 4), "gemeinsamer Dateizeiger");
        Assert.AreEqual(0, Call(child, Close, (ulong)fd));
        Assert.AreEqual(4, Call(parent, Write, (ulong)fd, mem, 4), "close im Kind lässt die Datei der Eltern offen");
        Assert.AreEqual(8, ((LinuxMemFile)parent.GetDescriptor((int)fd)!).Seek(0, 1));
        child.Destroy();
        Assert.IsTrue(parent.IsOpenAnywhere(parent.GetDescriptor((int)fd)!));
    }

    [TestMethod]
    public void TidZeigerUndTlsWieLinux()
    {
        LinuxUserProcess parent = NewParent();
        ulong mem = MapAnon(parent, Page, MapPrivate);
        parent.FsBase = 0x7000;
        const ulong flags = LinuxUserProcess.CloneChildSettid | LinuxUserProcess.CloneChildCleartid | LinuxUserProcess.CloneParentSettid | SigChld;
        long pid = Call(parent, LinuxSyscalls.Clone, flags, mem + 0x800, mem, mem + 8, 0);
        Assert.IsTrue(pid > 0);
        LinuxUserProcess child = _children[0];
        Assert.AreEqual(mem + 0x800, _stacks[0]);
        var b = new byte[4];
        Assert.IsTrue(child.TryRead(mem + 8, b));
        Assert.AreEqual((int)pid, BitConverter.ToInt32(b), "CLONE_CHILD_SETTID im Speicher des Kinds");
        Assert.IsTrue(parent.TryRead(mem + 8, b));
        Assert.AreEqual(0, BitConverter.ToInt32(b), "nicht bei den Eltern");
        Assert.IsTrue(parent.TryRead(mem, b));
        Assert.AreEqual((int)pid, BitConverter.ToInt32(b), "CLONE_PARENT_SETTID bei den Eltern");
        Assert.IsTrue(child.TryRead(mem, b));
        Assert.AreEqual(0, BitConverter.ToInt32(b), "nach der Kopie geschrieben: nicht im Kind");
        Assert.AreEqual(mem + 8, child.MainThread.ClearChildTid);
        Assert.AreEqual(0x7000UL, child.MainThread.FsBase, "ohne CLONE_SETTLS die FS-Basis des Aufrufers");

        Assert.IsTrue(Call(parent, LinuxSyscalls.Clone, LinuxUserProcess.CloneSettls | SigChld, 0, 0, 0, 0x9000) > 0);
        Assert.AreEqual(0x9000UL, _children[1].MainThread.FsBase);
    }

    [TestMethod]
    public void NichtUnterstuetzteFlagsBleibenEnosys()
    {
        LinuxUserProcess parent = NewParent();
        Assert.AreEqual(-ENOSYS, Call(parent, LinuxSyscalls.Clone, LinuxUserProcess.CloneFiles | SigChld));
        Assert.AreEqual(-ENOSYS, Call(parent, LinuxSyscalls.Clone, LinuxUserProcess.CloneFs | SigChld));
        Assert.AreEqual(-ENOSYS, Call(parent, LinuxSyscalls.Clone, LinuxUserProcess.CloneParent | SigChld));
        Assert.AreEqual(-ENOSYS, Call(parent, LinuxSyscalls.Clone, 0), "Austrittssignal 0 (nur mit __WCLONE sichtbar)");
        Assert.AreEqual(-ENOSYS, Call(parent, LinuxSyscalls.Clone, LinuxUserProcess.CloneVfork | SigChld), "CLONE_VFORK ohne CLONE_VM");
        Assert.AreEqual(-EINVAL, Call(parent, LinuxSyscalls.Clone, LinuxUserProcess.CloneSighand | SigChld), "CLONE_SIGHAND ohne CLONE_VM");
        Assert.AreEqual(0, _children.Count);
    }

    [TestMethod]
    public void Clone3WieFork()
    {
        LinuxUserProcess parent = NewParent();
        ulong args = MapAnon(parent, Page, MapPrivate);
        var ca = new byte[64];
        BitConverter.TryWriteBytes(ca.AsSpan(32), SigChld);
        Assert.IsTrue(parent.TryWrite(args, ca));
        long pid = Call(parent, LinuxSyscalls.Clone3, args, 64);
        Assert.IsTrue(pid > 0, $"clone3 {pid}");
        Assert.AreEqual(_children[0].Pid, pid);

        Assert.AreEqual(-EINVAL, Call(parent, LinuxSyscalls.Clone3, args, 8), "zu kleine Struktur");
        BitConverter.TryWriteBytes(ca.AsSpan(32), 0x7fUL);
        Assert.IsTrue(parent.TryWrite(args, ca));
        Assert.AreEqual(-EINVAL, Call(parent, LinuxSyscalls.Clone3, args, 64), "ungültiges exit_signal");
        BitConverter.TryWriteBytes(ca.AsSpan(32), SigChld);
        BitConverter.TryWriteBytes(ca.AsSpan(0), SigChld);
        Assert.IsTrue(parent.TryWrite(args, ca));
        Assert.AreEqual(-EINVAL, Call(parent, LinuxSyscalls.Clone3, args, 64), "Signal in den Flags");

        // Stack und Größe: Linux übergibt stack + stack_size; nur eins von beiden ist -EINVAL.
        ca = new byte[72];
        BitConverter.TryWriteBytes(ca.AsSpan(32), SigChld);
        BitConverter.TryWriteBytes(ca.AsSpan(40), 0x10000UL);
        Assert.IsTrue(parent.TryWrite(args, ca));
        Assert.AreEqual(-EINVAL, Call(parent, LinuxSyscalls.Clone3, args, 64), "Stack ohne Größe");
        BitConverter.TryWriteBytes(ca.AsSpan(48), 0x2000UL);
        ca[64] = 1;
        Assert.IsTrue(parent.TryWrite(args, ca));
        Assert.AreEqual(-E2BIG, Call(parent, LinuxSyscalls.Clone3, args, 72), "unbekanntes Feld gesetzt");
        ca[64] = 0;
        Assert.IsTrue(parent.TryWrite(args, ca));
        Assert.IsTrue(Call(parent, LinuxSyscalls.Clone3, args, 72) > 0);
        Assert.AreEqual(0x12000UL, _stacks[^1]);
    }

    [TestMethod]
    public void OhneSpeicherEnomemUndNichtsBleibt()
    {
        // Der Rechner hat nur noch drei Frames frei, die Eltern halten zehn: Die Kopie scheitert mittendrin.
        LinuxUserProcess parent = NewParent();
        ulong priv = MapAnon(parent, 10 * Page, MapPrivate);
        for (ulong i = 0; i < 10; i++)
        {
            Put(parent, priv + (i * Page), 1);
        }

        int before = _store.Live.Count;
        _store.Limit = before + 3;
        Assert.AreEqual(-ENOMEM, Call(parent, LinuxSyscalls.Fork));
        Assert.AreEqual(before, _store.Live.Count, "die halbe Kopie ist zurückgegeben");
        Assert.AreEqual(0, parent.Children.Length);
        Assert.AreEqual(0, _children.Count);
        Assert.IsFalse(parent.IdInUse(2), "die vergebene PID ist wieder frei");

        // Jeder Prozess hat seine eigene Obergrenze (wie RLIMIT_AS je Prozess): 10 von 16 Seiten lassen sich kopieren.
        _store.Limit = int.MaxValue;
        LinuxUserProcess small = NewParent(16 * Page);
        ulong p2 = MapAnon(small, 10 * Page, MapPrivate);
        for (ulong i = 0; i < 10; i++)
        {
            Put(small, p2 + (i * Page), 1);
        }

        Assert.IsTrue(Call(small, LinuxSyscalls.Fork) > 0);
        Assert.AreEqual(10, _children[0].FrameCount);
    }

    [TestMethod]
    public void StartScheitertEagainUndPidFrei()
    {
        LinuxUserProcess parent = NewParent();
        ulong priv = MapAnon(parent, Page, MapPrivate);
        Put(parent, priv, 1);
        int before = _store.Live.Count;
        _refuseStart = true;
        Assert.AreEqual(-EAGAIN, Call(parent, LinuxSyscalls.Fork));
        Assert.AreEqual(before, _store.Live.Count);
        Assert.AreEqual(0, parent.Children.Length);
        Assert.IsFalse(parent.IdInUse(2), "die vergebene PID ist wieder frei");
    }

    [TestMethod]
    public void KindKannSelbstForken()
    {
        LinuxUserProcess parent = NewParent();
        ulong priv = MapAnon(parent, Page, MapPrivate);
        Put(parent, priv, 1);
        Assert.IsTrue(Call(parent, LinuxSyscalls.Fork) > 0);
        LinuxUserProcess child = _children[0];
        Put(child, priv, 2);
        Assert.IsTrue(Call(child, LinuxSyscalls.Fork) > 0);
        LinuxUserProcess grandchild = _children[1];
        Assert.AreSame(child, grandchild.Parent);
        Assert.AreEqual(2, Get(grandchild, priv));
        Assert.AreEqual(1, Get(parent, priv));
    }

    [TestMethod]
    public void GrosserGeteilterAnonymerBereichWieNoreserve()
    {
        // V21-Review P3 (3): Die Länge hängt nicht am Speicher des Prozesses (64 MiB), erst die benutzten Seiten zählen; wie privat.
        LinuxUserProcess parent = NewParent();
        ulong big = MapAnon(parent, 1UL << 30, MapShared);
        ulong priv = MapAnon(parent, 1UL << 30, MapPrivate);
        Put(parent, big, 1);
        Put(parent, big + (1UL << 30) - 1, 2);
        Put(parent, priv, 3);
        Assert.AreEqual(1, Get(parent, big));
        Assert.AreEqual(2, Get(parent, big + (1UL << 30) - 1));
        Assert.AreEqual(1, parent.SharedRegionCount);

        // Die Speichergrenze greift beim Zugriff: Ein Prozess mit 4 Seiten bildet 1 GiB ab, kann aber nicht mehr Seiten belegen.
        LinuxUserProcess small = NewParent(4 * Page);
        ulong s = MapAnon(small, 1UL << 30, MapShared);
        int touched = 0;
        for (ulong i = 0; i < 8; i++)
        {
            if (small.TryWrite(s + (i * Page), new byte[] { 1 }))
            {
                touched++;
            }
        }

        Assert.IsTrue(touched is > 0 and <= 4, $"belegt {touched}");
    }

    [TestMethod]
    public void MremapAufGeteiltemAnonymemBereich()
    {
        // V21-Review P3 (2): mremap vergrößert nicht (-ENOMEM, auch an Ort und Stelle), also entsteht kein Stück ohne geteilten Bereich;
        // Verkleinern kürzt den geteilten Bereich wie munmap.
        const long Mremap = 25, ENOMEMr = -12;
        LinuxUserProcess parent = NewParent();
        ulong shared = MapAnon(parent, 2 * Page, MapShared);
        Put(parent, shared, 7);
        Assert.AreEqual(ENOMEMr, Call(parent, Mremap, shared, 2 * Page, 4 * Page, 0));
        Assert.AreEqual(ENOMEMr, Call(parent, Mremap, shared, 2 * Page, 4 * Page, 1));
        Assert.AreEqual(1, parent.SharedRegionCount);

        Assert.IsTrue(Call(parent, LinuxSyscalls.Fork) > 0);
        LinuxUserProcess child = _children[0];
        Assert.AreEqual((long)shared, Call(parent, Mremap, shared, 2 * Page, Page, 0));
        Assert.AreEqual(1, parent.SharedRegionCount);
        Assert.IsFalse(parent.TryRead(shared + Page, new byte[1]), "Ende freigegeben");
        Put(child, shared, 8);
        Assert.AreEqual(8, Get(parent, shared), "der verbleibende Teil bleibt mit dem Kind geteilt");
    }

    [TestMethod]
    public void MunmapGeteiltImKindLaesstElternBereich()
    {
        LinuxUserProcess parent = NewParent();
        ulong shared = MapAnon(parent, 2 * Page, MapShared);
        Put(parent, shared, 9);
        Assert.IsTrue(Call(parent, LinuxSyscalls.Fork) > 0);
        LinuxUserProcess child = _children[0];
        Assert.AreEqual(0, Call(child, Munmap, shared, 2 * Page));
        Assert.AreEqual(1, parent.SharedRegionCount);
        Assert.AreEqual(9, Get(parent, shared));
        Put(parent, shared + Page, 4);
        Assert.AreEqual(4, Get(parent, shared + Page));
    }

    [TestMethod]
    public void GeteilterFutexWirdUeberProzesseWach()
    {
        LinuxUserProcess parent = NewParent();
        ulong shared = MapAnon(parent, Page, MapShared);
        Assert.IsTrue(Call(parent, LinuxSyscalls.Fork) > 0);
        LinuxUserProcess child = _children[0];
        const long Futex = 202;
        long result = 1;
        var waiter = new System.Threading.Thread(() => result = Call(child, Futex, shared, 0, 0)); // FUTEX_WAIT ohne PRIVATE, Wert 0
        waiter.Start();
        System.Threading.Thread.Sleep(100);
        Assert.IsTrue(waiter.IsAlive, "das Kind schläft");
        Put(parent, shared, 1);
        Assert.AreEqual(0, Call(parent, Futex, shared, 1, 1), "das Wecken der Eltern erreicht die Tabelle des Kinds nicht");
        Assert.IsTrue(waiter.Join(2000), "nach der Wertänderung wacht das Kind spätestens nach einer Scheibe auf");
        Assert.AreEqual(0, result);

        // Gegenprobe mit FUTEX_PRIVATE_FLAG: kein Neulesen, erst das Wecken über die eigene Tabelle beendet das Warten.
        Put(parent, shared, 0);
        result = 1;
        var t2 = new System.Threading.Thread(() => result = Call(child, Futex, shared, 128, 0));
        t2.Start();
        System.Threading.Thread.Sleep(60);
        Put(parent, shared, 1);
        Assert.IsFalse(t2.Join(300), "privat: die Wertänderung allein weckt nicht");
        Assert.AreEqual(1, Call(child, Futex, shared, 129, 1));
        Assert.IsTrue(t2.Join(2000));
        Assert.AreEqual(0, result);
    }
}
