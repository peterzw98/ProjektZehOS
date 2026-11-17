using System;
using System.Collections.Generic;
using System.Text;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.Storage;

namespace ProjektZeh.Storage;

/// <summary>Liest Partitionstyp und FAT32-Volumelabel aus Cosmos fuer den reinen <see cref="DataDiskSelector"/>.</summary>
internal static class CosmosDataDisk
{
    // Offsets im FAT32-Bootsektor (FatBootSector.Fat32VolLabOffset / Fat32FilSysTypeOffset sind in Cosmos intern).
    private const int Fat32LabelOffset = 71;
    private const int Fat32LabelLength = 11;
    private const int Fat32TypeOffset = 82;

    public static DataDiskCandidate Describe(IBlockDevice device)
    {
        // Der Typ steht nur in der MBR-Tabelle; Partition kennt ihn nicht, daher Zuordnung ueber den Startsektor.
        var found = StorageManager.GetPartitions(device);
        if (found.Count == 0) return new(device.Name, device.BlockSize, device.BlockCount, []);
        var entries = Mbr.IsMbr(device) ? Mbr.Parse(device) : new List<MbrPartitionEntry>();
        // GPT-Typen fuer das 7a-Layout; Cosmos nummeriert die Partitionen in Parse-Reihenfolge, zugeordnet wird trotzdem ueber den Startsektor.
        var gptEntries = Gpt.IsGpt(device) ? Gpt.Parse(device) : new List<GptPartitionEntry>();
        var partitions = new List<DataDiskPartition>();
        foreach (Partition partition in found)
        {
            byte type = 0;
            foreach (var entry in entries)
                if (entry.StartSector == partition.StartSector) type = entry.SystemId;
            Guid gptType = Guid.Empty;
            foreach (var entry in gptEntries)
                if (entry.StartSector == partition.StartSector) gptType = entry.PartitionType;
            // Basic Data mitlesen, damit der Installer auch eine GPT-ZEHOSDATA erkennt (IsInstallExcluded).
            bool fat = type == DataDiskSelector.FatLbaType || gptType == Partitioning.GptLayout.EspType || gptType == Partitioning.GptLayout.BasicDataType;
            partitions.Add(new(type, fat ? ReadFat32Label(partition) : null, gptType));
        }
        return new(device.Name, device.BlockSize, device.BlockCount, partitions);
    }

    /// <summary>Installer-Ausschluss: ZEHOSDATA mit MBR oder GPT (<see cref="DataDiskSelector.IsInstallExcluded"/>).</summary>
    public static bool HasDataPartition(IBlockDevice device)
    {
        foreach (var partition in Describe(device).Partitions)
            if (DataDiskSelector.IsInstallExcluded(partition)) return true;
        return false;
    }

    private static string? ReadFat32Label(Partition partition)
    {
        if (partition.BlockSize < 512 || partition.BlockCount == 0) return null;
        var sector = new byte[partition.BlockSize];
        partition.ReadBlock(0, 1, sector);
        if (sector[510] != 0x55 || sector[511] != 0xAA
            || Encoding.ASCII.GetString(sector, Fat32TypeOffset, 5) != "FAT32") return null;
        return Encoding.ASCII.GetString(sector, Fat32LabelOffset, Fat32LabelLength);
    }
}
