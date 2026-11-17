using System;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.Storage;

namespace ProjektZeh.Storage.Ext2;

/// <summary>
/// Duenner Cosmos-Adapter fuer <see cref="RootVolumeState"/>: Superblock direkt ueber das Host-Geraet (Sektoren 2-3 der
/// Partition), Sync ueber den eingehaengten Superblock. Die gesamte Logik und ihre Tests liegen in RootVolumeState.
/// </summary>
/// <param name="mount">Nur fuer Sync noetig; zum blossen Lesen des Superblocks null.</param>
internal sealed class CosmosRootVolume(Partition partition, VfsMount? mount) : IRootVolume
{
    private const ulong SuperblockSector = 1024 / 512;
    private const ulong SuperblockSectors = 1024 / 512;

    public bool Sync() => mount != null && mount.Superblock.SuperOperations.Sync(mount.Superblock);

    public void ReadSuperblock(Span<byte> superblock)
    {
        if (partition.Host.BlockSize != 512) throw new InvalidOperationException("Root nur mit 512-Byte-Sektoren.");
        partition.Host.ReadBlock(partition.StartSector + SuperblockSector, SuperblockSectors, superblock[..(int)(SuperblockSectors * 512)]);
    }

    public void WriteSuperblock(ReadOnlySpan<byte> superblock)
    {
        if (partition.Host.BlockSize != 512) throw new InvalidOperationException("Root nur mit 512-Byte-Sektoren.");
        // Nur der erste Sektor: s_state liegt bei Byte 58; ein kleineres Fenster fuer einen nebenlaeufigen Treiber-Rueckschreib.
        partition.Host.WriteBlock(partition.StartSector + SuperblockSector, 1, superblock[..512].ToArray());
    }

    public void Flush() => partition.Host.Flush();
}
