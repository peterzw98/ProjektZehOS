using System;
using System.Security.Cryptography;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Partitioning;

/// <summary>Schreibt ganze Sektoren ab <paramref name="block"/>.</summary>
internal delegate void BlockWriter(ulong block, ReadOnlySpan<byte> data);

/// <summary>
/// Schmaler GPT-Writer fuer das 7a-Layout (ESP 512 MiB + Linux-Root bis Plattenende) mit CRC32 und Backup; ersetzt
/// Gpt.Create/AddPartition aus Cosmos 3.0.97 (CRC 0, kein Backup, GUIDs aus der Plattengroesse). Alle Pruefungen und
/// die Zufallsbytes liegen vor dem ersten Schreibzugriff. Noch nirgends eingehaengt (7a Schritt 1).
/// </summary>
internal static class GptLayoutWriter
{
    public const string EspName = "ZEHOS-BOOT";
    public const string RootName = "ZEHOS-ROOT";
    public const ulong AlignmentSectors = 2048;
    public const ulong EspSectors = 512UL * 1024 * 1024 / GptLayout.SectorSize;
    /// <summary>Root endet auf einer 4-KiB-Grenze, damit der ext2-Formatter keinen angebrochenen Block sieht.</summary>
    public const ulong RootGranuleSectors = 4096 / GptLayout.SectorSize;
    private const ulong WipeSectors = AlignmentSectors;

    /// <summary>Plant die Tabelle ohne Plattenzugriff: Geometrie, Zufalls-GUIDs (Version 4) und Partitionsgrenzen.</summary>
    public static ResultObject<GptTable> Plan(ulong blockSize, ulong blockCount, Action<byte[]> random)
    {
        if (blockSize != GptLayout.SectorSize)
            return GptLayout.Failure("UnsupportedSectorSize", "Nur 512-Byte-Sektoren.");
        if (blockCount < GptLayout.MinBlockCount || blockCount > GptLayout.MaxBlockCount)
            return GptLayout.Failure("UnsupportedDiskSize", "Das GPT-Layout gilt nur fuer 32 bis 128 GiB.");

        var bytes = new byte[48];
        try
        {
            random(bytes);
        }
        catch (CryptographicException exception)
        {
            // Kein Ersatz aus Plattengroesse oder Zeit: gleiche GUIDs auf zwei Platten waeren nicht unterscheidbar.
            return GptLayout.Failure("RandomUnavailable", exception.Message);
        }

        Guid disk = Version4(bytes.AsSpan(0, 16)), esp = Version4(bytes.AsSpan(16, 16)), root = Version4(bytes.AsSpan(32, 16));
        if (disk == esp || disk == root || esp == root)
            return GptLayout.Failure("RandomRejected", "Die Zufallsquelle lieferte gleiche GUIDs.");

        ulong espFirst = AlignmentSectors, espLast = espFirst + EspSectors - 1;
        ulong rootFirst = espLast + 1;
        ulong rootSectors = (GptLayout.LastUsableLba(blockCount) - rootFirst + 1) / RootGranuleSectors * RootGranuleSectors;
        return ResultObjectFactory.CreateSuccess(new GptTable(disk,
        [
            new GptEntry(GptLayout.EspType, esp, espFirst, espLast, EspName),
            new GptEntry(GptLayout.LinuxFileSystemType, root, rootFirst, rootFirst + rootSectors - 1, RootName),
        ]));
    }

    /// <summary>
    /// Schreibt in fester Reihenfolge, jeweils mit Flush: (1) LBA 0..2047 und die letzten 33 Sektoren nullen (alter MBR, alte Koepfe),
    /// (2) Backup-Eintraege und Backupkopf, (3) Primaer-Eintraege, (4) Primaerkopf, (5) Schutz-MBR. Bricht ein Schritt ab,
    /// ist die Platte nie gueltig und zeigt nie mehr die alte MBR-Partition; die Ausnahme geht an den Aufrufer.
    /// RescanPartitions und Verify danach sind Sache des Aufrufers.
    /// </summary>
    public static ResultObject<GptTable> Write(ulong blockSize, ulong blockCount, Action<byte[]> random, BlockWriter write, Action flush)
    {
        var plan = Plan(blockSize, blockCount, random);
        if (!plan.IsOK()) return plan;
        GptTable table = plan.Value!;

        byte[] entries = GptLayout.BuildEntries(table.Entries);
        uint entriesCrc = Crc32.Compute(entries);
        ulong backupHeader = GptLayout.BackupHeaderLba(blockCount), backupEntries = GptLayout.BackupEntriesLba(blockCount);

        // Auch das Ende nullen: ein alter gueltiger Backupkopf liesse Firmware/Linux nach einem Abbruch die alte Tabelle herstellen.
        write(0, new byte[WipeSectors * GptLayout.SectorSize]);
        write(backupEntries, new byte[(GptLayout.EntrySectors + 1) * GptLayout.SectorSize]);
        flush();
        write(backupEntries, entries);
        write(backupHeader, GptLayout.BuildHeader(blockCount, table.DiskGuid, backupHeader, GptLayout.PrimaryHeaderLba, backupEntries, entriesCrc));
        flush();
        write(GptLayout.PrimaryEntriesLba, entries);
        flush();
        write(GptLayout.PrimaryHeaderLba, GptLayout.BuildHeader(blockCount, table.DiskGuid, GptLayout.PrimaryHeaderLba, backupHeader, GptLayout.PrimaryEntriesLba, entriesCrc));
        flush();
        write(0, GptLayout.BuildProtectiveMbr(blockCount));
        flush();
        return plan;
    }

    /// <summary>Setzt Version 4 und RFC-4122-Variante im Guid-Bytelayout (gemischtes Endian, wie GPT es speichert).</summary>
    private static Guid Version4(Span<byte> bytes)
    {
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}
