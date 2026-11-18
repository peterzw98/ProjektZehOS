using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Build.API.Attributes;
using Cosmos.Kernel.Core.Scheduler;
using ProjektZeh.Storage;

namespace ProjektZeh.Runtime;

#pragma warning disable COSMOS0001 // Cosmos-Scheduler-Typen gelten als Vorschau.

// Cosmos 3.0.97 endet jeden CPU-Ausnahmepfad in Panic.CpuException -> Panic.HaltCpu() (Cosmos.Kernel.dll, IL gelesen
// 18.11.). Der Plug ersetzt den Endlos-Halt; ausserhalb von Gastcode des Wasm-Workers haelt er unveraendert an.
// Immer im Build, auch ohne Wasm: scripts/check-plug-binding.py prueft die Quellen ohne Build-Konstanten.
[Plug(TargetName = "Cosmos.Kernel.Panic")]
public static class CosmosPanicHaltPlug
{
    // Der Koerper wird nach Cosmos.Kernel kopiert und darf keinen Typ von dort nennen: nur ein Aufruf in ProjektZeh.
    [PlugMember]
    public static void HaltCpu() => FaultIsolation.OnHalt();
}

/// <summary>
/// Gibt den Wasm-Worker auf, wenn er in Gastcode eine CPU-Ausnahme ausloest (Roadmap 12.4-I1): Quarantaene im eigenen
/// Scheduler und Wechsel in den naechsten Thread noch im Ausnahmepfad. Den Lauf beendet danach die Hauptschleife
/// (<c>WasmRuntime.PollFaultIsolation</c>).
/// </summary>
internal static unsafe class FaultIsolation
{
    private const string SchedulerManagerType = "Cosmos.Kernel.Core.Scheduler.SchedulerManager, Cosmos.Kernel.Core";
    private const string InternalCpuType = "Cosmos.Kernel.Core.CPU.InternalCpu, Cosmos.Kernel.Core";

    /// <summary>Vom eigenen Scheduler gelesen: aufgegebene Worker werden nie wieder eingereiht.</summary>
    internal static readonly ThreadQuarantine Quarantine = new(SchedulerThread.MaxThreadCount);

    private static int workerThreadId = -1;
    private static int inNativeRun;
    private static volatile int* hostDepth;
    private static int isolatedThreadId = -1;

    /// <summary>Vom Worker selbst beim Start gerufen, damit die Id seinem SchedulerThread entspricht.</summary>
    internal static void RegisterWorker()
    {
#if ZEHOS_WASM
        hostDepth = HostDepthAddress();
#endif
        SchedulerThread? current = SchedulerManager.GetCpuState(0)?.CurrentThread;
        if (current != null) Volatile.Write(ref workerThreadId, (int)current.Id);
    }

    /// <summary>Um den nativen Lauf (wasm3 oder AOT); Hostaufrufe darin zaehlt die C-Schicht.</summary>
    internal static void EnterNativeRun() => Volatile.Write(ref inNativeRun, 1);

    internal static void LeaveNativeRun() => Volatile.Write(ref inNativeRun, 0);

    /// <summary>Id des zuletzt aufgegebenen Workers oder -1; liest und loescht.</summary>
    internal static int TakeIsolated() => Interlocked.Exchange(ref isolatedThreadId, -1);

    // Laeuft im Ausnahmepfad mit gesperrten Interrupts auf dem Stack des fehlerhaften Threads: nichts allokieren, nichts
    // sperren, nicht protokollieren (das Protokoll kann eine Sperre eines unterbrochenen Threads brauchen).
    internal static void OnHalt()
    {
        PerCpuState? state = SchedulerManager.GetCpuState(0);
        SchedulerThread? current = state?.CurrentThread;
        int* depth = hostDepth;
        if (state != null && current != null && depth != null
            && FaultIsolationPolicy.Decide((int)current.Id, Volatile.Read(ref workerThreadId), Volatile.Read(ref inNativeRun) == 1,
                *depth, StorageLock.IsHeldByCurrentThread, AppStorageGate.IsHeldByCurrentThread) == IsolationDecision.Isolate
            && Quarantine.Add((int)current.Id))
        {
            int id = (int)current.Id;
            Volatile.Write(ref inNativeRun, 0);
            Volatile.Write(ref workerThreadId, -1);
            Volatile.Write(ref isolatedThreadId, id);
            // Der gespeicherte Stackzeiger wird nie wieder geladen; er muss nur im Stack dieses Threads liegen.
            ScheduleFromInterrupt(null, state.CpuId, (nuint)(&id));
            return;
        }

        while (true) Halt(null);
    }

#if ZEHOS_WASM
    [DllImport("*", EntryPoint = "zehos_host_depth_address", ExactSpelling = true)]
    private static extern int* HostDepthAddress();
#endif

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ScheduleFromInterrupt")]
    private static extern void ScheduleFromInterrupt([UnsafeAccessorType(SchedulerManagerType)] object? owner, uint cpuId, nuint currentRsp);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Halt")]
    private static extern void Halt([UnsafeAccessorType(InternalCpuType)] object? owner);
}
