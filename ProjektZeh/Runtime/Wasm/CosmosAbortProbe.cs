#if ZEHOS_ABORT_PROBE && ZEHOS_WASM
using System;
using System.Diagnostics;
using System.Threading;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>
/// Gastprobe 12.4-I2-A/B (<c>-p:ZehOSAbortProbe=true</c>, nur Probe): Schliessen ueber WasmRuntime.CloseRequested, wie
/// es WasmGameApp tut. Fall 1 fragt should_exit und endet selbst (Ok, sofort); Fall 2 fragt nie und muss nach der Frist
/// mit Aborted enden (Pruefpunkt Hostaufruf); Fall 3 kreist in einer Schleife ganz ohne Aufruf und muss ebenso enden
/// (Pruefpunkt je Schleifendurchlauf, I2-B); danach ist der Worker frei und ein vierter, normaler Lauf endet Ok. Den
/// Abbruch selbst loest die Hauptschleife aus (PollAbort); die Probe laeuft deshalb auf einem eigenen Thread neben ihr.
/// </summary>
internal static class CosmosAbortProbe
{
    // Module wie in scripts/tests/test_wasm_abort_native.py (dort gegen wasm3 auf dem Host geprueft).
    // loop { call should_exit; i32.eqz; br_if 0 } i32.const 0
    private static readonly byte[] Obedient = Convert.FromHexString(
        "0061736d010000000105016000017f021801087a65686f735f76310b73686f756c645f657869740000030201000707010372756e00010a0e010c0003401000450d000b41000b");
    // call ticks_ms; drop; loop { i32.const 10; call sleep_ms; br 0 } i32.const 0
    private static readonly byte[] Deaf = Convert.FromHexString(
        "0061736d010000000109026000017f60017f00022902087a65686f735f7631087469636b735f6d730000087a65686f735f763108736c6565705f6d730001030201000707010372756e00020a1201100010001a0340410a10010c000b41000b");
    // call ticks_ms; drop; loop { br 0 } i32.const 0: nach dem ersten Hostaufruf weder Host- noch Wasm-Aufruf.
    private static readonly byte[] Tight = Convert.FromHexString(
        "0061736d010000000105016000017f021501087a65686f735f7631087469636b735f6d730000030201000707010372756e00010a0e010c0010001a03400c000b41000b");
    // i32.const 7
    private static readonly byte[] Seven = Convert.FromHexString("0061736d010000000105016000017f030201000707010372756e00000a0601040041070b");

    internal static void Start() => new Thread(Probe).Start();

    private static void Probe()
    {
        Log("ABORT_PROBE start");
        bool passed = Case(1, Obedient, WasmStatus.Ok, 0, 1000, out _);
        passed &= Case(2, Deaf, WasmStatus.Aborted, AbortWatchdog.GraceMilliseconds, AbortWatchdog.GraceMilliseconds + 1000, out _);
        passed &= Case(3, Tight, WasmStatus.Aborted, AbortWatchdog.GraceMilliseconds, AbortWatchdog.GraceMilliseconds + 1000, out _);
        bool idle = !WasmRuntime.IsBusy;
        Log("ABORT_PROBE idle=" + (idle ? 1 : 0));
        var status = WasmRuntime.Run(Seven, "run", 0, new ProbeSession(), out int result, out string message);
        Log($"ABORT_PROBE case=4 status={status} result={result} {message}");
        passed &= idle && status == WasmStatus.Ok && result == 7;
        Log(passed ? "ABORT_PROBE PASS" : "ABORT_PROBE FAIL");
    }

    // Startet den Lauf, schliesst ihn nach dem ersten Hostaufruf und misst bis zu seinem Ende.
    private static bool Case(int number, byte[] module, WasmStatus expected, long minimumMilliseconds, long maximumMilliseconds,
        out long elapsed)
    {
        var session = new ProbeSession();
        // Ohne using: Endet der Lauf erst nach der Wartefrist, setzt der Rueckruf das Ereignis noch auf dem Worker.
        var done = new ManualResetEvent(false);
        WasmStatus status = WasmStatus.Trap;
        string text = string.Empty;
        WasmRuntime.Enqueue(module, "run", 0, session, (s, _, m) => { status = s; text = m; done.Set(); });
        elapsed = -1;
        if (!session.Started.WaitOne(30_000))
        {
            Log($"ABORT_PROBE case={number} kein Hostaufruf");
            return false;
        }
        long closed = Stopwatch.GetTimestamp();
        session.Close();
        WasmRuntime.CloseRequested(session);
        bool ended = done.WaitOne((int)maximumMilliseconds + 10_000);
        elapsed = (long)Stopwatch.GetElapsedTime(closed).TotalMilliseconds;
        Log($"ABORT_PROBE case={number} status={(ended ? status.ToString() : "Timeout")} ms={elapsed} {text}");
        return ended && status == expected && elapsed >= minimumMilliseconds && elapsed < maximumMilliseconds;
    }

    private static void Log(string text) => Cosmos.Kernel.System.Diagnostics.Log.WriteString(text + "\n");

    // Ohne Fenster; ShouldExit wird mit dem Schliessen wahr. Started meldet den ersten Hostaufruf, der die Sitzung fragt
    // (should_exit in Fall 1, ticks_ms in Fall 2 und 3).
    private sealed class ProbeSession : IWasmSession
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private volatile bool exit;
        public ManualResetEvent Started { get; } = new(false);
        public void Close() => exit = true;
        public string? Root => null;
        public string? SaveRoot => null;
        public bool AllowsNetwork => false;
        public bool ShouldExit { get { Started.Set(); return exit; } }
        public uint TicksMilliseconds { get { Started.Set(); return (uint)clock.ElapsedMilliseconds; } }
        public int PollKey() => -1;
        public bool TryReadEvent(out WasmInputEvent value) { value = default; return false; }
        public bool SetPointerCapture(bool enabled) => false;
        public void Write(int fd, ReadOnlySpan<byte> data) { }
        public void Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test) { }
    }
}
#endif
