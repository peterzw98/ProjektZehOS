using System;
using System.Collections.Generic;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Installer;

internal sealed record InstallCandidate(InstallDisk Disk, bool IsInstallMedium);

internal interface IInstallPlatform
{
    ResultObject<InstallCandidate[]> GetCandidates();
    ResultObject<IInstallTarget> OpenTarget(string diskName);
    /// <summary>Prueft eingebundenes Ziel, Ausschluss und freie Mountpunkte; schreibt nichts.</summary>
    ResultObject<IGptInstallTarget> OpenGptTarget(string diskName) =>
        ResultObjectFactory.CreateFailure<IGptInstallTarget>(ResultCode.Failure, InstallService.GptNotEnabled);
    ResultErr Restart();
}

/// <param name="gptEnabled">Freischaltung des GPT-Wegs; nur Kernel.cs bildet -p:ZehOSGptInstaller darauf ab.</param>
/// <param name="random">Hardware-Zufallsquelle fuer die GPT-GUIDs; wird nur in BeginGpt aufgerufen.</param>
internal sealed class InstallService(IInstallPlatform platform, byte[] efi, byte[] kernel, string version,
    bool gptEnabled = false, Action<byte[]>? random = null) : IInstallService
{
    internal const ulong MinimumDiskSize = 128UL * 1024 * 1024;
    internal const string GptNotEnabled = "Das GPT-Layout ist in diesem Build nicht freigeschaltet.";
    public string ProductVersion => version;

    public ResultObject<InstallDisk[]> GetDisks()
    {
        var candidates = platform.GetCandidates();
        if (!candidates.IsOK()) return ResultObjectFactory.CreateFailure<InstallDisk[]>(candidates.Status);
        var disks = new List<InstallDisk>();
        foreach (InstallCandidate candidate in candidates.Value!)
            if (!candidate.IsInstallMedium) disks.Add(candidate.Disk);
        return ResultObjectFactory.CreateSuccess(disks.ToArray());
    }

    public ResultObject<IInstallJob> Begin(string diskName, bool eraseExisting)
    {
        var candidates = platform.GetCandidates();
        if (!candidates.IsOK()) return ResultObjectFactory.CreateFailure<IInstallJob>(candidates.Status);
        foreach (InstallCandidate candidate in candidates.Value!)
        {
            if (!string.Equals(candidate.Disk.Name, diskName, StringComparison.OrdinalIgnoreCase)) continue;
            if (candidate.IsInstallMedium) return Failure("Das Installationsmedium ist kein Installationsziel.");
            if (candidate.Disk.SizeBytes < MinimumDiskSize) return Failure("Das Laufwerk muss mindestens 128 MiB gross sein.");
            if (candidate.Disk.RequiresErase && !eraseExisting)
                return Failure("Das Laufwerk enthaelt Partitionen oder Daten; ausdrueckliche Loeschbestaetigung erforderlich.");
            if (efi.Length == 0 || kernel.Length == 0) return Failure("Die Installationsdateien fehlen.");
            var target = platform.OpenTarget(candidate.Disk.Name);
            if (!target.IsOK()) return ResultObjectFactory.CreateFailure<IInstallJob>(target.Status);
            return ResultObjectFactory.CreateSuccess<IInstallJob>(new InstallJob(target.Value!, efi, kernel, version));
        }
        return ResultObjectFactory.CreateFailure<IInstallJob>(ResultCode.NotFound, "Installationsziel nicht gefunden.");
    }

    public ResultObject<IInstallJob> BeginGpt(string diskName, bool eraseExisting)
    {
        if (!gptEnabled || random == null) return Failure(GptNotEnabled);
        var candidates = platform.GetCandidates();
        if (!candidates.IsOK()) return ResultObjectFactory.CreateFailure<IInstallJob>(candidates.Status);
        foreach (InstallCandidate candidate in candidates.Value!)
        {
            if (!string.Equals(candidate.Disk.Name, diskName, StringComparison.OrdinalIgnoreCase)) continue;
            // Alle Pruefungen vor dem ersten Schreibzugriff (Spec 7a-S4, Abschnitt 2).
            if (candidate.IsInstallMedium) return Failure("Das Installationsmedium ist kein Installationsziel.");
            if (candidate.Disk.GptBlockedReason is { } reason) return Failure(reason + ".");
            if (!eraseExisting) return Failure("GPT-Installation loescht die Platte; --erase erforderlich.");
            if (efi.Length == 0 || kernel.Length == 0) return Failure("Die Installationsdateien fehlen.");
            if (GptInstallJob.EspBytes(efi, kernel, version) > GptInstallJob.EspCapacity)
                return Failure("Die Installationsdateien passen nicht auf die EFI-Partition.");
            var plan = GptLayoutWriter.Plan(candidate.Disk.SectorSize, candidate.Disk.SizeBytes / candidate.Disk.SectorSize, random);
            if (!plan.IsOK()) return Failure("Keine brauchbare Hardware-Zufallsquelle (RDRAND/RDSEED/virtio-rng): " + plan.Status.Message);
            var target = platform.OpenGptTarget(candidate.Disk.Name);
            if (!target.IsOK()) return ResultObjectFactory.CreateFailure<IInstallJob>(target.Status);
            return ResultObjectFactory.CreateSuccess<IInstallJob>(new GptInstallJob(target.Value!, plan.Value!, candidate.Disk.Name, efi, kernel, version));
        }
        return ResultObjectFactory.CreateFailure<IInstallJob>(ResultCode.NotFound, "Installationsziel nicht gefunden.");
    }

    public ResultErr Restart() => platform.Restart();
    private static ResultObject<IInstallJob> Failure(string message) => ResultObjectFactory.CreateFailure<IInstallJob>(ResultCode.Failure, message);
}
