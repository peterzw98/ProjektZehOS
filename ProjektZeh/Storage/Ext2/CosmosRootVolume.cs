using System;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.Storage;

namespace ProjektZeh.Storage.Ext2;

/// <summary>
/// Duenner Cosmos-Adapter fuer <see cref="RootVolumeState"/>: Superblock direkt ueber das Host-Geraet (Sektoren 2-3 der
/// Partition), Sync ueber den eingehaengten Superblock. Die gesamte Logik und ihre Tests liegen in RootVolumeState.
/// </summary>
internal sealed class CosmosRootVolume(Partition partition, VfsMount mount) : IRootVolume
{
    private const ulong SuperblockSector = 1024 / 512;
    private const ulong SuperblockSectors = 1024 / 512;

    public bool Sync() => mount.Superblock.SuperOperations.Sync(mount.Superblock);

    public void ReadSuperblock(Span<byte> superblock)
    {
        if (partition.Host.BlockSize != 512) throw new InvalidOperationException("Root nur mit 512-Byte-Sektoren.");
        var buffer = new byte[SuperblockSectors * 512];
        partition.Host.ReadBlock(partition.StartSector + SuperblockSector, SuperblockSectors, buffer);
        buffer.CopyTo(superblock);
    }

    public void WriteSuperblock(ReadOnlySpan<byte> superblock)
    {
        if (partition.Host.BlockSize != 512) throw new InvalidOperationException("Root nur mit 512-Byte-Sektoren.");
        partition.Host.WriteBlock(partition.StartSector + SuperblockSector, SuperblockSectors, superblock.ToArray());
    }

    public void Flush() => partition.Host.Flush();
}
