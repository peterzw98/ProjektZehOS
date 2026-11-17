using System;
using System.Collections.Generic;
using System.Threading;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Shell;

/// <summary>Zeigt Protokoll und Ergebnis nur in der startenden Shell; der Worker schreibt nie in die Oberflaeche.</summary>
internal sealed class MigrationShellCommands(IMigrationService service, BackgroundTaskService tasks, Func<bool> isAdministrator,
    Func<bool> isLegacy, Func<int>? getSessionGeneration = null, Action<string>? log = null)
{
    private const int PendingLines = 64;
    private readonly ShellJob job = new(tasks, "system", "Migrationsauftrag", log, getSessionGeneration);
    private readonly object gate = new();
    private readonly Queue<string> messages = new();
    private int generation = getSessionGeneration?.Invoke() ?? 0;
    private bool closed;

    public ShellCommand[] Commands => [new("system", "system migrate <Ziel> --yes|cancel",
        "Migriert den Datenbestand auf eine vorbereitete ZehOS-Platte oder bricht den eigenen Auftrag ab.", true, Execute)];

    private ResultErr Execute(string[] arguments, ITextOutput output)
    {
        if (arguments.Length == 1 && arguments[0] == "cancel") return job.Cancel(output);
        if (arguments.Length != 3 || arguments[0] != "migrate" || arguments[2] != "--yes" || string.IsNullOrWhiteSpace(arguments[1]))
            return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: system migrate <Ziel> --yes | system cancel");
        if (!isAdministrator()) return ResultErr.CreateFailure(ResultCode.Failure, "Migration erfordert Administratorrechte.");
        if (!isLegacy()) return ResultErr.CreateFailure(ResultCode.Failure, "Migration ist nur im Legacy-Profil moeglich.");
        int current = getSessionGeneration?.Invoke() ?? 0;
        lock (gate)
        {
            if (generation != current) messages.Clear();
            generation = current;
        }
        return job.Start("system migrate", output, cancellation =>
        {
            var result = service.Run(arguments[1], text => Publish(text, current, cancellation), cancellation);
            return result.IsOK() ? $"Migration abgeschlossen: {result.Value!.Files} Dateien; Quellinventar {result.Value.SourceHash}." : result.Status.Message;
        });
    }

    private void Publish(string text, int current, CancellationToken cancellation)
    {
        log?.Invoke(text);
        while (!cancellation.IsCancellationRequested)
        {
            lock (gate)
            {
                if (closed || current != (getSessionGeneration?.Invoke() ?? 0)) return;
                if (messages.Count < PendingLines) { messages.Enqueue(text); return; }
            }
            // Begrenzter Puffer: die Ereignisschleife kann alle Meldungen anzeigen, ohne dass ein grosses Inventar den Heap fuellt.
            Thread.Sleep(1);
        }
    }

    public string? TakeResult()
    {
        lock (gate)
        {
            if (generation != (getSessionGeneration?.Invoke() ?? 0)) messages.Clear();
            else if (messages.Count != 0) return messages.Dequeue();
        }
        return job.TakeResult();
    }

    public bool Interrupt(ITextOutput output) => job.Cancel(output).IsOK();

    public void Close()
    {
        lock (gate) { closed = true; messages.Clear(); }
        job.Close();
    }
}
