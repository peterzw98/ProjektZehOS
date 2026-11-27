using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V29: minimales /proc (status, stat, maps, /proc/stat, meminfo, cpuinfo, exe anderer Prozesse des Laufs).</summary>
[TestClass]
public sealed class LinuxProcFilesTests
{
    private const ulong Page = 4096;
    private const int Pid = 1;
    private const ulong Spawn = LinuxUserProcess.CloneVm | LinuxUserProcess.CloneVfork | 17;
    private const long Read = 0, Lseek = 8, Access = 21, Readlink = 89, Openat = 257, Newfstatat = 262, Close = 3;

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
        public long Mono = 5_000_000_000; // 5 s seit dem Start
        public long RealtimeNanos => 1_800_000_000_000_000_000;
        public long MonotonicNanos => Mono;
        public bool TryFillRandom(Span<byte> dst) { dst.Fill(7); return true; }
        public void SleepNanos(long ns) { }
    }

    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    private sealed class Fixture
    {
        public readonly Host Clock = new();
        public readonly LinuxUserProcess Parent;
        public readonly ulong Mem;
        public LinuxUserProcess? Child;

        public Fixture(int pid = Pid)
        {
            var term = new TerminalDescriptor(new Lines());
            Parent = new LinuxUserProcess(new Pages(), term, pid, 64 << 20, Clock, new LinuxFileSystem(new MemoryFileStore(), term));
            Parent.StartThread = _ => true;
            Parent.StartVfork = (c, _) =>
            {
                Child = c;
                c.OnExit = _ => c.ReportEnd(c.WaitStatusOf(9));
                return true;
            };
            Parent.ExePath = "/bin/Ladybird";
            Mem = Parent.MapAnonymous(16 * Page, 3);
            Assert.AreNotEqual(0UL, Mem);
        }

        public static long Call(LinuxUserProcess p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0)
        {
            var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4 };
            return LinuxSyscalls.Dispatch(ref a, p);
        }

        public ulong Path(string path)
        {
            byte[] b = Encoding.UTF8.GetBytes(path + "\0");
            Assert.IsTrue(Parent.TryWrite(Mem + (10 * Page), b));
            return Mem + (10 * Page);
        }

        public long Open(LinuxUserProcess p, string path, int flags = 0) =>
            Call(p, Openat, unchecked((ulong)-100), Path(path), (ulong)flags, 0);

        /// <summary>Liest bis zum Ende in Stücken zu <paramref name="chunk"/> Bytes.</summary>
        public string ReadAll(LinuxUserProcess p, long fd, int chunk = 4096)
        {
            var all = new List<byte>();
            byte[] buf = new byte[chunk];
            while (true)
            {
                long n = Call(p, Read, (ulong)fd, Mem + (12 * Page), (ulong)chunk);
                Assert.IsTrue(n >= 0, "read: " + n);
                if (n == 0)
                {
                    return Encoding.UTF8.GetString(all.ToArray());
                }

                Assert.IsTrue(p.TryRead(Mem + (12 * Page), buf.AsSpan(0, (int)n)));
                all.AddRange(buf.AsSpan(0, (int)n).ToArray());
            }
        }

        public string ReadFile(LinuxUserProcess p, string path)
        {
            long fd = Open(p, path);
            Assert.IsTrue(fd >= 0, path + ": " + fd);
            string text = ReadAll(p, fd);
            Assert.AreEqual(0, Call(p, Close, (ulong)fd));
            return text;
        }
    }

    private static string Field(string status, string name)
    {
        foreach (string line in status.Split('\n'))
        {
            string[] parts = line.Split(':', 2);
            if (parts.Length == 2 && parts[0] == name)
            {
                return parts[1].Trim();
            }
        }

        Assert.Fail("Zeile fehlt: " + name);
        return string.Empty;
    }

    [TestMethod]
    public void StatusNenntTracerPidUndSpeicherWieLadybirdSieLiest()
    {
        var f = new Fixture();
        Assert.IsTrue(f.Parent.TryWrite(f.Mem, new byte[3 * Page]), "drei Seiten belegen");
        string status = f.ReadFile(f.Parent, "/proc/self/status");
        Assert.AreEqual("Ladybird", Field(status, "Name"));
        Assert.AreEqual("0", Field(status, "TracerPid"), "is_being_debugged");
        Assert.AreEqual("1", Field(status, "Pid"));
        Assert.AreEqual("0", Field(status, "PPid"));
        Assert.AreEqual("R (running)", Field(status, "State"));
        Assert.AreEqual("1", Field(status, "Threads"));
        Assert.IsTrue(status.Contains("\nRssAnon:\t      ", StringComparison.Ordinal), "Breite 8 wie Linux");
        Assert.IsTrue(long.Parse(Field(status, "RssAnon").Replace(" kB", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture) >= 12);
        Assert.AreEqual("0 kB", Field(status, "RssShmem"));
        Assert.AreEqual("0 kB", Field(status, "VmSwap"));

        // Stückweise gelesen ergibt denselben Text (Fortsetzung ohne Neuerzeugung ab Position > 0). Erneut ganz gelesen, weil das erste
        // Lesen die Seite des Lesepuffers belegt hat (RSS wächst).
        status = f.ReadFile(f.Parent, "/proc/self/status");
        long fd = f.Open(f.Parent, "/proc/1/status");
        Assert.AreEqual(status, f.ReadAll(f.Parent, fd, 7));
    }

    [TestMethod]
    public void SignalmaskenInStatus()
    {
        var f = new Fixture();
        f.Parent.MainThread.Blocked = 1UL << 9;
        f.Parent.Signals.Handler[13] = 1; // SIGPIPE ignoriert
        f.Parent.Signals.Handler[15] = 0x401000; // SIGTERM abgefangen
        string status = f.ReadFile(f.Parent, "/proc/self/status");
        Assert.AreEqual("0000000000000200", Field(status, "SigBlk"));
        Assert.AreEqual("0000000000001000", Field(status, "SigIgn"));
        Assert.AreEqual("0000000000004000", Field(status, "SigCgt"));
    }

    [TestMethod]
    public void ProcStatLiestNachSeekNeu()
    {
        var f = new Fixture();
        long fd = f.Open(f.Parent, "/proc/stat");
        Assert.IsTrue(fd >= 0);
        string first = f.ReadAll(f.Parent, fd);
        Assert.IsTrue(first.StartsWith("cpu  0 0 0 500 0 0 0 0 0 0\ncpu0 ", StringComparison.Ordinal), first);
        Assert.IsTrue(first.Contains("\nbtime 1799999995\n", StringComparison.Ordinal), first);
        Assert.IsTrue(first.Contains("\nprocesses 1\n", StringComparison.Ordinal), first);

        // Wie Ladybird (update_process_statistics): dieselbe Datei, seek(0), erneut lesen.
        f.Clock.Mono += 2_000_000_000;
        Assert.AreEqual(0, Fixture.Call(f.Parent, Lseek, (ulong)fd, 0, 0));
        string second = f.ReadAll(f.Parent, fd);
        Assert.IsTrue(second.StartsWith("cpu  0 0 0 700 ", StringComparison.Ordinal), second);
        Assert.AreEqual(-LinuxErrno.EINVAL, Fixture.Call(f.Parent, Lseek, (ulong)fd, 0, 2), "SEEK_END wie seq_file");
    }

    [TestMethod]
    public void PidStatUndStatusEinesKindesImLauf()
    {
        var f = new Fixture();
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0));
        LinuxUserProcess child = f.Child!;
        child.ExePath = "/bin/WebContent";
        string pid = child.Pid.ToString(CultureInfo.InvariantCulture);

        string stat = f.ReadFile(f.Parent, "/proc/" + pid + "/stat");
        Assert.IsTrue(stat.EndsWith('\n'));
        Assert.IsTrue(stat.StartsWith(pid + " (WebContent) S 1 1 1 ", StringComparison.Ordinal), stat);
        string[] all = stat.TrimEnd('\n').Split(' ');
        Assert.AreEqual(52, all.Length, "Felder wie Linux 6.1");

        // Ladybird process_resource_usage: Felder nach ") ", Index 11 und 12 sind utime und stime.
        string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        Assert.AreEqual("0", fields[11]);
        Assert.AreEqual("0", fields[12]);

        string status = f.ReadFile(f.Parent, "/proc/" + pid + "/status");
        Assert.AreEqual("WebContent", Field(status, "Name"));
        Assert.AreEqual("1", Field(status, "PPid"));
        Assert.AreEqual("S (sleeping)", Field(status, "State"));

        // Das Kind sieht den Elternprozess ebenfalls.
        Assert.IsTrue(f.ReadFile(child, "/proc/1/stat").StartsWith("1 (Ladybird) S 0 ", StringComparison.Ordinal));

        Assert.AreEqual(-LinuxErrno.ENOENT, f.Open(f.Parent, "/proc/99/stat"), "nicht im Lauf");
        Assert.AreEqual(-LinuxErrno.ENOENT, f.Open(f.Parent, "/proc/" + pid + "/fd"), "fremde Deskriptortabelle");
        Assert.AreEqual(-LinuxErrno.ENOENT, f.Open(f.Parent, "/proc/01/stat"), "führende Null wie Linux");
        Assert.AreEqual(-LinuxErrno.ENOENT, f.Open(f.Parent, "/proc/self/environ"), "nicht nachgebildet");
    }

    [TestMethod]
    public void ZombieUndAbgeholterProzess()
    {
        var f = new Fixture();
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0));
        LinuxUserProcess child = f.Child!;
        string path = "/proc/" + child.Pid.ToString(CultureInfo.InvariantCulture) + "/stat";
        long fd = f.Open(f.Parent, path);
        Assert.IsTrue(fd >= 0);
        Fixture.Call(child, LinuxSyscalls.ExitGroup, 3);

        string zombie = f.ReadAll(f.Parent, fd);
        Assert.IsTrue(zombie.Contains(") Z 1 ", StringComparison.Ordinal), zombie);
        Assert.IsTrue(f.ReadFile(f.Parent, "/proc/" + child.Pid.ToString(CultureInfo.InvariantCulture) + "/status").Contains("\nState:\tZ (zombie)\n", StringComparison.Ordinal));

        Assert.AreEqual(child.Pid, Fixture.Call(f.Parent, LinuxSyscalls.Wait4, unchecked((ulong)-1), 0, 0, 0));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Lseek, (ulong)fd, 0, 0));
        Assert.AreEqual(-3, Fixture.Call(f.Parent, Read, (ulong)fd, f.Mem + (12 * Page), 4096), "ESRCH nach dem Abholen");
        Assert.AreEqual(-LinuxErrno.ENOENT, f.Open(f.Parent, path));
    }

    [TestMethod]
    public void MapsZeigtBereicheMitRechten()
    {
        var f = new Fixture();
        string maps = f.ReadFile(f.Parent, "/proc/self/maps");
        string expected = f.Mem.ToString("x8", CultureInfo.InvariantCulture) + "-" + (f.Mem + (16 * Page)).ToString("x8", CultureInfo.InvariantCulture)
            + " rw-p 00000000 00:00 0\n";
        Assert.IsTrue(maps.Contains(expected, StringComparison.Ordinal), maps);
        Assert.AreEqual(0, Fixture.Call(f.Parent, 10 /* mprotect */, f.Mem, Page, 1));
        maps = f.ReadFile(f.Parent, "/proc/self/maps");
        Assert.IsTrue(maps.Contains(f.Mem.ToString("x8", CultureInfo.InvariantCulture) + "-" + (f.Mem + Page).ToString("x8", CultureInfo.InvariantCulture) + " r--p ",
            StringComparison.Ordinal), maps);
    }

    [TestMethod]
    public void MapsBenenntHeapUndStack()
    {
        var s = new LinuxProcSnapshot
        {
            Regions =
            [
                new LinuxProcRegion(0x400000, 0x401000, MapAccess.ReadExecute, false, string.Empty),
                new LinuxProcRegion(0x500000, 0x502000, MapAccess.ReadWrite, false, "[heap]"),
                new LinuxProcRegion(0x7000_0000_0000, 0x7000_0000_1000, MapAccess.ReadWrite, true, "/memfd:skia (deleted)"),
            ],
        };
        string[] lines = LinuxProcFiles.Maps(s).TrimEnd('\n').Split('\n');
        Assert.AreEqual("00400000-00401000 r-xp 00000000 00:00 0", lines[0]);
        Assert.AreEqual(73, lines[1].IndexOf('[', StringComparison.Ordinal), "Name ab Spalte 74 wie Linux");
        Assert.IsTrue(lines[2].StartsWith("700000000000-700000001000 rw-s ", StringComparison.Ordinal));
        Assert.IsTrue(lines[2].EndsWith(" /memfd:skia (deleted)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MeminfoUndCpuinfo()
    {
        var f = new Fixture();
        string mem = f.ReadFile(f.Parent, "/proc/meminfo");
        Assert.IsTrue(mem.StartsWith("MemTotal:              0 kB\nMemFree:               0 kB\nMemAvailable:          0 kB\n", StringComparison.Ordinal), mem);
        Assert.IsTrue(mem.Contains("\nSwapTotal:             0 kB\n", StringComparison.Ordinal));
        string cpu = f.ReadFile(f.Parent, "/proc/cpuinfo");
        Assert.IsTrue(cpu.StartsWith("processor\t: 0\n", StringComparison.Ordinal));
        Assert.AreEqual(1, cpu.Split("processor\t:").Length - 1, "eine CPU wie sched_getaffinity");
    }

    [TestMethod]
    public void NurLesbarUndStatAccess()
    {
        var f = new Fixture();
        Assert.AreEqual(-LinuxErrno.EACCES, f.Open(f.Parent, "/proc/self/status", 1));
        Assert.AreEqual(-LinuxErrno.EEXIST, f.Open(f.Parent, "/proc/self/status", 0xC0));
        Assert.AreEqual(-LinuxErrno.ENOTDIR, f.Open(f.Parent, "/proc/self/status", 0x10000));

        ulong st = f.Mem + 256;
        Assert.AreEqual(0, Fixture.Call(f.Parent, Newfstatat, unchecked((ulong)-100), f.Path("/proc/self/stat"), st, 0));
        byte[] b = new byte[144];
        Assert.IsTrue(f.Parent.TryRead(st, b));
        Assert.AreEqual(0x8124u, BitConverter.ToUInt32(b, 24), "S_IFREG 0444");
        Assert.AreEqual(0L, BitConverter.ToInt64(b, 48), "Größe 0 wie Linux");
        Assert.AreEqual(-LinuxErrno.ENOENT, Fixture.Call(f.Parent, Newfstatat, unchecked((ulong)-100), f.Path("/proc/42/status"), st, 0));

        Assert.AreEqual(0, Fixture.Call(f.Parent, Access, f.Path("/proc/self/status"), 4));
        Assert.AreEqual(-LinuxErrno.EACCES, Fixture.Call(f.Parent, Access, f.Path("/proc/self/status"), 2));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Access, f.Path("/proc/self/fd"), 5), "Verzeichnis: lesen und betreten");
    }

    [TestMethod]
    public void ExeLinkEinesAnderenProzessesImLauf()
    {
        var f = new Fixture();
        Assert.AreEqual(0, Fixture.Call(f.Parent, LinuxSyscalls.Clone, Spawn, 0));
        f.Child!.ExePath = "/bin/RequestServer";
        string path = "/proc/" + f.Child.Pid.ToString(CultureInfo.InvariantCulture) + "/exe";
        long n = Fixture.Call(f.Parent, Readlink, f.Path(path), f.Mem + (12 * Page), 256);
        Assert.AreEqual(18, n);
        byte[] b = new byte[n];
        Assert.IsTrue(f.Parent.TryRead(f.Mem + (12 * Page), b));
        Assert.AreEqual("/bin/RequestServer", Encoding.UTF8.GetString(b));
        Assert.AreEqual(-LinuxErrno.ENOENT, Fixture.Call(f.Parent, Readlink, f.Path("/proc/77/exe"), f.Mem + (12 * Page), 256));
    }

    [TestMethod]
    public void CommWirdAufFuenfzehnBytesGekuerzt()
    {
        Assert.AreEqual("ImageDecoderSer", LinuxProcFiles.CommOf("/usr/libexec/ImageDecoderServer"));
        Assert.AreEqual("linux", LinuxProcFiles.CommOf(null));
    }

    [TestMethod]
    public void ProzessEinesAnderenLaufsBleibtUnsichtbar()
    {
        // V29b: Zwei Läufe nebeneinander (je eigener Prozessbaum); keiner sieht Status, stat oder exe des anderen.
        var a = new Fixture();
        Assert.AreEqual(0, Fixture.Call(a.Parent, LinuxSyscalls.Clone, Spawn, 0));
        string childPid = a.Child!.Pid.ToString(CultureInfo.InvariantCulture);
        var b = new Fixture(pid: 50);
        Assert.IsTrue(b.ReadFile(b.Parent, "/proc/50/stat").StartsWith("50 (Ladybird) R 0 ", StringComparison.Ordinal), "eigener Lauf");

        foreach (string path in new[] { "/proc/1/status", "/proc/1/stat", "/proc/1/maps", "/proc/" + childPid + "/stat" })
        {
            Assert.AreEqual(-LinuxErrno.ENOENT, b.Open(b.Parent, path), path);
            Assert.AreEqual(-LinuxErrno.ENOENT, Fixture.Call(b.Parent, Access, b.Path(path), 4), path);
        }

        Assert.AreEqual(-LinuxErrno.ENOENT, Fixture.Call(b.Parent, Readlink, b.Path("/proc/1/exe"), b.Mem + (12 * Page), 256));
        Assert.AreEqual(-LinuxErrno.ENOENT, a.Open(a.Parent, "/proc/50/status"), "umgekehrt ebenso");
        Assert.AreEqual(-LinuxErrno.ENOENT, a.Open(a.Child, "/proc/50/stat"), "auch nicht aus dem Kind");
    }

    [TestMethod]
    public void KurzesLesenUndSeekHinterDasEnde()
    {
        var f = new Fixture();
        long fd = f.Open(f.Parent, "/proc/self/stat");
        Assert.IsTrue(fd >= 0);
        ulong buf = f.Mem + (12 * Page);

        Assert.AreEqual(0, Fixture.Call(f.Parent, Read, (ulong)fd, buf, 0), "Länge 0");
        Assert.AreEqual(5, Fixture.Call(f.Parent, Read, (ulong)fd, buf, 5), "Kurzlesen liefert genau die erbetene Länge");
        byte[] head = new byte[5];
        Assert.IsTrue(f.Parent.TryRead(buf, head));
        Assert.AreEqual("1 (La", Encoding.UTF8.GetString(head));
        string rest = f.ReadAll(f.Parent, fd, 3);
        Assert.IsTrue(rest.StartsWith("dybird) R 0 ", StringComparison.Ordinal), rest);
        long length = 5 + Encoding.UTF8.GetByteCount(rest);

        // Hinter das Ende: lseek gelingt wie bei Linux, Lesen liefert 0 (EOF), keinen Fehler und keine fremden Bytes.
        Assert.AreEqual(length + 100, Fixture.Call(f.Parent, Lseek, (ulong)fd, (ulong)(length + 100), 0));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Read, (ulong)fd, buf, 4096));
        Assert.AreEqual(long.MaxValue, Fixture.Call(f.Parent, Lseek, (ulong)fd, long.MaxValue, 0));
        Assert.AreEqual(0, Fixture.Call(f.Parent, Read, (ulong)fd, buf, 4096), "größte Position");
        Assert.AreEqual(-LinuxErrno.EINVAL, Fixture.Call(f.Parent, Lseek, (ulong)fd, 1, 1), "SEEK_CUR über long.MaxValue");
        Assert.AreEqual(-LinuxErrno.EINVAL, Fixture.Call(f.Parent, Lseek, (ulong)fd, unchecked((ulong)-1), 0), "negativ");

        // Seek vor dem ersten Lesen auf einer neuen Datei: Text entsteht, gelesen wird ab der Position.
        long fd2 = f.Open(f.Parent, "/proc/self/stat");
        Assert.AreEqual(3, Fixture.Call(f.Parent, Lseek, (ulong)fd2, 3, 0));
        Assert.IsTrue(f.ReadAll(f.Parent, fd2).StartsWith("Ladybird) ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ZeilenumbruchImNamenWirdMaskiert()
    {
        var f = new Fixture();
        f.Parent.ExePath = "/bin/a\nb\\c";
        string status = f.ReadFile(f.Parent, "/proc/self/status");
        Assert.IsTrue(status.StartsWith("Name:\ta\\nb\\\\c\nUmask:", StringComparison.Ordinal), status);
        Assert.IsTrue(f.ReadFile(f.Parent, "/proc/self/stat").StartsWith("1 (a\nb\\c) R ", StringComparison.Ordinal), "stat roh wie Linux");

        var s = new LinuxProcSnapshot
        {
            Regions = [new LinuxProcRegion(0x7000_0000_0000, 0x7000_0000_1000, MapAccess.ReadWrite, true, "/memfd:x\ny\\z (deleted)")],
        };
        string maps = LinuxProcFiles.Maps(s);
        Assert.AreEqual(1, maps.Split('\n').Length - 1, "eine Zeile");
        Assert.IsTrue(maps.EndsWith(" /memfd:x\\012y\\z (deleted)\n", StringComparison.Ordinal), maps);
    }

    [TestMethod]
    public void VerschmolzeneBereicheWerdenAnHeapStackUndMemfdGeteilt()
    {
        // Karte nach Verschmelzung: BSS und Heap in einem rw-Bereich, memfd mitten in anonymem rw-Speicher, Stack mit anonymem Nachbarn.
        var raw = new List<(ulong Start, ulong End, MapAccess Access)>
        {
            (0x400000, 0x401000, MapAccess.ReadExecute),
            (0x402000, 0x506000, MapAccess.ReadWrite),
            (0x7000_0000_0000, 0x7000_0000_4000, MapAccess.ReadWrite),
            (0x7FFF_0000_0000, 0x7FFF_0090_0000, MapAccess.ReadWrite),
        };
        var shared = new List<(ulong Start, ulong End, string Name)> { (0x7000_0000_1000, 0x7000_0000_2000, "/memfd:skia (deleted)") };
        var regions = new List<LinuxProcRegion>();
        LinuxProcFiles.NameRegions(raw, shared, 0x500000, 0x503000, 0x7FFF_0010_0000, 0x7FFF_0090_0000, regions);

        var expected = new List<LinuxProcRegion>
        {
            new(0x400000, 0x401000, MapAccess.ReadExecute, false, string.Empty),
            new(0x402000, 0x500000, MapAccess.ReadWrite, false, string.Empty),
            new(0x500000, 0x503000, MapAccess.ReadWrite, false, "[heap]"),
            new(0x503000, 0x506000, MapAccess.ReadWrite, false, string.Empty),
            new(0x7000_0000_0000, 0x7000_0000_1000, MapAccess.ReadWrite, false, string.Empty),
            new(0x7000_0000_1000, 0x7000_0000_2000, MapAccess.ReadWrite, true, "/memfd:skia (deleted)"),
            new(0x7000_0000_2000, 0x7000_0000_4000, MapAccess.ReadWrite, false, string.Empty),
            new(0x7FFF_0000_0000, 0x7FFF_0010_0000, MapAccess.ReadWrite, false, string.Empty),
            new(0x7FFF_0010_0000, 0x7FFF_0090_0000, MapAccess.ReadWrite, false, "[stack]"),
        };
        CollectionAssert.AreEqual(expected, regions);

        // Ohne Heap (brk unverändert) und ohne Stack bleibt die Karte ungeteilt.
        regions.Clear();
        LinuxProcFiles.NameRegions(raw, Array.Empty<(ulong, ulong, string)>(), 0x500000, 0x500000, 0, 0, regions);
        Assert.AreEqual(raw.Count, regions.Count);
    }

    [TestMethod]
    public void MapsNenntMemfdMittenInAnonymemSpeicher()
    {
        // Ende-zu-Ende über den Schnappschuss: memfd (MAP_SHARED) direkt hinter dem anonymen rw-Bereich der Fixture, dahinter anonymer
        // Speicher gleicher Rechte per MAP_FIXED_NOREPLACE; die Karte verschmilzt die drei, maps muss sie trotzdem getrennt nennen.
        var f = new Fixture();
        long mfd = Fixture.Call(f.Parent, 319 /* memfd_create */, f.Path("skia"), 0);
        Assert.IsTrue(mfd >= 0, "memfd_create: " + mfd);
        Assert.AreEqual(0, Fixture.Call(f.Parent, 77 /* ftruncate */, (ulong)mfd, Page));
        long at = Fixture.Call(f.Parent, 9 /* mmap */, 0, Page, 3, 0x01 /* MAP_SHARED */, (ulong)mfd);
        Assert.IsTrue(at > 0, "mmap memfd: " + at);
        ulong m = (ulong)at;
        const ulong NoReplaceAnon = 0x100000 | 0x20 | 0x02; // MAP_FIXED_NOREPLACE | MAP_ANONYMOUS | MAP_PRIVATE
        Assert.AreEqual(f.Mem + (16 * Page), m, "memfd direkt hinter dem anonymen Bereich der Fixture");
        Assert.AreEqual((long)(m + Page), Fixture.Call(f.Parent, 9, m + Page, Page, 3, NoReplaceAnon, unchecked((ulong)-1)));

        string X(ulong v) => v.ToString("x8", CultureInfo.InvariantCulture);
        string maps = f.ReadFile(f.Parent, "/proc/self/maps");
        Assert.IsTrue(maps.Contains(X(m) + "-" + X(m + Page) + " rw-s ", StringComparison.Ordinal), maps);
        Assert.IsTrue(maps.Contains(" /memfd:skia (deleted)\n" + X(m + Page) + "-", StringComparison.Ordinal), maps);
        Assert.IsTrue(maps.Contains(X(f.Mem) + "-" + X(m) + " rw-p 00000000 00:00 0\n", StringComparison.Ordinal), maps);
        Assert.IsTrue(maps.Contains(X(m + Page) + "-" + X(m + (2 * Page)) + " rw-p 00000000 00:00 0\n", StringComparison.Ordinal), maps);
    }
}
