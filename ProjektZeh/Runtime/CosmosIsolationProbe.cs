#if ZEHOS_ISOLATION_PROBE
using System;
using System.Diagnostics;
using System.Threading;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe 12.4-I1 (<c>-p:ZehOSIsolationProbe=true</c>, nur Probe): #DE und #PF im Wasm-Worker, danach GC und ein
/// weiterer Lauf auf dem neuen Worker. <c>-p:ZehOSIsolationProbeNegative=true</c> loest zum Schluss dieselbe Ausnahme im
/// Probe-Thread aus; das System muss dann wie bisher anhalten. <c>-p:ZehOSIsolationProbeNmi=true</c> haelt den Worker
/// zum Schluss im Gastzustand, bis das Skript per QMP einen NMI schickt; auch der muss anhalten (Whitelist, P2-1).
/// </summary>
internal static class CosmosIsolationProbe
{
    private static volatile int zero;
    private static volatile int sink;

    internal static void Start() => new Thread(Probe).Start();

    private static unsafe void Probe()
    {
        Log("ISOLATION_PROBE start");
        zero = 0;
        // Unter Cosmos/NativeAOT loest die verwaltete Ganzzahldivision durch null eine echte CPU-Ausnahme aus.
        Fault("de", () => sink = 1 / zero);
        Fault("pf", () => sink = *(int*)0);
        GC.Collect();
        Log("ISOLATION_PROBE gc ok");
        Fault("none", () => sink = 1);
        Log("ISOLATION_PROBE DONE");
#if ZEHOS_ISOLATION_PROBE_NMI
        // Der Worker rechnet bis zu zwei Minuten im Gastzustand; das Skript loest waehrenddessen den NMI aus.
        Wasm.WasmRuntime.ProbeEnqueue(() =>
        {
            FaultIsolation.EnterNativeRun();
            try
            {
                Log("ISOLATION_PROBE nmi window open");
                long end = Stopwatch.GetTimestamp() + 120 * Stopwatch.Frequency;
                while (Stopwatch.GetTimestamp() < end) sink++;
                Log("ISOLATION_PROBE nmi window closed");
            }
            finally
            {
                FaultIsolation.LeaveNativeRun();
            }
        });
#endif
#if ZEHOS_ISOLATION_PROBE_NEGATIVE
        Log("ISOLATION_PROBE negative pf on probe thread");
        FaultIsolation.EnterNativeRun();
        sink = *(int*)0;
        Log("ISOLATION_PROBE negative NOT HALTED");
#endif
    }

    // Der Probelauf tut so, als liefe Gastcode (nativer Lauf, kein Hostaufruf); die Hauptschleife ersetzt den Worker.
    private static void Fault(string kind, Action trigger)
    {
        int done = 0;
        int before = Wasm.WasmRuntime.IsolatedRuns;
        Wasm.WasmRuntime.ProbeEnqueue(() =>
        {
            FaultIsolation.EnterNativeRun();
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
                FaultIsolation.LeaveNativeRun();
                Volatile.Write(ref done, 1);
            }
        });

        for (int wait = 0; wait < 100; wait++)
        {
            if (Volatile.Read(ref done) == 1) return;
            if (Wasm.WasmRuntime.IsolatedRuns != before)
            {
                Log("ISOLATION_PROBE fault=" + kind + " handled=1 vector=" + FaultIsolation.IsolatedVector);
                return;
            }
            Thread.Sleep(100);
        }
        Log("ISOLATION_PROBE " + kind + " TIMEOUT");
    }

    private static void Log(string text) => Cosmos.Kernel.System.Diagnostics.Log.WriteString(text + "\n");
}
#endif
