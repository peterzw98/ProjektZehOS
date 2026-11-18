using System.IO;
using Cosmos.Kernel.System.FileSystem;

namespace ProjektZeh.Storage;

/// <summary>Die Cosmos-3.0.97-Parsergrenze gilt nur auf beschreibbarem ext2, auch unter verschachtelten Mounts.</summary>
internal static class CosmosDirectoryEntryLimit
{
    public static int? ForDirectory(string directory) => StorageLock.Run(() => ForDirectoryStorageLocked(directory));

    private static int? ForDirectoryStorageLocked(string directory)
    {
        string? candidate = directory;
        while (candidate != null)
        {
            if (VfsManager.TryGetMount(candidate, out VfsMount? mount))
            {
                if (mount.IsReadOnly) return null;
                if (!mount.Superblock.SuperOperations.StatFs(mount.Superblock, out VfsStatFs stats))
                    throw new IOException("Dateisystemstatistik nicht lesbar: " + candidate);
                // Der innerste Mount entscheidet; ein FAT-Mount unter ext2 erbt dessen Grenze nicht.
                return stats.Type == 0xEF53 ? FileService.MaximumExt2DirectoryEntries : null;
            }
            candidate = Path.GetDirectoryName(candidate);
        }
        return null;
    }
}
