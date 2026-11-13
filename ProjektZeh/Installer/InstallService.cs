using System;
using System.Collections.Generic;
using ProjektZeh.Shared;

namespace ProjektZeh.Installer;

internal sealed record InstallCandidate(InstallDisk Disk, bool IsInstallMedium);

internal interface IInstallPlatform
{
    ResultObject<InstallCandidate[]> GetCandidates();
    ResultObject<IInstallTarget> OpenTarget(string diskName);
    ResultErr Restart();
}

internal sealed class InstallService(IInstallPlatform platform, byte[] efi, byte[] kernel, string version) : IInstallService
{
    internal const ulong MinimumDiskSize = 128UL * 1024 * 1024;
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
            if (candidate.Disk.PartitionCount != 0 && !eraseExisting)
                return Failure("Das Laufwerk enthaelt Partitionen; ausdrueckliche Loeschbestaetigung erforderlich.");
            if (efi.Length == 0 || kernel.Length == 0) return Failure("Die Installationsdateien fehlen.");
            var target = platform.OpenTarget(candidate.Disk.Name);
            if (!target.IsOK()) return ResultObjectFactory.CreateFailure<IInstallJob>(target.Status);
            return ResultObjectFactory.CreateSuccess<IInstallJob>(new InstallJob(target.Value!, efi, kernel, version));
        }
        return ResultObjectFactory.CreateFailure<IInstallJob>(ResultCode.NotFound, "Installationsziel nicht gefunden.");
    }

    public ResultErr Restart() => platform.Restart();
    private static ResultObject<IInstallJob> Failure(string message) => ResultObjectFactory.CreateFailure<IInstallJob>(ResultCode.Failure, message);
}
