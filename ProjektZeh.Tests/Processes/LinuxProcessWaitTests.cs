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

    /// <summary>Kind des ersten Prozesses nach execve, das selbst ein Kind per vfork und execve startet (Enkel).</summary>
    private static (LinuxUserProcess Mid, LinuxUserProcess Grand) SpawnGrandchild(Root r)
    {
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        LinuxUserProcess child = r.Spawn();
        Assert.AreEqual(0L, child.Exec("/hello", ["/hello"], []));
        LinuxUserProcess mid = r.Exec!;
        child.Release();

        LinuxUserProcess? grand = null;
        LinuxUserProcess? vforked = null;
        mid.StartVfork = (c, _) =>
        {
            vforked = c;
            c.CreatePages = () => new Pages();
            c.StartExec = (n, _) =>
            {
                grand = n;
                return true;
            };
            return true;
        };
        Assert.AreEqual(0L, Sys(mid, LinuxSyscalls.Clone, Spawn));
        Assert.AreEqual(0L, vforked!.Exec("/hello", ["/hello"], []));
        vforked.Release();
        return (mid, grand!);
    }

    [TestMethod]
    public void KillErreichtDenWaisenHinterEinemZombie()
    {
        var r = new Root();
        (LinuxUserProcess mid, LinuxUserProcess grand) = SpawnGrandchild(r);
        int killed = 0;
        grand.OnKilled = _ => killed++;
        mid.OnKilled = _ => Assert.Fail("der Zombie bekommt kein Signal");
        r.P.OnExit = _ => Assert.Fail("kill(-1) trifft den Aufrufer nicht");

        mid.ReportEnd(LinuxWaitStatus.Exited(0));
        mid.Destroy(); // FreeIds: der Enkel läuft und bleibt in der Liste des toten Zwischenprozesses

        Assert.AreEqual(0L, Sys(r.P, KillNr, (ulong)grand.Pid, 0), "Waise hinter dem Zombie-Eintrag ist ein Ziel");
        Assert.AreEqual(0L, Sys(r.P, KillNr, unchecked((ulong)-1), 15), "kill(-1) erreicht ihn auch");
        Assert.AreEqual(1, killed);
        Assert.AreEqual(15, grand.TerminatedBySignal);
    }

    [TestMethod]
    public void KillErreichtDenWaisenNachDemErntenDesZwischenprozesses()
    {
        var r = new Root();
        (LinuxUserProcess mid, LinuxUserProcess grand) = SpawnGrandchild(r);
        int killed = 0;
        grand.OnKilled = _ => killed++;

        mid.ReportEnd(LinuxWaitStatus.Exited(0));
        mid.Destroy();
        Assert.AreEqual((long)mid.Pid, r.Wait4(mid.Pid, 0, 0), "Eintrag des Zwischenprozesses ist ausgetragen");
        Assert.AreEqual(-ESRCH, Sys(r.P, KillNr, (ulong)mid.Pid, 0), "der Zwischenprozess selbst ist weg");

        Assert.AreEqual(0L, Sys(r.P, KillNr, (ulong)grand.Pid, 0));
        Assert.AreEqual(0L, Sys(r.P, KillNr, 0, 9), "kill(0) erreicht den Waisen");
        Assert.AreEqual(1, killed);
        Assert.AreEqual(9, grand.TerminatedBySignal);
    }

    [TestMethod]
    public void KillWaehrendVforkMerktAlleSignaleUndSigkillGewinnt()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        LinuxUserProcess child = r.Spawn();
        Assert.AreEqual(0L, Sys(r.P, KillNr, (ulong)child.Pid, 17), "SIGCHLD: Standard ignorieren");
        Assert.AreEqual(0L, Sys(r.P, KillNr, (ulong)child.Pid, 9));
        Assert.IsNull(child.ExitCode);
        Assert.AreEqual(0L, child.Exec("/hello", ["/hello"], []));
        LinuxUserProcess next = r.Exec!;
        Assert.AreEqual(9, next.TerminatedBySignal, "das spätere SIGKILL geht nicht hinter SIGCHLD verloren");
        Assert.AreEqual(137, next.ExitCode);
        child.Release();
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
        Assert.IsFalse(r.P.IdInUse(child.Pid), "PID beim Ernten frei");
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
        child.ReportEnd(LinuxWaitStatus.Exited(3));
        Assert.AreEqual(-ECHILD, r.Wait4(-1, 0, LinuxUserProcess.WNohang), "kein Zombie, schon vor dem Abrechnen");
        child.Release();
        Assert.AreEqual(0, r.P.Children.Length);
        Assert.IsFalse(r.P.IdInUse(child.Pid), "PID frei");
        Assert.AreEqual(-ECHILD, r.Wait4(-1, 0, 0));
    }

    [TestMethod]
    public void KindendeSchicktSigchldMitSiginfoAnDieEltern()
    {
        var r = new Root();
        LinuxUserProcess a = r.Spawn(), b = r.Spawn();
        r.P.Signals.Handler[17] = 0x401000; // Handler (V18)
        r.P.Signals.Flags[17] = LinuxUserProcess.SaSiginfo | LinuxUserProcess.SaRestorer;
        a.ReportEnd(LinuxWaitStatus.Exited(3));
        Assert.IsTrue(r.P.SignalWaiting);
        Assert.IsTrue(r.P.TakeSignal(out LinuxSignalDelivery d));
        Assert.AreEqual((17, 1, a.Pid, 3), (d.Signal, d.Code, d.Pid, d.Status), "CLD_EXITED mit Exitcode");
        Assert.AreEqual(1UL << 16, r.P.MainThread.Blocked, "im Handler blockiert");
        r.P.MainThread.Blocked = 0;
        b.ReportEnd(LinuxWaitStatus.Signaled(9));
        Assert.IsTrue(r.P.TakeSignal(out d));
        Assert.AreEqual((2, b.Pid, 9), (d.Code, d.Pid, d.Status), "CLD_KILLED mit Signal");
        Assert.AreEqual(a.Pid, r.Wait4(a.Pid, 0, 0), "Zombie bleibt für wait4");

        r.P.Signals.Handler[17] = 0; // SIG_DFL, nicht blockiert: ignoriert, nichts anhängig
        r.P.MainThread.Blocked = 0;
        LinuxUserProcess c = r.Spawn();
        c.ReportEnd(LinuxWaitStatus.Exited(0));
        Assert.AreEqual(0UL, r.P.PendingSignals);
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
    public void Wait4KehrtErstNachGeschlossenenDateienDesKindesZurueck()
    {
        // Review V15 P2: Das Ende wird gemeldet, bevor Release/Destroy laufen (Brücke: ReportEnd, dann Aufräumen). Schon nach der Meldung
        // muss das Schreibende zu sein, sonst sähe der Elternprozess nach wait4 kein EOF.
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        Assert.AreEqual(0L, Sys(r.P, Pipe2, r.Buf, ONonblock));
        int rfd = r.ReadInt(r.Buf), wfd = r.ReadInt(r.Buf + 4);

        LinuxUserProcess child = r.Spawn();
        Assert.AreEqual(0L, Sys(r.P, Close, (ulong)wfd), "Eltern schließen ihr Schreibende, das vfork-Kind hält seins");
        child.ReportEnd(LinuxWaitStatus.Exited(0));
        Assert.AreEqual((long)child.Pid, r.Wait4(-1, 0, 0));
        Assert.AreEqual(0L, Sys(r.P, Read, (ulong)rfd, r.Buf + 0x100, 16), "vfork-Kind: EOF direkt nach wait4");
        child.Release();

        // Dasselbe für ein Kind mit eigenem Abbild (ProcessBody: ReportEnd vor Destroy).
        Assert.AreEqual(0L, Sys(r.P, Pipe2, r.Buf, ONonblock));
        rfd = r.ReadInt(r.Buf);
        wfd = r.ReadInt(r.Buf + 4);
        child = r.Spawn();
        Assert.AreEqual(0L, child.Exec("/hello", ["/hello"], []));
        LinuxUserProcess next = r.Exec!;
        child.Release();
        Assert.AreEqual(0L, Sys(r.P, Close, (ulong)wfd));
        next.ReportEnd(LinuxWaitStatus.Exited(0));
        Assert.AreEqual((long)next.Pid, r.Wait4(-1, 0, 0));
        Assert.AreEqual(0L, Sys(r.P, Read, (ulong)rfd, r.Buf + 0x100, 16), "neues Abbild: EOF direkt nach wait4");
        Assert.IsTrue(next.IdInUse(r.P.Pid), "PID des ersten Prozesses bleibt belegt");
        next.Destroy();
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

    private const long KillNr = 62, ESRCH = 3;

    [TestMethod]
    public void KillAufSichSelbstBeendetMitSignal()
    {
        var r = new Root();
        Assert.AreEqual(0L, Sys(r.P, KillNr, RootPid, 0), "Signal 0 prüft nur");
        Assert.AreEqual(-EINVAL, Sys(r.P, KillNr, RootPid, 65));
        Assert.AreEqual(-ESRCH, Sys(r.P, KillNr, 77, 15), "unbekannte PID");
        Assert.AreEqual(-ESRCH, Sys(r.P, KillNr, unchecked((ulong)-77), 15), "fremde Gruppe");
        Assert.IsNull(r.P.ExitCode);
        Assert.AreEqual(0L, Sys(r.P, KillNr, RootPid, 15));
        Assert.AreEqual(143, r.P.ExitCode);
        Assert.AreEqual(15, r.P.TerminatedBySignal);
    }

    [TestMethod]
    public void SignalNachExitGroupLaesstDessenStatus()
    {
        // Risiko-Review 2, P3-9: exit_group(0) kam zuerst; ein Signal mit Standardaktion danach darf den Status nicht auf "signal 15" drehen.
        var r = new Root();
        r.P.Exit(0);
        Assert.AreEqual(0L, Sys(r.P, KillNr, RootPid, 15));
        Assert.AreEqual(0, r.P.ExitCode);
        Assert.IsNull(r.P.TerminatedBySignal);
        r.P.RecordFault(11);
        Assert.IsNull(r.P.TerminatedBySignal, "auch der Fehler eines Geschwisters nach exit_group nicht");
    }

    [TestMethod]
    public void ExitGroupGegenSignalGleichzeitigErgibtEinenStatus()
    {
        // S3-Review P3-1: exit_group(0) und ein Signal mit Standardaktion auf zwei Threads gleichzeitig. Gewinnt einer, gilt sein Status ganz:
        // ExitCode 0 ohne Signal oder 143 mit Signal 15, nie ExitCode 0 zu "signal 15" (getrennte Sperrabschnitte in Terminate und Exit).
        int exitWon = 0, signalWon = 0;
        for (int round = 0; round < 300; round++)
        {
            var r = new Root();
            using var start = new Barrier(2);
            Task exit = Task.Run(() =>
            {
                start.SignalAndWait();
                r.P.Exit(0);
            });
            Task kill = Task.Run(() =>
            {
                start.SignalAndWait();
                Sys(r.P, KillNr, RootPid, 15);
            });
            Task.WaitAll(exit, kill);
            if (r.P.TerminatedBySignal == null)
            {
                Assert.AreEqual(0, r.P.ExitCode, $"Runde {round}");
                exitWon++;
            }
            else
            {
                Assert.AreEqual(15, r.P.TerminatedBySignal, $"Runde {round}");
                Assert.AreEqual(143, r.P.ExitCode, $"Runde {round}: Signal ohne seinen Code");
                signalWon++;
            }
        }

        Assert.AreEqual(300, exitWon + signalWon);
    }

    [TestMethod]
    public void SigactionTauschGleichzeitigLiestNieEineHalbeAktion()
    {
        // S3-Review P3-2: ExchangeSignalAction tauscht Handler, Flags, Restorer und Maske unter _threadGate. Ein Leser auf einem anderen
        // Thread sieht immer alle vier Werte einer Aktion, nie eine Mischung aus alter und neuer.
        var r = new Root();
        const int Rounds = 100_000;
        using var start = new Barrier(2);
        Task writer = Task.Run(() =>
        {
            Span<ulong> action = stackalloc ulong[4];
            start.SignalAndWait();
            for (int i = 0; i < Rounds; i++)
            {
                ulong v = (ulong)(i & 1) + 0x1000;
                action[0] = v;
                action[1] = v;
                action[2] = v;
                action[3] = v;
                r.P.ExchangeSignalAction(10, set: true, action);
            }
        });
        int torn = 0;
        Task reader = Task.Run(() =>
        {
            Span<ulong> action = stackalloc ulong[4];
            start.SignalAndWait();
            for (int i = 0; i < Rounds; i++)
            {
                r.P.ExchangeSignalAction(10, set: false, action);
                if (action[0] != action[1] || action[1] != action[2] || action[2] != action[3])
                {
                    torn++;
                }
            }
        });
        Task.WaitAll(writer, reader);
        Assert.AreEqual(0, torn, "halb geschriebene Aktion gelesen");
        Assert.AreEqual(0x1001UL, r.P.Signals.Handler[10]);
    }

    [TestMethod]
    public void KillNachExecveBeendetDasKindVonAussen()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        LinuxUserProcess child = r.Spawn();
        Assert.AreEqual(0L, child.Exec("/hello", ["/hello"], []));
        LinuxUserProcess next = r.Exec!;
        child.Release();
        int? killed = null;
        bool ownExit = false;
        next.OnKilled = code => killed = code;
        next.OnExit = _ => ownExit = true;
        r.P.OnExit = _ => Assert.Fail("der Aufrufer bleibt");

        Assert.AreEqual(0L, Sys(r.P, KillNr, (ulong)next.Pid, 0));
        Assert.IsNull(killed);
        Assert.AreEqual(0L, Sys(r.P, KillNr, (ulong)next.Pid, 15));
        Assert.AreEqual(143, killed, "Brücke holt die Threads des Kindes");
        Assert.IsFalse(ownExit, "nicht OnExit: das verließe den Ring 3 des Aufrufers");
        Assert.AreEqual(15, next.TerminatedBySignal);
        Assert.IsNull(r.P.ExitCode);

        next.ReportEnd(next.WaitStatusOf(9));
        Assert.AreEqual(0L, Sys(r.P, KillNr, (ulong)next.Pid, 9), "Zombie: gelingt ohne Wirkung");
        Assert.AreEqual((long)next.Pid, r.Wait4(next.Pid, r.Buf, 0));
        Assert.AreEqual(15, LinuxWaitStatus.TermSignal(r.ReadInt(r.Buf)));
        Assert.AreEqual(-ESRCH, Sys(r.P, KillNr, (ulong)next.Pid, 9), "geerntet");
    }

    [TestMethod]
    public void KillAufEinEndendesKindLaesstDenExitstatus()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        LinuxUserProcess child = r.Spawn();
        Assert.AreEqual(0L, child.Exec("/hello", ["/hello"], []));
        LinuxUserProcess next = r.Exec!;
        child.Release();
        bool killed = false;
        next.OnExit = _ => { };
        next.OnKilled = _ => killed = true;
        Sys(next, LinuxSyscalls.ExitGroup, 3);
        Assert.AreEqual(0L, Sys(r.P, KillNr, (ulong)next.Pid, 9), "läuft noch ab, also ein Ziel");
        Assert.IsFalse(killed);
        Assert.IsNull(next.TerminatedBySignal);
        Assert.AreEqual(LinuxWaitStatus.Exited(3), next.WaitStatusOf(9));
    }

    [TestMethod]
    public void KillWaehrendVforkTrifftDasNeueAbbild()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        LinuxUserProcess child = r.Spawn();
        child.OnExit = _ => Assert.Fail("vfork-Kind läuft auf dem Kernelthread des Elternprozesses");
        Assert.AreEqual(0L, Sys(r.P, KillNr, (ulong)child.Pid, 9));
        Assert.IsNull(child.ExitCode, "gemerkt, nicht zugestellt");
        Assert.AreEqual(0L, child.Exec("/hello", ["/hello"], []));
        LinuxUserProcess next = r.Exec!;
        Assert.AreEqual(9, next.TerminatedBySignal, "execve stellt zu");
        Assert.AreEqual(0L, Sys(r.P, KillNr, (ulong)child.Pid, 0), "die PID gehört jetzt dem Abbild");
        child.Release();
        next.ReportEnd(next.WaitStatusOf(9));
        Assert.AreEqual((long)next.Pid, r.Wait4(-1, 0, 0));
    }

    [TestMethod]
    public void KillAufDieGruppeTrifftAlleKinderUndZuletztDenAufrufer()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        var ends = new List<string>();
        LinuxUserProcess[] kids = new LinuxUserProcess[2];
        for (int i = 0; i < 2; i++)
        {
            LinuxUserProcess c = r.Spawn();
            Assert.AreEqual(0L, c.Exec("/hello", ["/hello"], []));
            c.Release();
            kids[i] = r.Exec!;
            int n = i;
            kids[i].OnKilled = _ => ends.Add("kind" + n);
        }

        r.P.OnExit = _ => ends.Add("ich");
        Assert.AreEqual(0L, Sys(kids[0], KillNr, unchecked((ulong)-1), 0), "-1: das andere Kind und nicht der erste Prozess");
        Assert.AreEqual(0L, Sys(r.P, KillNr, 0, 15));
        Assert.AreEqual("kind0 kind1 ich", string.Join(" ", ends));
    }

    // V35: Prozessgruppen und Sitzungen (setpgid, getpgid, setsid; kill und wait4 nach Gruppe).
    private const long EPERM = 1, EACCES = 13;

    private static long Setpgid(LinuxUserProcess p, int pid, int pgid) => Sys(p, LinuxSyscalls.Setpgid, unchecked((ulong)pid), unchecked((ulong)pgid));

    /// <summary>posix_spawn mit execve: neues Abbild als Kind des ersten Prozesses.</summary>
    private static LinuxUserProcess SpawnExec(Root r)
    {
        LinuxUserProcess c = r.Spawn();
        Assert.AreEqual(0L, c.Exec("/hello", ["/hello"], []));
        c.Release();
        return r.Exec!;
    }

    [TestMethod]
    public void VforkKindGruendetMitSetpgidNullNullEineNeueGruppe()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        LinuxUserProcess child = r.Spawn();
        Assert.AreEqual((long)RootPid, Sys(child, LinuxSyscalls.Getpgrp), "geerbt");
        Assert.AreEqual(0L, Setpgid(child, 0, 0), "Hub-Kind: setpgid(0,0) im vfork-Kind");
        Assert.AreEqual((long)child.Pid, Sys(child, LinuxSyscalls.Getpgrp));
        Assert.AreEqual((long)child.Pid, Sys(r.P, LinuxSyscalls.Getpgid, (ulong)child.Pid), "der Elternprozess sieht die Gruppe");
        Assert.AreEqual((long)RootPid, Sys(child, LinuxSyscalls.Getsid, 0), "gleiche Sitzung");
        Assert.AreEqual((long)RootPid, Sys(r.P, LinuxSyscalls.Getpgrp), "die Eltern bleiben");

        Assert.AreEqual(0L, child.Exec("/hello", ["/hello"], []));
        LinuxUserProcess next = r.Exec!;
        Assert.AreEqual(-EACCES, Setpgid(r.P, child.Pid, RootPid), "execve abgeschlossen, das Kind trägt das neue Abbild");
        child.Release();
        Assert.AreEqual(child.Pid, next.ProcessGroup, "execve behält die Gruppe");
        Assert.AreEqual(-EACCES, Setpgid(r.P, next.Pid, RootPid), "nach execve darf der Elternprozess nicht mehr");
        Assert.AreEqual(0L, Setpgid(next, 0, RootPid), "der Prozess selbst schon, in eine Gruppe der Sitzung");
        Assert.AreEqual(RootPid, next.ProcessGroup);
        Assert.AreEqual(-EPERM, Setpgid(r.P, 0, 0), "erster Prozess ist Sitzungsführer");
    }

    [TestMethod]
    public void SetpgidPrueftZielUndGruppeWieLinux()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        LinuxUserProcess a = r.Spawn();
        Assert.AreEqual(0L, Setpgid(r.P, a.Pid, 0), "Eltern setzen die Gruppe des Kindes vor execve (pgid 0 = PID des Ziels)");
        Assert.AreEqual(0L, a.Exec("/hello", ["/hello"], []));
        a.Release();
        LinuxUserProcess leader = r.Exec!;
        LinuxUserProcess b = r.Spawn();

        Assert.AreEqual(-EINVAL, Setpgid(r.P, b.Pid, -1));
        Assert.AreEqual(-ESRCH, Setpgid(r.P, 4242, 0), "unbekannt");
        Assert.AreEqual(-ESRCH, Setpgid(b, RootPid, 0), "der Elternprozess ist kein eigenes Kind");
        Assert.AreEqual(-ESRCH, Setpgid(b, leader.Pid, 0), "Geschwister ebenso");
        Assert.AreEqual(-EPERM, Setpgid(r.P, b.Pid, 4242), "Gruppe gibt es nicht");
        Assert.AreEqual(0L, Setpgid(r.P, b.Pid, leader.Pid), "bestehende Gruppe in derselben Sitzung");
        Assert.AreEqual(leader.Pid, b.ProcessGroup);
        Assert.AreEqual(0L, Setpgid(b, 0, RootPid), "zurück in die Gruppe des ersten Prozesses");

        Assert.AreEqual((long)b.Pid, Sys(b, LinuxSyscalls.Setsid), "kein Gruppenführer: neue Sitzung");
        Assert.AreEqual((long)b.Pid, Sys(r.P, LinuxSyscalls.Getsid, (ulong)b.Pid));
        Assert.AreEqual((long)b.Pid, Sys(b, LinuxSyscalls.Getpgrp));
        Assert.AreEqual(-EPERM, Setpgid(r.P, b.Pid, b.Pid), "andere Sitzung");
        Assert.AreEqual(-EPERM, Setpgid(b, 0, 0), "Sitzungsführer");
        Assert.AreEqual(-EPERM, Sys(b, LinuxSyscalls.Setsid), "schon Gruppenführer");
        Assert.AreEqual(-EPERM, Setpgid(leader, 0, b.Pid), "Gruppe einer fremden Sitzung");
        Assert.AreEqual(-EPERM, Sys(leader, LinuxSyscalls.Setsid), "führt seine Gruppe");
        Root.End(b, LinuxWaitStatus.Exited(0));
    }

    [TestMethod]
    public void KillUndWait4MitNegativerGruppeTreffenNurDieGruppe()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        LinuxUserProcess c1 = r.Spawn();
        Assert.AreEqual(0L, Setpgid(c1, 0, 0));
        Assert.AreEqual(0L, c1.Exec("/hello", ["/hello"], []));
        c1.Release();
        LinuxUserProcess g1 = r.Exec!;
        LinuxUserProcess other = SpawnExec(r); // bleibt in der Gruppe des ersten Prozesses
        var hit = new List<string>();
        g1.OnKilled = _ => hit.Add("g1");
        other.OnKilled = _ => hit.Add("other");
        r.P.OnExit = _ => Assert.Fail("der Aufrufer steht nicht in der Gruppe");

        Assert.AreEqual(0L, Sys(r.P, KillNr, unchecked((ulong)-g1.Pid), 15));
        Assert.AreEqual("g1", string.Join(" ", hit));
        Assert.AreEqual(-ESRCH, Sys(r.P, KillNr, unchecked((ulong)-4242), 15), "leere Gruppe");

        Assert.AreEqual(0L, r.Wait4(-g1.Pid, 0, LinuxUserProcess.WNohang), "Gruppe hat ein laufendes Kind");
        g1.ReportEnd(g1.WaitStatusOf(9));
        Assert.AreEqual(0L, Sys(r.P, KillNr, unchecked((ulong)-g1.Pid), 0), "Zombie der Gruppe: gelingt ohne Wirkung");
        Assert.AreEqual((long)g1.Pid, r.Wait4(-g1.Pid, 0, LinuxUserProcess.WNohang));
        Assert.AreEqual(-ECHILD, r.Wait4(-g1.Pid, 0, LinuxUserProcess.WNohang), "Gruppe leer");
        Assert.AreEqual(0L, r.Wait4(0, 0, LinuxUserProcess.WNohang), "eigene Gruppe: other läuft");
        Assert.AreEqual(-ESRCH, Sys(r.P, KillNr, unchecked((ulong)-g1.Pid), 0), "geerntet: Gruppe weg");

        r.P.OnExit = _ => hit.Add("ich");
        Assert.AreEqual(0L, Sys(r.P, KillNr, 0, 15), "kill(0): eigene Gruppe, Aufrufer zuletzt");
        Assert.AreEqual("g1 other ich", string.Join(" ", hit));
    }

    [TestMethod]
    public void ZombieHaeltSeineGruppeFuerSetpgid()
    {
        var r = new Root();
        WriteFile(r.Fs, "/hello", LinuxSpawnTests.Elf());
        LinuxUserProcess c1 = r.Spawn();
        Assert.AreEqual(0L, Setpgid(c1, 0, 0));
        Assert.AreEqual(0L, c1.Exec("/hello", ["/hello"], []));
        c1.Release();
        LinuxUserProcess g1 = r.Exec!;
        g1.ReportEnd(LinuxWaitStatus.Exited(0));
        LinuxUserProcess c2 = r.Spawn();
        Assert.AreEqual(0L, Setpgid(c2, 0, g1.Pid), "Linux: die Gruppe besteht, bis der Zombie ausgetragen ist");
        Assert.AreEqual((long)g1.Pid, Sys(r.P, LinuxSyscalls.Getpgid, (ulong)g1.Pid), "getpgid findet den Zombie");
        Assert.AreEqual(0L, Setpgid(c2, 0, RootPid));
        Assert.AreEqual((long)g1.Pid, r.Wait4(g1.Pid, 0, 0));
        Assert.AreEqual(-EPERM, Setpgid(c2, 0, g1.Pid), "ausgetragen: Gruppe weg");
        Root.End(c2, LinuxWaitStatus.Exited(0));
    }
}
