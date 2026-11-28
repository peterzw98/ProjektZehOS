using System.Diagnostics;
using System.Threading;
using Cosmos.Kernel.System.Timers;
using Cosmos.TestRunner.Framework;
using Sys = Cosmos.Kernel.System;
using SysThread = System.Threading.Thread;
using Monitor = System.Threading.Monitor;

namespace Cosmos.Kernel.Tests.Threading;

/// <summary>
/// N4-SMP S0 (ZehOS, Entwurf docs/superpowers/specs/2026-11-28-ladybird-n4-smp-entwurf.md): belegt im Gast auf
/// mehreren CPUs, dass Monitor.Enter/Exit und Wait/Pulse über CPUs tragen. Nur Test, keine Änderung am Kern.
/// Jeder Arbeiter bindet sich selbst auf seine CPU (ThreadAffinity.Set braucht einen gestarteten Thread) und prüft
/// am Ende, dass er dort gelaufen ist. Ein verlorenes Wecken zeigt sich als Zeitüberschreitung von Wait (gezählt,
/// Erwartung 0), ein Hänger als Join-Zeitlimit mit dem Zählerstand in der Meldung.
/// </summary>
internal static class MonitorCrossCpuCells
{
    // 2·10^4 Runden je Arbeiter: unter TCG smp4 gemessen 10^5 Runden = 4,5 s je Wake-Zelle (KVM 0,5 s); so bleibt die Zelle klein gegen das Runner-Limit. Join-Grenze weit, damit ein Hänger mit Zählerstand gemeldet wird.
    private const int Rounds = 20_000;
    private const int JoinMs = 600_000;
    private const int WaitMs = 5_000;

    private static readonly object s_gate = new();
    private static int s_turn;
    private static int s_progress;
    private static int s_timeouts;
    private static int s_wrongCpu;
    private static int s_counterPlain;
    private static volatile bool s_failed;
    private static int s_ready;
    private static volatile bool s_go;
    private const int ContendedRounds = 300_000;

    private sealed record PingPongArgs(int Me, uint Cpu, bool PulseAll);

    private static void Reset()
    {
        s_turn = 0;
        s_progress = 0;
        s_timeouts = 0;
        s_wrongCpu = 0;
        s_counterPlain = 0;
        s_failed = false;
        s_ready = 0;
        s_go = false;
    }

    private static void Pin(uint cpu)
    {
        Sys.Threading.ThreadAffinity.Current = Sys.Threading.CpuSet.Single(cpu);
    }

    // Wartet mit Zeitlimit; ein Ablauf der Frist ist ein verlorenes Wecken (der Partner pulst nach jedem Zug).
    private static void PingPongWorker(object? state)
    {
        PingPongArgs args = (PingPongArgs)state!;
        int me = args.Me;
        uint cpu = args.Cpu;
        bool pulseAll = args.PulseAll;
        Pin(cpu);
        for (int i = 0; i < Rounds && !s_failed; i++)
        {
            lock (s_gate)
            {
                while (s_turn != me)
                {
                    if (!Monitor.Wait(s_gate, WaitMs))
                    {
                        // Frist abgelaufen: Zug trotzdem nur nehmen, wenn er jetzt dran ist (Wecken verloren, Wert aber da).
                        Interlocked.Increment(ref s_timeouts);
                        if (s_turn != me)
                        {
                            s_failed = true;
                            return;
                        }
                    }
                }

                if (Sys.Threading.KernelCpus.Current != cpu)
                {
                    Interlocked.Increment(ref s_wrongCpu);
                }

                s_progress++;
                s_turn = 1 - me;
                if (pulseAll)
                {
                    Monitor.PulseAll(s_gate);
                }
                else
                {
                    Monitor.Pulse(s_gate);
                }
            }
        }
    }

    private static void RunPingPong(uint cpuA, uint cpuB, bool pulseAll)
    {
        Reset();
        SysThread a = new(PingPongWorker);
        SysThread b = new(PingPongWorker);
        a.Start(new PingPongArgs(0, cpuA, pulseAll));
        b.Start(new PingPongArgs(1, cpuB, pulseAll));
        bool joinedA = a.Join(JoinMs);
        bool joinedB = b.Join(JoinMs);
        if (!joinedA || !joinedB)
        {
            s_failed = true;
        }

        Assert.True(joinedA && joinedB, "both workers finish (progress " + s_progress + " of " + 2 * Rounds + ")");
        Assert.Equal(2 * Rounds, s_progress, "every handover happened exactly once");
        Assert.Equal(0, s_timeouts, "no Wait ran into its timeout: no wake was lost");
        Assert.Equal(0, s_wrongCpu, "each worker ran on the CPU it was pinned to");
    }

    /// <summary>Wait/PulseAll im Wechsel zwischen CPU 1 und CPU 2.</summary>
    public static void WaitPulseAllPingPong()
    {
        RunPingPong(1, 2, pulseAll: true);
    }

    /// <summary>Wait/Pulse (ein Wartender) im Wechsel zwischen CPU 1 und CPU 2.</summary>
    public static void WaitPulsePingPong()
    {
        RunPingPong(1, 2, pulseAll: false);
    }

    /// <summary>Dasselbe auf dem Paar CPU 2 und 3, damit mehr als ein Paar geprüft ist.</summary>
    public static void WaitPulsePingPongOtherPair()
    {
        RunPingPong(2, 3, pulseAll: false);
    }

    private sealed record CpuArg(uint Cpu);

    private static void ContendedWorker(object? state)
    {
        uint cpu = ((CpuArg)state!).Cpu;
        Pin(cpu);
        // Startschranke: ohne sie ist der erste Arbeiter fertig, bevor der zweite läuft (eine Vorstufe ohne Schranke blieb auch ohne lock grün; Gegenprobe mit Schranke siehe PR/Review).
        Interlocked.Increment(ref s_ready);
        while (!s_go)
        {
            Thread.SpinWait(10);
        }

        for (int i = 0; i < ContendedRounds; i++)
        {
            lock (s_gate)
            {
                // Absichtlich nicht atomar: nur der Monitor schützt.
                int v = s_counterPlain;
                s_counterPlain = v + 1;
            }
        }

        Interlocked.Increment(ref s_progress);
    }

    /// <summary>Drei Arbeiter auf CPU 1 bis 3 zählen je 3·10^5 Mal unter derselben Sperre; die Summe muss stimmen.</summary>
    public static void EnterExitContendedAcrossCpus()
    {
        Reset();
        SysThread[] workers = { new(ContendedWorker), new(ContendedWorker), new(ContendedWorker) };
        for (uint i = 0; i < 3; i++)
        {
            workers[i].Start(new CpuArg(i + 1));
        }

        for (int i = 0; i < 6000 && Volatile.Read(ref s_ready) < 3; i++)
        {
            TimerManager.Wait(10);
        }

        Assert.Equal(3, Volatile.Read(ref s_ready), "all three workers reached the start barrier");
        s_go = true;
        bool all = true;
        foreach (SysThread worker in workers)
        {
            all &= worker.Join(JoinMs);
        }

        Assert.True(all, "all three workers finish (done " + s_progress + " of 3)");
        Assert.Equal(3 * ContendedRounds, s_counterPlain, "the plain counter lost no increment: Monitor excludes across CPUs (sum " + s_counterPlain + " of " + 3 * ContendedRounds + ")");
    }
}
