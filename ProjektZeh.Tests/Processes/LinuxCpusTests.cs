using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>N4-S2: CPU-Menge des Laufs in sched_getaffinity, getcpu, /proc/stat und /proc/cpuinfo (<see cref="ILinuxCpus"/>).</summary>
[TestClass]
public sealed class LinuxCpusTests
{
    private const ulong Page = 4096;

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

    private class Host : ILinuxHost
    {
        public long RealtimeNanos => 0;
        public long MonotonicNanos => 5_000_000_000;
        public bool TryFillRandom(Span<byte> dst) { dst.Fill(7); return true; }
        public void SleepNanos(long ns) { }
    }

    private sealed class CpuHost(int count, int current) : Host, ILinuxCpus
    {
        public int CpuCount => count;
        public int CurrentCpu => current;
    }

    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    private static (LinuxUserProcess P, ulong Mem) Start(ILinuxHost host)
    {
        var term = new TerminalDescriptor(new Lines());
        var p = new LinuxUserProcess(new Pages(), term, 1, 64 << 20, host, new LinuxFileSystem(new MemoryFileStore(), term));
        ulong mem = p.MapAnonymous(Page, 3);
        Assert.AreNotEqual(0UL, mem);
        return (p, mem);
    }

    private static long Call(LinuxUserProcess p, long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0)
    {
        var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2 };
        return LinuxSyscalls.Dispatch(ref a, p);
    }

    private static ulong Read64(LinuxUserProcess p, ulong addr)
    {
        Span<byte> b = stackalloc byte[8];
        Assert.IsTrue(p.TryRead(addr, b));
        return BitConverter.ToUInt64(b);
    }

    [TestMethod]
    public void AffinitaetOhneCpuHostEineCpu()
    {
        var (p, mem) = Start(new Host());
        Assert.AreEqual(8L, Call(p, LinuxSyscalls.SchedGetaffinity, 0, 128, mem));
        Assert.AreEqual(1UL, Read64(p, mem));
    }

    [TestMethod]
    [DataRow(1, 1UL)]
    [DataRow(3, 7UL)]
    [DataRow(64, ulong.MaxValue)]
    public void AffinitaetLaufmengeDicht(int count, ulong expected)
    {
        var (p, mem) = Start(new CpuHost(count, 0));
        Assert.AreEqual(8L, Call(p, LinuxSyscalls.SchedGetaffinity, 0, 8, mem));
        Assert.AreEqual(expected, Read64(p, mem));
    }

    [TestMethod]
    public void GetcpuSchreibtCpuUndKnoten()
    {
        var (p, mem) = Start(new CpuHost(3, 2));
        Assert.IsTrue(p.TryWrite(mem, new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff }));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Getcpu, mem, mem + 4, 0));
        Assert.AreEqual(2UL, Read64(p, mem), "CPU 2 in den unteren 4 Bytes, Knoten 0 darüber");
    }

    [TestMethod]
    public void GetcpuZeigerOptionalFehlerBeiUngueltigem()
    {
        var (p, mem) = Start(new CpuHost(2, 1));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Getcpu, 0, 0, 0));
        Assert.AreEqual(0L, Call(p, LinuxSyscalls.Getcpu, 0, mem, 0));
        Assert.AreEqual(-LinuxSyscalls.EFAULT, Call(p, LinuxSyscalls.Getcpu, 0x10, 0, 0));
    }

    [TestMethod]
    public void ProcStatEineZeileJeCpu()
    {
        var (p, _) = Start(new CpuHost(3, 0));
        string stat = LinuxProcFiles.SysStat(p);
        Assert.IsTrue(stat.StartsWith("cpu  0 0 0 1500 0 0 0 0 0 0\ncpu0 0 0 0 500 0 0 0 0 0 0\ncpu1 0 0 0 500 0 0 0 0 0 0\ncpu2 0 0 0 500 0 0 0 0 0 0\nintr ", StringComparison.Ordinal), stat);
    }

    [TestMethod]
    public void CpuInfoEinBlockJeCpuEineCpuWieVorher()
    {
        string three = LinuxProcFiles.CpuInfo(3);
        Assert.AreEqual(3, three.Split("processor\t:").Length - 1);
        Assert.IsTrue(three.Contains("processor\t: 2\n", StringComparison.Ordinal));
        Assert.IsTrue(three.Contains("siblings\t: 3\ncore id\t\t: 2\ncpu cores\t: 3\napicid\t\t: 2\n", StringComparison.Ordinal), three);
        Assert.IsTrue(three.EndsWith("power management:\n\n", StringComparison.Ordinal));

        string one = LinuxProcFiles.CpuInfo();
        Assert.IsTrue(one.StartsWith("processor\t: 0\nvendor_id\t: ZehOS\n", StringComparison.Ordinal));
        Assert.IsTrue(one.Contains("siblings\t: 1\ncore id\t\t: 0\ncpu cores\t: 1\napicid\t\t: 0\n", StringComparison.Ordinal));
        Assert.AreEqual(1, one.Split("processor\t:").Length - 1);
    }
}
