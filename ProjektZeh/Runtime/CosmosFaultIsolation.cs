using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Build.API.Attributes;
using Cosmos.Kernel.Core.Scheduler;
using ProjektZeh.Storage;
using Serial = Cosmos.Kernel.System.Diagnostics.Log;

namespace ProjektZeh.Runtime;

#pragma warning disable COSMOS0001 // Cosmos-Scheduler-Typen gelten als Vorschau.

// Cosmos 3.0.97 endet jeden CPU-Ausnahmepfad in Panic.CpuException -> PrintCpuContext(ref IRQContext), Stacktrace,
// Panic.HaltCpu() (Cosmos.Kernel.dll, IL gelesen 18.11.; alle 18 ExceptionHandler rufen CpuException). Die Plugs merken
// sich aus dem Kontext den Vektor und ersetzen den Endlos-Halt; ausserhalb von Gastcode des Wasm-Workers und fuer
// Vektoren ausserhalb der Whitelist haelt das System unveraendert an.
// Immer im Build, auch ohne Wasm: scripts/check-plug-binding.py prueft die Quellen ohne Build-Konstanten.
[Plug(TargetName = "Cosmos.Kernel.Panic")]
public static class CosmosPanicHaltPlug
{
    // Die Koerper werden nach Cosmos.Kernel kopiert und duerfen keinen Typ von dort nennen: nur ein Aufruf in ProjektZeh.
    [PlugMember]
    public static void HaltCpu() => FaultIsolation.OnHalt();

    // Ersetzt die Registerausgabe von Panic.CpuException; der Patcher vergleicht Parametertypen ueber den vollen Namen,
    // deshalb bindet die Attrappe unten an das interne Cosmos-Struct. Der Zeiger zeigt auf den Rahmen des Interrupt-Stubs.
    [PlugMember]
    public static unsafe void PrintCpuContext(ref Cosmos.Kernel.Core.CPU.IRQContext context) =>
        FaultIsolation.OnCpuContext((nint)Unsafe.AsPointer(ref context));
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
    private static long contextVector = FaultIsolationPolicy.UnknownVector;
    private static long isolatedVector = FaultIsolationPolicy.UnknownVector;
    private static ulong isolatedRip;
    private static ulong contextRip;

    // Feldversatz in Cosmos.Kernel.Core.CPU.IRQContext 3.0.97 (Sequential, Pack 1, 192 Bytes; per Reflection gelesen
    // 18.11.): 15 Register zu 8 Bytes, dann interrupt; rip nach cpu_flags, fault_address und temp_rcx. Die Gastprobe
    // vergleicht den gelesenen Vektor mit dem erwarteten #DE/#PF und faellt bei verschobenen Feldern auf.
    private const int R15 = 0, R14 = 8, R13 = 16, R12 = 24, R11 = 32, R10 = 40, R9 = 48, R8 = 56, Rdi = 64, Rsi = 72,
        Rbp = 80, Rbx = 88, Rdx = 96, Rcx = 104, Rax = 112, Interrupt = 120, CpuFlags = 128, FaultAddress = 136, Rip = 152,
        Rflags = 168, Rsp = 176;

    /// <summary>Vom Worker selbst beim Start gerufen, damit die Id seinem SchedulerThread entspricht.</summary>
    internal static void RegisterWorker()
    {
#if ZEHOS_WASM
        hostDepth = HostDepthAddress();
#else
        // Ohne Wasm-Laufzeit gibt es keinen Zaehler; OnHalt haelt dann immer an.
        hostDepth = null;
#endif
        SchedulerThread? current = SchedulerManager.GetCpuState(0)?.CurrentThread;
        if (current != null) Volatile.Write(ref workerThreadId, (int)current.Id);
    }

    /// <summary>Um den nativen Lauf (wasm3 oder AOT); Hostaufrufe darin zaehlt die C-Schicht.</summary>
    internal static void EnterNativeRun() => Volatile.Write(ref inNativeRun, 1);

    internal static void LeaveNativeRun() => Volatile.Write(ref inNativeRun, 0);

    /// <summary>Id des zuletzt aufgegebenen Workers oder -1; liest und loescht.</summary>
    internal static int TakeIsolated() => Interlocked.Exchange(ref isolatedThreadId, -1);

    /// <summary>Vektor der zuletzt isolierten Ausnahme (fuer Protokoll und Gastprobe).</summary>
    internal static long IsolatedVector => Volatile.Read(ref isolatedVector);

    /// <summary>RIP der zuletzt isolierten Ausnahme (fuer das Protokoll).</summary>
    internal static ulong IsolatedRip => Volatile.Read(ref isolatedRip);

