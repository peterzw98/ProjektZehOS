using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Processes.Vfs;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>V15: wait4/waitid, Zombies, getppid, FD_CLOEXEC und die geteilte Deskriptortabelle eines vfork-Kindes.</summary>
[TestClass]
public sealed class LinuxProcessFamilyTests
{
    private const long Open = 2, Close = 3, Dup = 32, Fcntl = 72, Dup3 = 292, Pipe2 = 293, Read = 0, Write = 1;
    private const int FGetfd = 1, FSetfd = 2, FDupfdCloexec = 1030, OCloexec = 0x80000;
    private const int RootPid = 1, ChildPid = 2;
    private const long ECHILD = 10, EINVAL = 22, EINTR = 4, EFAULT = 14;

    private sealed class Pages : IUserPages
    {
        private readonly Dictionary<ulong, byte[]> _frames = new();
        private ulong _next = 0x100000;

        public ulong AllocateFrame()
        {
            ulong f = _next;
            _next += 4096;
            _frames[f] = new byte[4096];
            return f;
        }

        public void FreeFrame(ulong frame) => _frames.Remove(frame);

        public Span<byte> Frame(ulong frame) => _frames[frame];

        public bool Map(ulong page, ulong frame, MapAccess access) => true;

        public bool Unmap(ulong page) => true;

