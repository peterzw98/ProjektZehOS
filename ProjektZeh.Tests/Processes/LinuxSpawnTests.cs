using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V15: vfork-Kind auf geteiltem Speicher, execve in einen neuen Adressraum, PIDs und TIDs über Prozesse.</summary>
[TestClass]
public sealed class LinuxSpawnTests
{
    private const ulong Page = 4096;
    private const int Pid = 1;
    private const ulong Spawn = LinuxUserProcess.CloneVm | LinuxUserProcess.CloneVfork | 17; // musl posix_spawn
    private const ulong Code = 0x401000;

    private sealed class Pages : IUserPages
    {
        private readonly Dictionary<ulong, byte[]> _frames = new();
        private ulong _next = 0x100000;
        public bool Destroyed;
        public ulong AllocateFrame() { _frames[_next] = new byte[Page]; _next += Page; return _next - Page; }
        public void FreeFrame(ulong frame) => _frames.Remove(frame);
        public Span<byte> Frame(ulong frame) => _frames[frame];
        public bool Map(ulong page, ulong frame, MapAccess access) => true;
        public bool Unmap(ulong page) => true;
        public void DestroyTable() => Destroyed = true;
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

    /// <summary>Elternprozess mit Speicher und Dateisystem; die Hooks spielen die Brücke des Kernels nach (ein Thread, kein Ring 3).</summary>
    private sealed class Fixture
    {
        public readonly LinuxUserProcess Parent;
        public readonly ulong Mem;
        public readonly MemoryFileStore Store = new();
        public LinuxUserProcess? Child;
        public ulong ChildStack;
        public LinuxUserProcess? Exec;
        public UserImage? Image;
        public readonly List<Pages> Created = new();

        public Fixture(Func<ILinuxFileStore, ILinuxFileStore>? wrap = null)
        {
            var term = new TerminalDescriptor(new Lines());
            Parent = new LinuxUserProcess(new Pages(), term, Pid, 64 << 20, new Host(), new LinuxFileSystem(wrap?.Invoke(Store) ?? Store, term));
            Parent.StartThread = _ => true;
            Parent.StartVfork = (c, stack) =>
            {
                Child = c;
                ChildStack = stack;
                c.CreatePages = () => { var p = new Pages(); Created.Add(p); return p; };
                c.StartExec = (next, image) => { Exec = next; Image = image; return true; };
                c.OnExit = _ => c.ReportEnd(c.WaitStatusOf(9));
                return true;
            };
            Mem = Parent.MapAnonymous(16 * Page, 3);
            Assert.AreNotEqual(0UL, Mem);
        }

        public static long Call(LinuxUserProcess p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0)
        {
            var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4 };
            return LinuxSyscalls.Dispatch(ref a, p);
        }

        /// <summary>Zeichenkette und Zeigerfeld (nullterminiert) im geteilten Speicher; liefert die Adresse des Felds.</summary>
        public ulong Strings(ulong at, params string[] values)
        {
            ulong text = at + 0x100;
            for (int i = 0; i < values.Length; i++)
            {
                byte[] b = Encoding.UTF8.GetBytes(values[i] + "\0");
                Assert.IsTrue(Parent.TryWrite(text, b));
                Assert.IsTrue(Parent.TryWrite(at + ((ulong)i * 8), BitConverter.GetBytes(text)));
                text += (ulong)b.Length;
            }

            Assert.IsTrue(Parent.TryWrite(at + ((ulong)values.Length * 8), new byte[8]));
            return at;
        }

        public void WriteFile(string path, byte[] content, uint mode = 0x1ED)
        {
            var fs = new LinuxFileSystem(Store, new TerminalDescriptor(new Lines()));
            Assert.AreEqual(0, fs.Open(path, 0x41, mode, out ILinuxFile? f));
            Assert.AreEqual(content.Length, f!.Write(content));
            Assert.AreEqual(0, f.Close());
        }
    }

