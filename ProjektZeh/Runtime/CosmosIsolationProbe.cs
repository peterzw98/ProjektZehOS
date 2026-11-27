#if ZEHOS_ISOLATION_PROBE
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.System.Diagnostics;
using Cosmos.Kernel.System.Threading;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe 12.4-I1 (<c>-p:ZehOSIsolationProbe=true</c>, nur Probe): #DE, #PF, #GP und #UD im Wasm-Worker, danach GC und ein
/// weiterer Lauf auf dem neuen Worker. <c>-p:ZehOSIsolationProbeNegative=true</c> loest zum Schluss dieselbe Ausnahme im
/// Probe-Thread aus; das System muss dann wie bisher anhalten. <c>-p:ZehOSIsolationProbeNmi=true</c> haelt den Worker
/// zum Schluss im Gastzustand, bis das Skript per QMP einen NMI schickt; auch der muss anhalten (Whitelist, P2-1).
/// Seit I2-C haelt jeder Fehlerlauf vorher 16 MiB in der Laufarena (mit frei= nach dem malloc); nach jeder Isolation
/// meldet die Probe, was die Arena noch zaehlt (erwartet 0), dazu den freien Speicher nach zwei Desktopbildern und einem
/// ersten Lauf "base" (Worker und Stack stehen dann) und nach der zweiten Ausnahme, je nach GC.Collect.
/// Mit WAMR (#582) folgen echte AOT-Laeufe ueber WasmRuntime.Run: ein sauberer Lauf, dann <see cref="AotRounds"/> Runden aus
/// #DE und #PF im AOT-Code und je einem weiteren sauberen AOT-Lauf, zum Schluss der freie Speicher gegen den Wert davor.
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
#if ZEHOS_WASM_AOT
        ProbeAot();
#endif
        Log("ISOLATION_PROBE DONE");
