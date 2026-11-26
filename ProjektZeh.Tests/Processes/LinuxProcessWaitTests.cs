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

/// <summary>
/// V15: wait4/waitid, Zombies, getppid über die Kinderliste des Elternprozesses; FD_CLOEXEC und die Referenzzählung offener Dateien
/// über vfork-Kind, execve-Abbild und Elternprozess (LinuxOpenFiles).
/// </summary>
[TestClass]
public sealed class LinuxProcessWaitTests
{
    private const long Open = 2, Close = 3, Lseek = 8, Dup = 32, Fcntl = 72, Dup3 = 292, Pipe2 = 293, Read = 0, Write = 1;
    private const int FGetfd = 1, FSetfd = 2, FDupfdCloexec = 1030, OCloexec = 0x80000, ONonblock = 0x800;
    private const int RootPid = 1;
    private const ulong Spawn = LinuxUserProcess.CloneVm | LinuxUserProcess.CloneVfork | 17;
    private const long ECHILD = 10, EINVAL = 22, EINTR = 4, EFAULT = 14, EAGAIN = 11;

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

    private sealed class Host : ILinuxHost
    {
        public long RealtimeNanos => 0;

        public long MonotonicNanos => 0;

        public bool TryFillRandom(Span<byte> dst)
        {
            dst.Fill(7);
            return true;
        }

        public void SleepNanos(long ns)
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

    /// <summary>Erster Prozess eines Laufs; die Hooks spielen die Brücke des Kernels nach (ein Thread, kein Ring 3).</summary>
    private sealed class Root
    {
        public readonly LinuxUserProcess P;
        public readonly ulong Buf;
        public readonly LinuxFileSystem Fs;
        public LinuxUserProcess? Child;
        public LinuxUserProcess? Exec;

        public Root()
        {
            var terminal = new TerminalDescriptor(new Lines());
            Fs = new LinuxFileSystem(new MemoryFileStore(), terminal);
            P = new LinuxUserProcess(new Pages(), terminal, RootPid, 64 << 20, new Host(), Fs);
            P.StartVfork = (c, _) =>
            {
                Child = c;
                c.CreatePages = () => new Pages();
                c.StartExec = (next, _) =>
                {
                    Exec = next;
                    return true;
                };
                c.OnExit = _ => c.ReportEnd(c.WaitStatusOf(9));
                return true;
            };
            Buf = P.MapAnonymous(0x10000, 3);
            Assert.AreNotEqual(0UL, Buf);
        }

        /// <summary>posix_spawn bis vor execve: neues vfork-Kind auf dem Speicher des ersten Prozesses.</summary>
        public LinuxUserProcess Spawn()
        {
            Assert.AreEqual(0L, Sys(P, LinuxSyscalls.Clone, LinuxProcessWaitTests.Spawn));
            return Child!;
        }

        /// <summary>Ende eines vfork-Kinds wie in der Brücke: Status an den Elternprozess, dann gibt das Objekt seine Nummern ab.</summary>
        public static void End(LinuxUserProcess child, int status)
        {
            child.ReportEnd(status);
            child.Release();
        }

        public ulong Path(string path)
        {
            Assert.IsTrue(P.TryWrite(Buf + 0x800, Encoding.UTF8.GetBytes(path + "\0")));
            return Buf + 0x800;
        }

        public int ReadInt(ulong addr)
        {
            Span<byte> b = stackalloc byte[4];
            Assert.IsTrue(P.TryRead(addr, b));
            return BinaryPrimitives.ReadInt32LittleEndian(b);
        }

        public long Wait4(int pid, ulong status, int options) => Sys(P, LinuxSyscalls.Wait4, unchecked((ulong)pid), status, (ulong)options);

        public long OpenRw(string path, int extra = 0) =>
            Sys(P, Open, Path(path), (ulong)(LinuxFileSystem.ORdWr | LinuxFileSystem.OCreat | extra), 0x1A4);
    }

    private static long Sys(LinuxUserProcess p, long number, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0)
    {
        var a = new SyscallArgs { Number = number, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4 };
        return LinuxSyscalls.Dispatch(ref a, p);
    }

