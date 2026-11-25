#if ZEHOS_AOT_LOOP_EXIT && ZEHOS_WASM_AOT
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.Core.CPU;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Runtime;

#pragma warning disable COSMOS0001 // Der Tick-Hook gehoert zur experimentellen Scheduler-Schnittstelle.

/// <summary>
/// Beendet AOT-Code in einer Schleife ohne Hostaufruf (#423, R8): Cosmos ruft im Scheduler-Tick den Hook (Patch
/// aot-loop-exit, nur Ring 0 und freigegebene Interrupts), der den Wasm-Worker auf zehos_aot_exit umlenkt.
/// In jedem AOT-Bau; <c>-p:ZehOSAotLoopExit=false</c> nur fuer die Gegenprobe.
/// </summary>
internal static unsafe class AotLoopExit
{
    // Feldfolge wie zehos_aot_exit_state (zehos_host_aot_exit.h).
    [StructLayout(LayoutKind.Sequential)]
    private struct AotExitState
    {
        public ulong Code;
        public ulong CodeEnd;
        public ulong Stack;
    }

    private static AotExitState* state;
    private static int* abort;
    private static int* depth;
    private static nuint exit;

    /// <summary>Vor dem Scheduler, einmal: Der Hook selbst ruft nichts Natives.</summary>
    internal static void Install()
    {
        state = StateAddress();
        abort = AbortAddress();
        depth = DepthAddress();
        exit = (nuint)ExitAddress();
        CpuTickHook.Handler = OnTick;
    }

    /// <summary>
    /// Der Lauf wird nach einer CPU-Ausnahme aufgegeben: zehos_aot_call kehrt nie zurueck und loescht den Zustand nicht.
    /// Im Ausnahmepfad gerufen, auf der CPU des Workers mit gesperrten Interrupts; nur Speicherzugriffe.
    /// </summary>
    internal static void Forget()
    {
        AotExitState* current = state;
        if (current == null) return;
        Volatile.Write(ref current->CodeEnd, 0);
        Volatile.Write(ref current->Code, 0);
        Volatile.Write(ref current->Stack, 0);
    }

    // Interruptkontext, auf jeder CPU: keine Allokation, keine Sperre, kein Protokoll. Der haeufige Fall endet am Merker.
    private static nuint OnTick(in CpuTickInfo tick)
    {
        if (Volatile.Read(ref *abort) == 0) return 0;
        return AotLoopExitPolicy.ShouldLeave(true, (int)tick.ThreadId, FaultIsolation.WorkerThreadId, FaultIsolation.InNativeRun,
            Volatile.Read(ref *depth), tick.InstructionPointer, tick.StackPointer, Volatile.Read(ref state->Code),
            Volatile.Read(ref state->CodeEnd), Volatile.Read(ref state->Stack)) ? exit : 0;
    }

    [DllImport("*", EntryPoint = "zehos_aot_exit_state_address", ExactSpelling = true)]
    private static extern AotExitState* StateAddress();

    [DllImport("*", EntryPoint = "zehos_host_abort_address", ExactSpelling = true)]
    private static extern int* AbortAddress();

    [DllImport("*", EntryPoint = "zehos_host_depth_address", ExactSpelling = true)]
    private static extern int* DepthAddress();

    [DllImport("*", EntryPoint = "zehos_aot_exit_address", ExactSpelling = true)]
    private static extern void* ExitAddress();
}
#endif
