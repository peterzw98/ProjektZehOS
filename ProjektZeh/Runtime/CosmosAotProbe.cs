#if ZEHOS_AOT_PROBE
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.Kernel.System.Diagnostics;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe fuer Etappe B des AOT-Plans: Laeuft Maschinencode aus dem Cosmos-Heap? Ergebnis seriell als
/// AOT_PROBE-Zeilen. Endet der Aufruf mit einer Seitenschutzverletzung, fehlt die PASS-Zeile; die vorher
/// gemeldeten Register und Adressen bleiben im seriellen Protokoll.
/// </summary>
internal static unsafe class CosmosAotProbe
{
    private const ulong EferNxe = 1ul << 11;
    private const ulong Cr0WriteProtect = 1ul << 16;
    private const ulong Cr4Smep = 1ul << 20;
    // -mcmodel=kernel: 32-Bit-Spruenge und RIP-relative Zugriffe reichen +-2 GiB weit.
    private const ulong NearCodeDistance = 1ul << 31;
    // ret42, call_host, oob, recurse, recurse_native und ret42 nach den Traps (aot_probe.c).
    private const int FixtureCases = 6;

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
        int passed = 0;
        var worker = new Thread(() => passed = NativeFixture(), WasmRuntime.ThreadStackBytes);
        worker.Start();
        worker.Join();
        if (passed != FixtureCases)
        {
            Write($"FAIL fixture {passed}/{FixtureCases}");
            return false;
        }
        Write($"fixture PASS {passed}/{FixtureCases}");
        Write("PASS");
        return true;
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
}
#endif
