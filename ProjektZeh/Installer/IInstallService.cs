using ProjektZeh.Shared;

namespace ProjektZeh.Installer;

/// <summary>Zielplatte fuer die Installation; das Startmedium wird nie als Ziel angeboten.</summary>
internal sealed record InstallDisk(string Name, ulong SizeBytes, int PartitionCount);

internal enum InstallStage
{
    Partitioning,
    Formatting,
    CopyingFiles,
    WritingBootConfig,
    Completed
}

/// <summary>Laufende Installation; jeder Step erledigt nur einen kurzen Abschnitt, damit die Oberflaeche weiterzeichnet.</summary>
internal interface IInstallJob
{
    InstallStage Stage { get; }
    int Percent { get; }
    bool IsCompleted { get; }
    ResultErr Step();
}

internal interface IInstallService
{
    string ProductVersion { get; }
    ResultObject<InstallDisk[]> GetDisks();
    /// <summary>Platten mit Partitionen werden nur mit eraseExisting=true angenommen.</summary>
    ResultObject<IInstallJob> Begin(string diskName, bool eraseExisting);
    ResultErr Restart();
}
