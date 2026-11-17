using System;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.Storage;

namespace ProjektZeh.Storage.Partitioning;

/// <summary>Zuordnung GPT-Eintrag -> Cosmos-Partition; gemeinsam fuer Profilwahl (4.3) und Migration (4.5).</summary>
internal static class CosmosGptPartitions
{
    /// <summary>Cosmos-Partition zum GPT-Eintrag des Typs (und der Partitions-GUID); Zuordnung ueber den Startsektor.</summary>
    public static Partition? Find(IBlockDevice device, GptTable table, Guid type, Guid? unique)
    {
        foreach (var entry in table.Entries)
        {
            if (entry.Type != type || (unique is { } wanted && entry.Unique != wanted)) continue;
            foreach (Partition partition in StorageManager.GetPartitions(device))
                if (partition.StartSector == entry.FirstLba) return partition;
        }

        return null;
    }
}
