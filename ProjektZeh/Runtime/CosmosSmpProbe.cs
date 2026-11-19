#if ZEHOS_SMP_PROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.Threading;
using ProjektZeh.Messaging;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe E6-D: prüft auf mehreren CPUs Zähler, Parallelität, Migration, Sperren, GC und Threadstart/-ende.
/// Ausgabe nur aus dem Hauptthread (die serielle Ausgabe ist nicht zeilenatomar); Format in
/// docs/superpowers/plans/2026-11-18-multicore-e6-e7-verteilung.md, Paket E6-D. "Worker-CPUs" sind
/// <see cref="KernelCpus.Compute"/>; auf einer Maschine mit einer CPU ist das CPU 0.
/// </summary>
internal static class CosmosSmpProbe
{
    private const int HandoffCount = 10000;
    private static uint[] workers = [];

    public static bool Run()
    {
        bool ok = true;
        try
        {
            workers = ListWorkers();
            ok &= Step("cpus", StepCpus);
            ok &= Step("counters", StepCounters);
            ok &= Step("parallel", StepParallel);
            ok &= Step("migrate", StepMigrate);
            ok &= Step("locks", StepLocks);
            ok &= Step("gc", StepGc);
            ok &= Step("threads", StepThreads);
            Write("isolation INFO test-isolation-probe.py laeuft getrennt");
        }
        catch (Exception exception)
        {
            Write($"FAIL {exception.GetType().Name}: {exception.Message}");
            return false;
        }
        Write(ok ? "PASS" : "FAIL");
        return ok;
    }

    private static uint[] ListWorkers()
    {
        var set = KernelCpus.Compute;
        var list = new List<uint>();
        for (uint cpu = 0; cpu < (uint)KernelCpus.Count; cpu++)
            if (set.Contains(cpu)) list.Add(cpu);
        if (list.Count == 0) list.Add(0);
        return list.ToArray();
    }

    // Eine Ausnahme in einem Schritt zählt als dessen FAIL; die übrigen Schritte laufen weiter.
    private static bool Step(string name, Func<string?> body)
    {
        string? failure;
        try { failure = body(); }
        catch (Exception exception) { failure = $"{exception.GetType().Name}: {exception.Message}"; }
        if (failure == null) { Write($"{name} PASS"); return true; }
        if (failure.StartsWith("SKIP ", StringComparison.Ordinal)) { Write($"{name} {failure}"); return true; }
        Write($"{name} FAIL {failure}");
        return false;
    }

    private static string? StepCpus()
    {
        Write($"INFO cpus count={KernelCpus.Count} online={KernelCpus.Online.Count} workers={workers.Length}");
        return null;
    }

    private static Thread Pinned(uint cpu, Action body)
    {
        var thread = new Thread(() =>
        {
            // Pin wirkt auf den Aufrufer erst nach der Umschaltung; danach muss der Thread dort laufen.
            ThreadAffinity.Current = CpuSet.Single(cpu);
            body();
        });
        return thread;
    }

    private static bool JoinAll(List<Thread> threads, int timeoutMs)
    {
        bool joined = true;
        var clock = Stopwatch.StartNew();
        foreach (var thread in threads)
            joined &= thread.Join(Math.Max(1, timeoutMs - (int)clock.ElapsedMilliseconds));
        return joined;
    }

