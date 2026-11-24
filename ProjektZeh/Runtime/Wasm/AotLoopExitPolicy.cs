namespace ProjektZeh.Runtime.Wasm;

/// <summary>
/// Entscheidet im Scheduler-Tick, ob der unterbrochene Wasm-Worker seinen AOT-Code verlaesst (#423, R8). Ohne Cosmos-Typen
/// fuer den Hosttest. Laeuft im Interruptkontext: keine Allokation, keine Sperre, kein Protokoll.
/// </summary>
internal static class AotLoopExitPolicy
{
    /// <summary>Groesse des Worker-Stacks; tiefer als so weit unter der Sicherung liegt kein Rahmen des Laufs.</summary>
    internal const ulong StackBytes = 8 * 1024 * 1024;

    /// <summary>
    /// true nur, wenn der Lauf abgebrochen werden soll und der Worker gerade AOT-Code selbst ausfuehrt. In WAMR-Helfern,
    /// im Allokator und in Hostfunktionen liegt <paramref name="instructionPointer"/> ausserhalb des Codebereichs; dort
    /// koennten Sperren oder Speicher gehalten sein, der naechste Tick versucht es erneut.
    /// </summary>
    public static bool ShouldLeave(bool abort, int threadId, int workerThreadId, bool inNativeRun, int hostDepth,
        ulong instructionPointer, ulong stackPointer, ulong code, ulong codeEnd, ulong savedStack)
    {
        if (!abort) return false;
        if (workerThreadId < 0 || threadId != workerThreadId) return false;
        if (!inNativeRun || hostDepth != 0) return false;
        // Nicht veroeffentlicht: Laden, Instanziieren, Abbau oder der Ausstieg laeuft schon.
        if (code == 0 || savedStack == 0) return false;
        if (instructionPointer < code || instructionPointer >= codeEnd) return false;
        return stackPointer < savedStack && savedStack - stackPointer <= StackBytes;
    }
}
