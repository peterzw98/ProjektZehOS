using System;
using System.Threading;
using ProjektZeh.Network;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

/// <summary>Haelt Ergebnis und Abbruch einer Netzoperation je Shellsitzung getrennt.</summary>
internal sealed class NetworkShellCommands(INetworkService network, BackgroundTaskService tasks, Func<bool> isAdministrator)
{
    private readonly object gate = new();
    private bool running;
    private int taskId;
    private string? result;

    public ShellCommand[] Commands =>
    [
        new("netinfo", "netinfo", "Zeigt Netzwerkadapter und IPv4-Konfiguration.", true, (a, o) => Start("info", a, o)),
        new("net", "net dhcp|static <IP> <Maske> <Gateway> <DNS>|status|cancel", "Konfiguriert IPv4 oder zeigt den Auftrag.", true, Net),
        new("ping", "ping <IPv4>", "Prueft die Erreichbarkeit einer IPv4-Adresse.", true, (a, o) => Start("ping", a, o)),
        new("nslookup", "nslookup <Name>", "Loest einen Namen ueber DNS auf.", true, (a, o) => Start("dns", a, o)),
        new("fetch", "fetch <http[s]://Host[:Port]/Pfad>", "Laedt eine begrenzte HTTP- oder HTTPS-Antwort.", true, (a, o) => Start("fetch", a, o))
    ];

    private ResultErr Net(string[] arguments, ITextOutput output)
    {
        if (arguments.Length == 1 && arguments[0] == "status")
        {
            lock (gate) output.WriteLine(running ? "Netzwerkauftrag laeuft." : result ?? "Kein Netzwerkauftrag.");
            return ResultErr.CreateSuccess();
        }
        if (arguments.Length == 1 && arguments[0] == "cancel")
        {
            lock (gate)
            {
                if (!running) return ResultErr.CreateFailure(ResultCode.Failure, "Kein Netzwerkauftrag laeuft.");
                var cancelled = tasks.Cancel(taskId);
                if (cancelled.IsOK()) output.WriteLine("Abbruch angefordert; Status mit net status pruefen.");
                return cancelled;
            }
        }
        if (arguments.Length >= 1 && arguments[0] is "dhcp" or "static")
        {
            if (!isAdministrator()) return ResultErr.CreateFailure(ResultCode.Failure, "Netzwerkkonfiguration erfordert Administratorrechte.");
            string[] parameters = new string[arguments.Length - 1];
            Array.Copy(arguments, 1, parameters, 0, parameters.Length);
            return Start(arguments[0], parameters, output);
        }
        return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: net dhcp|static <IP> <Maske> <Gateway> <DNS>|status|cancel");
    }

    private ResultErr Start(string operation, string[] arguments, ITextOutput output)
    {
        lock (gate)
        {
            if (running) return ResultErr.CreateFailure(ResultCode.Failure, "Ein Netzwerkauftrag dieser Sitzung laeuft bereits.");
            running = true;
            result = null;
            var started = tasks.Start("net " + operation, context => Run(operation, arguments, context.Token));
            if (!started.IsOK()) { running = false; return started.Status; }
            taskId = started.Value;
            output.WriteLine("Netzwerkauftrag gestartet; Ergebnis mit net status abrufen.");
            return ResultErr.CreateSuccess();
        }
    }

    private void Run(string operation, string[] arguments, CancellationToken cancellation)
    {
        string text;
        try
        {
            var response = network.Execute(operation, arguments, cancellation);
            text = response.IsOK() ? response.Value ?? "Abgeschlossen." : response.Status.Message;
            if (cancellation.IsCancellationRequested && response.IsOK()) text = "Netzwerkauftrag abgebrochen.";
        }
        catch (Exception exception) { text = "Netzwerkauftrag fehlgeschlagen: " + exception.Message; }
        lock (gate) { result = text; running = false; }
    }

    public void Close()
    {
        lock (gate) if (running) tasks.Cancel(taskId);
    }
}
