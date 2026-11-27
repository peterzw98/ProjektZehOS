using System.Threading;

namespace ProjektZeh.Runtime;

/// <summary>Ergebnis der Pruefung, ob eine CPU-Ausnahme nur den Wasm-Worker beendet (Roadmap 12.4-I1).</summary>
internal enum IsolationDecision
{
    Isolate,
    NotProgramFault,
    NotWorker,
    InterruptsMasked,
    OutsideGuest,
    InHostFunction,
    HoldsStorageLock,
    HoldsAppStorageGate,
}

/// <summary>
/// Reine Entscheidung fuer den Ausnahmepfad: Isoliert wird nur Gastcode des Wasm-Workers, weil nur dort sicher keine
/// Kernelsperre gehalten wird. Alles andere haelt das System wie bisher an (Entscheidung 3 von Peter, 18.11.).
/// </summary>
internal static class FaultIsolationPolicy
{
    /// <summary>Kein gueltiger Vektor: Der Halt kam nicht ueber Panic.CpuException oder ohne frischen Kontext.</summary>
    public const long UnknownVector = -1;

    /// <summary>
    /// Nur Fehler, die der ausgefuehrte Code selbst ausloest: #DE (0), #UD (6), #GP (13) und #PF (14). NMI, Machine
    /// Check, Double Fault und alle uebrigen Vektoren melden einen Zustand der Maschine statt einen App-Fehler und halten
    /// deshalb an, auch wenn sie zufaellig waehrend Gastcode eintreffen (Review pink P2-1).
    /// </summary>
    public static bool IsProgramFault(long vector) => vector is 0 or 6 or 13 or 14;

    /// <param name="interruptsEnabled">
    /// Interruptflag des fehlerhaften Codes (Cosmos <c>CpuExceptionInfo.InterruptsEnabled</c>, Patch ausnahme-interrupts).
    /// Gastcode laeuft immer mit freigegebenen Interrupts. Ein Interrupt-Handler laeuft maskiert auf dem Stack des Threads,
    /// den er unterbrochen hat; trifft er den Worker im Gastcode, meldet Cosmos den Worker, <paramref name="inNativeRun"/>
    /// ist 1 und die Hosttiefe 0. Eine Ausnahme im Handler ist aber Kernelcode, der Sperren halten kann: Halt (E6-C.10, #390).
    /// </param>
    public static IsolationDecision Decide(long vector, int currentThreadId, int workerThreadId, bool inNativeRun,
        bool interruptsEnabled, int hostDepth, bool holdsStorageLock, bool holdsAppStorageGate)
    {
        if (!IsProgramFault(vector)) return IsolationDecision.NotProgramFault;
        if (workerThreadId < 0 || currentThreadId != workerThreadId) return IsolationDecision.NotWorker;
        if (!interruptsEnabled) return IsolationDecision.InterruptsMasked;
        if (!inNativeRun) return IsolationDecision.OutsideGuest;
        if (hostDepth != 0) return IsolationDecision.InHostFunction;
        if (holdsStorageLock) return IsolationDecision.HoldsStorageLock;
        if (holdsAppStorageGate) return IsolationDecision.HoldsAppStorageGate;
        return IsolationDecision.Isolate;
    }
}

/// <summary>
/// Ergebnis eines Abraeumversuchs, als eigener Typ ohne Cosmos fuer den Hosttest. Die ersten vier Werte entsprechen
/// Cosmos <c>ThreadReapResult</c>; <see cref="Failed"/> steht fuer eine Ausnahme aus <c>TryReapAbandoned</c>.
/// </summary>
internal enum ReapOutcome
{
    Reaped,
    NotFound,
    NotAbandoned,
    StillOnCpu,
    Failed,
}

/// <summary>Was die Hauptschleife nach einem Abraeumversuch tut.</summary>
internal enum ReapStep
{
    /// <summary>Nichts schliessen, in der naechsten Runde erneut versuchen.</summary>
    Wait,
    /// <summary>Wie <see cref="Wait"/>, und einmal melden, dass die Wasm-Laeufe haengen.</summary>
    WaitAndWarn,
    /// <summary>Versuch beenden, Laufressourcen freigeben und den Ersatzworker starten.</summary>
    Proceed,
}

/// <summary>
/// Aufgegebener Worker, den die Hauptschleife abraeumen will (E6-G, #433). Cosmos raeumt ihn erst ab, wenn die CPU der Ausnahme
/// seinen Stack verlassen hat (<c>TryReapAbandoned</c> meldet bis dahin <c>StillOnCpu</c>); solange versucht die Hauptschleife
/// es in jeder Runde erneut. Gehoert nur der Hauptschleife, deshalb ohne Sperre.
/// </summary>
internal sealed class PendingReap
{
    /// <summary>Runden mit StillOnCpu, nach denen einmal gewarnt wird: Haengt die CPU im Bericht, bleibt die Wasm-Queue stehen.</summary>
    public const int WarnAfterRounds = 1000;

    private int threadId = -1;
    private int rounds;

    public bool HasPending => threadId >= 0;

    public int ThreadId => threadId;

