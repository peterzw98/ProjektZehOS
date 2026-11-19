using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.Core.CPU;
using Cosmos.Kernel.Core.Scheduler;
using ProjektZeh.Storage;
using Serial = Cosmos.Kernel.System.Diagnostics.Log;

namespace ProjektZeh.Runtime;

#pragma warning disable COSMOS0001 // Cosmos-Scheduler-Typen und der Ausnahme-Hook gelten als Vorschau.

/// <summary>
/// Gibt den Wasm-Worker auf, wenn er in Gastcode eine CPU-Ausnahme ausloest (Roadmap 12.4-I1): Cosmos ruft dafuer den
/// Ausnahme-Hook (<see cref="CpuExceptionHook"/>, Patch ausnahme-hook im Branch cosmos), ZehOS entscheidet, stellt den
/// Thread unter Quarantaene im eigenen Scheduler, und Cosmos wechselt noch im Ausnahmepfad in den naechsten Thread. Den
/// Lauf beendet danach die Hauptschleife (<c>WasmRuntime.PollFaultIsolation</c>).
/// </summary>
internal static unsafe class FaultIsolation
{
    /// <summary>Vom eigenen Scheduler gelesen: aufgegebene Worker werden nie wieder eingereiht.</summary>
    internal static readonly ThreadQuarantine Quarantine = new(SchedulerThread.MaxThreadCount);

    private static int workerThreadId = -1;
    private static int inNativeRun;
    private static volatile int* hostDepth;
    private static int isolatedThreadId = -1;
    private static long isolatedVector = FaultIsolationPolicy.UnknownVector;
    private static ulong isolatedRip;

    /// <summary>Vom Worker selbst beim Start gerufen, damit die Id seinem SchedulerThread entspricht.</summary>
    internal static void RegisterWorker()
    {
#if ZEHOS_WASM
        hostDepth = HostDepthAddress();
#else
        // Ohne Wasm-Laufzeit gibt es keinen Zaehler; OnCpuException haelt dann immer an.
        hostDepth = null;
#endif
        // Der Worker kann zwischen zwei Lesezugriffen die CPU wechseln (seit SMP 5b laufen Threads auf allen CPUs);
        // KernelCpus.Current mit GetCpuState lieferte dann den Thread einer anderen CPU. Cosmos' CurrentThread liest CPU
        // und Thread mit maskierten Interrupts in einem Schritt.
        SchedulerThread? current = CurrentThread(null);
        if (current != null) Volatile.Write(ref workerThreadId, (int)current.Id);
    }

    // In a6bd07d8 intern; oeffentlich gibt es nur GetCpuState(cpu), das ohne Maske nicht wanderungssicher ist.
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "get_CurrentThread")]
    private static extern SchedulerThread? CurrentThread(
        [UnsafeAccessorType("Cosmos.Kernel.Core.Scheduler.SchedulerManager, Cosmos.Kernel.Core")] object? owner);

    /// <summary>Um den nativen Lauf (wasm3 oder AOT); Hostaufrufe darin zaehlt die C-Schicht.</summary>
    internal static void EnterNativeRun() => Volatile.Write(ref inNativeRun, 1);

    internal static void LeaveNativeRun() => Volatile.Write(ref inNativeRun, 0);

    /// <summary>Id des zuletzt aufgegebenen Workers oder -1; liest und loescht.</summary>
    internal static int TakeIsolated() => Interlocked.Exchange(ref isolatedThreadId, -1);

    /// <summary>Vektor der zuletzt isolierten Ausnahme (fuer Protokoll und Gastprobe).</summary>
    internal static long IsolatedVector => Volatile.Read(ref isolatedVector);

    /// <summary>RIP der zuletzt isolierten Ausnahme (fuer das Protokoll).</summary>
    internal static ulong IsolatedRip => Volatile.Read(ref isolatedRip);

    /// <summary>
    /// Ausnahme-Hook, in <c>Kernel.Run</c> vor dem Scheduler gesetzt. Cosmos ruft ihn nach Register- und Stackausgabe und
    /// nur fuer die erste, nicht verschachtelte Ausnahme; bei <see cref="CpuExceptionAction.AbandonThread"/> haelt Cosmos
    /// trotzdem an, wenn es keinen anderen Thread einplanen kann.
    /// </summary>
    // Laeuft mit gesperrten Interrupts auf dem Stack des fehlerhaften Threads: nichts allokieren, nichts sperren, nicht
    // protokollieren (das Protokoll kann eine Sperre eines unterbrochenen Threads brauchen); nur die serielle Zeile unten.
    internal static CpuExceptionAction OnCpuException(in CpuExceptionInfo exception)
    {
        long vector = exception.Vector <= 255 ? (long)exception.Vector : FaultIsolationPolicy.UnknownVector;
        // Cosmos liest den Thread der fehlerhaften CPU selbst (Panic.CpuException, mit Maske, ohne Sperre); vorher las
        // ZehOS fest CPU 0 und haette auf einer anderen CPU den falschen Thread isoliert.
        int currentId = exception.ThreadId == CpuExceptionInfo.NoThread ? -1 : (int)exception.ThreadId;

        // Belegt in der Gastprobe, welchen Thread die Ausnahme traf (NMI-Gegenprobe: muss der Worker im Gastcode sein).
        Serial.WriteString("ZehOS: Thread ");
        Serial.WriteNumber(currentId);
        Serial.WriteString(", Wasm-Worker ");
        Serial.WriteNumber(Volatile.Read(ref workerThreadId));
        Serial.WriteString(", Gastcode ");
        Serial.WriteNumber(Volatile.Read(ref inNativeRun));
        Serial.WriteString("\n");

        int* depth = hostDepth;
        if (currentId < 0 || depth == null
            || FaultIsolationPolicy.Decide(vector, currentId, Volatile.Read(ref workerThreadId), Volatile.Read(ref inNativeRun) == 1,
                *depth, StorageLock.IsHeldByCurrentThread, AppStorageGate.IsHeldByCurrentThread) != IsolationDecision.Isolate
            || !Quarantine.Add(currentId))
        {
            return CpuExceptionAction.Halt;
        }

        Volatile.Write(ref inNativeRun, 0);
        Volatile.Write(ref workerThreadId, -1);
        Volatile.Write(ref isolatedVector, vector);
        Volatile.Write(ref isolatedRip, exception.InstructionPointer);
        Volatile.Write(ref isolatedThreadId, currentId);
        return CpuExceptionAction.AbandonThread;
    }

#if ZEHOS_WASM
    [DllImport("*", EntryPoint = "zehos_host_depth_address", ExactSpelling = true)]
    private static extern int* HostDepthAddress();
#endif
}