#if ZEHOS_ISOLATION_PROBE_NMI
        // Der Worker rechnet bis zu zwei Minuten im Gastzustand; das Skript loest waehrenddessen den NMI aus.
        Wasm.WasmRuntime.ProbeEnqueue(() =>
        {
            // QEMU stellt inject-nmi unter KVM nur ueber LVT LINT1 zu; Cosmos richtet LINT1 seit Patch lint1-nmi
            // (patches/lint1-nmi.md im Branch cosmos) selbst unmaskiert mit Zustellart NMI ein.
            // QEMU stellt den NMI allen CPUs zu, Cosmos laesst LINT1 aber wie Linux nur auf der Boot-CPU offen
            // (LocalApic.InitializeCurrentCpu maskiert es auf den APs). Der Worker muss also auf CPU 0 rechnen, wenn der
            // NMI kommt; die Pinnung wirkt erst nach der naechsten Umschaltung (#515).
            ThreadAffinity.Current = CpuSet.Single(0);
            Thread.Sleep(1);
            // Auf CPU 0 laeuft auch die Hauptschleife und haette den NMI in allen Laeufen abgefangen (Thread = Hauptthread);
            // sie parkt, sobald sie das Fenster sieht (ParkMainInNmiWindow), und meldet das, bevor das Skript den NMI sendet.
            FaultIsolation.EnterNativeRun();
            try
            {
                Log("ISOLATION_PROBE nmi window open cpu=" + KernelCpus.Current);
                // Erst nach der Zeile "window open": Das Skript wertet ab dort aus (evaluate_nmi), "main parked" muss dahinter stehen.
                Volatile.Write(ref nmiWindow, 1);
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

#if ZEHOS_ISOLATION_PROBE_NMI
    private static int nmiWindow;

    /// <summary>
    /// Aus der Hauptschleife: Ist das NMI-Fenster offen, schlaeft der Hauptthread fuer immer, damit auf CPU 0 nur noch der
    /// Worker im Gastzustand rechnet. Die Probe endet mit dem Halt durch den NMI; danach braucht niemand die Hauptschleife.
    /// </summary>
    internal static void ParkMainInNmiWindow()
    {
        if (Volatile.Read(ref nmiWindow) == 0) return;
        Log("ISOLATION_PROBE nmi main parked");
        while (true) Thread.Sleep(1000);
    }

#endif
    // Der Probelauf tut so, als liefe Gastcode (nativer Lauf, kein Hostaufruf); die Hauptschleife ersetzt den Worker.
    private static void Fault(string kind, Action trigger)
    {
        int done = 0;
        int before = Wasm.WasmRuntime.IsolatedRuns;
        ulong releasedBefore = SchedulerDiagnostics.ReleasedStacks;
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
        // Nach ProbeEnqueue: Der Worker dieses Laufs und sein Stack sind schon angelegt. Nach dem Abraeumen ersetzt der Stack
        // des Ersatzworkers seinen, der freie Speicher muss also etwa gleich bleiben (Review laser #576, Befund 6).
        ulong freeBefore = MemoryDiagnostics.FreePages * MemoryDiagnostics.PageSizeBytes / 1024;

        for (int wait = 0; wait < 100; wait++)
        {
            if (Volatile.Read(ref done) == 1) return;
            if (Wasm.WasmRuntime.IsolatedRuns != before)
            {
                Log("ISOLATION_PROBE fault=" + kind + " handled=1 vector=" + FaultIsolation.IsolatedVector);
                // E6-G (#433): Die Hauptschleife zaehlt die Isolation erst nach dem Abraeumen und dem Start des Ersatzworkers.
                // Ohne Abraeumen sinkt der freie Speicher je Isolation um den 8-MiB-Stack, mit bleibt er etwa gleich. Ob
                // genau dieser Worker abgeraeumt wurde, steht in der Zeile der Hauptschleife davor ("Thread und Stack frei");
                // released zaehlt systemweit und ist nur ein Zusatz.
                Log("ISOLATION_PROBE memory kind=" + kind + " released=" + (SchedulerDiagnostics.ReleasedStacks - releasedBefore)
                    + " free_before_kib=" + freeBefore
                    + " free_kib=" + MemoryDiagnostics.FreePages * MemoryDiagnostics.PageSizeBytes / 1024);
                return;
            }
            Thread.Sleep(100);
        }
        Log("ISOLATION_PROBE " + kind + " TIMEOUT");
    }

#if ZEHOS_WASM_AOT
    // Zehn Runden (20 Isolationen): Ein kleiner Rest je Isolation faellt erst ueber viele Runden auf (Review #582, P3-2).
    private const int AotRounds = 10;
    private const string AotEntry = "ret42";
    private static readonly byte[] NoModule = [];
    // Je am Anfang jeder Funktion des .aot, der Rest des Codes ist ud2: "xor ecx, ecx; div ecx" loest #DE aus,
    // "xor eax, eax; mov eax, [rax]" einen #PF an Adresse 0 (wie der Lauf pf oben). RIP liegt damit im AOT-Code.
    private static readonly byte[] DivideTrigger = [0x31, 0xC9, 0xF7, 0xF1];
    private static readonly byte[] PageTrigger = [0x31, 0xC0, 0x8B, 0x00];

    // Gegen den Lauf vor der ersten Isolation: Bliebe je Isolation die Instanz liegen, fehlte schon ihr 1-MiB-Wasm-Stack
    // (WasmRuntime.WasmStackBytes); das Skript wertet aus (scripts/test-isolation-probe.py). Den Abbau ueber WAMR belegt der
    // Speicher allein nicht (die Arena gaebe dieselben Bloecke auch ohne ihn frei), sondern "Aot Released" und der saubere
    // Lauf danach (ohne ausgeglichenen Referenzzaehler und geloeschte Sperre endete er mit LoadFailed).
    private static unsafe void ProbeAot()
    {
        uint size;
        byte* bytes = ProbeAotFixture(&size);
        byte[] fixture = new ReadOnlySpan<byte>(bytes, (int)size).ToArray();
        byte[]? divide = PatchAot(fixture, DivideTrigger);
        byte[]? page = PatchAot(fixture, PageTrigger);
        if (divide == null || page == null)
        {
            Log("ISOLATION_PROBE aot fixture unbrauchbar");
            return;
        }
        RunAot("ok", 0, fixture);
        Log("ISOLATION_PROBE aot frei_vor=" + FreeKib());
        for (int round = 1; round <= AotRounds; round++)
        {
            RunAot("de", round, divide);
            RunAot("pf", round, page);
            RunAot("ok", round, fixture);
            // Je Runde: das Skript prueft den Verlauf, nicht nur Anfang und Ende.
            Log($"ISOLATION_PROBE aot round={round} frei={FreeKib()}");
        }
        Log("ISOLATION_PROBE aot frei_nach=" + FreeKib());
    }

    private static void RunAot(string kind, int round, byte[] aot)
    {
        int before = Wasm.WasmRuntime.IsolatedRuns;
        Wasm.WasmStatus status = Wasm.WasmRuntime.Run(NoModule, AotEntry, 0, new ProbeSession(), out int result, out string message, aot);
        // Run kehrt bei einer Isolation erst zurueck, wenn die Hauptschleife den Lauf beendet und dabei gezaehlt hat.
        bool isolated = Wasm.WasmRuntime.IsolatedRuns != before;
        Log($"ISOLATION_PROBE aot kind={kind} round={round} status={status} result={result} isolated={(isolated ? 1 : 0)} " +
            $"vector={(isolated ? FaultIsolation.IsolatedVector : -1)} arena_used={Wasm.WasmRuntime.ProbeArenaUsedBytes} {message}");
    }

    private static ulong FreeKib()
    {
        GC.Collect();
        return CosmosSystemInfo.PhysicalMemory().Free / 1024;
    }

    // Wie fault_fixture (scripts/test-wasm-doom-desktop.py): Abschnitt 2 ist der Code (nach den Konstanten), Abschnitt 3 nennt je
    // Funktion zuerst einen 64-Bit-Versatz in den Code (danach 12 weitere Bytes je Funktion, aot_loader.c
    // load_function_section). null, wenn die Datei nicht so aussieht.
    private static byte[]? PatchAot(byte[] aot, byte[] trigger)
    {
        byte[] data = (byte[])aot.Clone();
        int code = -1, codeEnd = -1;
        int offset = 8;
        while (offset + 8 <= data.Length)
        {
            offset = (offset + 3) & ~3;
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            offset += 8;
            if (size > (uint)(data.Length - offset)) return null;
            if (kind == 2)
            {
                code = offset + 4 + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                codeEnd = offset + (int)size;
                for (int index = code; index < codeEnd; index++) data[index] = (index - code) % 2 == 0 ? (byte)0x0F : (byte)0x0B;
            }
            else if (kind == 3 && code >= 0)
            {
                if (size == 0 || size % 20 != 0) return null;
                for (int function = 0; function < size / 20; function++)
                {
                    ulong at = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 8 * function));
                    if (at > (ulong)(codeEnd - code - trigger.Length)) return null;
                    trigger.CopyTo(data, code + (int)at);
                }
                return data;
            }
            offset += (int)size;
        }
        return null;
    }

    [DllImport("*", EntryPoint = "zehos_probe_aot_fixture", ExactSpelling = true)]
    private static extern unsafe byte* ProbeAotFixture(uint* size);

    // Ohne Fenster und Eingabe; Ausgaben gibt es von ret42 nicht.
    private sealed class ProbeSession : Wasm.IWasmSession
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public string? Root => null;
        public string? SaveRoot => null;
        public bool AllowsNetwork => false;
        public bool ShouldExit => false;
        public uint TicksMilliseconds => (uint)clock.ElapsedMilliseconds;
        public void Write(int fd, ReadOnlySpan<byte> data) { }
        public void Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test) { }
        public int PollKey() => -1;
        public bool TryReadEvent(out Wasm.WasmInputEvent value) { value = default; return false; }
        public bool SetPointerCapture(bool enabled) => false;
    }

#endif
    private static void Log(string text) => Cosmos.Kernel.System.Diagnostics.Log.WriteString(text + "\n");
}
#endif
