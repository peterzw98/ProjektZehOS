using ProjektZeh.Shared;

namespace ProjektZeh.Installer;

/// <summary>Zielplatte fuer die Installation; das Startmedium wird nie als Ziel angeboten.</summary>
/// <param name="IsBlank">Auch ohne erkannte Partitionen koennen Dateisysteme oder fremde Tabellen auf der Platte liegen.</param>
internal sealed record InstallDisk(string Name, ulong SizeBytes, int PartitionCount, bool IsBlank = true)
{
    public bool RequiresErase => PartitionCount > 0 || !IsBlank;
    public string ContentText => PartitionCount switch
    {
        0 when IsBlank => "keine Daten",
        0 => "unbekannte Daten",
        1 => "1 Partition",
        _ => $"{PartitionCount} Partitionen"
    };
}

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
