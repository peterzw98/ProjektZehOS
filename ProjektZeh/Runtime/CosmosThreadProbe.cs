#if ZEHOS_THREAD_PROBE
using System;
using System.Diagnostics;
using System.Threading;
using Cosmos.Kernel.Core.Scheduler;
using Cosmos.Kernel.System.Diagnostics;

// Die öffentliche Scheduler-Schnittstelle ist in Cosmos 3.0.94 experimentell.
#pragma warning disable COSMOS0001

namespace ProjektZeh.Runtime;

internal static class CosmosThreadProbe
{
    public static bool Run()
    {
        try
        {
            Check(SchedulerManager.IsReady, "Scheduler nicht bereit.");
            var scheduler = SchedulerManager.Current ?? throw new InvalidOperationException("Scheduler fehlt.");
            Write($"Scheduler={scheduler.Name}; Tick={SchedulerManager.DefaultQuantumNs}ns");
            CheckPreemption();
            CheckWaiting();
            Write("PASS");
            return true;
        }
        catch (Exception exception)
        {
            Write($"FAIL {exception.Message}");
            return false;
        }
    }

    private static void CheckPreemption()
    {
        using var start = new ManualResetEvent(false);
        int stop = 0;
        int firstCount = 0;
        int secondCount = 0;
        int waitFailures = 0;
        var first = new Thread(() => Count(ref firstCount));
        var second = new Thread(() => Count(ref secondCount));
        bool firstStarted = false;
        bool secondStarted = false;
        bool firstJoined = false;
        bool secondJoined = false;
        try
        {
            first.Start();
            firstStarted = true;
            second.Start();
            secondStarted = true;
            start.Set();
            var clock = Stopwatch.StartNew();
            Thread.Sleep(200);
            Check(clock.ElapsedMilliseconds >= 150, "Sleep kehrte zu früh zurück.");
            int firstBefore = Volatile.Read(ref firstCount);
            int secondBefore = Volatile.Read(ref secondCount);
            Thread.Sleep(200);
            Check(firstBefore > 0 && secondBefore > 0 &&
                Volatile.Read(ref firstCount) > firstBefore && Volatile.Read(ref secondCount) > secondBefore,
                "Nicht beide Rechenaufgaben machen ohne Yield Fortschritt.");
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            start.Set();
            firstJoined = !firstStarted || first.Join(2000);
            secondJoined = !secondStarted || second.Join(2000);
        }
        Check(firstJoined && secondJoined, "Rechenaufgaben enden nicht innerhalb des Join-Zeitlimits.");
        Check(waitFailures == 0, "Startsignal nicht empfangen.");
        Write($"preemption PASS first={firstCount} second={secondCount}; sleep/stop/join PASS");

        void Count(ref int count)
        {
            if (!start.WaitOne(2000))
            {
                Interlocked.Increment(ref waitFailures);
                return;
            }
            // Kein Sleep/Yield: Fortschritt beider Worker erfordert Timer-Verdrängung.
            while (Volatile.Read(ref stop) == 0) Interlocked.Increment(ref count);
        }
    }

    private static void CheckWaiting()
    {
        using var signal = new AutoResetEvent(false);
        using var entered = new ManualResetEvent(false);
        int received = 0;
        var waiter = new Thread(() =>
        {
            entered.Set();
            if (signal.WaitOne(2000)) Interlocked.Exchange(ref received, 1);
        });
        waiter.Start();
        bool joined;
        try
        {
            Check(entered.WaitOne(2000), "Wartethread startet nicht.");
            Thread.Sleep(40);
            Check((waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, "Wartethread ist nicht blockiert.");
            signal.Set();
            joined = waiter.Join(2000);
        }
        finally
        {
            signal.Set();
            if (waiter.IsAlive) waiter.Join(2000);
        }
        Check(joined && Volatile.Read(ref received) == 1, "Wartethread wurde nicht aufgeweckt.");
        signal.Reset();
        signal.Set();
        Check(signal.WaitOne(0), "Vorher gesetztes Signal verloren.");
        var clock = Stopwatch.StartNew();
        Check(!signal.WaitOne(30), "Ungesetztes Signal meldet Erfolg.");
        Check(clock.ElapsedMilliseconds >= 20, "Wartezeitlimit kehrte zu früh zurück.");
        Thread.Yield();
        Write("wait/wake/presignal/timeout/yield PASS");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Write(string message) => Log.WriteString($"THREAD_PROBE {message}\n");
}
#endif
