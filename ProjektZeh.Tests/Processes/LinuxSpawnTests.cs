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

        public Fixture()
        {
            var term = new TerminalDescriptor(new Lines());
            Parent = new LinuxUserProcess(new Pages(), term, Pid, 64 << 20, new Host(), new LinuxFileSystem(Store, term));
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

        public void WriteFile(string path, byte[] content)
        {
            var fs = new LinuxFileSystem(Store, new TerminalDescriptor(new Lines()));
            Assert.AreEqual(0, fs.Open(path, 0x41, 0x1ED, out ILinuxFile? f));
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
        f.Parent.Signals.Blocked = 0x10;
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
        Assert.AreEqual(0x10UL, next.Signals.Blocked);
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
