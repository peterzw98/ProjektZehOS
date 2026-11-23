#if ZEHOS_WASM
using System.Runtime.InteropServices;
using System.Threading;

namespace ProjektZeh.Runtime.Wasm;

// Roadmap 12.4-I2-C: Die Laufarena der C-Laufzeit (Native/Wasm/libc/libc.c) zaehlt die nativen Bloecke eines Laufs, misst
// ihre Spitze und gibt nach einer CPU-Isolation frei, was der aufgegebene Worker hielt. Grenze 0: nur messen (das
// Budget setzt I2-E). Eigene Datei wie WasmRuntime.Abort.cs, damit WasmRuntime.cs neben dem Multicore-Zweig klein bleibt.
internal static unsafe partial class WasmRuntime
{
    private const int ArenaNone = 0, ArenaWasm3 = 1, ArenaAot = 2;
    // Wer die Arena haelt: vom Worker vor dem Start gesetzt und nach dem Ende geloescht, nach einer CPU-Isolation von der
    // Hauptschleife gelesen (der Worker laeuft dann nie wieder).
    private static int arenaHolder;

    // Direkt vor NativeRun/NativeRunAot, nach EnterNativeRun. false: Der Lauf geht ohne Arena weiter; das meldet
    // ReportArenaStart nach LeaveNativeRun (keine Protokollzeile im nativen Abschnitt).
    private static bool BeginArena(bool aot)
    {
        Volatile.Write(ref arenaHolder, aot ? ArenaAot : ArenaWasm3);
        if (ArenaBegin(0) != 0) return true;
        Volatile.Write(ref arenaHolder, ArenaNone);
        return false;
    }

    // Im finally desselben Blocks, vor LeaveNativeRun; nur wenn BeginArena gelang. Sonst gehoert die laufende Arena einem
    // frueheren Lauf (aufgegebener AOT-Worker), und end gaebe dessen Bloecke frei.
    private static void EndArena(bool started)
    {
        if (!started) return;
        ArenaEnd();
        Volatile.Write(ref arenaHolder, ArenaNone);
    }

    private static void ReportArenaStart(bool started)
    {
        if (!started) Cosmos.Kernel.System.Diagnostics.Log.WriteString("WASM Arena nicht gestartet\n");
    }

    // PollFaultIsolation, nach ReleaseRunResources: Der aufgegebene Worker steht in der Quarantaene und laeuft nie wieder,
    // erst jetzt duerfen seine Bloecke frei werden. Unter AOT bleiben sie belegt wie vor I2-C: WAMR haelt globalen
    // Zustand (runtime_ref_count, registrierte Hostfunktionen), den nur wasm_runtime_destroy des toten Laufs abgebaut
    // haette; der naechste AOT-Lauf benutzt ihn weiter. Die Arena bleibt dann beim toten Lauf, spaetere Laeufe messen nicht.
    private static void ReleaseIsolatedArena()
    {
        int holder = Interlocked.Exchange(ref arenaHolder, ArenaNone);
        if (holder == ArenaWasm3) ArenaEnd();
        else if (holder == ArenaAot)
            Cosmos.Kernel.System.Diagnostics.Log.WriteString("WASM Arena nach CPU-Fehler unter AOT nicht freigegeben\n");
    }

    // Spitze des letzten Laufs mit Arena in Bytes; bleibt bis zum naechsten erfolgreichen Start stehen.
    private static ulong ArenaPeakBytes => ArenaPeak();

#if ZEHOS_ISOLATION_PROBE
    // Isolationsprobe (I2-C): Der Probelauf haelt vor seiner CPU-Ausnahme einen Block in der Arena; dass ihn
    // PollFaultIsolation freigibt, zeigt "frei=" der Speicherzeilen vor und nach den Ausnahmen.
    internal static void ProbeArenaBlock(nuint bytes)
    {
        bool allocated = BeginArena(aot: false) && ProbeMalloc(bytes) != null;
        Cosmos.Kernel.System.Diagnostics.Log.WriteString($"ISOLATION_PROBE arena={(allocated ? 1 : 0)}\n");
    }

    [DllImport("*", EntryPoint = "malloc", ExactSpelling = true)]
    private static extern void* ProbeMalloc(nuint size);

#endif
    [DllImport("*", EntryPoint = "zehos_arena_begin", ExactSpelling = true)]
    private static extern int ArenaBegin(nuint limit);

    [DllImport("*", EntryPoint = "zehos_memory_end", ExactSpelling = true)]
    private static extern void ArenaEnd();

    [DllImport("*", EntryPoint = "zehos_memory_peak", ExactSpelling = true)]
    private static extern nuint ArenaPeak();
}
#endif
