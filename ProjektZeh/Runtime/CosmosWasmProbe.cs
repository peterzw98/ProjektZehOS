#if ZEHOS_WASM_PROBE
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cosmos.Kernel.System.Diagnostics;
using ProjektZeh.Runtime.Wasm;
using ProjektZeh.Security;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe fuer den statisch gebundenen wasm3-Interpreter: kleine Fehlerfallmodule, eine extern gebaute
/// C#-App und optional Doom ohne Fenster. Ergebnis seriell als WASM_PROBE PASS bzw. FAIL.
/// </summary>
internal static class CosmosWasmProbe
{
    private const string AppPath = "/data/csprobe.wasm";
    private const string DoomPath = "/data/doom.wasm";
    private const int MaximumModuleBytes = 64 * 1024 * 1024;
    private const ulong FixtureGas = 100_000_000;
    private const long AbortLimitMilliseconds = 250;
    private const int AppRuns = 10;
    private const ulong LeakToleranceBytes = 8ul * 1024 * 1024;
    private const int DoomSeconds = 60;
    private const int DoomRestarts = 4;

    // Von Hand erzeugte Minimalmodule; jeweils eine exportierte Funktion "run" ohne Parameter mit i32-Ergebnis.
    private static readonly byte[] Add = Convert.FromHexString("0061736d010000000105016000017f030201000707010372756e00000a09010700412841026a0b");
    private static readonly byte[] Loop = Convert.FromHexString("0061736d010000000105016000017f030201000707010372756e00000a0b01090003400c000b41000b");
    private static readonly byte[] Grow = Convert.FromHexString("0061736d010000000105016000017f0302010005030100010707010372756e00000a0a0108004180800440000b");
    private static readonly byte[] Recurse = Convert.FromHexString("0061736d010000000105016000017f030201000707010372756e00000a0601040010000b");
    // run ruft WASI random_get(0, 16) und liefert dessen Status.
    private static readonly byte[] RandomGet = Convert.FromHexString("0061736d01000000010b0260027f7f017f6000017f02250116776173695f736e617073686f745f70726576696577310a72616e646f6d5f67657400000302010105030100010707010372756e00010a0a0108004100411010000b");
    // run oeffnet csprobe.wasm ueber path_open (gelingt das nicht: Ergebnis 99) und trappt danach bzw. ruft proc_exit(7).
    private static readonly byte[] OpenThenTrap = Convert.FromHexString("0061736d0100000001160360097f7f7f7f7f7e7e7f7f017f60017f006000017f02470216776173695f736e617073686f745f707265766965773109706174685f6f70656e000016776173695f736e617073686f745f70726576696577310970726f635f6578697400010302010205030100010707010372756e00020a2201200041034100418001410c410042004200410041c0001000450440000b41e3000b0b1301004180010b0c637370726f62652e7761736d");
    private static readonly byte[] OpenThenExit = Convert.FromHexString("0061736d0100000001160360097f7f7f7f7f7e7e7f7f017f60017f006000017f02470216776173695f736e617073686f745f707265766965773109706174685f6f70656e000016776173695f736e617073686f745f70726576696577310970726f635f6578697400010302010205030100010707010372756e00020a2501230041034100418001410c410042004200410041c0001000450440410710010b41e3000b0b1301004180010b0c637370726f62652e7761736d");