    [TestMethod]
    public void Wait4OhneKinderEchildUndGetppidNull()
    {
        var r = new Root();
        Assert.AreEqual(-ECHILD, r.Wait4(-1, 0, 0));
        Assert.AreEqual(-ECHILD, r.Wait4(-1, 0, LinuxUserProcess.WNohang), "WNOHANG ohne Kinder ist ECHILD, nicht 0");
        Assert.AreEqual(0L, Sys(r.P, LinuxSyscalls.Getppid), "erster Prozess hat den Kernel als Elternteil");
    }

    [TestMethod]
    public void ExitcodeKommtUeberWait4UndZombieVerschwindetDanach()
    {
        var r = new Root();
        LinuxUserProcess child = r.Spawn();
        Assert.AreEqual((long)RootPid, Sys(child, LinuxSyscalls.Getppid));
        Assert.AreEqual(0L, r.Wait4(-1, r.Buf, LinuxUserProcess.WNohang), "Kind lebt");
        Assert.AreEqual(-ECHILD, r.Wait4(99, 0, LinuxUserProcess.WNohang), "fremde PID");

        Sys(child, LinuxSyscalls.ExitGroup, 42);
        child.Release();
        Assert.AreEqual(1, r.P.Children.Length, "Zombie bis wait4");
        Assert.AreEqual((long)child.Pid, r.Wait4(child.Pid, r.Buf, 0));
        int status = r.ReadInt(r.Buf);
        Assert.AreEqual(0x2a00, status);
        Assert.AreEqual(42, LinuxWaitStatus.ExitCode(status));
        Assert.AreEqual(0, r.P.Children.Length, "abgeholt");
        Assert.AreEqual(-ECHILD, r.Wait4(-1, 0, 0));
    }

    [TestMethod]
    public void Wait4SchlaeftBisZumEndeDesKindes()
    {
        var r = new Root();
        LinuxUserProcess child = r.Spawn();
        var waiter = Task.Run(() => r.Wait4(0, r.Buf, 0));
        Thread.Sleep(80);
        Assert.IsFalse(waiter.IsCompleted, "wartet");
        Root.End(child, LinuxWaitStatus.Signaled(9));
        Assert.IsTrue(waiter.Wait(5000));
        Assert.AreEqual((long)child.Pid, waiter.Result);
        Assert.AreEqual(9, r.ReadInt(r.Buf), "WTERMSIG 9, kein Exitcode");
    }

    [TestMethod]
    public void AbbruchDesElternprozessesBeendetDasWarten()
    {
        var r = new Root();
        _ = r.Spawn();
        var waiter = Task.Run(() => r.Wait4(-1, 0, 0));
        Thread.Sleep(30);
        r.P.Exit(1);
        Assert.IsTrue(waiter.Wait(5000));
        Assert.AreEqual(-EINTR, waiter.Result);
    }

    [TestMethod]
    public void WaitidMitWnowaitLaesstDenZombieStehen()
    {
        var r = new Root();
        LinuxUserProcess child = r.Spawn();
        Root.End(child, LinuxWaitStatus.Signaled(11, core: true));
        ulong info = r.Buf + 0x100;
        int opts = LinuxUserProcess.WExited | LinuxUserProcess.WNowait;
        Assert.AreEqual(0L, Sys(r.P, LinuxSyscalls.Waitid, 1, (ulong)child.Pid, info, (ulong)opts));
        Assert.AreEqual(17, r.ReadInt(info), "si_signo SIGCHLD");
        Assert.AreEqual(3, r.ReadInt(info + 8), "si_code CLD_DUMPED");
        Assert.AreEqual(child.Pid, r.ReadInt(info + 16), "si_pid");
        Assert.AreEqual(11, r.ReadInt(info + 24), "si_status = Signal");
        Assert.AreEqual(1, r.P.Children.Length, "WNOWAIT");
        Assert.AreEqual((long)child.Pid, r.Wait4(-1, 0, 0), "danach noch abholbar");
    }

