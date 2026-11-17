using System;
using System.Collections.Generic;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

internal sealed record DataDiskPartition(byte Type, string? Label);

internal sealed record DataDiskCandidate(string Name, ulong BlockSize, ulong BlockCount, IReadOnlyList<DataDiskPartition> Partitions);

/// <summary>Gewaehlte Datenpartition; <paramref name="FromFallback"/> bei der Wahl ueber den Namen sata0 statt ueber das Label.</summary>
internal sealed record DataDiskSelection(string DeviceName, int PartitionIndex, bool FromFallback);

/// <summary>Reine Auswahl der Datenpartition und Groessenpruefung fuer disk init, ohne Cosmos-Abhaengigkeit.</summary>
internal static class DataDiskSelector
{
    internal const string DataLabel = "ZEHOSDATA";
    internal const byte FatLbaType = 0x0C;
    internal const string FallbackDevice = "sata0";
    internal const ulong SectorSize = 512;
    internal const ulong MinimumDiskSize = 64UL * 1024 * 1024;
    internal const ulong MaximumDiskSize = 128UL * 1024 * 1024 * 1024;

    /// <summary>Genau eine Partition Typ 0x0C mit Label ZEHOSDATA; mehrere Treffer sind ein Fehler, kein Treffer faellt auf sata0 mit genau einer Partition zurueck.</summary>
    public static ResultObject<DataDiskSelection> Select(IReadOnlyList<DataDiskCandidate> devices)
    {
        DataDiskSelection? found = null;
        foreach (var device in devices)
        {
            for (int index = 0; index < device.Partitions.Count; index++)
            {
                if (!IsDataPartition(device.Partitions[index])) continue;
                if (found != null) return Fail(ResultCode.Failure, "AmbiguousDataDisk", "Mehr als eine Datenpartition ZEHOSDATA gefunden; es wird nichts eingebunden.");
                found = new(device.Name, index, false);
            }
        }
        if (found != null) return ResultObjectFactory.CreateSuccess(found);

        foreach (var device in devices)
        {
            if (!string.Equals(device.Name, FallbackDevice, StringComparison.OrdinalIgnoreCase)) continue;
            return device.Partitions.Count == 1
                ? ResultObjectFactory.CreateSuccess(new DataDiskSelection(device.Name, 0, true))
                : Fail(ResultCode.Failure, "UnexpectedPartitionCount", "Zum Einbinden wird genau eine Datenpartition erwartet.");
        }
        return Fail(ResultCode.NotFound, "DeviceNotFound", "Keine Datenplatte gefunden.");
    }

    internal static bool IsDataPartition(DataDiskPartition partition) =>
        partition.Type == FatLbaType && string.Equals(partition.Label?.TrimEnd(), DataLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>512-Byte-Sektoren und 64 MiB bis 128 GiB; die Division verhindert einen Ueberlauf von BlockCount * BlockSize.</summary>
    public static ResultErr CheckInitSize(ulong blockSize, ulong blockCount)
    {
        if (blockSize != SectorSize)
            return ResultErr.CreateFailure(ResultCode.Failure, "cause='UnsupportedSectorSize' message='Die Platte benoetigt 512-Byte-Sektoren.'");
        if (blockCount < MinimumDiskSize / SectorSize || blockCount > MaximumDiskSize / SectorSize)
            return ResultErr.CreateFailure(ResultCode.Failure, "cause='UnsupportedDiskSize' message='Die Platte muss zwischen 64 MiB und 128 GiB gross sein.'");
        return ResultErr.CreateSuccess();
    }

    private static ResultObject<DataDiskSelection> Fail(ResultCode code, string cause, string message) =>
        ResultObjectFactory.CreateFailure<DataDiskSelection>(code, $"cause='{cause}' message='{message}'");
}
