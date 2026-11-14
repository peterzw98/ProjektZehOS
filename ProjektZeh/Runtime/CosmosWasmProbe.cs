#if ZEHOS_WASM_PROBE
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using Cosmos.Kernel.System.Diagnostics;
using ProjektZeh.Security;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe fuer den statisch gebundenen wasm3-Interpreter: kleine Fehlerfallmodule und eine
/// extern gebaute C#-App von /data. Ergebnis seriell als WASM_PROBE PASS bzw. FAIL.
/// </summary>
internal static unsafe class CosmosWasmProbe
{
    private const string AppPath = "/data/csprobe.wasm";
    private const int MaximumAppBytes = 64 * 1024 * 1024;
    private const ulong FixtureGas = 100_000_000;
    private const long AbortLimitMilliseconds = 250;
    private const int ThreadStackBytes = 8 * 1024 * 1024;
    private const uint WasmStackBytes = 1024 * 1024;
    private const ulong MemoryLimitBytes = 256ul * 1024 * 1024;
    private const int AppRuns = 10;
    private const ulong LeakToleranceBytes = 8ul * 1024 * 1024;
    private static readonly StringBuilder appOutput = new();

    // Von Hand erzeugte Minimalmodule; jeweils eine exportierte Funktion "run" ohne Parameter mit i32-Ergebnis.
    private static readonly byte[] Add = Convert.FromHexString("0061736d010000000105016000017f030201000707010372756e00000a09010700412841026a0b");
    private static readonly byte[] Loop = Convert.FromHexString("0061736d010000000105016000017f030201000707010372756e00000a0b01090003400c000b41000b");
    private static readonly byte[] Grow = Convert.FromHexString("0061736d010000000105016000017f0302010005030100010707010372756e00000a0a0108004180800440000b");
    private static readonly byte[] Recurse = Convert.FromHexString("0061736d010000000105016000017f030201000707010372756e00000a0601040010000b");

    private enum Status { Ok = 0, NoMemory = 1, InvalidModule = 2, LoadFailed = 3, EntryMissing = 4, Trap = 5, OutOfGas = 6, LimitFailed = 7 }

    public static bool Run()
    {
        MeasureThreadStackRelease();
        bool passed = false;
        // Ein einziger Worker mit festem Stack fuer alle Laeufe; das native wasm3-Budget (d_m3MaxNativeStack) ist darauf abgestimmt.
        var worker = new Thread(() => passed = RunAll(), ThreadStackBytes);
        worker.Start();
        worker.Join();
        return passed;
    }

    // Befund fuer COSMOS_SUPPORT.md: wird der Stack beendeter Threads in Cosmos 3.0.94 freigegeben?
    private static void MeasureThreadStackRelease()
    {
        ulong before = FreeBytes();
        for (int index = 0; index < 3; index++)
        {
            var empty = new Thread(() => { }, ThreadStackBytes);
            empty.Start();
            empty.Join();
        }
        ulong after = FreeBytes();
        Write($"threadstack threads=3 stackMiB={ThreadStackBytes >> 20} lostMiB={(before - Math.Min(before, after)) >> 20}");
    }

    private static bool RunAll()
    {
        try
        {
            Expect("add", Add, "run", FixtureGas, Status.Ok, 42);
            long abortMilliseconds = Expect("loop", Loop, "run", FixtureGas, Status.OutOfGas, 0);
            Check(abortMilliseconds <= AbortLimitMilliseconds, $"Endlosschleife erst nach {abortMilliseconds} ms beendet.");
            Expect("grow", Grow, "run", FixtureGas, Status.Ok, -1);
            Expect("recurse", Recurse, "run", FixtureGas, Status.Trap, 0);
            Expect("truncated", Add.AsSpan(0, 20).ToArray(), "run", FixtureGas, Status.InvalidModule, 0);
            Expect("entry", Add, "fehlt", FixtureGas, Status.EntryMissing, 0);
            RunApplication();
            Write("PASS");
            return true;
        }
        catch (Exception exception)
        {
            Write($"FAIL {exception.Message}");
            return false;
        }
    }

