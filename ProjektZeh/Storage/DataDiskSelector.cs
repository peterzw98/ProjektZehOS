using System;
using System.Collections.Generic;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

internal sealed record DataDiskPartition(byte Type, string? Label);

internal sealed record DataDiskCandidate(string Name, ulong BlockSize, ulong BlockCount, IReadOnlyList<DataDiskPartition> Partitions);

internal enum DataDiskAction
{
    /// <summary>Bestand auf sata0: diese Partition unter /data einbinden.</summary>
    Mount,
    /// <summary>Keine Datenplatte erkannt: Boot wie bisher ohne /data.</summary>
    NoDataDisk,
    /// <summary>Eine ZEHOSDATA-Platte ist da, wird aber nie automatisch eingebunden (disk use).</summary>
    LabelledAvailable,
    /// <summary>Erkannt, aber nicht sicher einbindbar; es wird nichts eingebunden und keine Ersteinrichtung angeboten.</summary>
    Blocked,
}

/// <param name="Hint">Zusatzhinweis fuers Protokoll, etwa eine nicht bevorzugte ZEHOSDATA-Platte neben dem Bestand.</param>
internal sealed record DataDiskDecision(DataDiskAction Action, string? DeviceName, int PartitionIndex, string Message, string? Hint = null);

/// <summary>Reine Auswahl der Datenpartition und Groessenpruefung fuer disk init, ohne Cosmos-Abhaengigkeit.</summary>
internal static class DataDiskSelector
{
    internal const string DataLabel = "ZEHOSDATA";
    internal const byte FatLbaType = 0x0C;
    internal const string ExistingDevice = "sata0";
    internal const ulong SectorSize = 512;
    internal const ulong MinimumDiskSize = 64UL * 1024 * 1024;
    internal const ulong MaximumDiskSize = 128UL * 1024 * 1024 * 1024;

    internal static bool IsDataPartition(DataDiskPartition partition) =>
        partition.Type == FatLbaType && string.Equals(partition.Label?.TrimEnd(), DataLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Bestand gewinnt: traegt sata0 irgendeine Partition, wird nur sie in Betracht gezogen (genau eine, sonst Blocked) und
    /// keine ZEHOSDATA-Platte bevorzugt. Ohne Bestand wird eine ZEHOSDATA-Platte nur angeboten, nie automatisch eingebunden;
    /// zwei oder mehr sind mehrdeutig.
    /// </summary>
    public static DataDiskDecision Decide(IReadOnlyList<DataDiskCandidate> devices)
    {
        DataDiskCandidate? existing = null;
        var labelled = new List<(string Device, int Index)>();
        foreach (var device in devices)
        {
            if (string.Equals(device.Name, ExistingDevice, StringComparison.OrdinalIgnoreCase)) existing = device;
            for (int index = 0; index < device.Partitions.Count; index++)
                if (IsDataPartition(device.Partitions[index])) labelled.Add((device.Name, index));
        }

        if (existing != null && existing.Partitions.Count > 0)
        {
            if (existing.Partitions.Count != 1)
                return new(DataDiskAction.Blocked, existing.Name, -1, Cause("UnexpectedPartitionCount", "Zum Einbinden wird genau eine Datenpartition erwartet."));
            // sata0 selbst kann ZEHOSDATA tragen; ein Hinweis ist nur fuer eine andere Platte noetig.
            var other = labelled.Find(entry => !string.Equals(entry.Device, existing.Name, StringComparison.OrdinalIgnoreCase));
            string? hint = other.Device == null ? null
                : $"Datenplatte ZEHOSDATA auf {other.Device} vorhanden; der Bestand auf {existing.Name} hat Vorrang (Umschalten: disk use {other.Device} --yes).";
            return new(DataDiskAction.Mount, existing.Name, 0, string.Empty, hint);
        }

        if (labelled.Count > 1)
            return new(DataDiskAction.Blocked, null, -1, Cause("AmbiguousDataDisk", "Mehr als eine Datenpartition ZEHOSDATA gefunden; es wird nichts eingebunden."));
        if (labelled.Count == 1)
            return new(DataDiskAction.LabelledAvailable, labelled[0].Device, labelled[0].Index,
                Cause("DataDiskNotMounted", $"Datenplatte ZEHOSDATA auf {labelled[0].Device} gefunden, aber nicht eingebunden; zum Einbinden: disk use {labelled[0].Device} --yes."));
        return new(DataDiskAction.NoDataDisk, null, -1, Cause("DeviceNotFound", "Keine Datenplatte gefunden."));
    }

    /// <summary>Index der einzigen ZEHOSDATA-Partition eines Laufwerks fuer <c>disk use</c>.</summary>
    public static ResultObject<int> FindLabelled(DataDiskCandidate device)
    {
        int found = -1;
        for (int index = 0; index < device.Partitions.Count; index++)
        {
            if (!IsDataPartition(device.Partitions[index])) continue;
            if (found >= 0) return ResultObjectFactory.CreateFailure<int>(ResultCode.Failure, Cause("AmbiguousDataDisk", "Mehr als eine Datenpartition ZEHOSDATA auf diesem Laufwerk."));
            found = index;
        }
        return found >= 0 ? ResultObjectFactory.CreateSuccess(found)
            : ResultObjectFactory.CreateFailure<int>(ResultCode.NotFound, Cause("NoDataPartition", "Das Laufwerk hat keine Partition ZEHOSDATA."));
    }

    /// <summary>
    /// disk init braucht eine ausdrueckliche Bestaetigung, sobald schon /data eingebunden ist oder sata0 eine Partition traegt
    /// (Bestand erkannt, im Zweifel Bestand).
    /// </summary>
    public static ResultErr CheckOverwrite(IReadOnlyList<DataDiskCandidate> devices, bool dataMounted, bool confirmed)
    {
        bool existing = dataMounted;
        foreach (var device in devices)
            if (string.Equals(device.Name, ExistingDevice, StringComparison.OrdinalIgnoreCase) && device.Partitions.Count > 0) existing = true;
        if (!existing || confirmed) return ResultErr.CreateSuccess();
        return ResultErr.CreateFailure(ResultCode.Failure,
            Cause("OverwriteNeedsConfirmation", "Es ist schon /data eingebunden oder sata0 traegt eine Partition; zur Bestaetigung 'disk init <Name> --yes-overwrite' verwenden."));
    }

    /// <summary>512-Byte-Sektoren und 64 MiB bis 128 GiB; die Division verhindert einen Ueberlauf von BlockCount * BlockSize.</summary>
    public static ResultErr CheckInitSize(ulong blockSize, ulong blockCount)
    {
        if (blockSize != SectorSize)
            return ResultErr.CreateFailure(ResultCode.Failure, "cause='UnsupportedSectorSize' message='Die Platte benoetigt 512-Byte-Sektoren.'");
        if (blockCount < MinimumDiskSize / SectorSize || blockCount > MaximumDiskSize / SectorSize)
            return ResultErr.CreateFailure(ResultCode.Failure, "cause='UnsupportedDiskSize' message='Die Platte muss zwischen 64 MiB und 128 GiB gross sein.'");
        return ResultErr.CreateSuccess();
    }

    private static string Cause(string cause, string message) => $"cause='{cause}' message='{message}'";
}
