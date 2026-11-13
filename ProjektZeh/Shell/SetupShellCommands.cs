using System;
using System.Threading;
using ProjektZeh.Installer;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

internal sealed class SetupShellCommands(IInstallService installer, Func<bool> isAdministrator)
{
    public ShellCommand[] Commands => [new("setup", "setup list|install <Laufwerk> [--erase]",
        "Installiert ZehOS auf eine Zielplatte; --erase bestaetigt das Loeschen vorhandener Partitionen.", true, Execute)];

    private ResultErr Execute(string[] arguments, ITextOutput output)
    {
        if (!isAdministrator()) return ResultErr.CreateFailure(ResultCode.Failure, "Die Installation erfordert Administratorrechte.");
        if (arguments.Length == 1 && arguments[0] == "list")
        {
            var disks = installer.GetDisks();
            if (!disks.IsOK()) return disks.Status;
            foreach (var disk in disks.Value!) output.WriteLine($"{disk.Name}: {disk.SizeBytes} Bytes, {disk.ContentText}");
            return ResultErr.CreateSuccess();
        }
        if (arguments.Length is 2 or 3 && arguments[0] == "install" && (arguments.Length == 2 || arguments[2] == "--erase"))
        {
            var begin = installer.Begin(arguments[1], arguments.Length == 3);
            if (!begin.IsOK()) return begin.Status;
            var job = begin.Value!;
            InstallStage? previous = null;
            while (!job.IsCompleted)
            {
                if (job.Stage != previous) { output.WriteLine($"Installation: {job.Stage} ({job.Percent}%)"); previous = job.Stage; }
                var step = job.Step();
                if (!step.IsOK()) return step;
                Thread.Sleep(1);
            }
            output.WriteLine($"ZehOS {installer.ProductVersion} installiert. Installationsmedium entfernen und neu starten.");
            return ResultErr.CreateSuccess();
        }
        return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: setup list|install <Laufwerk> [--erase]");
    }
}