    private static void RunApplication()
    {
        byte[] module = File.ReadAllBytes(AppPath);
        Check(module.Length <= MaximumAppBytes, "App zu gross.");
        ulong freeBefore = FreeBytes();
        ulong freeAfterFirst = 0;
        for (int run = 1; run <= AppRuns; run++)
        {
            appOutput.Clear();
            // Gas 0 bedeutet ohne Budget; die C#-Laufzeit braucht fuer ihren Start sehr viele Instruktionen.
            long elapsed = Execute(module, "_start", 0, out Status status, out int result, out string message);
            ulong free = FreeBytes();
            if (run == 1) freeAfterFirst = free;
            Write($"app run={run} status={status} exit={result} ms={elapsed} bytes={module.Length} freeMiB={free >> 20} " +
                $"output='{appOutput.ToString().Trim()}' {message}");
            Check(status == Status.Ok && result == 0, "C#-App nicht erfolgreich beendet.");
            Check(appOutput.ToString().Contains("CSPROBE 26", StringComparison.Ordinal), "Unerwartete App-Ausgabe.");
        }
        ulong freeAfterLast = FreeBytes();
        Write($"memory freeBeforeMiB={freeBefore >> 20} afterFirstMiB={freeAfterFirst >> 20} afterLastMiB={freeAfterLast >> 20}");
        // Wiederholte Starts duerfen nicht fortlaufend Speicher verlieren; der erste Lauf darf Caches anlegen.
        Check(freeAfterFirst - Math.Min(freeAfterFirst, freeAfterLast) <= LeakToleranceBytes, "Speicher waechst ueber wiederholte Starts.");
    }

    private static ulong FreeBytes()
    {
        GC.Collect();
        return CosmosSystemInfo.PhysicalMemory().Free;
    }

    private static long Expect(string name, byte[] module, string entry, ulong gas, Status expectedStatus, int expectedResult)
    {
        long elapsed = Execute(module, entry, gas, out Status status, out int result, out string message);
        Write($"{name} status={status} result={result} ms={elapsed} {message}");
        Check(status == expectedStatus && result == expectedResult, $"{name}: erwartet {expectedStatus}/{expectedResult}.");
        return elapsed;
    }

    private static long Execute(byte[] module, string entry, ulong gas, out Status status, out int result, out string message)
    {
        byte[] entryBytes = Encoding.ASCII.GetBytes(entry + "\0");
        byte* text = stackalloc byte[256];
        int value = 0;
        var watch = Stopwatch.StartNew();
        int code;
        fixed (byte* bytes = module)
        fixed (byte* name = entryBytes)
        {
            code = Run(bytes, (uint)module.Length, name, gas, WasmStackBytes, MemoryLimitBytes, &value, text, 256);
        }
        status = (Status)code;
        result = value;
        message = Marshal.PtrToStringAnsi((IntPtr)text) ?? string.Empty;
        return watch.ElapsedMilliseconds;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Write(string message) => Log.WriteString($"WASM_PROBE {message}\n");

    [DllImport("*", EntryPoint = "zehos_wasm_run", ExactSpelling = true)]
    private static extern int Run(byte* bytes, uint length, byte* entry, ulong gas, uint stackBytes, ulong memoryBytes,
        int* result, byte* message, uint messageLength);

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_write")]
    private static void HostWrite(int fd, byte* data, uint length)
    {
        _ = fd;
        string text = Encoding.UTF8.GetString(data, (int)Math.Min(length, 4096));
        if (appOutput.Length < 4096) appOutput.Append(text);
        Log.WriteString(text);
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_time_ns")]
    private static ulong HostTime(int clockId)
    {
        // WASI-Uhr 0 ist Realzeit (UTC vom Hypervisor), alle anderen messen monoton ab Boot.
        if (clockId == 0) return (ulong)(DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100;
        return (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));
    }

    [UnmanagedCallersOnly(EntryPoint = "zehos_wasm_host_random")]
    private static void HostRandom(byte* buffer, uint length)
    {
        var bytes = new byte[length];
        CosmosSaltSource.Fill(bytes);
        new ReadOnlySpan<byte>(bytes).CopyTo(new Span<byte>(buffer, (int)length));
    }
}
#endif