    private static string? StepCounters()
    {
        int n = workers.Length;
        var own = new long[n * 8]; // je Zähler eine eigene Cache-Zeile (8 × long)
        long shared = 0;
        int stop = 0;
        using var start = new ManualResetEvent(false);
        var threads = new List<Thread>();
        for (int i = 0; i < n; i++)
        {
            int index = i;
            var thread = Pinned(workers[i], () =>
            {
                start.WaitOne(5000);
                while (Volatile.Read(ref stop) == 0)
                {
                    own[index * 8]++;
                    Interlocked.Increment(ref shared);
                }
            });
            thread.Start();
            threads.Add(thread);
        }
        start.Set();
        Thread.Sleep(300);
        // Unter TCG laufen die vCPUs reihum und ein Thread kann in 300 ms noch gar nicht gezaehlt haben:
        // bis zu 10 s warten, bis jeder eigene Zaehler > 0 ist.
        var window = Stopwatch.StartNew();
        while (window.ElapsedMilliseconds < 10000 && !AllStarted(own, n)) Thread.Sleep(50);
        Volatile.Write(ref stop, 1);
        if (!JoinAll(threads, 5000)) return "Zaehlerthreads enden nicht";
        long sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (own[i * 8] <= 0) return $"Zaehler auf CPU {workers[i]} steht bei 0";
            sum += own[i * 8];
        }
        Write($"INFO counters shared={shared} sum={sum}");
        return shared == sum ? null : $"gemeinsamer Zaehler {shared} != Summe {sum}";
    }

    private static bool AllStarted(long[] own, int n)
    {
        for (int i = 0; i < n; i++)
            if (Volatile.Read(ref own[i * 8]) <= 0) return false;
        return true;
    }

    // Feste Rechenlast ohne Speicherzugriffe; das Ergebnis verhindert, dass der Bau die Schleife entfernt.
    private static long Spin(long iterations)
    {
        long x = 1;
        for (long i = 0; i < iterations; i++) x = x * 6364136223846793005L + 1442695040888963407L;
        return x;
    }

    private static string? StepParallel()
    {
        int n = workers.Length;
        if (n < 2) return $"SKIP cpus={KernelCpus.Count}";
        const long Work = 20_000_000;
        long sink = 0;
        var single = Stopwatch.StartNew();
        var one = Pinned(workers[0], () => Interlocked.Add(ref sink, Spin(Work * n)));
        one.Start();
        if (!one.Join(120000)) return "Einzellauf endet nicht";
        long singleMs = Math.Max(1, single.ElapsedMilliseconds);
        var threads = new List<Thread>();
        using var start = new ManualResetEvent(false);
        for (int i = 0; i < n; i++)
        {
            var thread = Pinned(workers[i], () => { start.WaitOne(5000); Interlocked.Add(ref sink, Spin(Work)); });
            thread.Start();
            threads.Add(thread);
        }
        Thread.Sleep(50); // alle Threads sind am Startsignal angekommen
        var all = Stopwatch.StartNew();
        start.Set();
        if (!JoinAll(threads, 120000)) return "Parallellauf endet nicht";
        long allMs = Math.Max(1, all.ElapsedMilliseconds);
        Write($"INFO parallel single_ms={singleMs} all_ms={allMs} workers={n} sink={sink & 0xFF}");
        Write($"INFO parallel speedup_permille={singleMs * 1000 / allMs}");
        return null;
    }

    private static string? StepMigrate()
    {
        int n = workers.Length;
        if (n < 2) return $"SKIP cpus={KernelCpus.Count}";
        string? failure = null;
        // Eigener Thread: der Hauptthread bleibt, wo Etappe 5 ihn hinstellt.
        var thread = new Thread(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                uint target = workers[i % n];
                ThreadAffinity.Current = CpuSet.Single(target);
                uint actual = KernelCpus.Current;
                if (actual != target) { failure = $"Schritt {i}: Ziel {target}, Lauf auf {actual}"; return; }
            }
        });
        thread.Start();
        if (!thread.Join(60000)) return "Migrationsthread endet nicht";
        return failure;
    }

    private sealed class Handoff
    {
        private readonly object gate = new();
        private int slot;
        private bool full;

        public bool Put(int value)
        {
            lock (gate)
            {
                while (full) if (!Monitor.Wait(gate, 5000)) return false;
                slot = value;
                full = true;
                Monitor.PulseAll(gate);
                return true;
            }
        }

        public bool Take(out int value)
        {
            lock (gate)
            {
                while (!full) if (!Monitor.Wait(gate, 5000)) { value = 0; return false; }
                value = slot;
                full = false;
                Monitor.PulseAll(gate);
                return true;
            }
        }
    }

    private static string? StepLocks()
    {
        int n = workers.Length;
        var errors = new string?[n * 2];
        var threads = new List<Thread>();
        for (int i = 0; i < n; i++)
        {
            int index = i;
            uint producerCpu = workers[i];
            uint consumerCpu = workers[(i + 1) % n];
            var monitor = new Handoff();
            var queue = new MessageQueue<int>(8);
            var monitorProducer = Pinned(producerCpu, () =>
            {
                for (int v = 1; v <= HandoffCount; v++)
                    if (!monitor.Put(v)) { errors[index * 2] = $"Monitor: Senden von {v} laeuft in Zeitlimit"; return; }
            });
            var monitorConsumer = Pinned(consumerCpu, () =>
            {
                for (int v = 1; v <= HandoffCount; v++)
                {
                    if (!monitor.Take(out int got)) { errors[index * 2] = $"Monitor: Empfang von {v} laeuft in Zeitlimit"; return; }
                    if (got != v) { errors[index * 2] = $"Monitor: erwartet {v}, erhalten {got}"; return; }
                }
            });
            var queueProducer = Pinned(producerCpu, () =>
            {
                for (int v = 1; v <= HandoffCount; v++)
                    if (queue.Send(v, 5000) != MessageQueueResult.Success) { errors[index * 2 + 1] = $"Queue: Senden von {v} fehlgeschlagen"; return; }
                queue.Close();
            });
            var queueConsumer = Pinned(consumerCpu, () =>
            {
                int expected = 1;
                while (true)
                {
                    var result = queue.Receive(out int got, 5000);
                    if (result == MessageQueueResult.Closed) break;
                    if (result != MessageQueueResult.Success) { errors[index * 2 + 1] = $"Queue: Empfang nach {expected - 1} Nachrichten: {result}"; return; }
                    if (got != expected) { errors[index * 2 + 1] = $"Queue: erwartet {expected}, erhalten {got}"; return; }
                    expected++;
                }
                if (expected != HandoffCount + 1) errors[index * 2 + 1] = $"Queue: {expected - 1} statt {HandoffCount} Nachrichten";
            });
            foreach (var thread in new[] { monitorProducer, monitorConsumer, queueProducer, queueConsumer })
            {
                thread.Start();
                threads.Add(thread);
            }
        }
        var clock = Stopwatch.StartNew();
        if (!JoinAll(threads, 180000)) return "Sperrenthreads enden nicht";
        Write($"INFO locks handoffs_per_pair={HandoffCount} ms={clock.ElapsedMilliseconds}");
        foreach (var error in errors)
            if (error != null) return error;
        return null;
    }

    private static string? StepGc()
    {
        int n = workers.Length;
        var results = new string?[n];
        long maxGapTicks = 0;
        int stop = 0;
        int workerStop = 0;
        var threads = new List<Thread>();
        for (int i = 0; i < n; i++)
        {
            int index = i;
            var thread = Pinned(workers[i], () =>
            {
                var retained = new byte[16][];
                var sums = new long[16];
                var random = new Random(1234 + index);
                while (Volatile.Read(ref workerStop) == 0)
                {
                    int slot = random.Next(16);
                    if (retained[slot] != null && Checksum(retained[slot]) != sums[slot]) { results[index] = $"Pruefsumme Feld {slot} falsch"; return; }
                    int size = 64 << random.Next(11); // 64 B .. 64 KiB
                    var field = new byte[size];
                    for (int b = 0; b < size; b++) field[b] = (byte)(b * 31 + slot + index);
                    retained[slot] = field;
                    sums[slot] = Checksum(field);
                }
                for (int slot = 0; slot < 16; slot++)
                    if (retained[slot] != null && Checksum(retained[slot]) != sums[slot]) { results[index] = $"Pruefsumme Feld {slot} falsch (Ende)"; return; }
            });
            thread.Start();
            threads.Add(thread);
        }
        // Taktthread auf der Standard-CPU: die größte Lücke zwischen zwei 1-ms-Schlafzeiten ist die GC-Pause.
        var ticker = new Thread(() =>
        {
            long last = Stopwatch.GetTimestamp();
            while (Volatile.Read(ref stop) == 0)
            {
                Thread.Sleep(1);
                long now = Stopwatch.GetTimestamp();
                long gap = now - last;
                if (gap > Volatile.Read(ref maxGapTicks)) Volatile.Write(ref maxGapTicks, gap);
                last = now;
            }
        });
        ticker.Start();
        var clock = Stopwatch.StartNew();
        int collections = 0;
        while (clock.ElapsedMilliseconds < 2000 || collections < 20)
        {
            if (collections < 20) { GC.Collect(); collections++; }
            Thread.Sleep(collections < 20 ? 90 : 10);
        }
        Volatile.Write(ref workerStop, 1);
        bool joined = JoinAll(threads, 10000);
        Volatile.Write(ref stop, 1);
        ticker.Join(2000);
        long gapMs = Volatile.Read(ref maxGapTicks) * 1000 / Stopwatch.Frequency;
        Write($"INFO gc max_pause_ms={gapMs} collections={collections}");
        if (!joined) return "Allokationsthreads enden nicht";
        foreach (var result in results)
            if (result != null) return result;
        return null;
    }

    private static long Checksum(byte[] data)
    {
        long sum = 17;
        for (int i = 0; i < data.Length; i++) sum = sum * 31 + data[i];
        return sum;
    }

    private static string? StepThreads()
    {
        GC.Collect();
        ulong before = CosmosSystemInfo.PhysicalMemory().Free;
        int ran = 0;
        for (int i = 0; i < 100; i++)
        {
            var thread = new Thread(() => Interlocked.Increment(ref ran));
            thread.Start();
            if (!thread.Join(5000)) return $"Thread {i} endet nicht";
        }
        GC.Collect();
        ulong after = CosmosSystemInfo.PhysicalMemory().Free;
        long delta = ((long)before - (long)after) / 1024;
        Write($"INFO threads free_delta_kib={delta}");
        return ran == 100 ? null : $"nur {ran} von 100 Threads liefen";
    }

    private static void Write(string message) => Log.WriteString($"SMP_PROBE {message}\n");
}
#endif
