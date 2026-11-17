using System;
using System.Threading;
using ProjektZeh.Installer;
using ProjektZeh.Shared;

namespace ProjektZeh.Shell;

/// <param name="gptEnabled">Freischaltung von --layout gpt (Spec 7a-S4); ohne sie sind Option und gpt-Spalte unsichtbar.</param>
internal sealed class SetupShellCommands(IInstallService installer, Func<bool> isAdministrator, bool gptEnabled = false)
{
    private string Usage => gptEnabled ? "setup list|install <Laufwerk> [--erase] [--layout gpt]" : "setup list|install <Laufwerk> [--erase]";

    public ShellCommand[] Commands => [new("setup", Usage,
        "Installiert ZehOS auf eine Zielplatte; --erase bestaetigt das Loeschen vorhandener Partitionen.", true, Execute)];

    private ResultErr Execute(string[] arguments, ITextOutput output)
    {
        if (!isAdministrator()) return ResultErr.CreateFailure(ResultCode.Failure, "Die Installation erfordert Administratorrechte.");
        if (arguments.Length == 1 && arguments[0] == "list")
        {
            var disks = installer.GetDisks();
            if (!disks.IsOK()) return disks.Status;
            foreach (var disk in disks.Value!)
            {
                string line = $"{disk.Name}: {disk.SizeBytes} Bytes, {disk.ContentText}";
                if (gptEnabled) line += disk.GptBlockedReason is { } reason ? $", gpt: nein ({reason})" : ", gpt: ja";
                output.WriteLine(line);
            }
            return ResultErr.CreateSuccess();
        }
        if (arguments.Length >= 2 && arguments[0] == "install" && TryParseOptions(arguments, out bool erase, out bool gpt))
        {
            if (gpt && !erase)
                return ResultErr.CreateFailure(ResultCode.Failure, "GPT-Installation loescht die Platte; --erase erforderlich.");
            var begin = gpt ? installer.BeginGpt(arguments[1], erase) : installer.Begin(arguments[1], erase);
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
        return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: " + Usage);
    }

    // Jede Option hoechstens einmal; --layout kennt nur gpt und nur mit Freischaltung.
    private bool TryParseOptions(string[] arguments, out bool erase, out bool gpt)
    {
        erase = gpt = false;
        for (int index = 2; index < arguments.Length; index++)
        {
            if (arguments[index] == "--erase" && !erase) erase = true;
            else if (arguments[index] == "--layout" && gptEnabled && !gpt && index + 1 < arguments.Length && arguments[index + 1] == "gpt")
            {
                gpt = true;
                index++;
            }
            else return false;
        }
        return true;
    }
}