    [TestMethod]
    public void VforkKindIstEigenerProzessAufGeteiltemSpeicher()
    {
        var f = new Fixture();
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, f.Mem + (8 * Page)), "der Thread läuft als Kind weiter");
        LinuxUserProcess child = f.Child!;
        Assert.AreEqual(f.Mem + (8 * Page), f.ChildStack);
        Assert.AreNotEqual(Pid, child.Pid);
        Assert.AreEqual(child.Pid, Fixture.Call(child, LinuxSyscalls.Getpid));
        Assert.AreSame(f.Parent, child.Parent);
        Assert.IsTrue(child.InVfork);
        Assert.AreEqual(1, f.Parent.Children.Length);
        Assert.AreEqual(child.Pid, f.Parent.Children[0].Pid);

        // Gleicher Speicher: was das Kind schreibt, liest der Elternprozess.
        Assert.IsTrue(child.TryWrite(f.Mem + 16, [1, 2, 3]));
        byte[] back = new byte[3];
        Assert.IsTrue(f.Parent.TryRead(f.Mem + 16, back));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, back);
        Assert.AreEqual(f.Parent.Brk, child.Brk);

        // Eigene Signalhandler: sigaction im Kind ändert den Elternprozess nicht.
        child.Signals.Handler[10] = 1;
        Assert.AreEqual(0UL, f.Parent.Signals.Handler[10]);
    }

    [TestMethod]
    public void VforkKindExitMeldetStatusAnElternprozess()
    {
        var f = new Fixture();
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        Fixture.Call(f.Child!, LinuxSyscalls.ExitGroup, 127);
        Assert.AreEqual(127 << 8, f.Parent.Children[0].WaitStatus);
        f.Child!.Release();
        Assert.IsTrue(f.Parent.TryRead(f.Mem, new byte[8]), "Speicher des Elternprozesses bleibt");
    }

    [TestMethod]
    public void CloneOhneVforkOderMitFremdenBitsBleibtAbgelehnt()
    {
        var f = new Fixture();
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, Fixture.Call(f.Parent, LinuxSyscalls.Clone, 17, 0), "fork");
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn | LinuxUserProcess.CloneSettls, 0));
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn | LinuxUserProcess.CloneFiles, 0));
        Assert.IsNull(f.Child);
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Vfork));
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, Fixture.Call(f.Child!, LinuxSyscalls.Vfork), "vfork im vfork-Kind");
    }

    [TestMethod]
    public void TidsUndPidsUeberProzesseEindeutig()
    {
        var f = new Fixture();
        long t1 = Fixture.Call(f.Parent, LinuxSyscalls.Clone, LinuxUserProcess.CloneThreadSet, f.Mem + Page);
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        f.Parent.CurrentThreadSource = null;
        long t2 = Fixture.Call(f.Parent, LinuxSyscalls.Clone, LinuxUserProcess.CloneThreadSet, f.Mem + Page);
        var ids = new HashSet<long> { Pid, t1, f.Child!.Pid, t2 };
        Assert.AreEqual(4, ids.Count, "PID " + f.Child.Pid + " TIDs " + t1 + "," + t2);
        Assert.AreEqual(Pid + 1, t1, "erste TID wie bisher PID + 1");
    }

    /// <summary>Zählt die Lesevorgänge (ReadAt) auf Dateien des Speichers, sonst reicht er alles durch.</summary>
    private sealed class CountingStore(ILinuxFileStore inner) : ILinuxFileStore
    {
        public int Reads;

        public StringComparison NameComparison => inner.NameComparison;
        public void BeginResolve() => inner.BeginResolve();
        public void EndResolve() => inner.EndResolve();
        public long Lookup(string path, out LinuxStoreNode node) => inner.Lookup(path, out node);
        public long ReadLink(string path, out string target) => inner.ReadLink(path, out target);
        public long List(string path, out string[] names) => inner.List(path, out names);
        public long OpenFile(string path, bool write, out ILinuxStoreFile? file)
        {
            long r = inner.OpenFile(path, write, out ILinuxStoreFile? f);
            file = f == null ? null : new CountingFile(this, f);
            return r;
        }

        public long CreateFile(string path, uint mode, out ILinuxStoreFile? file) => inner.CreateFile(path, mode, out file);
        public long CreateDirectory(string path, uint mode) => inner.CreateDirectory(path, mode);
        public long CreateSymlink(string path, string target) => inner.CreateSymlink(path, target);
        public long DeleteFile(string path) => inner.DeleteFile(path);
        public long DeleteDirectory(string path) => inner.DeleteDirectory(path);
        public long Rename(string from, string to) => inner.Rename(from, to);
    }

    private sealed class CountingFile(CountingStore store, ILinuxStoreFile inner) : ILinuxStoreFile
    {
        public long Length() => inner.Length();
        public long ReadAt(long offset, Span<byte> dst) { store.Reads++; return inner.ReadAt(offset, dst); }
        public long WriteAt(long offset, ReadOnlySpan<byte> src) => inner.WriteAt(offset, src);
        public long SetLength(long length) => inner.SetLength(length);
        public long Close() => inner.Close();
    }

    [TestMethod]
    public void ExecveLiestKopfDerDateiMitEinemLesevorgang()
    {
        // N2-Z: Jeder Lesevorgang der Platte kostet im Kern Puffer; Kopf und Programmtabelle kommen aus einem Lesevorgang am Anfang.
        CountingStore? counting = null;
        var f = new Fixture(store => counting = new CountingStore(store));
        f.WriteFile("/hello", Elf());
        ulong path = f.Strings(f.Mem + (2 * Page), "/hello") + 0x100;
        ulong argv = f.Strings(f.Mem + (4 * Page), "/hello");
        ulong envp = f.Strings(f.Mem + (6 * Page), "A=1");
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        counting!.Reads = 0;
        Assert.AreEqual(0, Fixture.Call(f.Child!, LinuxSyscalls.Execve, path, argv, envp));
        Assert.AreEqual(1, counting.Reads, "Kopf, Programmtabelle und Rückgabepuffer aus einem Lesevorgang");
        Assert.AreEqual(Code, f.Image!.Entry);
    }

    [TestMethod]
    public void ExecveLaedtNeuesAbbildMitGleicherPid()
    {
        var f = new Fixture();
        f.WriteFile("/hello", Elf());
        ulong path = f.Strings(f.Mem + (2 * Page), "/hello") + 0x100; // Zeichenkette selbst
        ulong argv = f.Strings(f.Mem + (4 * Page), "/hello", "x");
        ulong envp = f.Strings(f.Mem + (6 * Page), "A=1");
        f.Parent.Signals.Handler[2] = 0x1234; // Handler fällt bei execve auf SIG_DFL
        f.Parent.Signals.Handler[13] = 1;     // SIG_IGN bleibt
        f.Parent.MainThread.Blocked = 0x10;
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess child = f.Child!;
        Assert.AreEqual(0, Fixture.Call(child, LinuxSyscalls.Execve, path, argv, envp));
        LinuxUserProcess next = f.Exec!;
        Assert.AreEqual(child.Pid, next.Pid);
        Assert.IsFalse(next.InVfork);
        Assert.AreSame(f.Parent, next.Parent);
        Assert.AreEqual("/hello", next.ExePath);
        Assert.AreSame(next, f.Parent.Children[0].Process);
        Assert.AreEqual(0UL, next.Signals.Handler[2]);
        Assert.AreEqual(1UL, next.Signals.Handler[13]);
        Assert.AreEqual(0x10UL, next.MainThread.Blocked);
        Assert.AreEqual(Code, f.Image!.Entry);
        Assert.IsNotNull(next.GetDescriptor(1), "Terminal geerbt");
        Assert.IsNull(child.GetDescriptor(1), "Tabelle ist ins neue Abbild gewandert");
        Assert.AreSame(f.Created[0], next.Pages);

        // Eigener Adressraum: der Speicher des Elternprozesses ist dort nicht abgebildet.
        Assert.IsFalse(next.TryRead(f.Mem, new byte[1]));
        next.Destroy();
        Assert.IsTrue(f.Created[0].Destroyed);
        Assert.IsTrue(f.Parent.TryRead(f.Mem, new byte[1]));
    }

    [TestMethod]
    public void ExecveOhneAusfuehrrechtIstEacces()
    {
        var f = new Fixture();
        f.WriteFile("/leer", [], 0x1A4);
        f.WriteFile("/leer-x", []);
        ulong plain = f.Strings(f.Mem + (2 * Page), "/leer") + 0x100;
        ulong exec = f.Strings(f.Mem + (3 * Page), "/leer-x") + 0x100;
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess child = f.Child!;
        Assert.AreEqual(-LinuxSyscalls.EACCES, Fixture.Call(child, LinuxSyscalls.Execve, plain, 0, 0));
        Assert.AreEqual(-8, Fixture.Call(child, LinuxSyscalls.Execve, exec, 0, 0), "mit Recht, aber kein ELF: ENOEXEC");
    }

    [TestMethod]
    public void ExecveAufArgv0FindetDasProgrammInDerWurzel()
    {
        var f = new Fixture();
        f.WriteFile("/hello", Elf());
        f.Parent.ExePath = "/hello";
        f.Parent.StartPath = "/data/s1/hello"; // Kernelpfad von argv[0], außerhalb der Wurzel
        ulong outside = f.Strings(f.Mem + (2 * Page), "/data/s1/hello") + 0x100;
        ulong other = f.Strings(f.Mem + (3 * Page), "/data/s1/anderes") + 0x100;
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess child = f.Child!;
        Assert.AreEqual(-LinuxErrno.ENOENT, Fixture.Call(child, LinuxSyscalls.Execve, other, 0, 0));
        Assert.AreEqual(0, Fixture.Call(child, LinuxSyscalls.Execve, outside, 0, 0));
        Assert.AreEqual("/hello", f.Exec!.ExePath);
        Assert.AreEqual("/data/s1/hello", f.Exec.StartPath);
    }

    [TestMethod]
    public void ExecveAufAnderesProgrammVerliertDenStartPfad()
    {
        var f = new Fixture();
        f.WriteFile("/hello", Elf());
        f.WriteFile("/anderes", Elf());
        f.Parent.ExePath = "/hello";
        f.Parent.StartPath = "/data/s1/hello";
        ulong other = f.Strings(f.Mem + (3 * Page), "/anderes") + 0x100;
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess child = f.Child!;
        Assert.AreEqual(0, Fixture.Call(child, LinuxSyscalls.Execve, other, 0, 0));
        Assert.AreEqual("/anderes", f.Exec!.ExePath);
        Assert.IsNull(f.Exec.StartPath);
    }

    [TestMethod]
    public void ExecveFehlerLassenDasKindWeiterlaufen()
    {
        var f = new Fixture();
        f.WriteFile("/kaputt", [0x7F, (byte)'E', (byte)'L', (byte)'F', 1]);
        ulong missing = f.Strings(f.Mem + (2 * Page), "/fehlt") + 0x100;
        ulong broken = f.Strings(f.Mem + (3 * Page), "/kaputt") + 0x100;
        ulong dir = f.Strings(f.Mem + (5 * Page), "/") + 0x100;
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, Fixture.Call(f.Parent, LinuxSyscalls.Execve, missing, 0, 0), "execve ohne vfork");
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess child = f.Child!;
        Assert.AreEqual(-LinuxErrno.ENOENT, Fixture.Call(child, LinuxSyscalls.Execve, missing, 0, 0));
        Assert.AreEqual(-8, Fixture.Call(child, LinuxSyscalls.Execve, broken, 0, 0), "ENOEXEC");
        Assert.AreEqual(-LinuxSyscalls.EACCES, Fixture.Call(child, LinuxSyscalls.Execve, dir, 0, 0));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Fixture.Call(child, LinuxSyscalls.Execve, missing, 8, 0), "argv nicht lesbar");
        Assert.IsNull(f.Exec);
        Assert.IsTrue(child.InVfork);
        Assert.IsNotNull(child.GetDescriptor(1));
    }

    [TestMethod]
    public void PidDesKindsBleibtBisZumErntenBelegt()
    {
        var f = new Fixture();
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess child = f.Child!;
        Fixture.Call(child, LinuxSyscalls.ExitGroup, 3);
        child.Release();
        Assert.IsTrue(f.Parent.IdInUse(child.Pid), "Zombie: PID bleibt bis wait4");
        Assert.IsFalse(f.Parent.Reap(child.Pid + 1), "unbekannte PID");
        Assert.IsTrue(f.Parent.Reap(child.Pid));
        Assert.IsFalse(f.Parent.IdInUse(child.Pid));
        Assert.AreEqual(0, f.Parent.Children.Length);
        Assert.IsFalse(f.Parent.Reap(child.Pid), "nur einmal");
    }

    [TestMethod]
    public void LaufendesKindLaesstSichNichtErnten()
    {
        var f = new Fixture();
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        Assert.IsFalse(f.Parent.Reap(f.Child!.Pid));
        Assert.IsTrue(f.Parent.IdInUse(f.Child.Pid));
    }

    [TestMethod]
    public void NachExecveGehoertDiePidDemNeuenAbbild()
    {
        var f = new Fixture();
        f.WriteFile("/hello", Elf());
        ulong path = f.Strings(f.Mem + (2 * Page), "/hello") + 0x100;
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess child = f.Child!;
        Assert.AreEqual(0, Fixture.Call(child, LinuxSyscalls.Execve, path, 0, 0));
        child.Release();
        Assert.IsTrue(f.Parent.IdInUse(child.Pid), "Release des vfork-Kinds gibt die PID des Abbilds nicht frei");
        LinuxUserProcess next = f.Exec!;
        next.ReportEnd(0);
        next.Destroy();
        Assert.IsTrue(f.Parent.IdInUse(child.Pid), "Zombie bis wait4");
        Assert.IsTrue(f.Parent.Reap(child.Pid));
        Assert.IsFalse(f.Parent.IdInUse(child.Pid));
    }

    [TestMethod]
    public void EagainBeimVforkGibtPidSofortFrei()
    {
        var f = new Fixture();
        int pid = 0;
        f.Parent.StartVfork = (c, _) => { pid = c.Pid; return false; };
        Assert.AreEqual(-LinuxSyscalls.EAGAIN, Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0));
        Assert.AreNotEqual(0, pid);
        Assert.IsFalse(f.Parent.IdInUse(pid));
        Assert.AreEqual(0, f.Parent.Children.Length);
    }

    [TestMethod]
    public void ProzessendeGibtTidsUndPidsVerwaisterKinderFrei()
    {
        var f = new Fixture();
        long tid = Fixture.Call(f.Parent, LinuxSyscalls.Clone, LinuxUserProcess.CloneThreadSet, f.Mem + Page);
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess zombie = f.Child!;
        Fixture.Call(zombie, LinuxSyscalls.ExitGroup, 0);
        zombie.Release();

        f.WriteFile("/hello", Elf());
        ulong path = f.Strings(f.Mem + (2 * Page), "/hello") + 0x100;
        f.Parent.CurrentThreadSource = null;
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess running = f.Child!;
        Assert.AreEqual(0, Fixture.Call(running, LinuxSyscalls.Execve, path, 0, 0));
        running.Release();

        f.Parent.Destroy();
        Assert.IsFalse(f.Parent.IdInUse((int)tid), "TID des Nebenthreads");
        Assert.IsFalse(f.Parent.IdInUse(Pid), "erster Prozess ohne Eltern");
        Assert.IsFalse(f.Parent.IdInUse(zombie.Pid), "beendetes Kind ohne Ernter");
        Assert.IsTrue(f.Parent.IdInUse(running.Pid), "laufendes Kind behält seine PID");

        // Endet das verwaiste Kind, erntet niemand mehr: die PID wird mit seinem Ende frei.
        f.Exec!.ReportEnd(0);
        f.Exec.Destroy();
        Assert.IsFalse(f.Parent.IdInUse(running.Pid));
        Assert.AreEqual(0, f.Parent.Children.Length);
    }

    // ---- V19: execve ohne vfork, PR_SET_PDEATHSIG, close_range, /proc/self/fd, ppoll ----

    private const long Prctl = 157, Openat = 257, Getdents64 = 217, Fstat = 5, Fcntl = 72;
    private static readonly string[] ProcFdNames = [".", "..", "0", "1", "2", "3"];

    /// <summary>Hooks für execve im Prozess selbst; liefert die Liste der Abbilder, die die Brücke übernimmt.</summary>
    private static List<LinuxUserProcess> WireReplace(Fixture f, LinuxUserProcess p)
    {
        var replaced = new List<LinuxUserProcess>();
        p.CreatePages = () => { var pg = new Pages(); f.Created.Add(pg); return pg; };
        p.ReplaceImage = (next, image) => { replaced.Add(next); f.Image = image; return true; };
        return replaced;
    }

    private static long OpenFile(Fixture f, LinuxUserProcess p, string path, int flags)
    {
        ulong at = f.Strings(f.Mem + (10 * Page), path) + 0x100;
        return Fixture.Call(p, Openat, unchecked((ulong)-100), at, (ulong)flags, 0x1A4);
    }

    [TestMethod]
    public void ExecveOhneVforkErsetztDenProzessMitGleicherPid()
    {
        var f = new Fixture();
        f.WriteFile("/hello", Elf());
        List<LinuxUserProcess> replaced = WireReplace(f, f.Parent);
        Assert.AreEqual(3, OpenFile(f, f.Parent, "/hello", 0));
        Assert.AreEqual(4, OpenFile(f, f.Parent, "/hello", 0x80000)); // O_CLOEXEC
        f.Parent.Signals.Handler[12] = 0x1234;
        f.Parent.Signals.Handler[10] = 1;
        f.Parent.MainThread.Blocked = 1UL << 14;
        Assert.AreEqual(0, Fixture.Call(f.Parent, Prctl, 1, 10));

        // Ein laufendes Kind (vfork + execve) und ein beendetes ziehen mit um.
        ulong path = f.Strings(f.Mem + (2 * Page), "/hello") + 0x100;
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess spawned = f.Child!;
        Assert.AreEqual(0, Fixture.Call(spawned, LinuxSyscalls.Execve, path, 0, 0));
        spawned.Release();
        LinuxUserProcess running = f.Exec!;
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess zombie = f.Child!;
        Fixture.Call(zombie, LinuxSyscalls.ExitGroup, 5);
        zombie.Release();

        ulong argv = f.Strings(f.Mem + (4 * Page), "/hello", "--kind");
        Pages oldPages = (Pages)f.Parent.Pages;
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Execve, path, argv, 0));
        Assert.AreEqual(1, replaced.Count);
        LinuxUserProcess next = replaced[0];
        Assert.AreEqual(Pid, next.Pid);
        Assert.AreEqual("/hello", next.ExePath);
        Assert.IsTrue(oldPages.Destroyed, "der alte Adressraum ist frei");
        Assert.IsNotNull(next.GetDescriptor(3), "ohne CLOEXEC bleibt");
        Assert.IsNull(next.GetDescriptor(4), "CLOEXEC endet");
        Assert.IsNull(f.Parent.GetDescriptor(3));
        Assert.AreEqual(0UL, next.Signals.Handler[12]);
        Assert.AreEqual(1UL, next.Signals.Handler[10]);
        Assert.AreEqual(1UL << 14, next.MainThread.Blocked);
        Assert.AreEqual(10, next.ParentDeathSignal, "PDEATHSIG bleibt über execve");
        Assert.AreEqual(2, next.Children.Length);
        Assert.AreEqual(0, f.Parent.Children.Length);
        Assert.AreSame(next, running.Parent);
        Assert.IsTrue(f.Parent.IdInUse(Pid), "PID gehört dem neuen Abbild");

        // Das Kind las Parent vor dem Umzug: Ende über das alte Objekt landet beim neuen Abbild.
        Assert.IsTrue(next.Reap(zombie.Pid));
        running.ReportEnd(7 << 8);
        Assert.AreEqual(7 << 8, Array.Find(next.Children, c => c.Pid == running.Pid)!.WaitStatus);
        Assert.IsTrue(next.Reap(running.Pid));
    }

    [TestMethod]
    public void ExecveOhneVforkFehlerUndMehrereThreads()
    {
        var f = new Fixture();
        List<LinuxUserProcess> replaced = WireReplace(f, f.Parent);
        ulong missing = f.Strings(f.Mem + (2 * Page), "/fehlt") + 0x100;
        ulong dir = f.Strings(f.Mem + (3 * Page), "/") + 0x100;
        Assert.AreEqual(-LinuxErrno.ENOENT, Fixture.Call(f.Parent, LinuxSyscalls.Execve, missing, 0, 0));
        Assert.AreEqual(-LinuxSyscalls.EACCES, Fixture.Call(f.Parent, LinuxSyscalls.Execve, dir, 0, 0));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Fixture.Call(f.Parent, LinuxSyscalls.Execve, 1, 0, 0));
        Assert.AreEqual(0, replaced.Count);
        Assert.IsNotNull(f.Parent.GetDescriptor(1), "der Prozess läuft unverändert weiter");

        f.WriteFile("/hello", Elf());
        ulong path = f.Strings(f.Mem + (5 * Page), "/hello") + 0x100;
        Assert.IsTrue(Fixture.Call(f.Parent, LinuxSyscalls.Clone, LinuxUserProcess.CloneThreadSet, f.Mem + Page) > 0);
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, Fixture.Call(f.Parent, LinuxSyscalls.Execve, path, 0, 0), "de_thread fehlt");
        Assert.AreEqual(0, replaced.Count);
    }

    [TestMethod]
    public void PdeathsigImVforkKindUeberlebtExecveUndWirdZugestellt()
    {
        var f = new Fixture();
        f.WriteFile("/hello", Elf());
        ulong path = f.Strings(f.Mem + (2 * Page), "/hello") + 0x100;
        Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0);
        LinuxUserProcess child = f.Child!;
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Fixture.Call(child, Prctl, 1, 65));
        Assert.AreEqual(0, Fixture.Call(child, Prctl, 1, 15));
        Assert.AreEqual(0, Fixture.Call(child, Prctl, 2, f.Mem + 64));
        byte[] got = new byte[4];
        Assert.IsTrue(child.TryRead(f.Mem + 64, got));
        Assert.AreEqual(15, BitConverter.ToInt32(got));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Fixture.Call(child, Prctl, 2, 8));
        Assert.AreEqual(0, f.Parent.ParentDeathSignal, "nur das Kind");
        Assert.AreEqual(0, Fixture.Call(child, LinuxSyscalls.Execve, path, 0, 0));
        child.Release();
        LinuxUserProcess next = f.Exec!;
        Assert.AreEqual(15, next.ParentDeathSignal);
        Assert.IsNull(next.TerminatedBySignal);

        f.Parent.ReportEnd(0);
        Assert.AreEqual(15, next.TerminatedBySignal, "SIGTERM mit Standardaktion beendet das Kind");
    }

    [TestMethod]
    public void CloseRangeSchliesstOderSetztCloexec()
    {
        var f = new Fixture();
        f.WriteFile("/a", [1]);
        for (int i = 3; i <= 6; i++)
        {
            Assert.AreEqual(i, OpenFile(f, f.Parent, "/a", 0));
        }

        Assert.AreEqual(-LinuxSyscalls.EINVAL, Fixture.Call(f.Parent, LinuxSyscalls.CloseRange, 5, 4, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Fixture.Call(f.Parent, LinuxSyscalls.CloseRange, 3, 4, 1));
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.CloseRange, 4, 5, 0));
        Assert.IsNotNull(f.Parent.GetDescriptor(3));
        Assert.IsNull(f.Parent.GetDescriptor(4));
        Assert.IsNull(f.Parent.GetDescriptor(5));
        Assert.IsNotNull(f.Parent.GetDescriptor(6));
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.CloseRange, 3, uint.MaxValue, 4)); // CLOSE_RANGE_CLOEXEC
        Assert.AreEqual(1, Fixture.Call(f.Parent, Fcntl, 3, 1));
        Assert.AreEqual(1, Fixture.Call(f.Parent, Fcntl, 6, 1));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Fcntl, 1, 1), "fd 1 liegt außerhalb");
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.CloseRange, 3, uint.MaxValue, 2)); // UNSHARE: schließt
        Assert.IsNull(f.Parent.GetDescriptor(3));
        Assert.IsNull(f.Parent.GetDescriptor(6));
        Assert.IsNotNull(f.Parent.GetDescriptor(2));
    }

    [TestMethod]
    public void ProcSelfFdListetOffeneNummern()
    {
        var f = new Fixture();
        Assert.AreEqual(-21, OpenFile(f, f.Parent, "/proc/self/fd", 2), "O_RDWR auf ein Verzeichnis");
        Assert.AreEqual(-17, OpenFile(f, f.Parent, "/proc/self/fd", 0xC0), "O_CREAT|O_EXCL auf Vorhandenes");
        Assert.AreEqual(-21, OpenFile(f, f.Parent, "/proc/self/fd", 0x40), "O_CREAT auf ein Verzeichnis");
        long fd = OpenFile(f, f.Parent, "/proc/" + Pid + "/fd", 0x10000); // O_DIRECTORY
        Assert.AreEqual(3, fd);
        Assert.AreEqual(0, Fixture.Call(f.Parent, Fstat, 3, f.Mem + 256));
        byte[] st = new byte[144];
        Assert.IsTrue(f.Parent.TryRead(f.Mem + 256, st));
        Assert.AreEqual(0x4000u, BitConverter.ToUInt32(st, 24) & 0xF000, "S_IFDIR");

        long n = Fixture.Call(f.Parent, Getdents64, 3, f.Mem + (12 * Page), 4096);
        Assert.IsTrue(n > 0);
        byte[] buf = new byte[n];
        Assert.IsTrue(f.Parent.TryRead(f.Mem + (12 * Page), buf));
        var names = new List<string>();
        for (int at = 0; at < n; at += BitConverter.ToUInt16(buf, at + 16))
        {
            int end = Array.IndexOf(buf, (byte)0, at + 19);
            names.Add(Encoding.UTF8.GetString(buf, at + 19, end - at - 19));
        }

        CollectionAssert.AreEqual(ProcFdNames, names);
        Assert.AreEqual(0, Fixture.Call(f.Parent, Getdents64, 3, f.Mem + (12 * Page), 4096), "Ende");
        Assert.AreEqual(-LinuxErrno.ENOENT, OpenFile(f, f.Parent, "/proc/99/fd", 0), "fremder Prozess");
    }

    [TestMethod]
    public void PpollTauschtDieMaskeNurWaehrendDesWartens()
    {
        var f = new Fixture();
        const ulong Usr1 = 1UL << 9, Term = 1UL << 14;
        ulong ts = f.Mem + 128, mask = f.Mem + 160;
        Assert.IsTrue(f.Parent.TryWrite(ts, new byte[16]));
        Assert.IsTrue(f.Parent.TryWrite(mask, BitConverter.GetBytes(Term)));
        f.Parent.MainThread.Blocked = Usr1;

        // Ergebnis ohne Signal: die alte Maske gilt sofort wieder.
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Ppoll, 0, 0, ts, mask, 8));
        Assert.AreEqual(Usr1, f.Parent.MainThread.Blocked);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Fixture.Call(f.Parent, LinuxSyscalls.Ppoll, 0, 0, ts, mask, 4));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Fixture.Call(f.Parent, LinuxSyscalls.Ppoll, 0, 0, 8, mask, 8));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Fixture.Call(f.Parent, LinuxSyscalls.Ppoll, 0, 0, ts, 8, 8));
        Assert.IsTrue(f.Parent.TryWrite(ts + 8, BitConverter.GetBytes(1_000_000_000L)));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, Fixture.Call(f.Parent, LinuxSyscalls.Ppoll, 0, 0, ts, mask, 8));
        Assert.IsTrue(f.Parent.TryWrite(ts + 8, new byte[8]));
        Assert.AreEqual(Usr1, f.Parent.MainThread.Blocked);

        // SIGUSR1 mit Handler wartet blockiert; ppoll gibt es frei: -EINTR, der Handler läuft unter der Wartemaske, der Rahmen trägt die alte.
        f.Parent.Signals.Handler[10] = 0x401000;
        f.Parent.Signals.Restorer[10] = 0x401000;
        f.Parent.Signals.Flags[10] = LinuxUserProcess.SaRestorer;
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Tkill, (ulong)Pid, 10));
        Assert.AreEqual(-LinuxSyscalls.EINTR, Fixture.Call(f.Parent, LinuxSyscalls.Ppoll, 0, 0, ts, mask, 8));
        Assert.AreEqual(Term, f.Parent.MainThread.Blocked, "Wartemaske bis zum Rücksprung");
        var r = new LinuxUserRegs { Rsp = f.Mem + (12 * Page), Rip = Code, Rflags = 0x202 };
        LinuxUserRegs entry = r;
        entry.Rax = (ulong)LinuxSyscalls.Ppoll;
        LinuxSignalFrame.AfterSyscall(f.Parent, ref r, new byte[512], entry.ToSyscallArgs(), -LinuxSyscalls.EINTR, f.Parent.SignalsTaken);
        Assert.AreEqual(0x401000UL, r.Rip, "Handler angesprungen");
        Assert.AreEqual(Term | Usr1, f.Parent.MainThread.Blocked, "Wartemaske und das Signal selbst");
        byte[] saved = new byte[8];
        Assert.IsTrue(f.Parent.TryRead(r.Rsp + 304, saved), "uc_sigmask im Rahmen");
        Assert.AreEqual(Usr1, BitConverter.ToUInt64(saved));

        // -EINTR ohne Handler-Rahmen (das Signal nahm niemand): die alte Maske gilt nach dem Rücksprung.
        f.Parent.MainThread.Blocked = Usr1;
        f.Parent.BeginWaitMask(f.Parent.CurrentThread, Term);
        r = new LinuxUserRegs { Rsp = f.Mem + (12 * Page), Rip = Code, Rflags = 0x202 };
        LinuxSignalFrame.AfterSyscall(f.Parent, ref r, new byte[512], entry.ToSyscallArgs(), -LinuxSyscalls.EINTR, f.Parent.SignalsTaken);
        Assert.AreEqual(Usr1, f.Parent.MainThread.Blocked);
        Assert.AreEqual(Code, r.Rip);
    }

    [TestMethod]
    public void PpollWartemaskeGiltNurFuerDenWartendenThread()
    {
        // V38: Die Maske gilt je Thread. Jeder Thread merkt sich seine eigene Maske vor dem Warten und stellt genau sie wieder her, egal in
        // welcher Reihenfolge die Threads enden; die Maske eines anderen Threads bleibt unberührt (bis V37 zählte der Prozess die Wartenden).
        var f = new Fixture();
        const ulong Usr1 = 1UL << 9, Term = 1UL << 14, Usr2 = 1UL << 11, Chld = 1UL << 16;
        var a = new LinuxThread(1001) { Blocked = Usr1 };
        var b = new LinuxThread(1002) { Blocked = Chld };
        f.Parent.MainThread.Blocked = 0;

        f.Parent.BeginWaitMask(a, Term);
        f.Parent.BeginWaitMask(b, Usr2);
        Assert.AreEqual(Usr1, a.SavedBlocked);
        Assert.AreEqual(Chld, b.SavedBlocked, "B merkt seine eigene Maske, nicht die von A");
        Assert.AreEqual(Term, a.Blocked);
        Assert.AreEqual(Usr2, b.Blocked);
        Assert.AreEqual(0UL, f.Parent.MainThread.Blocked, "andere Threads unberührt");
        f.Parent.EndWaitMask(a);
        Assert.AreEqual(Usr1, a.Blocked);
        Assert.AreEqual(Usr2, b.Blocked, "B wartet noch");
        f.Parent.EndWaitMask(b);
        Assert.AreEqual(Chld, b.Blocked);

        // Über einen Signalrahmen (restore: false) bleibt die Wartemaske bis rt_sigreturn; ein zweites Ende ist wirkungslos.
        f.Parent.BeginWaitMask(a, Term);
        Assert.IsTrue(f.Parent.LeaveWaitMask(a, restore: false));
        Assert.AreEqual(Term, a.Blocked);
        Assert.IsFalse(f.Parent.LeaveWaitMask(a, restore: true));
        Assert.AreEqual(Term, a.Blocked, "nichts herzustellen");
    }

    [TestMethod]
    public void PpollRestzeitLaeuftBeiRiesigerFristNichtUeber()
    {
        var f = new Fixture();
        const ulong Usr1 = 1UL << 9;
        const long Sec = 10_000_000_000L; // sec * 10^9 > long.MaxValue
        ulong ts = f.Mem + 128, mask = f.Mem + 160;
        Assert.IsTrue(f.Parent.TryWrite(ts, BitConverter.GetBytes(Sec)));
        Assert.IsTrue(f.Parent.TryWrite(ts + 8, BitConverter.GetBytes(5L)));
        Assert.IsTrue(f.Parent.TryWrite(mask, new byte[8]));
        f.Parent.MainThread.Blocked = Usr1;
        f.Parent.Signals.Handler[10] = 0x401000;
        f.Parent.Signals.Restorer[10] = 0x401000;
        f.Parent.Signals.Flags[10] = LinuxUserProcess.SaRestorer;
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Tkill, (ulong)Pid, 10));

        // Das anhängige SIGUSR1 wird frei: sofort -EINTR, die Restzeit steht wieder in tmo_p.
        Assert.AreEqual(-LinuxSyscalls.EINTR, Fixture.Call(f.Parent, LinuxSyscalls.Ppoll, 0, 0, ts, mask, 8));
        byte[] left = new byte[16];
        Assert.IsTrue(f.Parent.TryRead(ts, left));
        long leftSec = BitConverter.ToInt64(left), leftNsec = BitConverter.ToInt64(left, 8);
        Assert.IsTrue(leftSec >= Sec - 2 && leftSec <= Sec, "Restzeit " + leftSec);
        Assert.IsTrue(leftNsec >= 0 && leftNsec < 1_000_000_000);
        f.Parent.EndWaitMask(f.Parent.CurrentThread);
    }

    // Statisches Programm wie in LinuxUserProcessTests: Code bei 0x401000 (ret), Daten bei 0x403000.
    internal static byte[] Elf()
    {
        byte[] b = new byte[3 * (int)Page];
        b[0] = 0x7F; b[1] = (byte)'E'; b[2] = (byte)'L'; b[3] = (byte)'F'; b[4] = 2; b[5] = 1; b[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), ElfFile.TypeExecutable);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), ElfFile.MachineX86_64);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(24), Code);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(54), ElfFile.ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), 3);
        Header(b, 0, 4, 0, 0x400000, 0x200, 0x200);
        Header(b, 1, 5, Page, Code, Page, Page);
        Header(b, 2, 6, 2 * Page, 0x403000, 0x10, 0x10 + (2 * Page));
        for (int i = 0; i < (int)Page; i++) b[(int)Page + i] = 0xC3;
        return b;
    }

    private static void Header(byte[] b, int index, uint flags, ulong offset, ulong vaddr, ulong fileSize, ulong memSize)
    {
        Span<byte> p = b.AsSpan(64 + (index * 56), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(p, ElfProgramHeader.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(p.Slice(4), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(8), offset);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(16), vaddr);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(24), vaddr);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(32), fileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(40), memSize);
        BinaryPrimitives.WriteUInt64LittleEndian(p.Slice(48), Page);
    }
}