    public void Begin(int id)
    {
        threadId = id;
        rounds = 0;
    }

    /// <summary>
    /// Bewertet einen Abraeumversuch. Weiter geht es nur, wenn der Worker sicher nie wieder laeuft: <c>Reaped</c>;
    /// <c>NotFound</c> (Anspruch schon gesetzt, und den setzt Cosmos erst fuer einen Dead-Thread, den keine CPU mehr
    /// fuehrt; einziger Aufrufer ist die Hauptschleife); <c>Failed</c> (die Ausnahme kann erst nach diesem Anspruch kommen,
    /// davor liest Cosmos nur). <c>NotAbandoned</c> dagegen hiesse, der Thread ist nicht als Dead aufgegeben und koennte
    /// noch laufen; Dateien und Netz zu schliessen waere dann unsicher. Er wartet deshalb wie <c>StillOnCpu</c>, die
    /// Wasm-Laeufe bleiben eingereiht, und nach der Schwelle kommt genau eine Warnung (Review laser #576, Befund 3).
    /// </summary>
    public ReapStep Next(ReapOutcome outcome)
    {
        if (outcome != ReapOutcome.StillOnCpu && outcome != ReapOutcome.NotAbandoned) return ReapStep.Proceed;
        return ++rounds == WarnAfterRounds ? ReapStep.WaitAndWarn : ReapStep.Wait;
    }

    /// <summary>Beendet den Versuch und liefert die Id.</summary>
    public int Finish()
    {
        int id = threadId;
        threadId = -1;
        rounds = 0;
        return id;
    }
}

/// <summary>Laufzeit, mit der ein isolierter Lauf nativ lief (<c>WasmRuntime.BeginArena</c>).</summary>
internal enum NativeEngine
{
    None,
    Wasm3,
    Aot,
}

/// <summary>Ergebnis von <c>zehos_wasm_aot_reclaim</c> (<c>zehos_host_wamr.c</c>); die Werte sind die der C-Funktion.</summary>
internal enum AotReclaim
{
    /// <summary>Kein AOT-Lauf offen: Die Ausnahme lag vor dem Start oder nach dem Abbau von WAMR.</summary>
    NotActive = 0,
    /// <summary>Instanz, Modul, Ausfuehrungsumgebung, Ladepuffer und WAMR selbst sind abgebaut; AOT ist wieder frei.</summary>
    Released = 1,
    /// <summary>Die Ausnahme lag nicht im AOT-Code; der halbe WAMR-Zustand bleibt, AOT bis zum Neustart gesperrt.</summary>
    Kept = 2,
}

/// <summary>Was die Hauptschleife nach einer Isolation mit dem nativen Speicher des Laufs tut (#582).</summary>
internal enum NativeReleasePlan
{
    /// <summary>Kein nativer Lauf, nichts belegt.</summary>
    Nothing,
    /// <summary>Die Laufarena freigeben (zehos_memory_end); danach ist nichts mehr belegt.</summary>
    EndArena,
    /// <summary>AOT ohne Arena, aber ueber WAMR abgebaut; nichts mehr belegt.</summary>
    Freed,
    /// <summary>Die Arena gehoert einem belassenen AOT-Lauf und bleibt fuer immer aktiv (spaetere Laeufe ohne Arena).</summary>
    KeepArena,
    /// <summary>Nativer Lauf ohne Arena, dessen Speicher niemand freigeben kann.</summary>
    Leaked,
}

/// <summary>Entscheidung ohne Cosmos und ohne C fuer den Hosttest; aufgerufen aus <c>WasmRuntime.ReleaseIsolatedNative</c>.</summary>
internal static class NativeReleasePolicy
{
    /// <summary>
    /// Freigabeweg nach einer Isolation. Die Arena zaehlt jeden Block des Laufs; sie freizugeben ist bei wasm3 immer sicher,
    /// weil wasm3 keinen globalen Zustand hat (je Lauf eigene Umgebung). Bei AOT nur, wenn WAMR schon abgebaut ist
    /// (<see cref="AotReclaim.Released"/>) oder nie lief: sonst zeigten WAMRs globale Listen und der Referenzzaehler auf
    /// freigegebene Bloecke. <paramref name="aot"/> gilt nur fuer <see cref="NativeEngine.Aot"/>.
    /// </summary>
    public static NativeReleasePlan Plan(NativeEngine engine, bool arenaHeld, AotReclaim aot)
    {
        if (engine == NativeEngine.Aot && aot == AotReclaim.Kept)
            return arenaHeld ? NativeReleasePlan.KeepArena : NativeReleasePlan.Leaked;
        if (arenaHeld) return NativeReleasePlan.EndArena;
        if (engine == NativeEngine.Wasm3) return NativeReleasePlan.Leaked;
        if (engine == NativeEngine.Aot && aot == AotReclaim.Released) return NativeReleasePlan.Freed;
        return NativeReleasePlan.Nothing;
    }

    /// <summary>true, wenn nach dem Plan nativer Speicher des Laufs belegt bleibt.</summary>
    public static bool LeavesNativeMemory(NativeReleasePlan plan) => plan is NativeReleasePlan.KeepArena or NativeReleasePlan.Leaked;
}