        public void DestroyTable()
        {
        }
    }

    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text)
        {
        }

        public void Clear()
        {
        }
    }

    private sealed record Run(LinuxUserProcess Process, ulong Buf, LinuxFileSystem Fs, ILinuxDescriptor Terminal);

    private static Run Root()
    {
        var terminal = new TerminalDescriptor(new Lines());
        var fs = new LinuxFileSystem(new MemoryFileStore(), terminal);
        var p = new LinuxUserProcess(new Pages(), terminal, RootPid, 64 << 20, fileSystem: fs);
        ulong buf = p.MapAnonymous(0x10000, 3);
        Assert.AreNotEqual(0UL, buf);
        return new Run(p, buf, fs, terminal);
    }

    private static Run Child(Run parent, int pid = ChildPid)
    {
        var p = new LinuxUserProcess(new Pages(), parent.Terminal, pid, 64 << 20, fileSystem: parent.Fs, family: parent.Process.Family);
        Assert.IsTrue(parent.Process.Family.AddChild(parent.Process.Pid, pid, p));
        p.CopyDescriptorsFrom(parent.Process);
        ulong buf = p.MapAnonymous(0x10000, 3);
        return new Run(p, buf, parent.Fs, parent.Terminal);
    }

    private static long Sys(Run r, long number, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0)
    {
        var a = new SyscallArgs { Number = number, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4 };
        return LinuxSyscalls.Dispatch(ref a, r.Process);
    }

    private static int ReadInt(Run r, ulong addr)
    {
        Span<byte> b = stackalloc byte[4];
        Assert.IsTrue(r.Process.TryRead(addr, b));
        return BinaryPrimitives.ReadInt32LittleEndian(b);
    }

    private static ulong Path(Run r, string path)
    {
        Assert.IsTrue(r.Process.TryWrite(r.Buf + 0x800, Encoding.UTF8.GetBytes(path + "\0")));
        return r.Buf + 0x800;
    }

    private static long Wait4(Run r, int pid, ulong status, int options) =>
        Sys(r, LinuxSyscalls.Wait4, unchecked((ulong)pid), status, (ulong)options);

    [TestMethod]
    public void Wait4OhneKinderEchildUndGetppidNull()
    {
        Run root = Root();
        Assert.AreEqual(-ECHILD, Wait4(root, -1, 0, 0));
        Assert.AreEqual(-ECHILD, Wait4(root, -1, 0, LinuxProcessFamily.WNohang), "WNOHANG ohne Kinder ist ECHILD, nicht 0");
        Assert.AreEqual(0L, Sys(root, LinuxSyscalls.Getppid), "erster Prozess hat den Kernel als Elternteil");
    }

    [TestMethod]
    public void ExitcodeKommtUeberWait4UndZombieVerschwindetDanach()
    {
        Run root = Root();
        Run child = Child(root);
        Assert.AreEqual((long)RootPid, Sys(child, LinuxSyscalls.Getppid));
        Assert.AreEqual(0L, Wait4(root, -1, root.Buf, LinuxProcessFamily.WNohang), "Kind lebt");
        Assert.AreEqual(-ECHILD, Wait4(root, 99, 0, LinuxProcessFamily.WNohang), "fremde PID");

        root.Process.Family.ReportExit(ChildPid, LinuxWaitStatus.Exited(42));
        Assert.IsTrue(root.Process.Family.Contains(ChildPid), "Zombie bis wait4");
        Assert.AreEqual((long)ChildPid, Wait4(root, ChildPid, root.Buf, 0));
        int status = ReadInt(root, root.Buf);
        Assert.AreEqual(0x2a00, status);
        Assert.IsTrue(LinuxWaitStatus.IsExited(status));
        Assert.AreEqual(42, LinuxWaitStatus.ExitCode(status));
        Assert.IsFalse(root.Process.Family.Contains(ChildPid), "abgeholt");
        Assert.AreEqual(-ECHILD, Wait4(root, -1, 0, 0));
    }

    [TestMethod]
    public void Wait4SchlaeftBisZumEndeDesKindes()
    {
        Run root = Root();
        _ = Child(root);
        var waiter = Task.Run(() => Wait4(root, 0, root.Buf, 0));
        Thread.Sleep(80);
        Assert.IsFalse(waiter.IsCompleted, "wartet");
        root.Process.Family.ReportExit(ChildPid, LinuxWaitStatus.Signaled(9));
        Assert.IsTrue(waiter.Wait(5000));
        Assert.AreEqual((long)ChildPid, waiter.Result);
        Assert.AreEqual(9, ReadInt(root, root.Buf), "WTERMSIG 9, kein Exitcode");
    }

    [TestMethod]
    public void AbbruchDesElternteilsBeendetDasWarten()
    {
        Run root = Root();
        _ = Child(root);
        var waiter = Task.Run(() => Wait4(root, -1, 0, 0));
        Thread.Sleep(30);
        root.Process.Exit(1);
        Assert.IsTrue(waiter.Wait(5000));
        Assert.AreEqual(-EINTR, waiter.Result);
    }

    [TestMethod]
    public void WaitidMitWnowaitLaesstDenZombieStehen()
    {
        Run root = Root();
        _ = Child(root);
        root.Process.Family.ReportExit(ChildPid, LinuxWaitStatus.Signaled(11, core: true));
        ulong info = root.Buf + 0x100;
        int opts = LinuxProcessFamily.WExited | LinuxProcessFamily.WNowait;
        Assert.AreEqual(0L, Sys(root, LinuxSyscalls.Waitid, 1, ChildPid, info, (ulong)opts));
        Assert.AreEqual(17, ReadInt(root, info), "si_signo SIGCHLD");
        Assert.AreEqual(3, ReadInt(root, info + 8), "si_code CLD_DUMPED");
        Assert.AreEqual(ChildPid, ReadInt(root, info + 16), "si_pid");
        Assert.AreEqual(11, ReadInt(root, info + 24), "si_status = Signal");
        Assert.IsTrue(root.Process.Family.Contains(ChildPid), "WNOWAIT");
        Assert.AreEqual((long)ChildPid, Wait4(root, -1, 0, 0), "danach noch abholbar");
    }

    [TestMethod]
    public void WaitidWnohangOhneFertigesKindSchreibtNullen()
    {
        Run root = Root();
        _ = Child(root);
        ulong info = root.Buf + 0x100;
        Assert.IsTrue(root.Process.TryWrite(info, new byte[] { 0xff, 0xff, 0xff, 0xff }));
        Assert.IsTrue(root.Process.TryWrite(info + 16, new byte[] { 0xff, 0xff, 0xff, 0xff }));
        Assert.AreEqual(0L, Sys(root, LinuxSyscalls.Waitid, 0, 0, info, (ulong)(LinuxProcessFamily.WExited | LinuxProcessFamily.WNohang)));
        Assert.AreEqual(0, ReadInt(root, info));
        Assert.AreEqual(0, ReadInt(root, info + 16));
    }

    [TestMethod]
    public void UngueltigeOptionenUndIdTypen()
    {
        Run root = Root();
        Assert.AreEqual(-EINVAL, Wait4(root, -1, 0, 0x10), "unbekanntes Bit");
        Assert.AreEqual(-3L, Wait4(root, int.MinValue, 0, 0), "ESRCH wie kernel_wait4");
        Assert.AreEqual(-EINVAL, Sys(root, LinuxSyscalls.Waitid, 0, 0, 0, (ulong)LinuxProcessFamily.WNohang), "ohne WEXITED/WSTOPPED/WCONTINUED");
        Assert.AreEqual(-EINVAL, Sys(root, LinuxSyscalls.Waitid, 1, 0, 0, (ulong)LinuxProcessFamily.WExited), "P_PID 0");
        Assert.AreEqual(-EINVAL, Sys(root, LinuxSyscalls.Waitid, 3, 5, 0, (ulong)LinuxProcessFamily.WExited), "P_PIDFD");
        Assert.AreEqual(-ECHILD, Sys(root, LinuxSyscalls.Waitid, 0, 0, 0, (ulong)LinuxProcessFamily.WExited));
    }

    [TestMethod]
    public void Wait4StatusAufSchlechtenZeigerIstEfaultKindTrotzdemAbgeholt()
    {
        Run root = Root();
        _ = Child(root);
        root.Process.Family.ReportExit(ChildPid, LinuxWaitStatus.Exited(0));
        Assert.AreEqual(-EFAULT, Wait4(root, -1, 0x8, 0));
        Assert.AreEqual(-ECHILD, Wait4(root, -1, 0, 0));
    }

    [TestMethod]
    public void IgnoriertesSigchldHinterlaesstKeinenZombie()
    {
        Run root = Root();
        _ = Child(root);
        root.Process.Signals.Handler[17] = 1; // SIG_IGN
        root.Process.Family.ReportExit(ChildPid, LinuxWaitStatus.Exited(3));
        Assert.IsFalse(root.Process.Family.Contains(ChildPid));
        Assert.AreEqual(-ECHILD, Wait4(root, -1, 0, 0));
    }

    [TestMethod]
    public void WaisenBekommenElternteilNullUndIhreZombiesVerschwinden()
    {
        Run root = Root();
        Run child = Child(root);
        Run grand = Child(child, 3);
        Run grand2 = Child(child, 4);
        Assert.AreEqual((long)ChildPid, Sys(grand, LinuxSyscalls.Getppid));
        root.Process.Family.ReportExit(4, LinuxWaitStatus.Exited(0)); // Zombie des Kindes
        root.Process.Family.ReportExit(ChildPid, LinuxWaitStatus.Exited(0));
        Assert.AreEqual(0L, Sys(grand, LinuxSyscalls.Getppid), "Waise");
        Assert.IsFalse(root.Process.Family.Contains(4), "Zombie ohne Elternteil verschwindet");
        root.Process.Family.ReportExit(3, LinuxWaitStatus.Exited(0));
        Assert.IsFalse(root.Process.Family.Contains(3), "Waise wird sofort abgeräumt");
        Assert.AreEqual((long)ChildPid, Wait4(root, -1, 0, 0), "Kind selbst bleibt Zombie des ersten");
        _ = grand2;
    }

    [TestMethod]
    public void CloexecFolgtOpenDupUndFcntl()
    {
        Run r = Root();
        long a = Sys(r, Open, Path(r, "/a"), (ulong)(LinuxFileSystem.ORdWr | LinuxFileSystem.OCreat | OCloexec), 0x1A4);
        Assert.IsTrue(a >= 3);
        Assert.AreEqual(1L, Sys(r, Fcntl, (ulong)a, FGetfd));
        long d = Sys(r, Dup, (ulong)a);
        Assert.AreEqual(0L, Sys(r, Fcntl, (ulong)d, FGetfd), "dup löscht FD_CLOEXEC");
        long e = Sys(r, Fcntl, (ulong)d, FDupfdCloexec, 10);
        Assert.AreEqual(10L, e);
        Assert.AreEqual(1L, Sys(r, Fcntl, 10, FGetfd));
        Assert.AreEqual(20L, Sys(r, Dup3, (ulong)d, 20, OCloexec));
        Assert.AreEqual(1L, Sys(r, Fcntl, 20, FGetfd));
        Assert.AreEqual(0L, Sys(r, Fcntl, (ulong)d, FSetfd, 0xff));
        Assert.AreEqual(1L, Sys(r, Fcntl, (ulong)d, FGetfd), "nur Bit 0 zählt");
        Assert.AreEqual(0L, Sys(r, Fcntl, (ulong)d, FSetfd, 0));
        Assert.AreEqual(0L, Sys(r, Close, 20));
        Assert.AreEqual(20L, Sys(r, Fcntl, (ulong)d, 0, 20), "F_DUPFD auf die freie 20");
        Assert.AreEqual(0L, Sys(r, Fcntl, 20, FGetfd), "die frei gewordene Nummer trägt kein altes Flag");

        Assert.AreEqual(0L, Sys(r, Pipe2, r.Buf, OCloexec));
        int rfd = ReadInt(r, r.Buf), wfd = ReadInt(r, r.Buf + 4);
        Assert.AreEqual(1L, Sys(r, Fcntl, (ulong)rfd, FGetfd));
        Assert.AreEqual(1L, Sys(r, Fcntl, (ulong)wfd, FGetfd));

        r.Process.CloseOnExec();
        Assert.IsNull(r.Process.GetDescriptor((int)a));
        Assert.IsNull(r.Process.GetDescriptor(10));
        Assert.IsNull(r.Process.GetDescriptor(rfd));
        Assert.IsNull(r.Process.GetDescriptor(wfd));
        Assert.IsNotNull(r.Process.GetDescriptor((int)d), "ohne Flag bleibt sie");
        Assert.IsNotNull(r.Process.GetDescriptor(0), "Terminal bleibt");
        Assert.AreEqual(1, r.Fs.OpenFiles, "Datei offen über d");
    }

    [TestMethod]
    public void KindKopiertTabelleUndSchliesstDateienDesElternteilsNicht()
    {
        Run root = Root();
        long a = Sys(root, Open, Path(root, "/a"), (ulong)(LinuxFileSystem.ORdWr | LinuxFileSystem.OCreat | OCloexec), 0x1A4);
        long b = Sys(root, Open, Path(root, "/b"), (ulong)(LinuxFileSystem.ORdWr | LinuxFileSystem.OCreat), 0x1A4);
        root.Process.Cwd = "/";
        Run child = Child(root);
        Assert.AreSame(root.Process.GetDescriptor((int)a), child.Process.GetDescriptor((int)a));
        Assert.AreEqual(1L, Sys(child, Fcntl, (ulong)a, FGetfd), "Flag kommt mit");
        Assert.AreSame(root.Process.FileGate, child.Process.FileGate, "eine Sperre für die Familie");

        child.Process.CloseOnExec();
        Assert.IsNull(child.Process.GetDescriptor((int)a));
        Assert.AreEqual(2, root.Fs.OpenFiles, "Elternteil hält /a noch");
        Assert.AreEqual(0L, Sys(child, Close, (ulong)b));
        Assert.AreEqual(2, root.Fs.OpenFiles, "close im Kind schließt die Datei des Elternteils nicht");
        child.Process.Destroy();
        root.Process.Family.ReportExit(ChildPid, LinuxWaitStatus.Exited(0));
        Assert.AreEqual(2, root.Fs.OpenFiles);
        Assert.AreEqual(0L, Sys(root, Close, (ulong)b));
        Assert.AreEqual(1, root.Fs.OpenFiles, "letzte Nummer schließt");
        root.Process.Destroy();
        Assert.AreEqual(0, root.Fs.OpenFiles);
    }

    [TestMethod]
    public void PipeEndeImKindHaeltEofBisAlleNummernZuSind()
    {
        Run root = Root();
        Assert.AreEqual(0L, Sys(root, Pipe2, root.Buf, 0));
        int rfd = ReadInt(root, root.Buf), wfd = ReadInt(root, root.Buf + 4);
        Run child = Child(root);
        Assert.AreEqual(0L, Sys(root, Close, (ulong)wfd), "Elternteil schließt sein Schreibende");
        Assert.IsTrue(child.Process.TryWrite(child.Buf, "hi"u8));
        Assert.AreEqual(2L, Sys(child, Write, (ulong)wfd, child.Buf, 2));
        child.Process.Destroy(); // Ende des Kindes: sein Schreibende war das letzte
        root.Process.Family.ReportExit(ChildPid, LinuxWaitStatus.Exited(0));
        Assert.AreEqual(2L, Sys(root, Read, (ulong)rfd, root.Buf + 0x100, 16));
        Assert.AreEqual(0L, Sys(root, Read, (ulong)rfd, root.Buf + 0x100, 16), "EOF statt Hänger");
    }
}
