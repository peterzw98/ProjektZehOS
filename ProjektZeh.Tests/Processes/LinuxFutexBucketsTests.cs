using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Processes;
using ProjektZeh.Processes.Syscalls;

namespace ProjektZeh.Tests.Processes;

/// <summary>
/// N4-S4: futex-Tabelle in Eimern mit einem Warteobjekt je Wartendem. Die Scheibe ist in den meisten Fällen eine Minute lang, damit
/// <see cref="LinuxFutexTable.WaitReturns"/> nur Wecken zählt und ein Wecken des falschen Wartenden sichtbar wird.
/// </summary>
[TestClass]
public sealed class LinuxFutexBucketsTests
{
    private const ulong Base = 0x10000;
    private const int LongSlice = 60_000;

    /// <summary>Benutzerspeicher aus 4-Byte-Wörtern, atomar les- und schreibbar (wie ein Wort im geteilten Adressraum).</summary>
    private sealed class Words : IUserMemory
    {
        public readonly int[] W = new int[1024];

        public ref int At(ulong addr) => ref W[(addr - Base) / 4];

        public bool TryRead(ulong addr, Span<byte> dst)
        {
            if (addr < Base || addr + (ulong)dst.Length > Base + (ulong)W.Length * 4 || dst.Length != 4)
            {
                return false;
            }

            BitConverter.TryWriteBytes(dst, Volatile.Read(ref At(addr)));
            return true;
        }

        public bool TryWrite(ulong addr, ReadOnlySpan<byte> src) => false;

        public bool CanWrite(ulong addr, ulong length) => false;

        public bool TryReadCString(ulong addr, int max, out string s)
        {
            s = "";
            return false;
        }
    }

    private static long Now() => Stopwatch.GetTimestamp() * (1_000_000_000L / Stopwatch.Frequency);

    private static Thread Start(Action body)
    {
        var t = new Thread(() => body()) { IsBackground = true };
        t.Start();
        return t;
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

    [TestMethod]
    public void EimerNachAdresse()
    {
        Assert.AreEqual(0, LinuxFutexTable.BucketOf(Base));
        Assert.AreEqual(1, LinuxFutexTable.BucketOf(Base + 4));
        Assert.AreEqual(15, LinuxFutexTable.BucketOf(Base + 60));
        Assert.AreEqual(0, LinuxFutexTable.BucketOf(Base + 64), "16 Wörter weiter: derselbe Eimer");
    }

    [TestMethod]
    public void WeckenTrifftImSelbenEimerNurDieGenannteAdresse()
    {
        var mem = new Words();
        var table = new LinuxFutexTable(Now, LongSlice);
        ulong a = Base, b = Base + 64;
        Assert.AreEqual(LinuxFutexTable.BucketOf(a), LinuxFutexTable.BucketOf(b));
        long ra = 1, rb = 1;
        var ta = Start(() => ra = table.Wait(mem, a, 0, uint.MaxValue, -1));
        var tb = Start(() => rb = table.Wait(mem, b, 0, uint.MaxValue, -1));
        SpinUntil(() => table.WaitingCount == 2);

        Assert.AreEqual(1, table.Wake(a, int.MaxValue));
        Assert.IsTrue(ta.Join(5000));
        Assert.AreEqual(0, ra);
        Thread.Sleep(30);
        Assert.IsTrue(tb.IsAlive, "der Wartende auf b schläft weiter");
        Assert.AreEqual(1, table.WaitingCount);
        Assert.AreEqual(1, table.WaitReturns, "nur der Wartende auf a kam aus dem Wait");

        Assert.AreEqual(1, table.Wake(b, 1));
        Assert.IsTrue(tb.Join(5000));
        Assert.AreEqual(0, rb);
        Assert.AreEqual(0, table.WaitingCount);
    }

    [TestMethod]
    public void WakeEinsWecktGenauEinenVonZwei()
    {
        var mem = new Words();
        var table = new LinuxFutexTable(Now, LongSlice);
        var results = new long[] { 1, 1 };
        var threads = new Thread[2];
        for (int i = 0; i < 2; i++)
        {
            int k = i;
            threads[i] = Start(() => results[k] = table.Wait(mem, Base, 0, uint.MaxValue, -1));
            SpinUntil(() => table.WaitingCount == k + 1);
        }

        Assert.AreEqual(1, table.Wake(Base, 1));
        Assert.IsTrue(threads[0].Join(5000), "der zuerst Wartende");
        Thread.Sleep(30);
        Assert.IsTrue(threads[1].IsAlive);
        Assert.AreEqual(1, table.WaitReturns, "PulseAll hätte beide geweckt");
        Assert.AreEqual(1, table.Wake(Base, 1));
        Assert.IsTrue(threads[1].Join(5000));
        Assert.AreEqual("0,0", string.Join(",", results));
    }

    [TestMethod]
    public void RequeueUeberZweiEimerInBeideRichtungen()
    {
        foreach (var (from, to) in new[] { (Base, Base + 4), (Base + 60, Base + 8) })
        {
            var mem = new Words();
            var table = new LinuxFutexTable(Now, LongSlice);
            Assert.AreNotEqual(LinuxFutexTable.BucketOf(from), LinuxFutexTable.BucketOf(to));
            mem.At(from) = 7;
            var results = new long[] { 1, 1, 1 };
            var threads = new Thread[3];
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                threads[i] = Start(() => results[k] = table.Wait(mem, from, 7, uint.MaxValue, -1));
                SpinUntil(() => table.WaitingCount == k + 1);
            }

            Assert.AreEqual(-LinuxSyscalls.EAGAIN, table.Requeue(mem, from, to, 1, 2, 8));
            Assert.AreEqual(3, table.Requeue(mem, from, to, 1, 2, 7), "einer geweckt, zwei umgehängt");
            Assert.IsTrue(threads[0].Join(5000));
            SpinUntil(() => table.WaitingCount == 2);
            Assert.AreEqual(0, table.Wake(from, int.MaxValue), "auf from wartet keiner mehr");
            Assert.AreEqual(1, table.Wake(to, 1));
            Assert.IsTrue(threads[1].Join(5000), "Reihenfolge bleibt");
            Assert.AreEqual(1, table.Wake(to, 1));
            Assert.IsTrue(threads[2].Join(5000));
            Assert.AreEqual("0,0,0", string.Join(",", results));
            Assert.AreEqual(0, table.WaitingCount);
        }
    }