    // Laeuft wie OnHalt im Ausnahmepfad: nur lesen, merken und die Register wie Cosmos ausgeben, nichts allokieren.
    internal static void OnCpuContext(nint context)
    {
        byte* frame = (byte*)context;
        ulong vector = Field(frame, Interrupt);
        Volatile.Write(ref contextRip, Field(frame, Rip));
        Volatile.Write(ref contextVector, vector <= 255 ? (long)vector : FaultIsolationPolicy.UnknownVector);

        Serial.WriteString("Interrupt Vector: ");
        Serial.WriteNumber(vector);
        Serial.WriteString("\nCPU Flags (ESR): 0x");
        Serial.WriteHex(Field(frame, CpuFlags));
        Serial.WriteString("\n\nRegisters:\n");
        Pair("  RAX: 0x", frame, Rax, "  RBX: 0x", Rbx);
        Pair("  RCX: 0x", frame, Rcx, "  RDX: 0x", Rdx);
        Pair("  RSI: 0x", frame, Rsi, "  RDI: 0x", Rdi);
        Pair("  RBP: 0x", frame, Rbp, "  R8:  0x", R8);
        Pair("  R9:  0x", frame, R9, "  R10: 0x", R10);
        Pair("  R11: 0x", frame, R11, "  R12: 0x", R12);
        Pair("  R13: 0x", frame, R13, "  R14: 0x", R14);
        Single("  R15: 0x", frame, R15);
        Pair("  RIP: 0x", frame, Rip, "  RSP: 0x", Rsp);
        Single("  RFL: 0x", frame, Rflags);
        Single("  CR2: 0x", frame, FaultAddress);
        // Belegt in der Gastprobe, welchen Thread die Ausnahme traf (NMI-Gegenprobe: muss der Worker im Gastcode sein).
        SchedulerThread? current = SchedulerManager.GetCpuState(0)?.CurrentThread;
        Serial.WriteString("ZehOS: Thread ");
        Serial.WriteNumber(current == null ? -1 : (int)current.Id);
        Serial.WriteString(", Wasm-Worker ");
        Serial.WriteNumber(Volatile.Read(ref workerThreadId));
        Serial.WriteString(", Gastcode ");
        Serial.WriteNumber(Volatile.Read(ref inNativeRun));
        Serial.WriteString("\n");
    }

    private static ulong Field(byte* frame, int offset) => *(ulong*)(frame + offset);

    private static void Pair(string first, byte* frame, int firstOffset, string second, int secondOffset)
    {
        Serial.WriteString(first);
        Serial.WriteHex(Field(frame, firstOffset));
        Single(second, frame, secondOffset);
    }

    private static void Single(string label, byte* frame, int offset)
    {
        Serial.WriteString(label);
        Serial.WriteHex(Field(frame, offset));
        Serial.WriteString("\n");
    }

    // Laeuft im Ausnahmepfad mit gesperrten Interrupts auf dem Stack des fehlerhaften Threads: nichts allokieren, nichts
    // sperren, nicht protokollieren (das Protokoll kann eine Sperre eines unterbrochenen Threads brauchen).
    internal static void OnHalt()
    {
        // Einmal verbrauchen: Ein spaeterer Halt ohne neuen Kontext (anderer Panic-Pfad) sieht keinen alten Vektor.
        long vector = Interlocked.Exchange(ref contextVector, FaultIsolationPolicy.UnknownVector);
        PerCpuState? state = SchedulerManager.GetCpuState(0);
        SchedulerThread? current = state?.CurrentThread;
        int* depth = hostDepth;
        if (state != null && current != null && depth != null
            && FaultIsolationPolicy.Decide(vector, (int)current.Id, Volatile.Read(ref workerThreadId), Volatile.Read(ref inNativeRun) == 1,
                *depth, StorageLock.IsHeldByCurrentThread, AppStorageGate.IsHeldByCurrentThread) == IsolationDecision.Isolate
            && Quarantine.Add((int)current.Id))
        {
            int id = (int)current.Id;
            Volatile.Write(ref inNativeRun, 0);
            Volatile.Write(ref workerThreadId, -1);
            Volatile.Write(ref isolatedVector, vector);
            Volatile.Write(ref isolatedRip, Volatile.Read(ref contextRip));
            Volatile.Write(ref isolatedThreadId, id);
            // Der gespeicherte Stackzeiger wird nie wieder geladen; er muss nur im Stack dieses Threads liegen.
            ScheduleFromInterrupt(null, state.CpuId, (nuint)(&id));
            // Den Wechsel fuehrt erst der Interrupt-Stub nach der Rueckkehr aus. Ist keiner angefordert, liefe der
            // fehlerhafte Code weiter: dann wie bisher anhalten.
            if (ContextSwitchTarget() != 0) return;
        }

        while (true) Halt(null);
    }

    [DllImport("*", EntryPoint = "_native_get_context_switch_sp", ExactSpelling = true)]
    private static extern nuint ContextSwitchTarget();

#if ZEHOS_WASM
    [DllImport("*", EntryPoint = "zehos_host_depth_address", ExactSpelling = true)]
    private static extern int* HostDepthAddress();
#endif

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ScheduleFromInterrupt")]
    private static extern void ScheduleFromInterrupt([UnsafeAccessorType(SchedulerManagerType)] object? owner, uint cpuId, nuint currentRsp);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Halt")]
    private static extern void Halt([UnsafeAccessorType(InternalCpuType)] object? owner);
}
