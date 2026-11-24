#if ZEHOS_SMP_PROBE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.Threading;
using ProjektZeh.Messaging;
using ProjektZeh.Scheduling;

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

    /// <summary>Vom Kernel nach dem Lesen der Topologie gesetzt; der Schritt placement braucht die Geschwister.</summary>
    public static CpuTopology? Topology { get; set; }

    public static bool Run()
    {
        bool ok = true;
        try
        {
            workers = ListWorkers();
#if ZEHOS_SMP_PROBE_PANIC
            ok &= Step("cpus", StepCpus);
            ok &= RunPanic(); // kehrt nur bei FAIL zurueck
#else
            ok &= Step("cpus", StepCpus);
            ok &= Step("counters", StepCounters);
            ok &= Step("parallel", StepParallel);
            ok &= Step("migrate", StepMigrate);
            ok &= Step("locks", StepLocks);
            ok &= Step("gc", StepGc);
            ok &= Step("threads", StepThreads);
            ok &= Step("placement", StepPlacement);
            ok &= Step("smt", StepSmt);
            ok &= Step("timedwait", StepTimedWait);
            Write("isolation INFO test-isolation-probe.py laeuft getrennt");
#endif
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
        // Lang genug (KVM auf MSI: etwa 200 ms je Worker), damit Threadende und Join (4 CPUs: bis 15 ms, 8 CPUs: bis 92 ms, #523) das Verhaeltnis nicht bestimmen.
        const long Work = 200_000_000;
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

    // E7-D: Ein Interaktiv- und ein Normal-Thread (Platzierung Workers) rechnen 500 ms und melden alle 50 ms ihre CPU.
    // Bestanden, wenn bei mindestens zwei Kernen unter den Worker-CPUs in mindestens 9 von 10 Meldungen die CPUs keine Geschwister sind.
    private static string? StepPlacement()
    {
        var topology = Topology;
        if (topology == null || !topology.Measured) return $"SKIP cpus={KernelCpus.Count}";
        var cores = new HashSet<int>();
        foreach (uint cpu in workers) if (CosmosBootOptions.WorkerCpus.Contains(cpu)) cores.Add(topology.CoreOf((int)cpu)); // mit zehos.smt=off bleibt oft nur ein Kern: SKIP
        if (cores.Count < 2) return $"SKIP cpus={KernelCpus.Count}";
        const int samples = 10;
        var interactive = new int[samples];
        var normal = new int[samples];
        var first = ThreadStarter.TryStart(() => Sample(interactive), threadClass: ThreadClass.Interactive, placement: ThreadPlacement.Workers);
        var second = ThreadStarter.TryStart(() => Sample(normal), threadClass: ThreadClass.Normal, placement: ThreadPlacement.Workers);
        if (!first.IsOK() || !second.IsOK()) return "Platzierungsthread startet nicht";
        if (first.Value?.Join(10000) != true || second.Value?.Join(10000) != true) return "Platzierungsthread endet nicht";
        int apart = 0;
        var line = new System.Text.StringBuilder();
        for (int i = 0; i < samples; i++)
        {
            int a = interactive[i], b = normal[i];
            bool sibling = a == b || (a >= 0 && b >= 0 && topology.CoreOf(a) == topology.CoreOf(b));
            if (!sibling) apart++;
            line.Append(a).Append('/').Append(b).Append(' ');
        }
        Write($"INFO placement apart={apart}/{samples} pairs={line.ToString().TrimEnd()}");
        return apart >= 9 ? null : $"nur {apart} von {samples} Meldungen auf getrennten Kernen";
    }

    // E7-D: So viele rechnende Normal-Threads wie Worker-CPUs melden 500 ms lang ihre CPU. Mit zehos.smt=off darf nie ein zweiter
    // logischer Prozessor eines Kerns auftauchen (FAIL); ohne den Schalter ist das Ergebnis nur INFO (dort treten sie bei voller Last auf).
    private static string? StepSmt()
    {
        var topology = Topology;
        if (topology == null || !topology.Measured || topology.CpuCount == topology.CoreCount) return $"SKIP cpus={KernelCpus.Count}";
        int count = workers.Length;
        if (count < 2) return $"SKIP cpus={KernelCpus.Count}";
        var seen = new int[count][];
        var threads = new Thread[count];
        for (int t = 0; t < count; t++)
        {
            int index = t;
            seen[index] = new int[10];
            var started = ThreadStarter.TryStart(() => Sample(seen[index]), threadClass: ThreadClass.Normal, placement: ThreadPlacement.Workers);
            if (!started.IsOK()) return "Thread startet nicht";
            threads[index] = started.Value!;
        }
        foreach (var thread in threads)
            if (!thread.Join(10000)) return "Thread endet nicht";
        var used = new SortedSet<int>();
        foreach (int[] list in seen) foreach (int cpu in list) if (cpu >= 0) used.Add(cpu);
        // Zweiter logischer Prozessor: nicht die kleinste Nummer seines Kerns unter allen Online-CPUs.
        var firstOfCore = new Dictionary<int, int>();
        for (int cpu = 0; cpu < KernelCpus.Count; cpu++)
        {
            int core = topology.CoreOf(cpu);
            if (!firstOfCore.ContainsKey(core)) firstOfCore[core] = cpu;
        }
        int second = 0;
        foreach (int cpu in used) if (firstOfCore[topology.CoreOf(cpu)] != cpu) second++;
        bool off = CosmosBootOptions.Current.SmtOff;
        Write($"INFO smt off={(off ? 1 : 0)} used={string.Join(",", used)} second={second}");
        return off && second > 0 ? $"{second} zweite logische Prozessoren trotz zehos.smt=off genutzt" : null;
    }

    private static void Sample(int[] cpus)
    {
        var watch = Stopwatch.StartNew();
        long sink = 0;
        for (int i = 0; i < cpus.Length; i++)
        {
            cpus[i] = (int)KernelCpus.Current;
            long until = (i + 1) * 50;
            while (watch.ElapsedMilliseconds < until) sink = sink * 31 + 7;
        }
        if (sink == 42) Write("INFO placement sink");
    }

    // E6-J: Ein Warten mit Frist muss nach Ablauf false liefern und darf nicht vor der Frist enden; mit Signal
    // vor Ablauf true. Alle drei Wege enden in Cosmos in LowLevelMonitor_TimedWait.
    private const int TimedWaitMs = 100;
    private const int TimedWaitSlackMs = 10; // Taktauflösung des Schedulers

    private static string? StepTimedWait()
    {
        var clock = Stopwatch.StartNew();
        using var unset = new ManualResetEvent(false);
        bool eventResult = unset.WaitOne(TimedWaitMs);
        long eventMs = clock.ElapsedMilliseconds;

        object gate = new();
        clock.Restart();
        bool monitorResult;
        lock (gate) monitorResult = Monitor.Wait(gate, TimedWaitMs);
        long monitorMs = clock.ElapsedMilliseconds;

        using var empty = new SemaphoreSlim(0);
        clock.Restart();
        bool semaphoreResult = empty.Wait(TimedWaitMs);
        long semaphoreMs = clock.ElapsedMilliseconds;

        // Gegenproben: Signal nach 20 ms, Frist 5 s.
        using var later = new ManualResetEvent(false);
        var setter = new Thread(() => { Thread.Sleep(20); later.Set(); });
        clock.Restart();
        setter.Start();
        bool signaledEvent = later.WaitOne(5000);
        long signaledEventMs = clock.ElapsedMilliseconds;
        if (!setter.Join(5000)) return "Setzer-Thread endet nicht";

        bool pulsed = false;
        var pulser = new Thread(() => { Thread.Sleep(20); lock (gate) { pulsed = true; Monitor.PulseAll(gate); } });
        clock.Restart();
        pulser.Start();
        bool signaledMonitor = true;
        lock (gate) while (!pulsed && signaledMonitor) signaledMonitor = Monitor.Wait(gate, 5000);
        long signaledMonitorMs = clock.ElapsedMilliseconds;
        if (!pulser.Join(5000)) return "Puls-Thread endet nicht";

        Write($"INFO timedwait event={eventResult}/{eventMs}ms monitor={monitorResult}/{monitorMs}ms " +
              $"semaphore={semaphoreResult}/{semaphoreMs}ms signal_event={signaledEvent}/{signaledEventMs}ms " +
              $"signal_monitor={signaledMonitor}/{signaledMonitorMs}ms");

        if (eventResult || monitorResult || semaphoreResult) return "Warten ohne Signal meldet true";
        long earliest = TimedWaitMs - TimedWaitSlackMs;
        if (eventMs < earliest || monitorMs < earliest || semaphoreMs < earliest) return "Warten endet vor der Frist";
        if (!signaledEvent || !signaledMonitor) return "Warten mit Signal meldet false";
        if (signaledEventMs >= 4000 || signaledMonitorMs >= 4000) return "Signal weckt erst nach der Frist";
        return null;
    }

#if ZEHOS_SMP_PROBE_PANIC
    private const int TickMs = 50;
    private const int PanicAfterMs = 1000;

    /// <summary>
    /// E7-B (Cosmos-Patch panic-stop-cpus): Ein Thread auf einer Worker-CPU schreibt alle 50 ms <c>SMP_PROBE tick</c>; nach
    /// 1 s loest ein Thread auf einer anderen CPU einen #PF aus. Er ist nicht der Wasm-Worker, der Ausnahme-Hook haelt also
    /// an (wie die Gegenprobe der Isolationsprobe). Nach <c>System halted.</c> darf keine tick-Zeile mehr folgen; das prueft
    /// <c>scripts/test-smp-probe.py --panic</c>. Die tick-Zeilen schreibt bewusst der Tickthread selbst: Haelt der NMI ihn
    /// mitten in einer Zeile mit der seriellen Sperre an, ist genau das der Fall, den der Patch abdecken muss.
    /// Kehrt nur zurueck, wenn die Probe nicht ausgefuehrt werden kann (FAIL).
    /// </summary>
    private static unsafe bool RunPanic()
    {
        if (KernelCpus.Count < 2) { Write($"panic FAIL nur {KernelCpus.Count} CPU, Halt anderer CPUs nicht pruefbar"); return false; }
        uint tickCpu = workers[workers.Length - 1];
        // Die fehlerhafte CPU muss eine andere sein, sonst belegt das Ende der Ticks nur den Halt der eigenen CPU.
        uint faultCpu = tickCpu == 0 ? 1u : 0u;
        var ticker = Pinned(tickCpu, () =>
        {
            for (int n = 1; ; n++)
            {
                Log.WriteString($"SMP_PROBE tick {n} cpu={KernelCpus.Current}\n");
                Thread.Sleep(TickMs);
            }
        });
        ticker.Start();
        Thread.Sleep(PanicAfterMs);
        var faulter = Pinned(faultCpu, () =>
        {
            Write($"panic fault cpu={KernelCpus.Current} tick_cpu={tickCpu}");
            // Unter Cosmos/NativeAOT ein echter #PF wie in CosmosIsolationProbe (negativer Fall).
            Volatile.Write(ref panicSink, *(int*)0);
            Write("panic FAIL nach dem #PF weitergelaufen");
        });
        faulter.Start();
        faulter.Join(5000);
        Write("panic FAIL System laeuft 5 s nach dem #PF weiter");
        return false;
    }

    private static int panicSink;
#endif

    private static void Write(string message) => Log.WriteString($"SMP_PROBE {message}\n");
}
#endif
