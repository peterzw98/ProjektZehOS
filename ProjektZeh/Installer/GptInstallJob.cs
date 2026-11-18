using System;
using System.Text;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Ext2;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Installer;

/// <summary>Plattenzugriffe des GPT-Installers; die Reihenfolge und alle Pruefungen dazwischen legt <see cref="GptInstallJob"/> fest.</summary>
internal interface IGptInstallTarget
{
    /// <summary>GptLayoutWriter mit der Tabelle aus Begin, RescanPartitions, Verify und genau ESP + Root an den geplanten Startsektoren.</summary>
    ResultErr WriteLayout(GptTable table);
    /// <summary>FAT32 ZEHOSBOOT auf Partition 1 und Einhaengen am Installationsmountpunkt.</summary>
    ResultErr FormatEsp();
    /// <summary>CosmosExt2Formatter.FormatForKernel auf Partition 2.</summary>
    ResultErr FormatRoot();
    /// <summary>Erste 1024 Byte des Root-Superblocks direkt vom Geraet.</summary>
    ResultObject<byte[]> ReadRootSuperblock();
    /// <summary>Root einhaengen, Verzeichnisse und Root-Marker anlegen, sauber aushaengen und flushen.</summary>
    ResultErr PrepareRoot(Guid diskGuid);
    ResultErr WriteFileChunk(string path, byte[] data, int offset, int count, bool first, bool last);
    ResultErr FlushEsp();
    /// <summary>ESP-Marker per Temp + Rename.</summary>
    ResultErr WriteMarker(string text);
    ResultObject<string> ReadMarker();
    /// <summary>ESP aushaengen und flushen.</summary>
    ResultErr Complete();
    /// <summary>Haengt aus, was eingehaengt ist (Root zuerst), und loescht nichts.</summary>
    ResultErr Abort();
}

/// <summary>
/// GPT-Installation (Spec 7a-S4, Abschnitt 3): ESP und ext2-Root, Root-Marker vor den Bootdateien, ESP-Marker als letzter
/// Schreibzugriff zwischen zwei Flushes. Jeder Abbruch davor hinterlaesst eine Platte ohne gueltigen ESP-Marker (Unclear).
/// </summary>
internal sealed class GptInstallJob : IInstallJob
{
    /// <summary>ESP 512 MiB minus 16 MiB Reserve fuer FAT-Verwaltung und Temp-Dateien.</summary>
    internal const long EspCapacity = (512L - 16) * 1024 * 1024;
    private const int ChunkSize = 64 * 1024;
    private const uint SparseSuper = 0x1;
    private const int RoCompatOffset = 100;
    private const int LogBlockSizeOffset = 24;
    private const uint LogBlockSize4096 = 2;
    private readonly IGptInstallTarget target;
    private readonly GptTable table;
    private readonly string diskName;
    private readonly string[] paths = InstallJob.Paths;
    private readonly byte[][] files;
    private readonly string marker;
    private int fileIndex;
    private int fileOffset;
    private ResultErr? failure;
    private bool completed;
    // Erreichte Meilensteine fuer die Abbruchmeldung (Spec 7a-S4, Abschnitt 4).
    private string state = "Partitionierung begonnen";

    public GptInstallJob(IGptInstallTarget target, GptTable table, string diskName, byte[] efi, byte[] kernel, string version)
    {
        this.target = target;
        this.table = table;
        this.diskName = diskName;
        files = InstallJob.Files(efi, kernel, version);
        GptEntry root = table.Entries[1];
        marker = new InstallMarker(table.DiskGuid, root.Unique, root.FirstLba, root.LastLba - root.FirstLba + 1).Serialize();
    }

    /// <summary>Platz aller Dateien auf der ESP: Kernel doppelt (.bak), EFI, Version, Limine-Konfiguration und Marker.</summary>
    public static long EspBytes(byte[] efi, byte[] kernel, string version)
    {
        long total = InstallJob.BootConfig.Length + InstallMarker.MaximumBytes;
        foreach (byte[] file in InstallJob.Files(efi, kernel, version)) total += file.Length;
        return total;
    }

    public InstallStage Stage { get; private set; } = InstallStage.Partitioning;
    public int Percent => Stage switch
    {
        InstallStage.Partitioning => 0,
        InstallStage.FormattingEsp => 5,
        InstallStage.FormattingRoot => 10,
        InstallStage.VerifyingRoot => 25,
        InstallStage.PreparingRoot => 27,
        InstallStage.CopyingFiles => 30 + (int)(60L * BytesCopied() / TotalBytes()),
        InstallStage.WritingBootConfig => 92,
        InstallStage.WritingMarker => 96,
        _ => 100
    };
    public bool IsCompleted => completed;

