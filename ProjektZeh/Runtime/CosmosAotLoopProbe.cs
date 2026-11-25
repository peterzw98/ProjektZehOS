#if ZEHOS_AOT_PROBE && ZEHOS_AOT_LOOP_EXIT
using System;
using System.Diagnostics;
using System.Threading;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe R8 (#423, <c>-p:ZehOSAotProbe=true</c>): Schliessen beendet AOT-Code in einer
/// Schleife ohne Hostaufruf, zehnmal hintereinander bei dauernder GC-Last; danach laeuft AOT weiter und der Speicher ist
/// wieder frei. Ohne wamrc: Das letzte <c>jmp</c> von hostcalls (Fixture D) wird im Speicher zu <c>jmp $</c>; davor liegen
/// seine Hostaufrufe, an denen die Probe den Start erkennt. Eigener Thread, weil die Hauptschleife PollAbort ruft.
/// </summary>
internal static class CosmosAotLoopProbe
{
    private const int Rounds = 10;
    private const long EndMilliseconds = AbortWatchdog.GraceMilliseconds + 1000;
    // Wie CosmosAotProbe, aber mit dem Muell der Lastthreads daneben.
    private const ulong LeakToleranceBytes = 4 * 1024 * 1024;
    // mov ebp, r13d; jmp <Epilog von hostcalls>
    private static readonly byte[] Tail = Convert.FromHexString("4489ede9cffdffff");
    private static readonly byte[] NoModule = [];
    private static volatile bool stopLoad;
    private static volatile object? sink;

    internal static void Start(byte[] fixture) => new Thread(() => Probe(fixture)).Start();

    private static void Probe(byte[] fixture)
    {
        int at = fixture.AsSpan().IndexOf(Tail);
        if (at < 0 || fixture.AsSpan(at + 1).IndexOf(Tail) >= 0)
        {
            Log("FAIL Schleifenstelle im Fixture nicht eindeutig");
            return;
        }
        byte[] loop = (byte[])fixture.Clone();
        loop[at + 3] = 0xeb;
        loop[at + 4] = 0xfe;

        Log("start");
        var load = new Thread(Allocate);
        load.Start();
        bool passed = true;
        ulong freeAfterSecond = 0;
        for (int round = 1; round <= Rounds; round++)
        {
            passed &= Round(round, loop);
            GC.Collect();
            ulong free = CosmosSystemInfo.PhysicalMemory().Free;
            if (round == 2) freeAfterSecond = free;
            Log($"round={round} freeKiB={free / 1024}");
            if (round == Rounds && free + LeakToleranceBytes < freeAfterSecond)
            {
                Log("FAIL Speicher waechst");
                passed = false;
            }
        }
        stopLoad = true;
        load.Join();
        // Ohne .wasm gibt es keinen Rueckfall: Ok heisst, AOT ist nach den Abbruechen noch aktiv.
        var status = WasmRuntime.Run(NoModule, "hostcalls", 0, new ProbeSession(), out int result, out string message, fixture);
        Log($"danach status={status} result={result} {message}");
        passed &= status == WasmStatus.Ok && result == 31;
        Log(passed ? "PASS" : "FAIL");
    }

    private static void Allocate()
    {
        while (!stopLoad)
        {
            for (int index = 0; index < 64; index++) sink = new byte[4096];
            GC.Collect();
            Thread.Sleep(5);
        }
    }

    private static bool Round(int number, byte[] loop)
    {
        var session = new ProbeSession();
        // Ohne using: Endet der Lauf erst nach der Wartefrist, setzt der Rueckruf das Ereignis noch auf dem Worker.
        var done = new ManualResetEvent(false);
        WasmStatus status = WasmStatus.Trap;
        string text = string.Empty;
        WasmRuntime.Enqueue(NoModule, "hostcalls", 0, session, (s, _, m) => { status = s; text = m; done.Set(); }, loop);
        if (!session.Started.WaitOne(30_000))
        {
            Log($"round={number} kein Hostaufruf");
            return false;
        }
        long closed = Stopwatch.GetTimestamp();
        WasmRuntime.CloseRequested(session);
        bool ended = done.WaitOne((int)EndMilliseconds + 10_000);
        long elapsed = (long)Stopwatch.GetElapsedTime(closed).TotalMilliseconds;
        Log($"round={number} status={(ended ? status.ToString() : "Timeout")} ms={elapsed} {text}");
        return ended && status == WasmStatus.Aborted && elapsed >= AbortWatchdog.GraceMilliseconds && elapsed < EndMilliseconds;
    }

    private static void Log(string text) => Cosmos.Kernel.System.Diagnostics.Log.WriteString($"AOT_LOOP {text}\n");

    private sealed class ProbeSession : IWasmSession
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public ManualResetEvent Started { get; } = new(false);
        public string? Root => null;
        public string? SaveRoot => null;
        public bool AllowsNetwork => false;
        public bool ShouldExit => false;
        public uint TicksMilliseconds { get { Started.Set(); return (uint)clock.ElapsedMilliseconds; } }
        public int PollKey() => -1;
        public bool TryReadEvent(out WasmInputEvent value) { value = default; return false; }
        public bool SetPointerCapture(bool enabled) => false;
        public void Write(int fd, ReadOnlySpan<byte> data) { }
        public void Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test) { }
    }
}
#endif
