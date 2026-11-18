using System;
using System.Collections.Generic;
using System.Threading;
using ProjektZeh.Shared;

namespace ProjektZeh.Scheduling;

/// <summary>
/// Ein dauerhafter Thread mit Warteschlange fuer seltene, aber zeitkritisch nicht planbare Arbeit (Kennwortoperationen).
/// Der Kernel legt ihn beim Boot an; spaete Thread-Starts scheitern unter Cosmos 3.0.97 nach Dateilast (siehe ThreadStarter).
/// Fehlt er (Boot-Start gescheitert), versucht jedes <see cref="Post"/> den Start erneut und meldet sonst den Fehler.
/// </summary>
internal sealed class DedicatedWorker(Action<string>? log = null, Func<ThreadStart, ResultObject<Thread>>? startThread = null)
{
    private readonly object gate = new();
    private readonly Queue<Action> work = new();
    private readonly Func<ThreadStart, ResultObject<Thread>> startThread =
        startThread ?? (body => ThreadStarter.TryStart(body, background: true));
    private Thread? thread;

    public ResultErr Start()
    {
        lock (gate) return StartLocked();
    }

    /// <summary>Reiht <paramref name="action"/> ein; Ausnahmen daraus muss die Aktion selbst behandeln.</summary>
    public ResultErr Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            var started = StartLocked();
            if (!started.IsOK()) return started;
            work.Enqueue(action);
            Monitor.Pulse(gate);
            return ResultErr.CreateSuccess();
        }
    }

    private ResultErr StartLocked()
    {
        if (thread != null) return ResultErr.CreateSuccess();
        var started = startThread(Run);
        if (!started.IsOK()) return started.Status;
        thread = started.Value;
        return ResultErr.CreateSuccess();
    }

    private void Run()
    {
        while (true)
        {
            Action next;
            lock (gate)
            {
                while (work.Count == 0) Monitor.Wait(gate);
                next = work.Dequeue();
            }
            // Eine fehlerhafte Aktion darf den einzigen Worker nicht beenden. Nur der Typ wird protokolliert: Meldungen von
            // Kennwortoperationen koennen Kennwortdetails enthalten.
            try { next(); }
            catch (Exception exception) { log?.Invoke("Worker-Aktion fehlgeschlagen: " + exception.GetType().Name); }
        }
    }
}