    [TestMethod]
    public void WaitidWnohangOhneFertigesKindSchreibtNullen()
    {
        var r = new Root();
        _ = r.Spawn();
        ulong info = r.Buf + 0x100;
        Assert.IsTrue(r.P.TryWrite(info, new byte[] { 0xff, 0xff, 0xff, 0xff }));
        Assert.IsTrue(r.P.TryWrite(info + 16, new byte[] { 0xff, 0xff, 0xff, 0xff }));
        Assert.AreEqual(0L, Sys(r.P, LinuxSyscalls.Waitid, 0, 0, info, (ulong)(LinuxUserProcess.WExited | LinuxUserProcess.WNohang)));
        Assert.AreEqual(0, r.ReadInt(info));
        Assert.AreEqual(0, r.ReadInt(info + 16));
    }

    [TestMethod]
    public void UngueltigeOptionenUndIdTypen()
    {
        var r = new Root();
        Assert.AreEqual(-EINVAL, r.Wait4(-1, 0, 0x10), "unbekanntes Bit");
        Assert.AreEqual(-3L, r.Wait4(int.MinValue, 0, 0), "ESRCH wie kernel_wait4");
        Assert.AreEqual(-EINVAL, Sys(r.P, LinuxSyscalls.Waitid, 0, 0, 0, (ulong)LinuxUserProcess.WNohang), "ohne WEXITED/WSTOPPED/WCONTINUED");
        Assert.AreEqual(-EINVAL, Sys(r.P, LinuxSyscalls.Waitid, 1, 0, 0, (ulong)LinuxUserProcess.WExited), "P_PID 0");
        Assert.AreEqual(-EINVAL, Sys(r.P, LinuxSyscalls.Waitid, 3, 5, 0, (ulong)LinuxUserProcess.WExited), "P_PIDFD");
        Assert.AreEqual(-ECHILD, Sys(r.P, LinuxSyscalls.Waitid, 0, 0, 0, (ulong)LinuxUserProcess.WExited));
    }

    [TestMethod]
    public void Wait4StatusAufSchlechtenZeigerIstEfaultKindTrotzdemAbgeholt()
    {
        var r = new Root();
        Root.End(r.Spawn(), LinuxWaitStatus.Exited(0));
        Assert.AreEqual(-EFAULT, r.Wait4(-1, 0x8, 0));
        Assert.AreEqual(-ECHILD, r.Wait4(-1, 0, 0));
    }

    [TestMethod]
    public void IgnoriertesSigchldHinterlaesstKeinenZombie()
    {
        var r = new Root();
        LinuxUserProcess child = r.Spawn();
        r.P.Signals.Handler[17] = 1; // SIG_IGN
        Root.End(child, LinuxWaitStatus.Exited(3));
        Assert.AreEqual(0, r.P.Children.Length);
        Assert.AreEqual(-ECHILD, r.Wait4(-1, 0, 0));
    }

    [TestMethod]
    public void CloexecFolgtOpenDupUndFcntl()
    {
        var r = new Root();
        long a = r.OpenRw("/a", OCloexec);
        Assert.IsTrue(a >= 3);
        Assert.AreEqual(1L, Sys(r.P, Fcntl, (ulong)a, FGetfd));
        long d = Sys(r.P, Dup, (ulong)a);
        Assert.AreEqual(0L, Sys(r.P, Fcntl, (ulong)d, FGetfd), "dup löscht FD_CLOEXEC");
        Assert.AreEqual(10L, Sys(r.P, Fcntl, (ulong)d, FDupfdCloexec, 10));
        Assert.AreEqual(1L, Sys(r.P, Fcntl, 10, FGetfd));
        Assert.AreEqual(20L, Sys(r.P, Dup3, (ulong)d, 20, OCloexec));
        Assert.AreEqual(1L, Sys(r.P, Fcntl, 20, FGetfd));
        Assert.AreEqual(0L, Sys(r.P, Fcntl, (ulong)d, FSetfd, 0xff));
        Assert.AreEqual(1L, Sys(r.P, Fcntl, (ulong)d, FGetfd), "nur Bit 0 zählt");
        Assert.AreEqual(0L, Sys(r.P, Fcntl, (ulong)d, FSetfd, 0));
        Assert.AreEqual(0L, Sys(r.P, Close, 20));
        Assert.AreEqual(20L, Sys(r.P, Fcntl, (ulong)d, 0, 20), "F_DUPFD auf die freie 20");
        Assert.AreEqual(0L, Sys(r.P, Fcntl, 20, FGetfd), "die frei gewordene Nummer trägt kein altes Flag");
    }

