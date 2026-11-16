using System;
using System.Threading;
using ProjektZeh.Network;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

/// <summary>Haelt Ergebnis und Abbruch einer Netzoperation je Shellsitzung getrennt.</summary>
internal sealed class NetworkShellCommands(INetworkService network, BackgroundTaskService tasks, Func<bool> isAdministrator,
    Action<string>? log = null)
{
    private readonly ShellJob job = new(tasks, "net", "Netzwerkauftrag", log);
    public string? TakeResult() => job.TakeResult();

    public ShellCommand[] Commands =>
    [
        new("netinfo", "netinfo", "Zeigt Netzwerkadapter und IPv4-Konfiguration.", true, (a, o) => Start("info", a, o)),
        new("net", "net dhcp|static <IP> <Maske> <Gateway> <DNS>|cancel", "Konfiguriert IPv4 oder bricht den Auftrag ab.", true, Net),
        new("ping", "ping <IPv4>", "Prueft die Erreichbarkeit einer IPv4-Adresse.", true, (a, o) => Start("ping", a, o)),
        new("nslookup", "nslookup <Name>", "Loest einen Namen ueber DNS auf.", true, (a, o) => Start("dns", a, o)),
        new("fetch", "fetch <http[s]://Host[:Port]/Pfad>", "Laedt eine begrenzte HTTP- oder HTTPS-Antwort.", true, (a, o) => Start("fetch", a, o))
    ];

    private ResultErr Net(string[] arguments, ITextOutput output)
    {
        if (arguments.Length == 1 && arguments[0] == "cancel")
            return job.Cancel(output);
        if (arguments.Length >= 1 && arguments[0] is "dhcp" or "static")
        {
            if (!isAdministrator()) return ResultErr.CreateFailure(ResultCode.Failure, "Netzwerkkonfiguration erfordert Administratorrechte.");
            string[] parameters = new string[arguments.Length - 1];
            Array.Copy(arguments, 1, parameters, 0, parameters.Length);
            return Start(arguments[0], parameters, output);
        }
        return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: net dhcp|static <IP> <Maske> <Gateway> <DNS>|cancel");
    }

    private ResultErr Start(string operation, string[] arguments, ITextOutput output)
        => job.Start("net " + operation, output, token => Run(operation, arguments, token));

    private string Run(string operation, string[] arguments, CancellationToken cancellation)
    {
        var response = network.Execute(operation, arguments, cancellation);
        if (cancellation.IsCancellationRequested) return "Netzwerkauftrag abgebrochen.";
        return response.IsOK() ? response.Value ?? "Abgeschlossen." : response.Status.Message;
    }

    public void Close() => job.Close();
}