    [TestMethod]
    public void UmgehaengterWartenderVerlaesstDenNeuenEimerNachFrist()
    {
        var mem = new Words();
        var table = new LinuxFutexTable(Now);
        ulong from = Base, to = Base + 4;
        long r = 1;
        var t = Start(() => r = table.Wait(mem, from, 0, uint.MaxValue, Now() + 300_000_000L));
        SpinUntil(() => table.WaitingCount == 1);
        Assert.AreEqual(1, table.Requeue(mem, from, to, 0, 1, 0));
        Assert.IsTrue(t.Join(5000));
        Assert.AreEqual(-LinuxSyscalls.ETIMEDOUT, r);
        Assert.AreEqual(0, table.WaitingCount, "aus dem Zieleimer entfernt");
        Assert.AreEqual(0, table.Wake(to, int.MaxValue));
    }

    /// <summary>
    /// S4-P2: Ein Wecken zwischen der Prüfung auf Woken und dem Austragen darf nicht verloren gehen (Linux: unqueue_me). Der Wecker läuft hier
    /// im Uhr- beziehungsweise Signalrückruf, also genau nach der letzten Prüfung von Woken; einsträngig, damit das Fenster sicher getroffen wird.
    /// </summary>
    [TestMethod]
    public void WeckenNachFristpruefungGehtNichtVerloren()
    {
        var mem = new Words();
        LinuxFutexTable? table = null;
        bool armed = true;
        int wokenByClock = -1;
        table = new LinuxFutexTable(() =>
        {
            if (armed)
            {
                armed = false;
                wokenByClock = table!.Wake(Base, 1);
            }

            return 1_000;
        }, LongSlice);
        long r = table.Wait(mem, Base, 0, uint.MaxValue, 500);
        Assert.AreEqual(1, wokenByClock, "Wecker hat den Wartenden gezählt");
        Assert.AreEqual(0, r, "gezähltes Wecken muss 0 liefern, nicht -ETIMEDOUT");
        Assert.AreEqual(0, table.WaitingCount);
    }

    [TestMethod]
    public void WeckenNachSignalpruefungGehtNichtVerloren()
    {
        var mem = new Words();
        var table = new LinuxFutexTable(Now, LongSlice);
        bool armed = true;
        int wokenBySignal = -1;
        table.SignalWaiting = () =>
        {
            if (armed)
            {
                armed = false;
                wokenBySignal = table.Wake(Base, 1);
            }

            return true;
        };
        long r = table.Wait(mem, Base, 0, uint.MaxValue, -1);
        Assert.AreEqual(1, wokenBySignal);
        Assert.AreEqual(0, r, "gezähltes Wecken muss 0 liefern, nicht -EINTR");
        Assert.AreEqual(0, table.WaitingCount);
    }

    [TestMethod]
    public void AbortErreichtJedenEimerUndDenSchlaf()
    {
        var mem = new Words();
        var table = new LinuxFutexTable(Now, LongSlice);
        ulong[] addrs = { Base, Base + 20, Base + 60 };
        var results = new long[] { 1, 1, 1, 1 };
        var threads = new Thread[4];
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            threads[i] = Start(() => results[k] = table.Wait(mem, addrs[k], 0, uint.MaxValue, -1));
        }

        threads[3] = Start(() => results[3] = table.Sleep(-1));
        SpinUntil(() => table.WaitingCount == 3);
        Thread.Sleep(20);
        var sw = Stopwatch.StartNew();
        table.Abort();
        foreach (var t in threads)
        {
            Assert.IsTrue(t.Join(5000));
        }