    public static bool Run()
    {
        try
        {
            MeasureThreadStackRelease();
            Expect("add", Add, "run", WasmStatus.Ok, 42);
            long abortMilliseconds = Expect("loop", Loop, "run", WasmStatus.OutOfGas, 0);
            Check(abortMilliseconds <= AbortLimitMilliseconds, $"Endlosschleife erst nach {abortMilliseconds} ms beendet.");
            Expect("grow", Grow, "run", WasmStatus.Ok, -1);
            Expect("recurse", Recurse, "run", WasmStatus.Trap, 0);
            Expect("truncated", Add.AsSpan(0, 20).ToArray(), "run", WasmStatus.InvalidModule, 0);
            Expect("entry", Add, "fehlt", WasmStatus.EntryMissing, 0);
            // Ohne Hardwarezufall muss der Gast EIO statt Erfolg mit Nullen erhalten (#61).
            Expect("random", RandomGet, "run", WasmStatus.Ok, HasHardwareRandom() ? 0 : 29);
            // Fehlerwege mit offener Datei: Trap und proc_exit beenden den Lauf, der Exitcode kommt an.
            Expect("opentrap", OpenThenTrap, "run", WasmStatus.Trap, 0, "/data");
            Expect("openexit", OpenThenExit, "run", WasmStatus.Ok, 7, "/data");
            RunApplication();
            if (File.Exists(DoomPath)) RunDoom();
            else Write("doom skipped (keine /data/doom.wasm)");
            Write("PASS");
            return true;
        }
        catch (Exception exception)
        {
            Write($"FAIL {exception.Message}");
            return false;
        }
    }

    // Befund fuer COSMOS_SUPPORT.md: wird der Stack beendeter Threads in Cosmos 3.0.94 freigegeben?
    private static void MeasureThreadStackRelease()
    {
        ulong before = FreeBytes();
        for (int index = 0; index < 3; index++)
        {
            var empty = new Thread(() => { }, WasmRuntime.ThreadStackBytes);
            empty.Start();
            empty.Join();
        }
        ulong after = FreeBytes();
        Write($"threadstack threads=3 stackMiB={WasmRuntime.ThreadStackBytes >> 20} lostMiB={(before - Math.Min(before, after)) >> 20}");
    }

    private static void RunApplication()
    {
        byte[] module = ReadModule(AppPath);
        var session = new ProbeSession(null);
        ulong freeBefore = FreeBytes();
        ulong freeAfterFirst = 0;
        for (int run = 1; run <= AppRuns; run++)
        {
            session.Output.Clear();
            var watch = Stopwatch.StartNew();
            // Gas 0 bedeutet ohne Budget; die C#-Laufzeit braucht fuer ihren Start sehr viele Instruktionen.
            WasmStatus status = WasmRuntime.Run(module, "_start", 0, session, out int result, out string message);
            ulong free = FreeBytes();
            if (run == 1) freeAfterFirst = free;
            Write($"app run={run} status={status} exit={result} ms={watch.ElapsedMilliseconds} bytes={module.Length} " +
                $"freeMiB={free >> 20} output='{session.Output.ToString().Trim()}' {message}");
            Check(status == WasmStatus.Ok && result == 0, "C#-App nicht erfolgreich beendet.");
            Check(session.Output.ToString().Contains("CSPROBE 26", StringComparison.Ordinal), "Unerwartete App-Ausgabe.");
        }
        ulong freeAfterLast = FreeBytes();
        Write($"memory freeBeforeMiB={freeBefore >> 20} afterFirstMiB={freeAfterFirst >> 20} afterLastMiB={freeAfterLast >> 20}");
        // Wiederholte Starts duerfen nicht fortlaufend Speicher verlieren; der erste Lauf darf Caches anlegen.
        Check(freeAfterFirst - Math.Min(freeAfterFirst, freeAfterLast) <= LeakToleranceBytes, "Speicher waechst ueber wiederholte Starts.");
    }

