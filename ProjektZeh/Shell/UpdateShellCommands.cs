using System;
using ProjektZeh.Shared;
using ProjektZeh.Update;

namespace ProjektZeh.Shell;

internal sealed class UpdateShellCommands(UpdateController updates, Func<bool> isAdministrator)
{
    public ShellCommand[] Commands => [new("update", "update status|source <host[:port]>|check|install [Datei]",
        "Prueft signierte Kernelupdates und behaelt die vorherige Version.", true, Execute)];

    private ResultErr Execute(string[] arguments, ITextOutput output)
    {
        if (!isAdministrator()) return ResultErr.CreateFailure(ResultCode.Failure, "Updates erfordern Administratorrechte.");
        if (arguments.Length == 1 && arguments[0] == "status")
        {
            output.WriteLine("Laufender Kernel: " + updates.CurrentVersion);
            var source = updates.GetSource();
            if (!source.IsOK()) return source.Status;
            output.WriteLine("Updatequelle: " + source.Value);
            return ResultErr.CreateSuccess();
        }
        if (arguments.Length == 2 && arguments[0] == "source") return updates.SetSource(arguments[1]);
        if (arguments.Length == 1 && arguments[0] == "check")
        {
            var check = updates.Check();
            if (!check.IsOK()) return check.Status;
            output.WriteLine($"Geprueftes Update: {check.Value!.Version} ({check.Value.Size} Bytes).");
            return ResultErr.CreateSuccess();
        }
        if (arguments.Length is 1 or 2 && arguments[0] == "install")
        {
            var installed = arguments.Length == 2 ? updates.InstallOffline(arguments[1]) : updates.Install();
            if (installed.IsOK()) output.WriteLine("Kernel installiert. Bitte neu starten; die vorherige Version bleibt im Bootmenue.");
            return installed;
        }
        return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: update status|source <host[:port]>|check|install [Datei]");
    }
}