        Assert.IsTrue(sw.ElapsedMilliseconds < 2000, "ohne Scheibenende: " + sw.ElapsedMilliseconds);
        Assert.AreEqual("-4,-4,-4,-4", string.Join(",", results));
        Assert.AreEqual(-LinuxSyscalls.EINTR, table.Wait(mem, Base, 0, uint.MaxValue, -1), "neue Wartende nach Abort");
        Assert.AreEqual(-LinuxSyscalls.EINTR, table.Sleep(-1));
    }

    [TestMethod]
    public void KickWecktWartendeUndSchlaeferBeiSignal()
    {
        var mem = new Words();
        var table = new LinuxFutexTable(Now, LongSlice);
        bool signal = false;
        table.SignalWaiting = () => Volatile.Read(ref signal);
        long rw = 1, rs = 1;
        var tw = Start(() => rw = table.Wait(mem, Base + 12, 0, uint.MaxValue, -1));
        var ts = Start(() => rs = table.Sleep(-1));
        SpinUntil(() => table.WaitingCount == 1);
        Thread.Sleep(20);
        Volatile.Write(ref signal, true);
        table.Kick();
        Assert.IsTrue(tw.Join(5000) && ts.Join(5000));
        Assert.AreEqual(-LinuxSyscalls.EINTR, rw);
        Assert.AreEqual(-LinuxSyscalls.EINTR, rs);
    }

    [TestMethod]
    public void WarteobjektDesThreadsWirdWiederverwendet()
    {
        var mem = new Words();
        var table = new LinuxFutexTable(Now, LongSlice);
        var mine = new LinuxFutexWaiter();
        for (int round = 0; round < 3; round++)
        {
            long r = 1;
            var t = Start(() => r = table.Wait(mem, Base, 0, uint.MaxValue, -1, waiter: mine));
            SpinUntil(() => table.WaitingCount == 1);
            Assert.AreEqual(1, table.Wake(Base, 1));
            Assert.IsTrue(t.Join(5000));
            Assert.AreEqual(0, r, "ein Wecken der Vorrunde gilt nicht weiter");
            Assert.AreEqual(-1, mine.Bucket);
        }

        Assert.AreEqual(-LinuxSyscalls.EAGAIN, table.Wait(mem, Base, 1, uint.MaxValue, -1, waiter: mine));
        Assert.AreEqual(0, table.Sleep(Now() + 1_000_000L, mine));
        Assert.AreEqual(-1, mine.Bucket);
    }

    /// <summary>Mutex nach Drepper (0 frei, 1 belegt, 2 belegt mit Wartenden), wie musl und glibc ihn über futex bauen.</summary>
    private static void Lock(Words mem, LinuxFutexTable table, ulong addr, LinuxFutexWaiter me)
    {
        ref int w = ref mem.At(addr);
        if (Interlocked.CompareExchange(ref w, 1, 0) == 0)
        {
            return;
        }

        while (Interlocked.Exchange(ref w, 2) != 0)
        {
            table.Wait(mem, addr, 2, uint.MaxValue, -1, waiter: me);
        }
    }

    private static void Unlock(Words mem, LinuxFutexTable table, ulong addr)
    {
        if (Interlocked.Exchange(ref mem.At(addr), 0) == 2)
        {
            table.Wake(addr, 1);
        }
    }

    [TestMethod]
    public void MutexeInGleichenUndVerschiedenenEimernZaehlenGenau()
    {
        // Scheibe 50 ms wie im Kernel: Ein verlorenes Wecken zeigte sich als Laufzeit, nicht als Hänger; die Zählung prüft den Ausschluss.
        var mem = new Words();
        var table = new LinuxFutexTable(Now);
        ulong[] locks = { Base, Base + 64, Base + 4 }; // zwei im selben Eimer, einer daneben
        var counters = new long[locks.Length];
        const int Threads = 6, Rounds = 20_000;
        var threads = new Thread[Threads];
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Threads; i++)
        {
            int k = i;
            threads[i] = Start(() =>
            {
                var me = new LinuxFutexWaiter();
                for (int r = 0; r < Rounds; r++)
                {
                    int m = (k + r) % locks.Length;
                    Lock(mem, table, locks[m], me);
                    long c = counters[m];
                    Thread.SpinWait(5);
                    counters[m] = c + 1; // ohne Ausschluss gingen hier Schritte verloren
                    Unlock(mem, table, locks[m]);
                }
            });
        }

        foreach (var t in threads)
        {
            Assert.IsTrue(t.Join(60_000), "Hänger");
        }

        Assert.AreEqual((long)Threads * Rounds, counters[0] + counters[1] + counters[2]);
        Assert.AreEqual(0, table.WaitingCount);
        Console.WriteLine($"{Threads}x{Rounds} Runden in {sw.ElapsedMilliseconds} ms, {table.WaitReturns} Rückkehrer aus Wait");
    }
}
