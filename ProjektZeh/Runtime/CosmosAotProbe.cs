#if ZEHOS_AOT_PROBE
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Cosmos.Kernel.System.Diagnostics;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe fuer Etappe B des AOT-Plans: Laeuft Maschinencode aus dem Cosmos-Heap? Ergebnis seriell als
/// AOT_PROBE-Zeilen. Endet der Aufruf mit einer Seitenschutzverletzung, fehlt die PASS-Zeile; die vorher
/// gemeldeten Register und Adressen bleiben im seriellen Protokoll. Etappe D prueft danach denselben Weg wie
/// Paket-Apps: WasmRuntime mit .aot-Bytes ueber zehos_wasm_run_aot (RunRuntime).
/// </summary>
internal static unsafe class CosmosAotProbe
{
    private const ulong EferNxe = 1ul << 11;
    private const ulong Cr0WriteProtect = 1ul << 16;
    private const ulong Cr4Smep = 1ul << 20;
    // -mcmodel=kernel: 32-Bit-Spruenge und RIP-relative Zugriffe reichen +-2 GiB weit.
    private const ulong NearCodeDistance = 1ul << 31;
    // ret42, call_host, fsleep, oob, recurse, recurse_native und ret42 nach den Traps (aot_probe.c).
    private const int FixtureCases = 7;