    public ResultErr Step()
    {
        if (failure != null) return failure;
        if (IsCompleted) return ResultErr.CreateSuccess();
        ResultErr result = Stage switch
        {
            InstallStage.Partitioning => Advance(target.WriteLayout(table), InstallStage.FormattingEsp, "GPT geschrieben"),
            InstallStage.FormattingEsp => Advance(target.FormatEsp(), InstallStage.FormattingRoot, "EFI-Partition formatiert"),
            InstallStage.FormattingRoot => Advance(target.FormatRoot(), InstallStage.VerifyingRoot, "Root formatiert"),
            InstallStage.VerifyingRoot => Advance(CheckSuperblock(requireClean: false), InstallStage.PreparingRoot, state),
            InstallStage.PreparingRoot => PrepareRoot(),
            InstallStage.CopyingFiles => CopyChunk(),
            InstallStage.WritingBootConfig => WriteBootConfig(),
            InstallStage.WritingMarker => WriteMarker(),
            _ => Complete(),
        };
        if (result.IsOK()) return result;
        if (Stage == InstallStage.Completed)
        {
            // Marker und Bootdateien stehen bereits geflusht auf der Platte; nur das Aushaengen scheiterte.
            failure = ResultErr.CreateFailure(ResultCode.Failure, "Installation geschrieben, Aushaengen fehlgeschlagen: " + result.Message);
            return failure;
        }
        var cleanup = target.Abort();
        string missing = Stage switch
        {
            InstallStage.WritingMarker => "Installationsmarker eventuell bereits geschrieben",
            InstallStage.CopyingFiles or InstallStage.WritingBootConfig => "Bootdateien unvollstaendig",
            _ => "Root noch nicht vorbereitet",
        };
        string message = $"{result.Message} Stand: {state}, {missing}. Der Zustand der Platte ist unklar; " +
            $"neu installieren mit setup install {diskName} --layout gpt --erase.";
        if (!cleanup.IsOK()) message += " Bereinigung fehlgeschlagen: " + cleanup.Message;
        failure = ResultErr.CreateFailure(ResultCode.Failure, message);
        return failure;
    }

    private ResultErr Advance(ResultErr result, InstallStage next, string reached)
    {
        if (!result.IsOK()) return result;
        Stage = next;
        state = reached;
        return result;
    }

    private ResultErr PrepareRoot()
    {
        var prepared = target.PrepareRoot(table.DiskGuid);
        if (!prepared.IsOK()) return prepared;
        // Zweites Lesen nach dem Aushaengen: der Treiber koennte einen gepufferten Superblock zurueckgeschrieben haben.
        return Advance(CheckSuperblock(requireClean: true), InstallStage.CopyingFiles, "Root vorbereitet");
    }

    private ResultErr CheckSuperblock(bool requireClean)
    {
        var read = target.ReadRootSuperblock();
        if (!read.IsOK()) return read.Status;
        byte[] superblock = read.Value!;
        if (requireClean && BitConverter.ToUInt16(superblock, Ext2FeatureGate.StateOffset) != 1)
            return Failed("Root nach dem Aushaengen nicht sauber (s_state != 1).");
        // Spec 7a-S4 §3 FormattingRoot: 4-KiB-Bloecke und sparse_super ausdruecklich, unabhaengig vom Gate.
        if (BitConverter.ToUInt32(superblock, LogBlockSizeOffset) != LogBlockSize4096) return Failed("Root nicht mit 4096-Byte-Bloecken formatiert (log_block_size != 2).");
        if (InstalledIdentity.CheckRoot(superblock) != RootCheck.Ok) return Failed("Root-Superblock nach dem Formatieren nicht im unterstuetzten Profil.");
        if ((BitConverter.ToUInt32(superblock, RoCompatOffset) & SparseSuper) == 0) return Failed("Root ohne sparse_super formatiert.");
        return ResultErr.CreateSuccess();
    }

    private ResultErr CopyChunk()
    {
        byte[] file = files[fileIndex];
        int count = Math.Min(ChunkSize, file.Length - fileOffset);
        var result = target.WriteFileChunk(paths[fileIndex], file, fileOffset, count, fileOffset == 0, fileOffset + count == file.Length);
        if (!result.IsOK()) return result;
        fileOffset += count;
        if (fileOffset < file.Length) return result;
        fileIndex++;
        fileOffset = 0;
        if (fileIndex == files.Length) Stage = InstallStage.WritingBootConfig;
        return result;
    }

    private ResultErr WriteBootConfig()
    {
        var result = target.WriteFileChunk("/boot/limine/limine.conf", InstallJob.BootConfig, 0, InstallJob.BootConfig.Length, true, true);
        // Dauerhaftigkeitsschranke: der Marker darf nie vor den Bootdateien auf der Platte stehen.
        if (result.IsOK()) result = target.FlushEsp();
        return Advance(result, InstallStage.WritingMarker, "Bootdateien geschrieben");
    }

    private ResultErr WriteMarker()
    {
        var written = target.WriteMarker(marker);
        if (written.IsOK()) written = target.FlushEsp();
        if (!written.IsOK()) return written;
        var read = target.ReadMarker();
        if (!read.IsOK()) return read.Status;
        if (!string.Equals(read.Value, marker, StringComparison.Ordinal) || !InstallMarker.Parse(read.Value!).IsOK())
            return Failed("Installationsmarker nach dem Schreiben nicht gleich zurueckgelesen.");
        Stage = InstallStage.Completed;
        return written;
    }

    private ResultErr Complete()
    {
        var result = target.Complete();
        if (result.IsOK()) completed = true;
        return result;
    }

    private long TotalBytes() => (long)files[0].Length + files[1].Length + files[2].Length + files[3].Length;
    private long BytesCopied()
    {
        long total = fileOffset;
        for (int index = 0; index < fileIndex; index++) total += files[index].Length;
        return total;
    }

    private static ResultErr Failed(string message) => ResultErr.CreateFailure(ResultCode.Failure, message);
}
