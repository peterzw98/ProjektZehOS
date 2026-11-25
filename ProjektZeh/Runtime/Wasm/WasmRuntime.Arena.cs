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
    // true, wenn seit der letzten Speicherzeile ein Lauf mit Arena lief; sonst nennt ReportMemory keine Spitze (spitze=-),
    // statt die eines frueheren Laufs zu wiederholen. Der Worker liest und schreibt,
    // nach einer CPU-Isolation loescht die Hauptschleife sie vor dem Start des Ersatzworkers.
    private static bool arenaMeasured;
    // true, sobald die Arena einem aufgegebenen AOT-Lauf gehoert und deshalb nie mehr endet. Nur die Hauptschleife setzt sie,
    // vor dem Start des Ersatzworkers.
    private static bool arenaLost;

    // Direkt vor NativeRun/NativeRunAot, nach EnterNativeRun und im try davor (LeaveNativeRun laeuft also auch, wenn das
    // hier wirft). false: Der Lauf geht ohne Arena weiter; das meldet ReportArenaStart nach LeaveNativeRun (keine
    // Protokollzeile im nativen Abschnitt).
    private static bool BeginArena(bool aot)
    {
        // Eine verlorene Arena gehoert nicht diesem Lauf: ohne Halter weiter, sonst gaebe ReleaseIsolatedArena sie frei.
        if (Volatile.Read(ref arenaLost)) return false;
        // Vor dem Start: Faellt der Worker gleich nach zehos_arena_begin, muss ReleaseIsolatedArena den Halter kennen, sonst
        // bliebe die Arena fuer immer aktiv. Faellt er davor, laeuft keine Arena, und end raeumt nichts.
        Volatile.Write(ref arenaHolder, aot ? ArenaAot : ArenaWasm3);
        if (ArenaBegin(0) == 0)
        {
            Volatile.Write(ref arenaHolder, ArenaNone);
            return false;
        }
        arenaMeasured = true;
        return true;
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

    // PollFaultIsolation, nach ReleaseRunResources: Der aufgegebene Worker ist Dead (nie wieder bereit) und laeuft nie wieder,
    // erst jetzt duerfen seine Bloecke frei werden. Unter AOT bleiben sie belegt wie vor I2-C: WAMR haelt globalen
    // Zustand (runtime_ref_count, registrierte Hostfunktionen), den nur wasm_runtime_destroy des toten Laufs abgebaut
    // haette; der naechste AOT-Lauf benutzt ihn weiter. Die Arena bleibt dann beim toten Lauf, spaetere Laeufe messen nicht.
    private static void ReleaseIsolatedArena()
    {
        int holder = Interlocked.Exchange(ref arenaHolder, ArenaNone);
        // Den Lauf meldet kein ReportMemory mehr; spaetere Laeufe ohne Arena sollen spitze=- nennen, nicht seine.
        arenaMeasured = false;
        if (holder == ArenaWasm3) ArenaEnd();
        else if (holder == ArenaAot)
        {
            Volatile.Write(ref arenaLost, true);
            Cosmos.Kernel.System.Diagnostics.Log.WriteString("WASM Arena nach CPU-Fehler unter AOT nicht freigegeben\n");
        }
    }

    // Fuer ReportMemory: Spitze in Bytes des Laufs seit der letzten Speicherzeile, "-" ohne Lauf mit Arena; liest und
    // loescht die Markierung.
    private static string TakeArenaPeak()
    {
        string peak = arenaMeasured ? ((ulong)ArenaPeak()).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
        arenaMeasured = false;
        return peak;
    }

#if ZEHOS_ISOLATION_PROBE
    // Isolationsprobe (I2-C): Der Probelauf haelt vor seiner CPU-Ausnahme einen Block in der Arena; dass ihn
    // PollFaultIsolation freigibt, zeigt "frei=" der Speicherzeilen vor und nach den Ausnahmen.
    internal static void ProbeArenaBlock(nuint bytes)
    {
        bool allocated = BeginArena(aot: false) && ProbeMalloc(bytes) != null;
        // frei= belegt, dass der gehaltene Block den freien Speicher wirklich senkt.
        Cosmos.Kernel.System.Diagnostics.Log.WriteString(
            $"ISOLATION_PROBE arena={(allocated ? 1 : 0)} used={ArenaUsed()} frei={CosmosSystemInfo.PhysicalMemory().Free / 1024}\n");
    }

    // Nach der Isolation vom Probethread gelesen: 0, wenn PollFaultIsolation die Bloecke freigegeben hat.
    internal static ulong ProbeArenaUsedBytes => ArenaUsed();

    [DllImport("*", EntryPoint = "zehos_memory_used", ExactSpelling = true)]
    private static extern nuint ArenaUsed();

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
