using ProjektZeh.Shared;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Installer;

/// <summary>Zielplatte fuer die Installation; das Startmedium wird nie als Ziel angeboten.</summary>
/// <param name="IsBlank">Nur wahr, wenn die Platte vollstaendig als leer gelesen wurde; ohne erkannte Partitionen koennen trotzdem Daten vorhanden sein.</param>
internal sealed record InstallDisk(string Name, ulong SizeBytes, int PartitionCount, bool IsBlank, ulong SectorSize = GptLayout.SectorSize)
{
    /// <summary>Warum das GPT-Layout (Spec 7a-S4) fuer diese Platte nicht in Frage kommt, sonst null.</summary>
    public string? GptBlockedReason =>
        SectorSize != GptLayout.SectorSize ? "GPT-Layout nur mit 512-Byte-Sektoren"
        : SizeBytes / SectorSize is < GptLayout.MinBlockCount or > GptLayout.MaxBlockCount ? "GPT-Layout nur fuer 32 bis 128 GiB"
        : null;

    public bool RequiresErase => PartitionCount > 0 || !IsBlank;
    public string ContentText => PartitionCount switch
    {
        0 when IsBlank => "keine Daten",
        0 => "moegliche Daten",
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
    Completed,
    // Nur GPT-Weg (Spec 7a-S4, Abschnitt 3); die Legacy-Werte bleiben unveraendert.
    FormattingEsp,
    FormattingRoot,
    VerifyingRoot,
    PreparingRoot,
    WritingMarker
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
    /// <summary>GPT-Weg mit ESP und ext2-Root (Spec 7a-S4); ohne Freischaltung immer ein Fehler.</summary>
    ResultObject<IInstallJob> BeginGpt(string diskName, bool eraseExisting) =>
        ResultObjectFactory.CreateFailure<IInstallJob>(ResultCode.Failure, InstallService.GptNotEnabled);
    ResultErr Restart();
}
