using System;
using System.Threading;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

/// <summary>Haelt Auftraege und Ergebnisse je Shell; nur die Ereignisschleife schreibt in die Anzeige.</summary>
internal sealed class ShellJob(BackgroundTaskService tasks, string command, string label, Action<string>? log = null)
{
    private readonly object gate = new();
    private bool running;
    private bool closed;
    private int taskId;
    private string? result;

    public ResultErr Start(string taskName, ITextOutput output, Func<CancellationToken, string> work)
    {
        lock (gate)
        {
            if (closed) return ResultErr.CreateFailure(ResultCode.Failure, "Sitzung geschlossen.");
            if (running || result != null) return ResultErr.CreateFailure(ResultCode.Failure, $"Ein {label} dieser Sitzung laeuft bereits.");
            running = true;
            var started = tasks.Start(taskName, context => Run(work, context.Token));
            if (!started.IsOK()) { running = false; return started.Status; }
            taskId = started.Value;
            output.WriteLine($"Laeuft ... Abbruch: {command} cancel");
            return ResultErr.CreateSuccess();
        }
    }

    public ResultErr Cancel(ITextOutput output)
    {
        lock (gate)
        {
            if (!running) return ResultErr.CreateFailure(ResultCode.Failure, $"Kein {label} laeuft.");
            var cancelled = tasks.Cancel(taskId);
            if (cancelled.IsOK()) output.WriteLine("Abbruch angefordert.");
            return cancelled;
        }
    }

    private void Run(Func<CancellationToken, string> work, CancellationToken token)
    {
        string text;
        try { text = work(token); }
        catch (OperationCanceledException) { text = label + " abgebrochen."; }
        catch (Exception exception) { text = label + " fehlgeschlagen: " + exception.Message; }
        lock (gate) { if (!closed) result = text; running = false; }
        log?.Invoke(text);
    }

    public string? TakeResult()
    {
        lock (gate) { var text = result; result = null; return text; }
    }

    public void Close()
    {
        lock (gate)
        {
            closed = true;
            result = null;
            if (running) tasks.Cancel(taskId);
        }
    }
}
