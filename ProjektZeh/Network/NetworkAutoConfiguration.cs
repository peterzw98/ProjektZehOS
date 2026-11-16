using System;
using System.Threading;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Network;

internal sealed class NetworkAutoConfiguration(INetworkService network, BackgroundTaskService tasks, Action<string> log)
{
    public const int RetryMilliseconds = 10000;
    private readonly object gate = new();
    private bool enabled;
    private bool running;
    private int taskId;

    public ResultErr Start()
    {
        lock (gate) { enabled = true; return running ? ResultErr.CreateSuccess() : StartWorker(); }
    }

    public void Stop()
    {
        lock (gate) { enabled = false; if (running) tasks.Cancel(taskId); }
    }

    private ResultErr StartWorker()
    {
        running = true;
        var started = tasks.Start("net auto", Work);
        if (!started.IsOK()) { running = false; return started.Status; }
        taskId = started.Value;
        return ResultErr.CreateSuccess();
    }

    private void Work(BackgroundTaskContext context)
    {
        string? failure = null;
        try { Run(network, context, log); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { failure = exception.Message; }
        ResultErr restarted = ResultErr.CreateSuccess();
        lock (gate)
        {
            running = false;
            // Ein fehlgeschlagener Shutdown kann Start anfordern, waehrend Cosmos noch im alten DHCP-Aufruf wartet.
            if (enabled && failure == null) restarted = StartWorker();
        }
        if (failure != null) log("Netzwerkautomatik fehlgeschlagen: " + failure);
        if (!restarted.IsOK()) log("Netzwerkautomatik nicht gestartet: " + restarted.Message);
    }

    public static void Run(INetworkService network, BackgroundTaskContext context, Action<string> log,
        int retryMilliseconds = RetryMilliseconds)
    {
        string? previous = null;
        while (!context.Token.IsCancellationRequested)
        {
            var status = network.Status;
            if (status.Adapter && status.Link && status.Address == null)
            {
                // Der Dienst prueft nach dem Warten auf die Sperre erneut, damit net static Vorrang behaelt.
                var result = network.Execute("auto-dhcp", [], context.Token);
                if (context.Token.IsCancellationRequested) return;
                string text = result.IsOK() ? result.Value ?? "DHCP abgeschlossen." : result.Status.Message;
                if (!string.Equals(previous, text, StringComparison.Ordinal)) { log(text); previous = text; }
            }
            if (!context.Sleep(retryMilliseconds)) return;
        }
    }
}
