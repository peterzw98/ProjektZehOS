#if ZEHOS_ISOLATION_PROBE
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe 12.4-I1 (<c>-p:ZehOSIsolationProbe=true</c>, nur Probe): #DE, #PF, #GP und #UD im Wasm-Worker, danach GC und ein
/// weiterer Lauf auf dem neuen Worker. <c>-p:ZehOSIsolationProbeNegative=true</c> loest zum Schluss dieselbe Ausnahme im
/// Probe-Thread aus; das System muss dann wie bisher anhalten. <c>-p:ZehOSIsolationProbeNmi=true</c> haelt den Worker
/// zum Schluss im Gastzustand, bis das Skript per QMP einen NMI schickt; auch der muss anhalten (Whitelist, P2-1).
/// Seit I2-C haelt jeder Fehlerlauf vorher 16 MiB in der Laufarena (mit frei= nach dem malloc); nach jeder Isolation
/// meldet die Probe, was die Arena noch zaehlt (erwartet 0), dazu den freien Speicher nach zwei Desktopbildern und einem
/// ersten Lauf "base" (Worker und Stack stehen dann) und nach der zweiten Ausnahme, je nach GC.Collect.
/// </summary>
internal static class CosmosIsolationProbe
{
    private static volatile int zero;
    private static volatile int sink;

    // #UD: zehos_probe_ud (zehos_probe_fault.c) fuehrt __builtin_trap() aus, im C-Code also ein echtes ud2.
    [DllImport("*", EntryPoint = "zehos_probe_ud", ExactSpelling = true)]
    private static extern void ProbeUd();

    private static volatile int framesRendered;

    internal static void Start() => new Thread(Probe).Start();

    /// <summary>Aus der Hauptschleife nach jedem gezeichneten Desktopbild; nur die Hauptschleife schreibt.</summary>
    internal static void MarkFrameRendered() { if (framesRendered < 2) framesRendered++; }

    private static unsafe void Probe()
    {
        Log("ISOLATION_PROBE start");
        // Der Desktop legt seine Bildpuffer (Szene, Hintergrund; je 1920 x 1080 x 4 = etwa 8 MiB) erst im ersten Bild an,
        // nicht schon beim Start (Desktop.Render). Faellt das zwischen free_before und free der ersten Isolation, sieht die
        // Speicherpruefung einen falschen Stackverlust (1 von 5 Laeufen @1123020b). Deshalb erst nach zwei gezeichneten
        // Bildern isolieren (Review #576 @25ea3a60); ohne Desktop nach hoechstens zehn Sekunden trotzdem.
        for (int wait = 0; wait < 100 && framesRendered < 2; wait++) Thread.Sleep(100);
        Log("ISOLATION_PROBE desktop frames=" + framesRendered);
        zero = 0;
        // I2-C: frei_vor erst, wenn Desktop-Puffer und erster Worker samt Stack stehen (Lauf base); dieselbe Wartezeile wie
        // in #523 (task/mc-e5-smp), damit beide Fassungen ohne Konflikt zusammengehen.
        const nuint arenaBlockBytes = 16 * 1024 * 1024;
        Fault("base", () => sink = 1);
        GC.Collect();
        Log("ISOLATION_PROBE frei_vor=" + CosmosSystemInfo.PhysicalMemory().Free / 1024);
        // Unter Cosmos/NativeAOT loest die verwaltete Ganzzahldivision durch null eine echte CPU-Ausnahme aus.
        Fault("de", () => { Wasm.WasmRuntime.ProbeArenaBlock(arenaBlockBytes); sink = 1 / zero; });
        Log("ISOLATION_PROBE arena_after=de used=" + Wasm.WasmRuntime.ProbeArenaUsedBytes);
        Fault("pf", () => { Wasm.WasmRuntime.ProbeArenaBlock(arenaBlockBytes); sink = *(int*)0; });
        Log("ISOLATION_PROBE arena_after=pf used=" + Wasm.WasmRuntime.ProbeArenaUsedBytes);
        // Nicht kanonische Adresse: der Zugriff loest #GP (Vektor 13) aus, kein #PF.
        Fault("gp", () => { Wasm.WasmRuntime.ProbeArenaBlock(arenaBlockBytes); sink = *(int*)unchecked((nint)0x8000_0000_0000_0000); });
        Log("ISOLATION_PROBE arena_after=gp used=" + Wasm.WasmRuntime.ProbeArenaUsedBytes);
        Fault("ud", () => { Wasm.WasmRuntime.ProbeArenaBlock(arenaBlockBytes); ProbeUd(); });
        Log("ISOLATION_PROBE arena_after=ud used=" + Wasm.WasmRuntime.ProbeArenaUsedBytes);
        GC.Collect();
        Log("ISOLATION_PROBE gc ok");
        Log("ISOLATION_PROBE frei_nach=" + CosmosSystemInfo.PhysicalMemory().Free / 1024);
        Fault("none", () => sink = 1);
        Log("ISOLATION_PROBE DONE");
#if ZEHOS_ISOLATION_PROBE_NMI
        // Der Worker rechnet bis zu zwei Minuten im Gastzustand; das Skript loest waehrenddessen den NMI aus.
        Wasm.WasmRuntime.ProbeEnqueue(() =>
        {
            // QEMU stellt inject-nmi unter KVM nur ueber LVT LINT1 zu; Cosmos richtet LINT1 seit Patch lint1-nmi
            // (patches/lint1-nmi.md im Branch cosmos) selbst unmaskiert mit Zustellart NMI ein.
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
