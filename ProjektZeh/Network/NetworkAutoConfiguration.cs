using System;
using System.Threading;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Network;

internal sealed class NetworkAutoConfiguration(INetworkService network, BackgroundTaskService tasks, Action<string> log)
{
    public const int RetryMilliseconds = 10000;
    /// <summary>Standard-Zeitserver (freigegeben von Peter, 17.11.2026).</summary>
    public const string TimeServer = "pool.ntp.org";
    private const int TimeAttempts = 3;
    private readonly object gate = new();
    private bool enabled;
    private bool running;
    private int taskId;
    private string? failure;

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
        var started = tasks.Start("net auto", Work, OnFinished);
        if (!started.IsOK()) { running = false; return started.Status; }
        taskId = started.Value;
        return ResultErr.CreateSuccess();
    }

    private void Work(BackgroundTaskContext context)
    {
        string? error = null;
        try { Run(network, context, log); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { error = exception.Message; }
        lock (gate) failure = error;
    }

    // Erst nach dem Ende des alten Jobs: sonst zaehlt er beim Neustart noch gegen die Aufgabengrenze (#244).
    private void OnFinished()
    {
        string? error;
        ResultErr restarted = ResultErr.CreateSuccess();
        lock (gate)
        {
            running = false;
            error = failure;
            failure = null;
            // Ein fehlgeschlagener Shutdown kann Start anfordern, waehrend Cosmos noch im alten DHCP-Aufruf wartet.
            if (enabled && error == null) restarted = StartWorker();
        }
        if (error != null) log("Netzwerkautomatik fehlgeschlagen: " + error);
        if (!restarted.IsOK()) log("Netzwerkautomatik nicht gestartet: " + restarted.Message);
    }

    public static void Run(INetworkService network, BackgroundTaskContext context, Action<string> log,
        int retryMilliseconds = RetryMilliseconds)
    {
        string? previous = null;
        int timeAttempts = 0;
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
                status = network.Status;
            }
            // Etappe 12.2: nach der ersten Adresse einmal die Uhr abgleichen, bei Fehlern hoechstens dreimal je Lauf.
            if (status.Address != null && timeAttempts < TimeAttempts && !ProjektZeh.SystemServices.SystemClock.IsSynchronized)
            {
                timeAttempts++;
                var time = network.Execute("time", [TimeServer], context.Token);
                if (context.Token.IsCancellationRequested) return;
                log(time.IsOK() ? time.Value ?? "Uhr abgeglichen." : "Zeitabgleich fehlgeschlagen: " + time.Status.Message);
            }
            if (!context.Sleep(retryMilliseconds)) return;
        }
    }
}