    [TestMethod]
    public void ExecveSchliesstCloexecNummernNurImKind()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        long a = r.OpenRw("/a", OCloexec);
        long b = r.OpenRw("/b");
        Assert.AreEqual(0L, Sys(r.P, Pipe2, r.Buf, OCloexec));
        int rfd = r.ReadInt(r.Buf), wfd = r.ReadInt(r.Buf + 4);

        LinuxUserProcess child = r.Spawn();
        Assert.AreSame(r.P.GetDescriptor((int)a), child.GetDescriptor((int)a));
        Assert.AreEqual(1L, Sys(child, Fcntl, (ulong)a, FGetfd), "Flag kommt mit");
        Assert.AreSame(r.P.FileGate, child.FileGate, "eine Sperre für den Lauf");
        Assert.AreEqual(0L, child.Exec("/hello", ["/hello"], []));
        LinuxUserProcess next = r.Exec!;
        child.Release(); // wie ResumeParent in der Brücke
        Assert.IsNull(next.GetDescriptor((int)a));
        Assert.IsNull(next.GetDescriptor(rfd));
        Assert.IsNull(next.GetDescriptor(wfd));
        Assert.AreSame(r.P.GetDescriptor((int)b), next.GetDescriptor((int)b), "ohne Flag geerbt");
        Assert.IsNotNull(next.GetDescriptor(0), "Terminal bleibt");
        Assert.AreEqual(2, r.Fs.OpenFiles, "die Eltern halten /a und /b");
        Assert.IsNotNull(r.P.GetDescriptor(rfd));

        Assert.AreEqual(0L, Sys(r.P, Close, (ulong)b));
        Assert.AreEqual(2, r.Fs.OpenFiles, "/b hält noch das neue Abbild");
        next.ReportEnd(LinuxWaitStatus.Exited(0));
        next.Destroy();
        Assert.AreEqual(1, r.Fs.OpenFiles, "letzte Nummer auf /b war im Kind");
        Assert.AreEqual((long)next.Pid, r.Wait4(-1, 0, 0));
    }

    [TestMethod]
    public void CloseImKindLaesstDiePipeDerElternOffen()
    {
        // musl posix_spawn: Das Kind schließt das Leseende seiner Fehlerpipe; der Elternprozess liest es danach (V15-Gastlauf, "spawn fehlt rc 0").
        var r = new Root();
        Assert.AreEqual(0L, Sys(r.P, Pipe2, r.Buf, OCloexec));
        int rfd = r.ReadInt(r.Buf), wfd = r.ReadInt(r.Buf + 4);
        LinuxUserProcess child = r.Spawn();
        Assert.AreEqual(0L, Sys(child, Close, (ulong)rfd));
        Assert.IsTrue(child.TryWrite(r.Buf + 0x200, BitConverter.GetBytes(2)));
        Assert.AreEqual(4L, Sys(child, Write, (ulong)wfd, r.Buf + 0x200, 4), "errno an die Eltern");
        Root.End(child, LinuxWaitStatus.Exited(127));
        Assert.AreEqual(0L, Sys(r.P, Close, (ulong)wfd));
        Assert.AreEqual(4L, Sys(r.P, Read, (ulong)rfd, r.Buf + 0x300, 8));
        Assert.AreEqual(2, r.ReadInt(r.Buf + 0x300));
        Assert.AreEqual(0L, Sys(r.P, Read, (ulong)rfd, r.Buf + 0x300, 8), "EOF: kein Schreibende mehr");
        Assert.AreEqual((long)child.Pid, r.Wait4(-1, r.Buf, 0));
        Assert.AreEqual(127, LinuxWaitStatus.ExitCode(r.ReadInt(r.Buf)));
    }

