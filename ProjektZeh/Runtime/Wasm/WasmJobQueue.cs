using System;
using System.Collections.Generic;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>
/// Warteschlange des Wasm-Workers. Haelt zu jedem Lauf seinen completed-Rueckruf, damit Laeufe ohne Worker (Ruhestand nach
/// CPU-Fehlern, gescheiterter Ersatzstart) ein Ende melden koennen; sonst bliebe ihr Fenster dauerhaft "beschaeftigt".
/// Nicht threadsicher: WasmRuntime greift nur unter seinem gate zu.
/// </summary>
internal sealed class WasmJobQueue
{
    private readonly Queue<(Action Run, Action<WasmStatus, int, string>? Completed)> jobs = new();

    public int Count => jobs.Count;

    /// <param name="completed">Rueckruf des Laufs; null fuer Diagnosejobs ohne Fenster.</param>
    public void Enqueue(Action run, Action<WasmStatus, int, string>? completed)
    {
        ArgumentNullException.ThrowIfNull(run);
        jobs.Enqueue((run, completed));
    }

    public Action Dequeue() => jobs.Dequeue().Run;

    /// <summary>Leert die Warteschlange und liefert die Rueckrufe der verworfenen Laeufe in Reihenfolge.</summary>
    public List<Action<WasmStatus, int, string>> Drain()
    {
        var completions = new List<Action<WasmStatus, int, string>>(jobs.Count);
        while (jobs.Count > 0)
            if (jobs.Dequeue().Completed is { } completed) completions.Add(completed);
        return completions;
    }
}
