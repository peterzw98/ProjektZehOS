using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;
using ProjektZeh.Shell;

namespace ProjektZeh.Tests.Processes;

/// <summary>Ladybird V10: Threads eines Linux-Prozesses (clone, futex mehrfädig, exit gegen exit_group, gettid, tgkill).</summary>
[TestClass]
public sealed class LinuxThreadsTests
{
    private const ulong Page = 4096;
    private const int Pid = 7;

    // Flags von musl pthread_create (glibc gleich, ohne CLONE_DETACHED).
    private const ulong Musl = LinuxUserProcess.CloneThreadSet | LinuxUserProcess.CloneSettls | LinuxUserProcess.CloneParentSettid
        | LinuxUserProcess.CloneChildCleartid | LinuxUserProcess.CloneDetached;

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
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public long RealtimeNanos => 1_000_000_000_000L + MonotonicNanos;
        public long MonotonicNanos => _clock.Elapsed.Ticks * 100;
        public bool TryFillRandom(Span<byte> dst) => false;
        public void SleepNanos(long ns) => Thread.Sleep((int)(ns / 1_000_000));
    }

    private sealed class Lines : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }

    /// <summary>Prozess mit 64 KiB anonymem Speicher; der aktuelle Thread kommt aus einem ThreadLocal wie im Kernel aus dem Kernelthread.</summary>
    private sealed class Fixture
    {
        public readonly LinuxUserProcess P;
        public readonly ulong Mem;
        public readonly List<LinuxThread> Started = new();
        public readonly List<(int Tid, int Code)> ThreadExits = new();
        public readonly List<int> ProcessExits = new();
        // Wie im Kernel der Kernelthread: je .NET-Thread der Linux-Thread, als der er gerade läuft.
        [ThreadStatic]
        private static LinuxThread? current;

        public static LinuxThread? Current { get => current; set => current = value; }
        public bool Refuse;

        public Fixture()
        {
            P = new LinuxUserProcess(new Pages(), new TerminalDescriptor(new Lines()), Pid, 16 << 20, new Host());
            P.CurrentThreadSource = () => current ?? P.MainThread;
            P.StartThread = t => { if (Refuse) return false; lock (Started) Started.Add(t); return true; };
            P.OnThreadExit = (t, c) => { lock (ThreadExits) ThreadExits.Add((t.Tid, c)); };
            P.OnExit = c => { lock (ProcessExits) ProcessExits.Add(c); };
            Mem = P.MapAnonymous(16 * Page, 3);
            Assert.AreNotEqual(0UL, Mem);
        }

        public long Call(long n, ulong a0 = 0, ulong a1 = 0, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0)
        {
            var a = new SyscallArgs { Number = n, A0 = a0, A1 = a1, A2 = a2, A3 = a3, A4 = a4, A5 = a5 };
            return LinuxSyscalls.Dispatch(ref a, P);
        }

        public int Read32(ulong addr)
        {
            Span<byte> b = stackalloc byte[4];
            Assert.IsTrue(P.TryRead(addr, b));
            return BitConverter.ToInt32(b);
        }

        public long Read64(ulong addr)
        {
            Span<byte> b = stackalloc byte[8];
            Assert.IsTrue(P.TryRead(addr, b));
            return BitConverter.ToInt64(b);
        }

        public void Write32(ulong addr, int value)
        {
            Assert.IsTrue(P.TryWrite(addr, BitConverter.GetBytes(value)));
        }

        public void WriteTimespec(ulong addr, long sec, long nsec)
        {
            Assert.IsTrue(P.TryWrite(addr, BitConverter.GetBytes(sec)));
            Assert.IsTrue(P.TryWrite(addr + 8, BitConverter.GetBytes(nsec)));
        }

        /// <summary>Wie musl: clone mit Stack, ptid, ctid, tls; liefert den gestarteten Thread.</summary>
        public LinuxThread Clone(ulong ptid, ulong ctid, ulong tls = 0x7000_0000)
        {
            long tid = Call(LinuxSyscalls.Clone, Musl, Mem + 8 * Page, ptid, ctid, tls);
            Assert.IsTrue(tid > 0, "clone " + tid);
            return Started[^1];
        }

        /// <summary>Führt <paramref name="body"/> auf einem echten Thread als <paramref name="t"/> aus.</summary>
        public static Thread RunAs(LinuxThread t, Action body)
        {
            var th = new Thread(() => { Current = t; body(); }) { IsBackground = true };
            th.Start();
            return th;
        }
    }

    private const int FutexWait = 0, FutexWake = 1, FutexCmpRequeue = 4, FutexWaitBitset = 9, Private = 128;

    [TestMethod]
    public void CloneLegtThreadMitTidTlsUndAdressenAn()
    {
        var f = new Fixture();
        ulong ptid = f.Mem, ctid = f.Mem + 4;
        long tid = f.Call(LinuxSyscalls.Clone, Musl, f.Mem + 8 * Page, ptid, ctid, 0x7000_0000);
        Assert.AreEqual(Pid + 1, tid, "TIDs zaehlen ab der PID");
        Assert.AreEqual(Pid + 1, f.Read32(ptid), "CLONE_PARENT_SETTID");
        Assert.AreEqual(0, f.Read32(ctid), "ohne CLONE_CHILD_SETTID unveraendert");
        LinuxThread child = f.Started[0];
        Assert.AreEqual(0x7000_0000UL, child.FsBase);
        Assert.AreEqual(ctid, child.ClearChildTid);
        Assert.AreEqual(f.Mem + 8 * Page, child.StackPointer);
        Assert.AreEqual(2, f.P.LiveThreads);

        Fixture.Current = child;
        Assert.AreEqual(Pid + 1, f.Call(LinuxSyscalls.Gettid));
        Assert.AreEqual(Pid, f.Call(LinuxSyscalls.Getpid), "getpid ist die Gruppe");
        Assert.AreEqual(Pid + 1, f.Call(LinuxSyscalls.SetTidAddress, ctid + 4));
        Assert.AreEqual(ctid + 4, child.ClearChildTid);
        Assert.AreEqual(0x7000_0000UL, f.P.FsBase, "FS-Basis je Thread");
        Fixture.Current = null;
        Assert.AreEqual(Pid, f.Call(LinuxSyscalls.Gettid));
        Assert.AreEqual(0UL, f.P.FsBase);
    }

    [TestMethod]
    public void CloneOhneThreadBitsOderOhneMittelScheitert()
    {
        var f = new Fixture();
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, f.Call(LinuxSyscalls.Clone, 17, 0, 0, 0, 0), "fork (SIGCHLD) gibt es nicht");
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, f.Call(LinuxSyscalls.Clone, LinuxUserProcess.CloneVm | LinuxUserProcess.CloneVfork | 17, f.Mem), "vfork/posix_spawn");
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.Clone, LinuxUserProcess.CloneVm | LinuxUserProcess.CloneThreadFlag, f.Mem), "THREAD ohne SIGHAND");
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.Clone, LinuxUserProcess.CloneSighand, f.Mem), "SIGHAND ohne VM");
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, f.Call(LinuxSyscalls.Clone, Musl | 0x2000_0000UL, f.Mem), "unbekanntes Bit (CLONE_NEWCGROUP)");
        f.Refuse = true;
        Assert.AreEqual(-LinuxSyscalls.EAGAIN, f.Call(LinuxSyscalls.Clone, Musl, f.Mem, f.Mem, f.Mem + 4, 0));
        Assert.AreEqual(1, f.P.LiveThreads);
        Assert.AreEqual(0, f.Started.Count);
    }

    [TestMethod]
    public void HoechstensMaxThreads()
    {
        var f = new Fixture();
        for (int i = 1; i < LinuxUserProcess.MaxThreads; i++)
        {
            f.Clone(0, 0);
        }

        Assert.AreEqual(-LinuxSyscalls.EAGAIN, f.Call(LinuxSyscalls.Clone, Musl, f.Mem, 0, 0, 0));
        Assert.AreEqual(LinuxUserProcess.MaxThreads, f.P.LiveThreads);
    }

    [TestMethod]
    public void FutexWaitSchlaeftBisEinAndererThreadWeckt()
    {
        var f = new Fixture();
        ulong word = f.Mem + 0x100;
        f.Write32(word, 5);
        LinuxThread child = f.Clone(0, 0);
        long result = 1;
        Thread waiter = Fixture.RunAs(child, () => result = f.Call(LinuxSyscalls.Futex, word, FutexWait | Private, 5));
        SpinUntil(() => f.P.Futexes.WaitingCount == 1);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Futex, word + 4, FutexWake | Private, 1), "andere Adresse weckt nicht");
        Thread.Sleep(20);
        Assert.IsTrue(waiter.IsAlive);
        f.Write32(word, 6);
        Assert.AreEqual(1, f.Call(LinuxSyscalls.Futex, word, FutexWake | Private, int.MaxValue));
        Assert.IsTrue(waiter.Join(5000));
        Assert.AreEqual(0, result);
        Assert.AreEqual(0, f.P.Futexes.WaitingCount);
    }

    [TestMethod]
    public void FutexWaitMitAnderemWertFristUndBitset()
    {
        var f = new Fixture();
        ulong word = f.Mem + 0x100, ts = f.Mem + 0x200;
        f.Write32(word, 1);
        Assert.AreEqual(-LinuxSyscalls.EAGAIN, f.Call(LinuxSyscalls.Futex, word, FutexWait, 2));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.Futex, word + 1, FutexWait, 1));
        f.WriteTimespec(ts, 0, 30_000_000);
        var sw = Stopwatch.StartNew();
        Assert.AreEqual(-LinuxSyscalls.ETIMEDOUT, f.Call(LinuxSyscalls.Futex, word, FutexWait, 1, ts));
        Assert.IsTrue(sw.ElapsedMilliseconds >= 25, "schlaeft die Frist wirklich: " + sw.ElapsedMilliseconds);
        f.WriteTimespec(ts, 0, -1);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.Futex, word, FutexWait, 1, ts));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.Futex, word, FutexWaitBitset, 1, 0, 0, 0), "Bitset 0");

        // WAIT_BITSET mit absoluter Frist in der Vergangenheit kehrt sofort zurück.
        f.WriteTimespec(ts, 0, 1);
        Assert.AreEqual(-LinuxSyscalls.ETIMEDOUT, f.Call(LinuxSyscalls.Futex, word, FutexWaitBitset, 1, ts, 0, uint.MaxValue));

        // Wecken nur bei passendem Bitset.
        LinuxThread child = f.Clone(0, 0);
        long result = 1;
        Thread waiter = Fixture.RunAs(child, () => result = f.Call(LinuxSyscalls.Futex, word, FutexWaitBitset, 1, 0, 0, 0b01));
        SpinUntil(() => f.P.Futexes.WaitingCount == 1);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Futex, word, 10, 1, 0, 0, 0b10));
        Assert.AreEqual(1, f.Call(LinuxSyscalls.Futex, word, 10, 1, 0, 0, 0b11));
        Assert.IsTrue(waiter.Join(5000));
        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public void CmpRequeueWecktEinenUndHaengtDenRestUm()
    {
        var f = new Fixture();
        ulong a = f.Mem + 0x100, b = f.Mem + 0x104;
        f.Write32(a, 3);
        var results = new long[2];
        var threads = new Thread[2];
        for (int i = 0; i < 2; i++)
        {
            int k = i;
            threads[i] = Fixture.RunAs(f.Clone(0, 0), () => results[k] = f.Call(LinuxSyscalls.Futex, a, FutexWait, 3));
            SpinUntil(() => f.P.Futexes.WaitingCount == k + 1);
        }

        Assert.AreEqual(-LinuxSyscalls.EAGAIN, f.Call(LinuxSyscalls.Futex, a, FutexCmpRequeue, 1, 1, b, 4), "Wert passt nicht");
        Assert.AreEqual(2, f.Call(LinuxSyscalls.Futex, a, FutexCmpRequeue, 1, 1, b, 3), "einer geweckt, einer umgehaengt");
        SpinUntil(() => f.P.Futexes.WaitingCount == 1);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Futex, a, FutexWake, 1), "auf a wartet keiner mehr");
        Assert.AreEqual(1, f.Call(LinuxSyscalls.Futex, b, FutexWake, 1));
        Assert.IsTrue(threads[0].Join(5000) && threads[1].Join(5000));
        Assert.AreEqual("0,0", string.Join(",", results));
    }

    [TestMethod]
    public void ThreadendeLoeschtCtidUndWecktJoin()
    {
        var f = new Fixture();
        ulong ptid = f.Mem, ctid = f.Mem + 4;
        LinuxThread child = f.Clone(ptid, ctid);

        // pthread_join (musl): wartet auf ctid, solange dort die TID steht. musl schreibt sie selbst über CLONE_PARENT_SETTID an dieselbe Stelle.
        f.Write32(ctid, child.Tid);
        long joined = 1;
        Thread joiner = Fixture.RunAs(f.P.MainThread, () => joined = f.Call(LinuxSyscalls.Futex, ctid, FutexWait, (ulong)child.Tid));
        SpinUntil(() => f.P.Futexes.WaitingCount == 1);
        Fixture.RunAs(child, () => f.Call(LinuxSyscalls.Exit, 3)).Join();
        Assert.IsTrue(joiner.Join(5000));
        Assert.AreEqual(0, joined);
        Assert.AreEqual(0, f.Read32(ctid), "CLONE_CHILD_CLEARTID");
        Assert.AreEqual((child.Tid, 3), f.ThreadExits[0]);
        Assert.AreEqual(1, f.ThreadExits.Count);
        Assert.AreEqual(0, f.ProcessExits.Count, "exit beendet nur den Thread");
        Assert.IsNull(f.P.ExitCode);
        Assert.AreEqual(1, f.P.LiveThreads);
        Assert.AreEqual(-LinuxSyscalls.ESRCH, f.Call(LinuxSyscalls.Tgkill, Pid, (ulong)child.Tid, 0), "beendeter Thread");
    }

    [TestMethod]
    public void LetzterThreadBeendetDenProzessMitSeinemCode()
    {
        var f = new Fixture();
        LinuxThread child = f.Clone(0, 0);
        f.Call(LinuxSyscalls.Exit, 0); // Hauptthread zuerst (pthread_exit in main)
        Assert.AreEqual((Pid, 0), f.ThreadExits[0]);
        Assert.AreEqual(1, f.ThreadExits.Count);
        Assert.IsNull(f.P.ExitCode);
        Fixture.Current = child;
        f.Call(LinuxSyscalls.Exit, 4);
        Assert.AreEqual(4, f.P.ExitCode);
        Assert.AreEqual("4", string.Join(",", f.ProcessExits));
    }

    [TestMethod]
    public void ExitGroupBeendetAlleUndWecktWartendeMitEintr()
    {
        var f = new Fixture();
        ulong word = f.Mem + 0x100;
        LinuxThread child = f.Clone(0, 0);
        long result = 1;
        Thread waiter = Fixture.RunAs(child, () => result = f.Call(LinuxSyscalls.Futex, word, FutexWait, 0));
        SpinUntil(() => f.P.Futexes.WaitingCount == 1);
        f.Call(LinuxSyscalls.ExitGroup, 9);
        Assert.IsTrue(waiter.Join(5000));
        Assert.AreEqual(-LinuxSyscalls.EINTR, result);
        Assert.AreEqual(9, f.P.ExitCode);
        Assert.AreEqual("9", string.Join(",", f.ProcessExits));
        Assert.AreEqual(2, f.P.RunningThreads().Length, "die Bruecke beendet die Kernelthreads");
        Assert.AreEqual(-LinuxSyscalls.EAGAIN, f.Call(LinuxSyscalls.Clone, Musl, f.Mem, 0, 0, 0), "kein neuer Thread nach exit_group");
        Assert.AreEqual(-LinuxSyscalls.EINTR, f.Call(LinuxSyscalls.Futex, word, FutexWait, 0));
    }

    [TestMethod]
    public void TgkillPrueftZielUndFolgtDerStandardaktion()
    {
        var f = new Fixture();
        LinuxThread child = f.Clone(0, 0);
        ulong tid = (ulong)child.Tid;
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 0));
        Assert.AreEqual(-LinuxSyscalls.ESRCH, f.Call(LinuxSyscalls.Tgkill, Pid + 5UL, tid, 0), "falsche Gruppe");
        Assert.AreEqual(-LinuxSyscalls.ESRCH, f.Call(LinuxSyscalls.Tgkill, Pid, 99, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.Tgkill, 0, tid, 0));
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 65));
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tkill, tid, 0));
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 17), "SIGCHLD wird ignoriert");
        f.P.Signals.Handler[10] = 0x401000;
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 10), "Handler: anhängig für die Brücke (V18)");
        Assert.AreEqual(1UL << 9, f.P.PendingSignals);
        Assert.IsFalse(f.P.SignalWaiting, "V38: an den anderen Thread gerichtet, der Hauptthread nimmt es nicht");
        Fixture.Current = child;
        Assert.IsTrue(f.P.SignalWaiting, "der Zielthread sieht es");
        Fixture.Current = null;
        Assert.IsNull(f.P.ExitCode);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 15));
        Assert.AreEqual(15, f.P.TerminatedBySignal);
        Assert.AreEqual(128 + 15, f.P.ExitCode);
    }

    [TestMethod]
    public void TidVergabeLaeuftUmUndUeberspringtPidUndBelegte()
    {
        var used = new HashSet<int> { 3, 5 };
        Assert.AreEqual(4, LinuxUserProcess.NextTid(2, 2, used.Contains), "3 belegt");
        Assert.AreEqual(1, LinuxUserProcess.NextTid(LinuxUserProcess.TidMax, 7, used.Contains), "nach TidMax wieder ab 1");
        Assert.AreEqual(2, LinuxUserProcess.NextTid(LinuxUserProcess.TidMax, 1, used.Contains), "PID 1 uebersprungen");
        Assert.AreEqual(6, LinuxUserProcess.NextTid(4, 1, used.Contains));
        Assert.AreEqual(1, LinuxUserProcess.NextTid(-5, 9, used.Contains), "nie negativ");
    }

    [TestMethod]
    public void BeendeteThreadsGebenIhreTidFrei()
    {
        var f = new Fixture();
        for (int i = 0; i < 3 * LinuxUserProcess.MaxThreads; i++)
        {
            LinuxThread child = f.Clone(0, 0);
            Assert.AreEqual(Pid + 1 + i, child.Tid, "TIDs zaehlen weiter, nicht sofort wiederverwendet");
            Fixture.Current = child;
            f.Call(LinuxSyscalls.Exit, 0);
            Fixture.Current = null;
        }

        Assert.AreEqual(1, f.P.LiveThreads);
        Assert.AreEqual(-LinuxSyscalls.ESRCH, f.Call(LinuxSyscalls.Tgkill, Pid, Pid + 1, 0), "beendeter Thread bleibt weg");
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, Pid, 0), "Hauptthread");
    }

    private static void SetMask(Fixture f, int how, ulong set)
    {
        Assert.IsTrue(f.P.TryWrite(f.Mem + 0x300, BitConverter.GetBytes(set)));
        Assert.AreEqual(0, f.Call(LinuxSyscalls.RtSigprocmask, (ulong)how, f.Mem + 0x300, 0, 8));
    }

    private static void SetHandler(Fixture f, int sig, ulong handler)
    {
        Assert.IsTrue(f.P.TryWrite(f.Mem + 0x400, new byte[32]));
        Assert.IsTrue(f.P.TryWrite(f.Mem + 0x400, BitConverter.GetBytes(handler)));
        Assert.AreEqual(0, f.Call(LinuxSyscalls.RtSigaction, (ulong)sig, f.Mem + 0x400, 0, 8));
    }

    [TestMethod]
    public void TgkillBlockiertesSignalBleibtAnhaengigBisZurFreigabe()
    {
        // V38: Maske je Thread; der Zielthread blockiert selbst (der Hauptthread nicht, er nimmt das an den Zielthread gerichtete nicht).
        var f = new Fixture();
        LinuxThread child = f.Clone(0, 0);
        ulong tid = (ulong)child.Tid;
        try
        {
            Fixture.Current = child;
            SetMask(f, 0, (1UL << 14) | (1UL << 9)); // SIG_BLOCK SIGTERM, SIGUSR1
            Assert.AreEqual(0UL, f.P.MainThread.Blocked, "Hauptthread unberührt");
            Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 15));
            Assert.IsNull(f.P.ExitCode, "blockiert: nicht beendet");
            Assert.AreEqual(1UL << 14, f.P.PendingSignals);
            SetMask(f, 1, 1UL << 9); // SIGUSR1 frei, SIGTERM bleibt blockiert
            Assert.IsNull(f.P.ExitCode);
            SetMask(f, 1, 1UL << 14); // SIG_UNBLOCK SIGTERM
            Assert.AreEqual(15, f.P.TerminatedBySignal);
            Assert.AreEqual(128 + 15, f.P.ExitCode);
            Assert.AreEqual(0UL, f.P.PendingSignals);
        }
        finally
        {
            Fixture.Current = null;
        }
    }

    [TestMethod]
    public void AnhaengigesSignalFaelltMitSigIgnWegHandlerBleibtAnhaengig()
    {
        // V38: tgkill an den Hauptthread, dessen Maske SetMask setzt (Maske je Thread).
        var f = new Fixture();
        _ = f.Clone(0, 0);
        const ulong tid = (ulong)Pid;
        SetMask(f, 2, 1UL << 9); // SIG_SETMASK SIGUSR1
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 10));
        Assert.AreEqual(1UL << 9, f.P.PendingSignals);
        SetHandler(f, 10, 1); // SIG_IGN verwirft das anhaengige Signal
        Assert.AreEqual(0UL, f.P.PendingSignals);
        SetMask(f, 2, 0);
        Assert.IsNull(f.P.ExitCode);

        SetMask(f, 2, 1UL << 11);
        SetHandler(f, 12, 0x401000);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 12), "Handler, blockiert: anhängig");
        Assert.AreEqual(1UL << 11, f.P.PendingSignals);
        Assert.IsFalse(f.P.SignalWaiting, "blockiert wartet nicht");
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 9), "SIGKILL ist nie blockiert");
        Assert.AreEqual(9, f.P.TerminatedBySignal);
    }

    [TestMethod]
    public void StoppsignaleWerdenInVerwaisterGruppeVerworfenSigstopBeendet()
    {
        var f = new Fixture();
        ulong tid = (ulong)f.Clone(0, 0).Tid;
        foreach (ulong sig in new ulong[] { 20, 21, 22 })
        {
            Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, sig), "Signal " + sig);
        }

        Assert.IsNull(f.P.ExitCode, "SIGTSTP/TTIN/TTOU halten nicht an und beenden nicht");
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 19));
        Assert.AreEqual(19, f.P.TerminatedBySignal, "SIGSTOP: kein Anhalten, also beenden");
    }

    [TestMethod]
    public void NanosleepSchlaeftUndExitGroupWecktSchlaeferUndPause()
    {
        var f = new Fixture();
        ulong ts = f.Mem + 0x200;
        f.WriteTimespec(ts, 0, 30_000_000);
        var sw = Stopwatch.StartNew();
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Nanosleep, ts));
        Assert.IsTrue(sw.ElapsedMilliseconds >= 25, "nanosleep " + sw.ElapsedMilliseconds);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.ClockNanosleep, 3, 0, ts), "Thread-CPU-Uhr");
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.ClockNanosleep, 1, 2, ts), "unbekanntes Flag");
        f.WriteTimespec(ts, 0, 1);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.ClockNanosleep, 1, 1, ts), "absolut in der Vergangenheit");
        f.WriteTimespec(ts, 0, -1);
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.Nanosleep, ts));

        f.WriteTimespec(ts, 3600, 0);
        long slept = 1, paused = 1;
        Thread a = Fixture.RunAs(f.Clone(0, 0), () => slept = f.Call(LinuxSyscalls.Nanosleep, ts));
        Thread b = Fixture.RunAs(f.Clone(0, 0), () => paused = f.Call(LinuxSyscalls.Pause));
        Thread.Sleep(30);
        f.Call(LinuxSyscalls.ExitGroup, 0);
        Assert.IsTrue(a.Join(5000) && b.Join(5000));
        Assert.AreEqual(-LinuxSyscalls.EINTR, slept);
        Assert.AreEqual(-LinuxSyscalls.EINTR, paused);
    }

    [TestMethod]
    public void SpeicherBleibtBeiGleichzeitigenThreadsKonsistent()
    {
        // Mehrere Threads belegen, beschreiben und geben frei; die Speichersperre hält Karte und Frames zusammen.
        var f = new Fixture();
        var threads = new List<Thread>();
        int failures = 0;
        for (int i = 0; i < 4; i++)
        {
            int k = i;
            threads.Add(Fixture.RunAs(f.Clone(0, 0), () =>
            {
                for (int j = 0; j < 200; j++)
                {
                    ulong m = f.P.MapAnonymous(2 * Page, 3);
                    if (m == 0 || !f.P.TryWrite(m + Page, BitConverter.GetBytes(k * 1000 + j)) || !f.P.Unmap(m, 2 * Page))
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            }));
        }

        foreach (Thread t in threads)
        {
            Assert.IsTrue(t.Join(20000));
        }

        Assert.AreEqual(0, failures);
    }

    private static void SpinUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.IsTrue(sw.ElapsedMilliseconds < 5000, "Bedingung nicht erreicht");
            Thread.Sleep(1);
        }
    }

    // ---- V18 Signalzustellung (LinuxSignals.cs) ----

    private const ulong Restorer = 0x401100, HandlerAddr = 0x401000;

    private static void SetAction(Fixture f, int sig, ulong handler, ulong flags, ulong mask = 0, ulong restorer = Restorer)
    {
        var b = new byte[32];
        BitConverter.GetBytes(handler).CopyTo(b, 0);
        BitConverter.GetBytes(flags | LinuxUserProcess.SaRestorer).CopyTo(b, 8);
        BitConverter.GetBytes(restorer).CopyTo(b, 16);
        BitConverter.GetBytes(mask).CopyTo(b, 24);
        Assert.IsTrue(f.P.TryWrite(f.Mem + 0x400, b));
        Assert.AreEqual(0, f.Call(LinuxSyscalls.RtSigaction, (ulong)sig, f.Mem + 0x400, 0, 8));
    }

    private static LinuxUserRegs Regs(Fixture f) => new()
    {
        R8 = 8, R9 = 9, R10 = 10, R11 = 0x246, R12 = 12, R13 = 13, R14 = 14, R15 = 15, Rdi = 0xd1, Rsi = 0x51, Rbp = 0xb9, Rbx = 0xb8,
        Rdx = 0xd8, Rax = 0, Rcx = 0x400123, Rsp = f.Mem + 12 * Page + 8, Rip = 0x400123, Rflags = 0x646, // mit DF
    };

    // Wie die Brücke: Argumente aus den Registern beim Eintritt (LinuxUserRegs.ToSyscallArgs), Signalzähler davor.
    private static void After(Fixture f, ref LinuxUserRegs r, byte[] fpu, long number, ulong rsi = 0, ulong rdx = 0, ulong r10 = 0,
        long result = 0, long? takenBefore = null)
    {
        LinuxUserRegs entry = r;
        entry.Rax = unchecked((ulong)number);
        entry.Rsi = rsi;
        entry.Rdx = rdx;
        entry.R10 = r10;
        LinuxSignalFrame.AfterSyscall(f.P, ref r, fpu, entry.ToSyscallArgs(), result, takenBefore ?? f.P.SignalsTaken);
    }

    private static byte[] Fpu(uint mxcsr)
    {
        var fpu = new byte[512];
        BitConverter.GetBytes((ushort)0x27F).CopyTo(fpu, 0);
        BitConverter.GetBytes(mxcsr).CopyTo(fpu, 24);
        BitConverter.GetBytes(0xFFFFu).CopyTo(fpu, 28);
        fpu[160] = 0xAB; // xmm0
        return fpu;
    }

    private static ulong Read64(Fixture f, ulong addr)
    {
        Span<byte> b = stackalloc byte[8];
        Assert.IsTrue(f.P.TryRead(addr, b));
        return BitConverter.ToUInt64(b);
    }

    [TestMethod]
    public void SignalmaskeJeThreadSigchldBleibtNachSpawnMaskeZustellbar()
    {
        // V38 (Hub sah nach dem ersten WebContent-Ende nie wieder SIGCHLD): Thread T läuft im SIGCHLD-Handler (Maske mit SIGCHLD), während
        // der Hauptthread wie LibCore fork_and_exec alle Signale blockiert und danach die alte Maske herstellt. Mit einer Maske je Prozess
        // sah der Hauptthread dabei SIGCHLD als "alt" und stellte es nach dem rt_sigreturn von T dauerhaft blockiert her.
        const int Chld = 17;
        const ulong ChldBit = 1UL << (Chld - 1);
        var f = new Fixture();
        try
        {
            SetAction(f, Chld, HandlerAddr, LinuxUserProcess.SaSiginfo);
            LinuxThread t = f.Clone(f.Mem, f.Mem + 4);
            Assert.AreEqual(0UL, t.Blocked, "clone erbt die Maske des Aufrufers");

            // T nimmt SIGCHLD und läuft im Handler.
            Fixture.Current = t;
            Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, Chld));
            LinuxUserRegs rt = Regs(f);
            byte[] fpu = Fpu(0x1F80);
            After(f, ref rt, fpu, LinuxSyscalls.Getpid);
            Assert.AreEqual(HandlerAddr, rt.Rip);
            Assert.AreEqual(ChldBit, t.Blocked, "im Handler nur T blockiert");

            // Hauptthread: pthread_sigmask(SIG_SETMASK, all, &old), dann SIG_SETMASK old.
            Fixture.Current = null;
            ulong set = f.Mem + 0x500, old = f.Mem + 0x508;
            Assert.IsTrue(f.P.TryWrite(set, BitConverter.GetBytes(ulong.MaxValue)));
            Assert.AreEqual(0, f.Call(LinuxSyscalls.RtSigprocmask, 2, set, old, 8));
            Assert.AreEqual(0L, f.Read64(old), "alte Maske des Hauptthreads, nicht die des Handlers in T");
            Assert.AreEqual(0UL, t.Blocked & ~ChldBit, "T unberührt");

            // T verlässt den Handler (rt_sigreturn), danach stellt der Hauptthread seine Maske her.
            Fixture.Current = t;
            rt.Rsp += 8;
            Assert.IsTrue(LinuxSignalFrame.Return(f.P, ref rt, fpu));
            Assert.AreEqual(0UL, t.Blocked);
            Fixture.Current = null;
            Assert.AreEqual(0, f.Call(LinuxSyscalls.RtSigprocmask, 2, old, 0, 8));
            Assert.AreEqual(0UL, f.P.MainThread.Blocked, "SIGCHLD wieder frei");

            // Blockiert nur der Hauptthread, nimmt T das nächste SIGCHLD; der Hauptthread wartet weiter.
            Assert.AreEqual(0, f.Call(LinuxSyscalls.RtSigprocmask, 2, set, 0, 8));
            Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, Chld));
            Assert.IsFalse(f.P.SignalWaiting, "für den Hauptthread blockiert");
            LinuxUserRegs rm = Regs(f);
            After(f, ref rm, Fpu(0x1F80), LinuxSyscalls.Getpid);
            Assert.AreEqual(Regs(f).Rip, rm.Rip, "Hauptthread nimmt es nicht");
            Fixture.Current = t;
            Assert.IsTrue(f.P.SignalWaiting, "T sieht es");
            rt = Regs(f);
            After(f, ref rt, Fpu(0x1F80), LinuxSyscalls.Getpid);
            Assert.AreEqual(HandlerAddr, rt.Rip, "T nimmt es");

            // Blockieren alle Threads, bleibt es anhängig, bis einer es freigibt.
            Assert.IsTrue(f.P.TryWrite(set, BitConverter.GetBytes(ChldBit)));
            Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, Chld));
            Assert.AreEqual(ChldBit, f.P.PendingSignals & ChldBit);
            Assert.IsFalse(f.P.SignalWaiting);
            Fixture.Current = null;
            Assert.AreEqual(0, f.Call(LinuxSyscalls.RtSigprocmask, 1, set, 0, 8)); // SIG_UNBLOCK
            Assert.IsTrue(f.P.SignalWaiting, "nach der Freigabe zustellbar");
        }
        finally
        {
            Fixture.Current = null;
        }
    }

    [TestMethod]
    public void HandlerBekommtRahmenUndRtSigreturnStelltAllesWiederHer()
    {
        var f = new Fixture();
        SetAction(f, 10, HandlerAddr, LinuxUserProcess.SaSiginfo, mask: 1UL << 11);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, 10));
        Assert.IsTrue(f.P.SignalWaiting);

        LinuxUserRegs r = Regs(f);
        LinuxUserRegs before = r;
        byte[] fpu = Fpu(0x1FA0);
        byte[] fpuBefore = (byte[])fpu.Clone();
        After(f, ref r, fpu, LinuxSyscalls.Kill, rsi: 0, r10: 0, result: 0);

        Assert.AreEqual(HandlerAddr, r.Rip);
        Assert.AreEqual(10UL, r.Rdi);
        Assert.AreEqual(8UL, r.Rsp % 16, "wie nach call");
        Assert.IsTrue(r.Rsp + 440 + 512 <= before.Rsp - 128, "unter der roten Zone");
        Assert.AreEqual(r.Rsp + 312, r.Rsi);
        Assert.AreEqual(r.Rsp + 8, r.Rdx);
        Assert.AreEqual(0UL, r.Rflags & 0x400, "DF gelöscht");
        Assert.AreEqual(Restorer, Read64(f, r.Rsp), "pretcode = sa_restorer");
        Assert.AreEqual(10, f.Read32(r.Rsi), "si_signo");
        Assert.AreEqual(0, f.Read32(r.Rsi + 8), "SI_USER");
        Assert.AreEqual(Pid, f.Read32(r.Rsi + 16), "si_pid");
        Assert.AreEqual(0x1F80, BitConverter.ToInt32(fpu, 24), "Handler mit frischem MXCSR");
        Assert.AreEqual(0, fpu[160]);
        Assert.AreEqual((1UL << 9) | (1UL << 11), f.P.MainThread.Blocked, "Signal und sa_mask im Handler blockiert");
        Assert.IsFalse(f.P.SignalWaiting);

        // Der Handler ändert Register; ret nimmt pretcode, dann rt_sigreturn.
        r.Rbx = 0xdead;
        r.Rsp += 8;
        Assert.IsTrue(LinuxSignalFrame.Return(f.P, ref r, fpu));
        Assert.AreEqual(before.Rip, r.Rip);
        Assert.AreEqual(before.Rsp, r.Rsp);
        Assert.AreEqual(before.Rbx, r.Rbx);
        Assert.AreEqual(before.R15, r.R15);
        Assert.AreEqual(before.Rax, r.Rax);
        Assert.AreEqual(before.Rflags, r.Rflags | 0x400 & before.Rflags);
        CollectionAssert.AreEqual(fpuBefore, fpu, "FPU-Zustand zurück");
        Assert.AreEqual(0UL, f.P.MainThread.Blocked);
    }

    [TestMethod]
    public void RtSigreturnUebernimmtTfUndRfAberNichtIoplNtOderIf()
    {
        // V45-P3: Linux FIX_EFLAGS nimmt TF und RF aus dem Rahmen (TF fängt danach einen Schritt), IOPL, NT, IF und ID nie.
        var f = new Fixture();
        SetAction(f, 10, HandlerAddr, LinuxUserProcess.SaSiginfo);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, 10));
        LinuxUserRegs r = Regs(f);
        After(f, ref r, Fpu(0x1F80), LinuxSyscalls.Kill, rsi: 0, r10: 0, result: 0);
        Assert.AreEqual(0UL, r.Rflags & 0x10100, "Handler ohne TF und RF");

        const ulong Tf = 0x100, Rf = 0x10000, Cf = 0x1, Iopl = 0x3000, Nt = 0x4000, If = 0x200, Id = 0x200000;
        ulong flags = Tf | Rf | Cf | Iopl | Nt | Id; // IF absichtlich aus
        f.Write32(r.Rdx + 40 + 136, (int)(uint)flags);
        f.Write32(r.Rdx + 40 + 140, (int)(uint)(flags >> 32));
        r.Rsp += 8;
        Assert.IsTrue(LinuxSignalFrame.Return(f.P, ref r, Fpu(0x1F80)));
        Assert.AreEqual(Tf | Rf | Cf, r.Rflags & (Tf | Rf | Cf), "TF, RF und CF aus dem Rahmen");
        Assert.AreEqual(0UL, r.Rflags & (Iopl | Nt | Id), "IOPL, NT und ID nicht");
        Assert.AreEqual(If, r.Rflags & If, "IF bleibt");
    }

    [TestMethod]
    public void SaRestartStartetReadNeuOhneLiefertEintr()
    {
        var f = new Fixture();
        SetAction(f, 10, HandlerAddr, LinuxUserProcess.SaRestart);
        SetAction(f, 12, HandlerAddr + 0x10, 0);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, 10));
        LinuxUserRegs r = Regs(f);
        r.Rax = unchecked((ulong)-LinuxSyscalls.EINTR);
        After(f, ref r, Fpu(0x1F80), 0, rsi: 0, r10: 0, result: -LinuxSyscalls.EINTR);
        ulong uc = r.Rdx;
        Assert.AreEqual(0x400121UL, Read64(f, uc + 40 + 128), "RIP auf syscall zurück");
        Assert.AreEqual(0UL, Read64(f, uc + 40 + 104), "rax = Nummer von read");

        r.Rsp += 8;
        Assert.IsTrue(LinuxSignalFrame.Return(f.P, ref r, Fpu(0x1F80)));
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, 12));
        LinuxUserRegs q = Regs(f);
        q.Rax = unchecked((ulong)-LinuxSyscalls.EINTR);
        After(f, ref q, Fpu(0x1F80), 0, rsi: 0, r10: 0, result: -LinuxSyscalls.EINTR);
        Assert.AreEqual(HandlerAddr + 0x10, q.Rip);
        Assert.AreEqual(0x400123UL, Read64(f, q.Rdx + 40 + 128), "ohne SA_RESTART kein Neustart");
        Assert.AreEqual(unchecked((ulong)-LinuxSyscalls.EINTR), Read64(f, q.Rdx + 40 + 104));

        // poll startet auch mit SA_RESTART nicht neu.
        Assert.IsFalse(LinuxSignalFrame.Restartable(LinuxSyscalls.Poll, 0, 0));
        Assert.IsTrue(LinuxSignalFrame.Restartable(LinuxSyscalls.Futex, 0, 0));
        Assert.IsFalse(LinuxSignalFrame.Restartable(LinuxSyscalls.Futex, 0, 0x7000), "futex mit Frist");
    }

    [TestMethod]
    public void EpipeLoestSigpipeAusStandardBeendetHandlerLaeuft()
    {
        var f = new Fixture();
        SetAction(f, 13, HandlerAddr, 0);
        LinuxUserRegs r = Regs(f);
        After(f, ref r, Fpu(0x1F80), 1, rsi: 0, r10: 0, result: -32);
        Assert.AreEqual(HandlerAddr, r.Rip);
        Assert.AreEqual(13UL, r.Rdi);

        var g = new Fixture();
        LinuxUserRegs s = Regs(g);
        // sendmsg(fd, msg, flags): flags in rdx; ein msg-Zeiger mit Bit 0x4000 in rsi zählt nicht (Review V18 P2).
        After(g, ref s, Fpu(0x1F80), 46, rsi: 0x7FFF_4000, rdx: 0x4000, result: -32);
        Assert.IsNull(g.P.ExitCode, "sendmsg mit MSG_NOSIGNAL");
        // sendto(fd, buf, len, flags): flags in r10; eine Länge mit Bit 0x4000 in rdx zählt nicht.
        After(g, ref s, Fpu(0x1F80), 44, rsi: 0x7FFF_4000, rdx: 0x4000, r10: 0x4000, result: -32);
        Assert.IsNull(g.P.ExitCode, "sendto mit MSG_NOSIGNAL");
        var h = new Fixture();
        LinuxUserRegs t = Regs(h);
        After(h, ref t, Fpu(0x1F80), 46, rsi: 0x7FFF_4000, rdx: 0, result: -32);
        Assert.AreEqual(13, h.P.TerminatedBySignal, "sendmsg ohne MSG_NOSIGNAL, auch wenn rsi das Bit trägt");
        var k = new Fixture();
        LinuxUserRegs u = Regs(k);
        After(k, ref u, Fpu(0x1F80), 44, rsi: 0x7FFF_4000, rdx: 0x4000, r10: 0, result: -32);
        Assert.AreEqual(13, k.P.TerminatedBySignal, "sendto ohne MSG_NOSIGNAL, auch wenn len das Bit trägt");
        After(g, ref s, Fpu(0x1F80), 1, rsi: 0, r10: 0, result: -32);
        Assert.AreEqual(13, g.P.TerminatedBySignal, "SIG_DFL beendet");
    }

    [TestMethod]
    public void EintrOhneSignalStartetNichtNeuNurNachFremdGenommenemSignal()
    {
        // Review V18 P3: -EINTR ohne Signal (abgehängtes Terminal) darf read nicht zurückspulen, sonst dreht der Thread endlos.
        var f = new Fixture();
        LinuxUserRegs r = Regs(f);
        r.Rax = unchecked((ulong)-LinuxSyscalls.EINTR);
        After(f, ref r, Fpu(0x1F80), 0, result: -LinuxSyscalls.EINTR);
        Assert.AreEqual(0x400123UL, r.Rip, "kein Neustart ohne Signal");
        Assert.AreEqual(unchecked((ulong)-LinuxSyscalls.EINTR), r.Rax);

        // Ein anderer Thread nahm während des Aufrufs das Signal: still neu starten wie Linux (ERESTARTSYS ohne Handler hier).
        SetAction(f, 10, HandlerAddr, LinuxUserProcess.SaRestart);
        long before = f.P.SignalsTaken;
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, 10));
        Assert.IsTrue(f.P.TakeSignal(out _));
        LinuxUserRegs q = Regs(f);
        After(f, ref q, Fpu(0x1F80), 0, result: -LinuxSyscalls.EINTR, takenBefore: before);
        Assert.AreEqual(0x400121UL, q.Rip, "RIP auf syscall zurück");
        Assert.AreEqual(0UL, q.Rax, "rax = Nummer von read");
    }

    [TestMethod]
    public void AlternativstackMitAutodisarmUndResethand()
    {
        var f = new Fixture();
        var ss = new byte[24];
        BitConverter.GetBytes(f.Mem + 2 * Page).CopyTo(ss, 0);
        BitConverter.GetBytes(0x80000000u).CopyTo(ss, 8); // SS_AUTODISARM
        BitConverter.GetBytes(4 * Page).CopyTo(ss, 16);
        Assert.IsTrue(f.P.TryWrite(f.Mem + 0x500, ss));
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Sigaltstack, f.Mem + 0x500, 0));
        SetAction(f, 12, HandlerAddr, LinuxUserProcess.SaOnstack | LinuxUserProcess.SaResethand);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, 12));
        LinuxUserRegs r = Regs(f);
        After(f, ref r, Fpu(0x1F80), LinuxSyscalls.Kill, rsi: 0, r10: 0, result: 0);
        Assert.IsTrue(r.Rsp > f.Mem + 2 * Page && r.Rsp < f.Mem + 6 * Page, "Rahmen auf dem Alternativstack");
        Assert.AreEqual(2, f.P.Signals.AltFlags, "abgeschaltet bis rt_sigreturn");
        Assert.AreEqual(0UL, f.P.Signals.Handler[12], "SA_RESETHAND");
        r.Rsp += 8;
        Assert.IsTrue(LinuxSignalFrame.Return(f.P, ref r, Fpu(0x1F80)));
        Assert.AreEqual(f.Mem + 2 * Page, f.P.Signals.AltSp);
        Assert.AreEqual(4 * Page, f.P.Signals.AltSize);
        Assert.AreEqual(unchecked((int)0x80000000), f.P.Signals.AltFlags);
    }

    [TestMethod]
    public void OhneRestorerOderKaputterStackEndetMitSigsegv()
    {
        var f = new Fixture();
        SetAction(f, 10, HandlerAddr, 0, restorer: 0);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, 10));
        LinuxUserRegs r = Regs(f);
        After(f, ref r, Fpu(0x1F80), LinuxSyscalls.Kill, rsi: 0, r10: 0, result: 0);
        Assert.AreEqual(11, f.P.TerminatedBySignal);

        var g = new Fixture();
        LinuxUserRegs q = Regs(g);
        q.Rsp = 0x10; // kein Rahmen lesbar
        Assert.IsFalse(LinuxSignalFrame.Return(g.P, ref q, Fpu(0x1F80)));

        // Review V18 P3: verbogener RSP im Rahmen. Nichtkanonisch: SIGBUS wie Linux; Kernelhälfte: kanonisch, UserMode.Prepare lehnt ab.
        Assert.IsTrue(LinuxSignalFrame.NonCanonicalStack(new LinuxUserRegs { Rsp = 0x8000_0000_0000_0000 }));
        Assert.IsTrue(LinuxSignalFrame.NonCanonicalStack(new LinuxUserRegs { Rsp = 0x0000_8000_0000_0000 }));
        Assert.IsFalse(LinuxSignalFrame.NonCanonicalStack(new LinuxUserRegs { Rsp = 0xFFFF_8000_0000_1000 }));
        Assert.IsFalse(LinuxSignalFrame.NonCanonicalStack(new LinuxUserRegs { Rsp = 0x0000_7FFF_FFFF_F000 }));
    }

    [TestMethod]
    public void WartendesSignalUnterbrichtPauseUndFutex()
    {
        var f = new Fixture();
        SetAction(f, 10, HandlerAddr, 0);
        long result = 1;
        var t = new Thread(() => result = f.Call(LinuxSyscalls.Pause));
        t.Start();
        Thread.Sleep(50);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, 10));
        Assert.IsTrue(t.Join(5000));
        Assert.AreEqual(-LinuxSyscalls.EINTR, result);

        // Blockiert: futex wartet weiter, bis die Frist abläuft.
        SetMask(f, 2, 1UL << 9);
        f.Write32(f.Mem + 0x600, 0);
        f.WriteTimespec(f.Mem + 0x610, 0, 50_000_000);
        Assert.AreEqual(-LinuxSyscalls.ETIMEDOUT, f.Call(LinuxSyscalls.Futex, f.Mem + 0x600, 0, 0, f.Mem + 0x610));
        SetMask(f, 2, 0);
        Assert.AreEqual(-LinuxSyscalls.EINTR, f.Call(LinuxSyscalls.Futex, f.Mem + 0x600, 0, 0, f.Mem + 0x610));
    }

    // ---- V24 Ausnahmen im Ring 3 als Signal (LinuxFault, LinuxSignalFrame.DeliverFault) ----

    [TestMethod]
    public void AusnahmeTabelleWieLinux()
    {
        byte[] fpu = Fpu(0x1F80);
        Assert.AreEqual(new LinuxFault(11, LinuxFault.SegvMaperr, 0x1234, 14, 4, 0x1234), LinuxFault.Of(14, 4, 0x1234, 0x400000, false, fpu));
        Assert.AreEqual(new LinuxFault(11, LinuxFault.SegvAccerr, 0x5000, 14, 7, 0x5000), LinuxFault.Of(14, 7, 0x5000, 0x400000, true, fpu));
        Assert.AreEqual(new LinuxFault(11, LinuxFault.SiKernel, 0, 13, 0, 0), LinuxFault.Of(13, 0, 0, 0x400000, false, fpu), "#GP: si_addr 0");
        Assert.AreEqual(new LinuxFault(4, LinuxFault.IllIllopn, 0x400010, 6, 0, 0), LinuxFault.Of(6, 0, 0, 0x400010, false, fpu));
        Assert.AreEqual(new LinuxFault(8, LinuxFault.FpeIntdiv, 0x400020, 0, 0, 0), LinuxFault.Of(0, 0, 0, 0x400020, false, fpu));
        Assert.AreEqual(5, LinuxFault.Of(3, 0, 0, 0x400030, false, fpu).Signal, "int3: SIGTRAP");
        Assert.AreEqual(7, LinuxFault.Of(12, 0, 0, 0x400030, false, fpu).Signal, "#SS: SIGBUS");
        Assert.AreEqual(7, LinuxFault.Of(17, 0, 0, 0x400030, false, fpu).Signal, "#AC: SIGBUS");

        // #XM: MXCSR mit gesetztem ZE (Bit 2) und freigegebener Maske ZM (Bit 9): FPE_FLTDIV; mit IE zusätzlich: FPE_FLTINV geht vor.
        Assert.AreEqual(3, LinuxFault.Of(19, 0, 0, 0x400040, false, Fpu(0x1F80 & ~0x200u | 0x4)).Code);
        Assert.AreEqual(7, LinuxFault.Of(19, 0, 0, 0x400040, false, Fpu(0x1F80 & ~0x280u | 0x5)).Code);
        Assert.AreEqual(0x400040UL, LinuxFault.Of(19, 0, 0, 0x400040, false, fpu).Address, "SIGFPE: si_addr = RIP");
    }

    [TestMethod]
    public void SeitenfehlerMitHandlerBautRahmenMitSiAddr()
    {
        var f = new Fixture();
        ulong none = f.P.MapAnonymous(Page, 0);
        Assert.AreNotEqual(0UL, none);
        Assert.IsTrue(f.P.HasMapping(none + 17), "PROT_NONE ist ein Bereich");
        Assert.IsFalse(f.P.HasMapping(0), "Adresse 0 nicht");

        SetAction(f, 11, HandlerAddr, LinuxUserProcess.SaSiginfo);
        LinuxUserRegs r = Regs(f);
        LinuxUserRegs before = r;
        byte[] fpu = Fpu(0x1FA0);
        byte[] fpuBefore = (byte[])fpu.Clone();
        LinuxFault fault = LinuxFault.Of(14, 4, none + 17, r.Rip, f.P.HasMapping(none + 17), fpu);
        Assert.IsTrue(LinuxSignalFrame.DeliverFault(f.P, ref r, fpu, fault));

        Assert.AreEqual(HandlerAddr, r.Rip);
        Assert.AreEqual(11UL, r.Rdi);
        Assert.AreEqual(11, f.Read32(r.Rsi), "si_signo");
        Assert.AreEqual(2, f.Read32(r.Rsi + 8), "SEGV_ACCERR");
        Assert.AreEqual(none + 17, Read64(f, r.Rsi + 16), "si_addr");
        Assert.AreEqual(4UL, Read64(f, r.Rdx + 40 + 152), "sigcontext err");
        Assert.AreEqual(14UL, Read64(f, r.Rdx + 40 + 160), "sigcontext trapno");
        Assert.AreEqual(none + 17, Read64(f, r.Rdx + 40 + 176), "sigcontext cr2");
        Assert.AreEqual(before.Rip, Read64(f, r.Rdx + 40 + 128), "RIP des fehlerhaften Befehls im Rahmen");
        Assert.AreEqual(1UL << 10, f.P.MainThread.Blocked, "SIGSEGV im Handler blockiert");
        Assert.AreEqual(0x1F80, BitConverter.ToInt32(fpu, 24), "Handler mit frischem MXCSR");
        Assert.IsNull(f.P.TerminatedBySignal);

        // Zweiter Fehler im Handler (SIGSEGV blockiert): Linux setzt SIG_DFL, der Prozess endet; Register bleiben.
        LinuxUserRegs inHandler = r;
        Assert.IsFalse(LinuxSignalFrame.DeliverFault(f.P, ref r, fpu, LinuxFault.Of(14, 4, 0, r.Rip, false, fpu)));
        Assert.AreEqual(inHandler.Rip, r.Rip);

        // Rückkehr per rt_sigreturn: wieder am fehlerhaften Befehl, Maske und FPU zurück.
        r.Rsp += 8;
        Assert.IsTrue(LinuxSignalFrame.Return(f.P, ref r, fpu));
        Assert.AreEqual(before.Rip, r.Rip);
        Assert.AreEqual(before.Rsp, r.Rsp);
        Assert.AreEqual(0UL, f.P.MainThread.Blocked);
        CollectionAssert.AreEqual(fpuBefore, fpu);
    }

    [TestMethod]
    public void AusnahmeOhneHandlerIgnoriertOderBlockiertBeendet()
    {
        var f = new Fixture();
        LinuxUserRegs r = Regs(f);
        byte[] fpu = Fpu(0x1F80);
        LinuxFault ud = LinuxFault.Of(6, 0, 0, r.Rip, false, fpu);
        Assert.IsFalse(LinuxSignalFrame.DeliverFault(f.P, ref r, fpu, ud), "SIG_DFL");

        SetAction(f, 4, 1, 0); // SIG_IGN
        Assert.IsFalse(LinuxSignalFrame.DeliverFault(f.P, ref r, fpu, ud), "ignoriert: Linux erzwingt SIG_DFL");

        SetAction(f, 4, HandlerAddr, 0);
        SetMask(f, 0, 1UL << 3); // SIG_BLOCK SIGILL
        Assert.IsFalse(LinuxSignalFrame.DeliverFault(f.P, ref r, fpu, ud), "blockiert");
        Assert.AreEqual(0x400123UL, r.Rip);

        SetMask(f, 1, 1UL << 3);
        Assert.IsTrue(LinuxSignalFrame.DeliverFault(f.P, ref r, fpu, ud));
        Assert.AreEqual(HandlerAddr, r.Rip);
        Assert.AreEqual(4UL, r.Rdi);
        Assert.AreEqual(2, f.Read32(r.Rsi + 8), "ILL_ILLOPN");
        Assert.AreEqual(0x400123UL, Read64(f, r.Rsi + 16), "si_addr = RIP");
    }

    [TestMethod]
    public void AusnahmeOhnePlatzFuerRahmenEndetMitSigsegv()
    {
        var f = new Fixture();
        SetAction(f, 8, HandlerAddr, 0);
        LinuxUserRegs r = Regs(f);
        r.Rsp = 0x2000; // unbelegt: der Rahmen lässt sich nicht schreiben
        byte[] fpu = Fpu(0x1F80);
        Assert.IsFalse(LinuxSignalFrame.DeliverFault(f.P, ref r, fpu, LinuxFault.Of(0, 0, 0, r.Rip, false, fpu)));
        Assert.AreEqual(11, f.P.TerminatedBySignal, "force_sigsegv statt SIGFPE");
        Assert.AreEqual(0x2000UL, r.Rsp);
    }

    [TestMethod]
    public void AusnahmeVorWartendenSignalenDieseLaufenZuerst()
    {
        // Linux stellt das Ausnahmesignal zuerst zu, danach in derselben Schleife die anhängigen; deren Rahmen liegt oben und läuft zuerst.
        var f = new Fixture();
        SetAction(f, 11, HandlerAddr, LinuxUserProcess.SaSiginfo);
        SetAction(f, 10, HandlerAddr + 0x40, 0);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Kill, Pid, 10));
        LinuxUserRegs r = Regs(f);
        byte[] fpu = Fpu(0x1F80);
        Assert.IsTrue(LinuxSignalFrame.DeliverFault(f.P, ref r, fpu, LinuxFault.Of(13, 0, 0, r.Rip, false, fpu)));
        Assert.AreEqual(HandlerAddr + 0x40, r.Rip, "SIGUSR1 oben");
        Assert.AreEqual(HandlerAddr, Read64(f, r.Rdx + 40 + 128), "darunter der SIGSEGV-Handler");
        Assert.IsFalse(f.P.SignalWaiting);
    }

    [TestMethod]
    public void CpuZeitJeThreadUndSummeBeendeterThreads()
    {
        var f = new Fixture();
        long mainNs = 1_500_000_000, childNs = 2_250_000_000;
        f.P.MainThread.CpuNanosSource = () => mainNs;
        LinuxThread child = f.Clone(f.Mem, f.Mem + 4);
        child.CpuNanosSource = () => childNs;

        const ulong Ts = 0x100, Ru = 0x200;
        Assert.AreEqual(0L, f.Call(LinuxSyscalls.ClockGettime, 3, f.Mem + Ts));
        Assert.AreEqual(1L, f.Read64(f.Mem + Ts));
        Assert.AreEqual(500_000_000L, f.Read64(f.Mem + Ts + 8), "CLOCK_THREAD_CPUTIME_ID: Hauptthread");
        Assert.AreEqual(0L, f.Call(LinuxSyscalls.ClockGettime, 2, f.Mem + Ts));
        Assert.AreEqual(3L, f.Read64(f.Mem + Ts));
        Assert.AreEqual(750_000_000L, f.Read64(f.Mem + Ts + 8), "CLOCK_PROCESS_CPUTIME_ID: Summe 3,75 s");

        Assert.AreEqual(0L, f.Call(LinuxSyscalls.Getrusage, 0, f.Mem + Ru));
        Assert.AreEqual(3L, f.Read64(f.Mem + Ru));
        Assert.AreEqual(750_000L, f.Read64(f.Mem + Ru + 8));
        Assert.AreEqual(0L, f.Read64(f.Mem + Ru + 16), "ru_stime 0");
        Assert.AreEqual(0L, f.Call(LinuxSyscalls.Getrusage, 1, f.Mem + Ru));
        Assert.AreEqual(1L, f.Read64(f.Mem + Ru), "RUSAGE_THREAD");
        Assert.AreEqual(-LinuxSyscalls.EINVAL, f.Call(LinuxSyscalls.Getrusage, 5, f.Mem + Ru));

        // Kindthread endet: sein Endwert bleibt in der Summe, auch wenn die Quelle weiterläuft oder weg ist.
        Fixture.RunAs(child, () => f.Call(LinuxSyscalls.Exit, 0)).Join();
        childNs = 9_000_000_000;
        Assert.AreEqual(3_750_000_000L, f.P.ProcessCpuNanos);
        mainNs = 2_000_000_000;
        Assert.AreEqual(4_250_000_000L, f.P.ProcessCpuNanos);
    }

    [TestMethod]
    public void CpuZeitThreadGoneFriertEndwertEin()
    {
        var f = new Fixture();
        long childNs = 1_000_000_000;
        LinuxThread child = f.Clone(f.Mem, f.Mem + 4);
        child.CpuNanosSource = () => childNs;
        f.P.ThreadGone(child); // Brücke räumt einen abgebrochenen Kernelthread auf
        childNs = 5_000_000_000;
        Assert.AreEqual(1_000_000_000L, f.P.ProcessCpuNanos, "Endwert zählt einmal, spätere Quelle nicht");
        f.P.ThreadGone(child); // zweiter Aufruf ändert nichts
        Assert.AreEqual(1_000_000_000L, f.P.ProcessCpuNanos);
    }

    [TestMethod]
    public void GetrusageFehlerfaelleEfaultUndChildren()
    {
        var f = new Fixture();
        f.P.MainThread.CpuNanosSource = () => 2_000_000_000;
        const ulong Ru = 0x200;
        Assert.AreEqual(-LinuxSyscalls.EFAULT, f.Call(LinuxSyscalls.Getrusage, 0, 0xFFFF_0000_0000UL), "RUSAGE_SELF, ungültiger Zeiger");
        Assert.AreEqual(-LinuxSyscalls.EFAULT, f.Call(LinuxSyscalls.Getrusage, 1, 0xFFFF_0000_0000UL), "RUSAGE_THREAD, ungültiger Zeiger");
        Assert.AreEqual(0L, f.Call(LinuxSyscalls.Getrusage, unchecked((ulong)-1L), f.Mem + Ru), "RUSAGE_CHILDREN");
        Assert.AreEqual(0L, f.Read64(f.Mem + Ru), "ru_utime.tv_sec 0");
        Assert.AreEqual(0L, f.Read64(f.Mem + Ru + 8), "ru_utime.tv_usec 0");
    }
}