    [TestMethod]
    public void KindEndetElternLesenUndSchreibenWeiter()
    {
        var r = new Root();
        long f = r.OpenRw("/daten");
        Assert.AreEqual(0L, Sys(r.P, Pipe2, r.Buf, 0));
        int rfd = r.ReadInt(r.Buf), wfd = r.ReadInt(r.Buf + 4);

        LinuxUserProcess child = r.Spawn();
        Assert.IsTrue(child.TryWrite(r.Buf + 0x200, "kind"u8));
        Assert.AreEqual(4L, Sys(child, Write, (ulong)f, r.Buf + 0x200, 4));
        Assert.AreEqual(0L, Sys(child, Close, (ulong)f), "close im Kind");
        Assert.AreEqual(0L, Sys(child, Close, (ulong)rfd));
        Root.End(child, LinuxWaitStatus.Exited(0));
        Assert.AreEqual((long)child.Pid, r.Wait4(-1, 0, 0));

        // Gleiche offene Datei, gleiche Position: Die Eltern schreiben hinter "kind" weiter und lesen alles.
        Assert.AreEqual(1, r.Fs.OpenFiles);
        Assert.IsTrue(r.P.TryWrite(r.Buf + 0x200, "eltern"u8));
        Assert.AreEqual(6L, Sys(r.P, Write, (ulong)f, r.Buf + 0x200, 6));
        Assert.AreEqual(0L, Sys(r.P, Lseek, (ulong)f, 0, 0));
        Assert.AreEqual(10L, Sys(r.P, Read, (ulong)f, r.Buf + 0x400, 32));
        byte[] back = new byte[10];
        Assert.IsTrue(r.P.TryRead(r.Buf + 0x400, back));
        Assert.AreEqual("kindeltern", Encoding.ASCII.GetString(back));

        // Pipe der Eltern: beide Enden offen, obwohl das Kind seine Nummern geschlossen hat.
        Assert.AreEqual(6L, Sys(r.P, Write, (ulong)wfd, r.Buf + 0x200, 6));
        Assert.AreEqual(6L, Sys(r.P, Read, (ulong)rfd, r.Buf + 0x500, 16));
        r.P.Destroy();
        Assert.AreEqual(0, r.Fs.OpenFiles);
    }

    [TestMethod]
    public void PipeEndeImNeuenAbbildHaeltEofBisEsEndet()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        Assert.AreEqual(0L, Sys(r.P, Pipe2, r.Buf, ONonblock));
        int rfd = r.ReadInt(r.Buf), wfd = r.ReadInt(r.Buf + 4);
        LinuxUserProcess child = r.Spawn();
        Assert.AreEqual(0L, child.Exec("/hello", ["/hello"], []));
        LinuxUserProcess next = r.Exec!;
        child.Release();
        Assert.AreEqual(0L, Sys(r.P, Close, (ulong)wfd), "Eltern schließen ihr Schreibende");
        Assert.AreEqual(-EAGAIN, Sys(r.P, Read, (ulong)rfd, r.Buf + 0x100, 16), "das Kind hält das Schreibende noch");
        next.ReportEnd(LinuxWaitStatus.Exited(0));
        next.Destroy();
        Assert.AreEqual(0L, Sys(r.P, Read, (ulong)rfd, r.Buf + 0x100, 16), "EOF statt Hänger");
    }

    [TestMethod]
    public void OffeneDateienZaehlenJedeNummerUndNichtDasTerminal()
    {
        var files = new LinuxOpenFiles();
        var terminal = new TerminalDescriptor(new Lines());
        files.Add(terminal);
        Assert.AreEqual(0, files.Distinct);
        files.Remove(terminal); // ohne Wirkung
        var r = new Root();
        long a = r.OpenRw("/a");
        ILinuxDescriptor d = r.P.GetDescriptor((int)a)!;
        files.Add(d);
        files.Add(d);
        Assert.AreEqual(2, files.CountOf(d));
        files.Remove(d);
        Assert.IsTrue(files.IsReferenced(d));
        files.Remove(d);
        Assert.IsFalse(files.IsReferenced(d));
        Assert.AreEqual(0, files.Distinct);
    }

    private static void WriteFile(LinuxFileSystem fs, string path, byte[] content)
    {
        Assert.AreEqual(0, fs.Open(path, 0x41, 0x1ED, out ILinuxFile? f));
        Assert.AreEqual(content.Length, f!.Write(content));
        Assert.AreEqual(0, f.Close());
    }
}