/// <summary>
/// Begrenzt die Isolationen, die etwas belegt lassen (#582). Bis E6-G kostete jede Isolation Thread, 8-MiB-Stack und den
/// nativen Speicher des Laufs, deshalb startete nach drei Isolationen kein Worker mehr. Jetzt zaehlen nur noch Isolationen,
/// nach denen Thread und Stack nicht abgeraeumt sind oder nativer Speicher belegt bleibt; eine saubere Isolation kostet
/// nichts, was ein Neustart zurueckgaebe. Die Grenze schuetzt nicht vor einem beschaedigten Heap (das kann schon die erste
/// Ausnahme), dafuer steht "Neustart empfohlen" in jeder Meldung. Gehoert nur der Hauptschleife, deshalb ohne Sperre.
/// </summary>
internal sealed class IsolationBudget
{
    /// <summary>Nach so vielen Isolationen mit Rest startet bis zum Neustart kein Worker mehr.</summary>
    public const int MaximumUnclean = 3;

    private int unclean;

    /// <summary>Bisher gezaehlte Isolationen mit Rest.</summary>
    public int Unclean => unclean;

    /// <summary>true, wenn nach dieser Isolation etwas belegt bleibt.</summary>
    public static bool LeavesResources(ReapOutcome reap, NativeReleasePlan native) =>
        reap != ReapOutcome.Reaped || NativeReleasePolicy.LeavesNativeMemory(native);

    /// <summary>Zaehlt eine Isolation; true, wenn danach kein Worker mehr starten darf.</summary>
    public bool Record(bool leavesResources)
    {
        if (leavesResources) unclean++;
        return unclean >= MaximumUnclean;
    }
}

/// <summary>
/// Uebergabe eines aufgegebenen Workers vom Ausnahmepfad an die Hauptschleife (E6-C.9, #390). Der Ausnahme-Hook meldet den
/// Thread an, bevor Cosmos den Wechsel weg von ihm anlegt (<c>TryAbandonCurrentFromInterrupt</c>); die Hauptschleife
/// uebernimmt ihn erst, wenn die CPU der Ausnahme ihn nicht mehr als laufenden Thread fuehrt. Vorher konnte die
/// Hauptschleife auf einer anderen CPU schon aufraeumen und einen neuen Worker starten, waehrend Cosmos noch entschied,
/// ob es den Wechsel ueberhaupt anlegen kann, und bei einem Fehlschlag das System anhielt (Datentraeger mitten im
/// Schreiben von <c>CloseAll</c>).
/// </summary>
/// <remarks>
/// Es gibt nur einen Wasm-Worker und der Hook isoliert nur ihn; zwei gleichzeitige Anmeldungen gibt es deshalb nicht.
/// <see cref="Stage"/> laeuft im Ausnahmepfad: keine Allokation, keine Sperre.
/// </remarks>
internal sealed class IsolationHandoff
{
    /// <summary>Wert fuer "keine CPU fuehrt einen Thread" (Cosmos <c>CpuExceptionInfo.NoThread</c>).</summary>
    public const long NoThread = uint.MaxValue;

    private int threadId = -1;
    private int cpu = -1;

    /// <summary>Vom Ausnahme-Hook: Thread <paramref name="faultingThreadId"/> auf <paramref name="faultingCpu"/> wird aufgegeben.</summary>
    public void Stage(int faultingThreadId, int faultingCpu)
    {
        // CPU zuerst: Wer die Id liest (Volatile, Erwerb), sieht danach die passende CPU.
        Volatile.Write(ref cpu, faultingCpu);
        Volatile.Write(ref threadId, faultingThreadId);
    }

    /// <summary>CPU der angemeldeten Ausnahme oder -1; fuer die Abfrage des laufenden Threads dort.</summary>
    public int PendingCpu => Volatile.Read(ref threadId) < 0 ? -1 : Volatile.Read(ref cpu);

    /// <summary>
    /// Von der Hauptschleife: liefert die Id des aufgegebenen Workers und loescht die Anmeldung, sobald
    /// <paramref name="runningOnCpu"/> (laufender Thread der CPU aus <see cref="PendingCpu"/>, <see cref="NoThread"/> ohne)
    /// ein anderer Thread ist; sonst -1, und die naechste Runde fragt erneut.
    /// </summary>
    /// <remarks>
    /// Den laufenden Thread einer CPU aendert nur diese CPU selbst; nach der Ausnahme nur noch der Wechsel aus dem
    /// Ausnahmepfad, und den legt Cosmos nur an, wenn es den Thread wirklich aufgibt. Haelt Cosmos stattdessen an, bleibt
    /// der Worker der laufende Thread, und die Hauptschleife raeumt nichts ab. Ein veralteter Lesewert verzoegert nur.
    /// </remarks>
    public int TryTake(long runningOnCpu)
    {
        int pending = Volatile.Read(ref threadId);
        if (pending < 0 || runningOnCpu == pending) return -1;
        return Interlocked.CompareExchange(ref threadId, -1, pending) == pending ? pending : -1;
    }
}