    private static void RunDoom()
    {
        byte[] module = ReadModule(DoomPath);
        var session = new ProbeSession("/data", DoomSeconds * 1000);
        var watch = Stopwatch.StartNew();
        WasmStatus status = WasmRuntime.Run(module, "_start", 0, session, out int result, out string message);
        long elapsed = watch.ElapsedMilliseconds;
        double playSeconds = Math.Max(1, session.LastFrameMs - session.FirstFrameMs) / 1000.0;
        Write($"doom status={status} exit={result} ms={elapsed} frames={session.Frames} fps={session.Frames / playSeconds:F1} " +
            $"tics={session.LastTic - session.FirstTic} ticsPerSecond={(session.LastTic - session.FirstTic) / playSeconds:F1} " +
            $"waitedMs={WasmRuntime.WaitedMilliseconds} testFrame={session.TestFrame} {message}");
        Check(status == WasmStatus.Ok && result == 0, "Doom nicht regulaer beendet.");
        Check(session.TestFrame == "ff0000/00ff00/0000ff/ffffff", "Pruefbild mit falscher Kanal- oder Zeilenfolge.");
        Check(session.Frames / playSeconds >= 20, "Doom unter 20 Bildern/s.");
        // Wiederholte kurze Starts: der Speicher muss nach jedem Lauf zurueckkommen.
        ulong freeAfterFirst = FreeBytes();
        for (int run = 1; run <= DoomRestarts; run++)
        {
            WasmStatus restart = WasmRuntime.Run(module, "_start", 0, new ProbeSession("/data", 5000), out int code, out string text);
            Write($"doom restart={run} status={restart} exit={code} freeMiB={FreeBytes() >> 20} {text}");
            Check(restart == WasmStatus.Ok && code == 0, "Doom-Neustart nicht regulaer beendet.");
        }
        ulong freeAfterLast = FreeBytes();
        Check(freeAfterFirst - Math.Min(freeAfterFirst, freeAfterLast) <= LeakToleranceBytes, "Speicher waechst ueber Doom-Neustarts.");
    }

    private static byte[] ReadModule(string path)
    {
        byte[] module = File.ReadAllBytes(path);
        Check(module.Length <= MaximumModuleBytes, "Modul zu gross.");
        return module;
    }

    private static long Expect(string name, byte[] module, string entry, WasmStatus expectedStatus, int expectedResult,
        string? root = null)
    {
        var watch = Stopwatch.StartNew();
        WasmStatus status = WasmRuntime.Run(module, entry, FixtureGas, new ProbeSession(root), out int result, out string message);
        long elapsed = watch.ElapsedMilliseconds;
        Write($"{name} status={status} result={result} ms={elapsed} {message}");
        Check(status == expectedStatus && result == expectedResult, $"{name}: erwartet {expectedStatus}/{expectedResult}.");
        return elapsed;
    }

    private static bool HasHardwareRandom()
    {
        try { CosmosSaltSource.Fill(new byte[16]); return true; }
        catch (CryptographicException) { return false; }
    }

    private static ulong FreeBytes()
    {
        GC.Collect();
        return CosmosSystemInfo.PhysicalMemory().Free;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Write(string message) => Log.WriteString($"WASM_PROBE {message}\n");

    private sealed class ProbeSession(string? root, long runMilliseconds = long.MaxValue) : IWasmSession
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public StringBuilder Output { get; } = new();
        public string? Root => root;
        public bool AllowsNetwork => false;
        public int Frames { get; private set; }
        public int FirstTic { get; private set; } = -1;
        public int LastTic { get; private set; }
        public long FirstFrameMs { get; private set; }
        public long LastFrameMs { get; private set; }
        public string TestFrame { get; private set; } = "-";
        public bool ShouldExit => clock.ElapsedMilliseconds >= runMilliseconds;
        public uint TicksMilliseconds => (uint)clock.ElapsedMilliseconds;
        public int PollKey() => -1;
        public bool TryReadEvent(out WasmInputEvent value) { value = default; return false; }
        // Die Probe läuft ohne Fenster und Zeiger; pointer_capture meldet der App deshalb -1.
        public bool SetPointerCapture(bool enabled) => false;

        public void Write(int fd, ReadOnlySpan<byte> data)
        {
            string text = Encoding.UTF8.GetString(data);
            if (Output.Length < 4096) Output.Append(text);
            Log.WriteString(text);
        }

        public void Present(ReadOnlySpan<uint> pixels, int width, int height, int tic, bool test)
        {
            if (test)
            {
                TestFrame = $"{pixels[0]:x6}/{pixels[width - 1]:x6}/{pixels[(height - 1) * width]:x6}/{pixels[height * width - 1]:x6}";
                return;
            }
            if (FirstTic < 0) { FirstTic = tic; FirstFrameMs = clock.ElapsedMilliseconds; }
            Frames++;
            LastTic = tic;
            LastFrameMs = clock.ElapsedMilliseconds;
        }
    }
}
#endif
