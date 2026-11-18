#if ZEHOS_ISOLATION_PROBE
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Cosmos.Build.API.Attributes;
using Cosmos.Kernel.Core.Scheduler;
using ProjektZeh.Scheduling;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime;

#pragma warning disable COSMOS0001 // Cosmos-Scheduler-Typen gelten als Vorschau.

// Nur Diagnose 12.4-I1 (Branch diag/i1-fault-isolation, nie mergen): Cosmos 3.0.97 endet jeden CPU-Ausnahmepfad mit
// Panic.CpuException -> Panic.HaltCpu(); der Plug ersetzt den Endlos-Halt.
[Plug(TargetName = "Cosmos.Kernel.Panic")]
public static class CosmosPanicHaltPlug
{
    // Koerper wird nach Cosmos.Kernel kopiert: nur ein Aufruf in ProjektZeh, kein Typ aus Cosmos.Kernel.
    [PlugMember]
    public static void HaltCpu() => FaultIsolation.OnHalt();
}

/// <summary>
/// Gibt den Wasm-Worker auf, wenn er in Gastcode eine CPU-Ausnahme ausloest: Quarantaene im eigenen Scheduler und
/// Wechsel in den naechsten Thread noch im Ausnahmepfad. Sonst haelt das System wie bisher an.
/// </summary>
internal static class FaultIsolation
{
    private const string SchedulerManagerType = "Cosmos.Kernel.Core.Scheduler.SchedulerManager, Cosmos.Kernel.Core";
    private const string InternalCpuType = "Cosmos.Kernel.Core.CPU.InternalCpu, Cosmos.Kernel.Core";

    private static int workerThreadId = -1;
    private static int inGuest;
    private static int isolatedThreadId = -1;

    /// <summary>Vom Worker selbst gerufen, damit die Id seinem SchedulerThread entspricht.</summary>
    internal static void RegisterWorker()
    {
        SchedulerThread? current = SchedulerManager.GetCpuState(0)?.CurrentThread;
        if (current != null) Volatile.Write(ref workerThreadId, (int)current.Id);
    }

    internal static void EnterGuest() => Volatile.Write(ref inGuest, 1);

    internal static void LeaveGuest() => Volatile.Write(ref inGuest, 0);

    /// <summary>Id des zuletzt aufgegebenen Workers oder -1; liest und loescht.</summary>
    internal static int TakeIsolated() => Interlocked.Exchange(ref isolatedThreadId, -1);

    // Laeuft im Ausnahmepfad mit gesperrten Interrupts auf dem Stack des fehlerhaften Threads: nichts allokieren, nichts
    // sperren, nicht protokollieren (Log kann eine Sperre eines unterbrochenen Threads brauchen).
    internal static unsafe void OnHalt()
    {
        PerCpuState? state = SchedulerManager.GetCpuState(0);
        SchedulerThread? current = state?.CurrentThread;
        // Unter StorageLock oder AppStorageGate aufgeben hiesse: Sperre fuer immer gehalten (Hinweis agent-peter-senior).
        if (state != null && current != null && (int)current.Id == Volatile.Read(ref workerThreadId) && Volatile.Read(ref inGuest) == 1
            && !StorageLock.IsHeldByCurrentThread && !AppStorageGate.IsHeldByCurrentThread)
        {
            int id = (int)current.Id;
            Volatile.Write(ref inGuest, 0);
            Volatile.Write(ref workerThreadId, -1);
            CosmosRoundRobinScheduler.Quarantine(id);
            Volatile.Write(ref isolatedThreadId, id);
            // Der gespeicherte Stackzeiger wird nie wieder geladen; er muss nur im Stack des Threads liegen.
            ScheduleFromInterrupt(null, state.CpuId, (nuint)(&id));
            return;
        }

        while (true) Halt(null);
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ScheduleFromInterrupt")]
    private static extern void ScheduleFromInterrupt([UnsafeAccessorType(SchedulerManagerType)] object? owner, uint cpuId, nuint currentRsp);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Halt")]
    private static extern void Halt([UnsafeAccessorType(InternalCpuType)] object? owner);
}

/// <summary>Gastprobe I1: #DE und #PF im Wasm-Worker, danach neuer Worker, GC und ein weiterer Lauf.</summary>
internal static class CosmosIsolationProbe
{
    private static volatile int zero;
    private static volatile int sink;

    internal static void Start() => new Thread(Probe).Start();

    private static unsafe void Probe()
    {
        Log("ISOLATION_PROBE start");
        zero = 0;
        Fault("de", () => sink = 1 / zero);
        Fault("pf", () => sink = *(int*)0);
        GC.Collect();
        Log("ISOLATION_PROBE gc ok");
        Fault("none", () => sink = 1);
        Log("ISOLATION_PROBE DONE");
    }

    private static void Fault(string kind, Action trigger)
    {
        int done = 0;
        Runtime.Wasm.WasmRuntime.ProbeEnqueue(() =>
        {
            FaultIsolation.EnterGuest();
            try
            {
                trigger();
                Log("ISOLATION_PROBE " + kind + " returned");
            }
            catch (Exception exception)
            {
                Log("ISOLATION_PROBE " + kind + " managed-exception " + exception.GetType().Name);
            }
            finally
            {
                FaultIsolation.LeaveGuest();
                Volatile.Write(ref done, 1);
            }
        });

        for (int wait = 0; wait < 100; wait++)
        {
            if (Volatile.Read(ref done) == 1) return;
            int isolated = FaultIsolation.TakeIsolated();
            if (isolated >= 0)
            {
                Log("ISOLATION_PROBE fault=" + kind + " handled=1 thread=" + isolated + " (Neustart empfohlen)");
                Runtime.Wasm.WasmRuntime.ProbeReplaceWorker();
                return;
            }
            Thread.Sleep(100);
        }
        Log("ISOLATION_PROBE " + kind + " TIMEOUT");
    }

    private static void Log(string text) => Cosmos.Kernel.System.Diagnostics.Log.WriteString(text + "\n");
}
#endif