    public static bool Run()
    {
        ulong efer = NativeEfer();
        ulong cr0 = NativeCr0();
        ulong cr4 = NativeCr4();
        Write($"regs efer=0x{efer:x} nxe={Bit(efer, EferNxe)} cr0.wp={Bit(cr0, Cr0WriteProtect)} cr4=0x{cr4:x} smep={Bit(cr4, Cr4Smep)} cr3=0x{NativeCr3():x}");

        ulong kernel = NativeKernelAddress();
        ulong heap = NativeHeapAddress();
        ulong distance = heap > kernel ? heap - kernel : kernel - heap;
        Write($"layout kernel=0x{kernel:x} heap=0x{heap:x} abstand=0x{distance:x} nah={(distance < NearCodeDistance ? 1 : 0)}");

        // Vor dem Aufruf melden: Ist der Heap nicht ausfuehrbar, kehrt der Aufruf nicht zurueck.
        Write("exec start");
        ulong block;
        int result = NativeExec(&block);
        Write($"exec block=0x{block:x} result={result}");
        if (result != 42)
        {
            Write("FAIL exec lieferte nicht 42");
            return false;
        }

        uint major, minor, patch;
        if (NativeWamrInit(&major, &minor, &patch) != 1)
        {
            Write("FAIL wamr wasm_runtime_full_init");
            return false;
        }
        Write($"wamr init PASS version={major}.{minor}.{patch}");

        // Wie Paket-Apps auf einem eigenen Thread mit 8 MiB Stack; die native Stackgrenze haengt davon ab.
        // Waehrend fsleep schlaeft, rechnet ein zweiter Thread mit double; beide Ergebnisse muessen stimmen.
        int passed = 0;
        bool done = false;
        bool noiseOk = true;
        var noise = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                double value = 3.0;
                for (int step = 0; step < 1000; step++) value = value * 0.999 + 0.003;
                if (Math.Abs(value - NoiseExpected()) > 1e-9) noiseOk = false;
                Thread.Sleep(1);
            }
        });
        var worker = new Thread(() => passed = NativeFixture(), WasmRuntime.ThreadStackBytes);
        noise.Start();
        worker.Start();
        worker.Join();
        Volatile.Write(ref done, true);
        noise.Join();
        if (!noiseOk)
        {
            Write("FAIL fixture Nebenthread rechnete falsch");
            return false;
        }
        if (passed != FixtureCases)
        {
            Write($"FAIL fixture {passed}/{FixtureCases}");
            return false;
        }
        Write($"fixture PASS {passed}/{FixtureCases}");
        if (!RunRuntime()) return false;
        Write("PASS");
        return true;
    }

    // Etappe D: Laeufe ueber WasmRuntime (gemeinsamer Worker, Hostfunktionstabelle, Statuscodes wie wasm3).
    private const string DataRoot = "/data";
    private const string ProbeFile = "/data/aotprobe.txt";
    private const string DoomAotPath = "/data/doom.aot";
    private const int MaximumModuleBytes = 64 * 1024 * 1024;
    private const int Restarts = 20;
    private const int FileRounds = 20;
    // WAMR startet je Lauf neu; nach dem ersten Lauf darf kein Speicher mehr verloren gehen. Die Toleranz deckt
    // Schwankungen des Cosmos-Allokators ab, nicht einen Verlust je Lauf (20 Laeufe).
    private const ulong LeakToleranceBytes = 1024 * 1024;
    private const int DoomSeconds = 20;
    private const string DoomTestFrame = "ff0000/00ff00/0000ff/ffffff";
    private static readonly byte[] NoModule = [];

    private static bool RunRuntime()
    {
        try
        {
            byte[] fixture = FixtureBytes(0);
            Expect("d ret42", fixture, "ret42", WasmStatus.Ok, 42);
            Expect("d oob", fixture, "oob", WasmStatus.Trap, 0, "out of bounds memory access");
            Expect("d recurse", fixture, "recurse", WasmStatus.Trap, 0, "out of bounds memory access");
            Expect("d recurse_native", fixture, "recurse_native", WasmStatus.Trap, 0, "native stack overflow");
            // Importe ausserhalb der Hostfunktionstabelle (B-Fixture: zehos.host_add) trappen erst beim Aufruf.
            Expect("d unlinked", fixture, "call_host", WasmStatus.Trap, 0, "unlinked import function");
            // Die B-Fixture hat keinen WASI-Einstieg.
            Expect("d entry", fixture, "_start", WasmStatus.EntryMissing, 0);
            byte[] invalid = (byte[])fixture.Clone();
            invalid[1] = (byte)'x';
            Expect("d invalid", invalid, "ret42", WasmStatus.InvalidModule, 0, "kein AOT-Modul");
            byte[] version = (byte[])fixture.Clone();
            version[4] = 4;
            Expect("d version", version, "ret42", WasmStatus.InvalidModule, 0, "unknown binary version");
            Expect("d truncated", fixture.AsSpan(0, 64).ToArray(), "ret42", WasmStatus.InvalidModule, 0);
            // Ein .wasm ist kein .aot: der Kernel enthaelt keinen WAMR-Interpreter.
            Expect("d wasm", Convert.FromHexString("0061736d01000000"), "ret42", WasmStatus.InvalidModule, 0, "kein AOT-Modul");
            RunRestarts(fixture);
            RunHostFixture();
            RunLimitFixture();
            RunDoom();
            return true;
        }
        catch (Exception exception)
        {
            Write($"FAIL {exception.Message}");
            return false;
        }
    }

    // 20 Starts hintereinander; der freie Speicher nach dem letzten darf nicht unter dem nach dem ersten liegen.
    private static void RunRestarts(byte[] fixture)
    {
        ulong freeAfterFirst = 0;
        for (int run = 1; run <= Restarts; run++)
        {
            Expect($"d restart={run}", fixture, "ret42", WasmStatus.Ok, 42);
            ulong free = FreeBytes();
            if (run == 1) freeAfterFirst = free;
            Write($"d restart={run} freeKiB={free / 1024}");
        }
        ulong freeAfterLast = FreeBytes();
        Write($"d memory afterFirstKiB={freeAfterFirst / 1024} afterLastKiB={freeAfterLast / 1024}");
        Check(freeAfterFirst - Math.Min(freeAfterFirst, freeAfterLast) <= LeakToleranceBytes,
            "Speicher waechst ueber wiederholte AOT-Starts.");
    }

    private static void RunHostFixture()
    {
        byte[] host = FixtureBytes(1);
        if (host.Length == 0)
        {
            Write("d host SKIP (aot_runtime_fixture_data.c nicht gebaut, braucht wamrc unter Linux)");
            return;
        }
        var session = new ProbeSession(null);
        Expect("d host _start", host, "_start", WasmStatus.Ok, 7, session: session);
        Check(session.Output.ToString().Contains("AOT_HOST hallo", StringComparison.Ordinal), "d host: Ausgabe fehlt.");
        Expect("d host calls", host, "hostcalls", WasmStatus.Ok, 31);
        Expect("d host oob", host, "host_oob", WasmStatus.Trap, 0, "out of bounds memory access");
        Expect("d host grow", host, "grow", WasmStatus.Ok, -1);
        if (!File.Exists(ProbeFile))
        {
            Write($"d host open SKIP (keine {ProbeFile})");
            return;
        }
        // Offene Datei nach Trap und nach proc_exit: der Kernel schliesst sie. Wiederholt ueber die 16 Plaetze hinaus,
        // damit ein Leck als "Datei nicht geoeffnet" (99) auffiele.
        for (int round = 1; round <= FileRounds; round++)
        {
            Expect($"d host opentrap={round}", host, "opentrap", WasmStatus.Trap, 0, root: DataRoot);
            Expect($"d host openexit={round}", host, "openexit", WasmStatus.Ok, 7, root: DataRoot);
        }
    }

    private static void RunLimitFixture()
    {
        byte[] limit = FixtureBytes(2);
        if (limit.Length == 0)
        {
            Write("d limit SKIP (aot_runtime_fixture_data.c nicht gebaut, braucht wamrc unter Linux)");
            return;
        }
        Expect("d limit", limit, "hostcalls", WasmStatus.LoadFailed, 0, "memory size exceeds limit");
    }

    // Von Hand bereitgestelltes Doom-.aot (scripts/test-wasm-loader.py --probe aot --doom-aot) laeuft bis zum ersten Bild
    // und endet nach DoomSeconds regulaer.
    private static void RunDoom()
    {
        if (!File.Exists(DoomAotPath))
        {
            Write($"d doom SKIP (keine {DoomAotPath})");
            return;
        }
        byte[] aot = File.ReadAllBytes(DoomAotPath);
        Check(aot.Length <= MaximumModuleBytes, "doom.aot zu gross.");
        var session = new ProbeSession(DataRoot, DoomSeconds * 1000);
        var watch = Stopwatch.StartNew();
        WasmStatus status = WasmRuntime.Run(NoModule, "_start", 0, session, out int result, out string message, aot);
        double playSeconds = Math.Max(1, session.LastFrameMs - session.FirstFrameMs) / 1000.0;
        Write($"d doom status={status} exit={result} ms={watch.ElapsedMilliseconds} firstFrameMs={session.FirstFrameMs} " +
            $"frames={session.Frames} fps={session.Frames / playSeconds:F1} testFrame={session.TestFrame} {message}");
        Check(status == WasmStatus.Ok && result == 0, "Doom-AOT nicht regulaer beendet.");
        Check(session.Frames > 0, "Doom-AOT ohne Bild.");
        Check(session.TestFrame == DoomTestFrame, "Doom-AOT: Pruefbild mit falscher Kanal- oder Zeilenfolge.");
    }

    private static void Expect(string name, byte[] aot, string entry, WasmStatus expectedStatus, int expectedResult,
        string? messagePart = null, string? root = null, ProbeSession? session = null)
    {
        var watch = Stopwatch.StartNew();
        WasmStatus status = WasmRuntime.Run(NoModule, entry, 0, session ?? new ProbeSession(root), out int result,
            out string message, aot);
        Write($"{name} status={status} result={result} ms={watch.ElapsedMilliseconds} {message}");
        Check(status == expectedStatus && result == expectedResult, $"{name}: erwartet {expectedStatus}/{expectedResult}.");
        Check(messagePart == null || message.Contains(messagePart, StringComparison.Ordinal),
            $"{name}: Meldung ohne '{messagePart}'.");
    }

    private static byte[] FixtureBytes(int which)
    {
        uint size;
        byte* bytes = NativeFixtureBytes(which, &size);
        return bytes == null || size == 0 ? [] : new ReadOnlySpan<byte>(bytes, (int)size).ToArray();
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

    // Wie die Sitzung der wasm3-Probe: ohne Fenster und Eingabe, Ausgabe seriell, Bilder nur gezaehlt.
    private sealed class ProbeSession(string? root, long runMilliseconds = long.MaxValue) : IWasmSession
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public StringBuilder Output { get; } = new();
        public string? Root => root;
        public bool AllowsNetwork => false;
        public int Frames { get; private set; }
        public long FirstFrameMs { get; private set; } = -1;
        public long LastFrameMs { get; private set; }
        public string TestFrame { get; private set; } = "-";
        public bool ShouldExit => clock.ElapsedMilliseconds >= runMilliseconds;
        public uint TicksMilliseconds => (uint)clock.ElapsedMilliseconds;
        public int PollKey() => -1;
        public bool TryReadEvent(out WasmInputEvent value) { value = default; return false; }
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
            if (FirstFrameMs < 0) FirstFrameMs = clock.ElapsedMilliseconds;
            Frames++;
            LastFrameMs = clock.ElapsedMilliseconds;
        }
    }

    // Referenzwert des Nebenthreads auf demselben Rechenweg.
    private static double NoiseExpected()
    {
        double value = 3.0;
        for (int step = 0; step < 1000; step++) value = value * 0.999 + 0.003;
        return value;
    }

    private static int Bit(ulong value, ulong mask) => (value & mask) != 0 ? 1 : 0;

    private static void Write(string message) => Log.WriteString($"AOT_PROBE {message}\n");

    [DllImport("*", EntryPoint = "zehos_aot_probe_efer", ExactSpelling = true)]
    private static extern ulong NativeEfer();

    [DllImport("*", EntryPoint = "zehos_aot_probe_cr0", ExactSpelling = true)]
    private static extern ulong NativeCr0();

    [DllImport("*", EntryPoint = "zehos_aot_probe_cr3", ExactSpelling = true)]
    private static extern ulong NativeCr3();

    [DllImport("*", EntryPoint = "zehos_aot_probe_cr4", ExactSpelling = true)]
    private static extern ulong NativeCr4();

    [DllImport("*", EntryPoint = "zehos_aot_probe_kernel_address", ExactSpelling = true)]
    private static extern ulong NativeKernelAddress();

    [DllImport("*", EntryPoint = "zehos_aot_probe_heap_address", ExactSpelling = true)]
    private static extern ulong NativeHeapAddress();

    [DllImport("*", EntryPoint = "zehos_aot_probe_wamr_init", ExactSpelling = true)]
    private static extern int NativeWamrInit(uint* major, uint* minor, uint* patch);

    [DllImport("*", EntryPoint = "zehos_aot_probe_fixture", ExactSpelling = true)]
    private static extern int NativeFixture();

    [DllImport("*", EntryPoint = "zehos_aot_probe_exec", ExactSpelling = true)]
    private static extern int NativeExec(ulong* blockAddress);

    [DllImport("*", EntryPoint = "zehos_aot_probe_fixture_bytes", ExactSpelling = true)]
    private static extern byte* NativeFixtureBytes(int which, uint* size);
}
#endif
