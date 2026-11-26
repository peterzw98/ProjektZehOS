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
        Assert.AreEqual(-LinuxSyscalls.ENOSYS, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 10), "Handler: Zustellen fehlt noch");
        Assert.IsNull(f.P.ExitCode);
        Assert.AreEqual(0, f.Call(LinuxSyscalls.Tgkill, Pid, tid, 15));
        Assert.AreEqual(15, f.P.TerminatedBySignal);
        Assert.AreEqual(128 + 15, f.P.ExitCode);
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
}
