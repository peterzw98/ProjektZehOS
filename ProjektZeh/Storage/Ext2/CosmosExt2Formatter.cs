using System;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem.Ext2;

namespace ProjektZeh.Storage.Ext2;

/// <summary>
/// ZehOS-Profil für das Formatieren: nur 4096-Byte-Blöcke und höchstens 128 GiB, jede Plattenoperation unter der
/// <see cref="StorageLock"/>. Das Layout (sparse_super, Sicherungs-Superblöcke, Füllbits) schreibt der Formatter aus Cosmos
/// (Patch <c>ext2-formatter</c>, PATCHES.md im Branch cosmos); hier steht nur die ZehOS-Politik.
/// </summary>
internal static class CosmosExt2Formatter
{
    /// <summary>Größtes Gerät, das ZehOS formatiert (128 GiB, qualifizierter Zielbereich).</summary>
    private const ulong MaxVolumeBytes = 128UL * 1024 * 1024 * 1024;

    /// <summary>
    /// Formatiert <paramref name="device"/> mit 4096-Byte-Blöcken (null => 4096). Andere Blockgrößen, Sektorgröße 0 und
    /// Geräte über 128 GiB werden vor jedem Schreibzugriff abgelehnt: der Cosmos-Treiber adressiert 1-KiB-Volumes
    /// falsch (Ext2Superblock ohne FirstDataBlock) und 2K ist nicht qualifiziert.
    /// </summary>
    /// <param name="device">Block device to format.</param>
    /// <param name="options">Null oder Optionen mit Blockgröße 4096.</param>
    /// <returns>true when the volume was written.</returns>
    public static bool FormatForKernel(IBlockDevice device, Ext2FormatOptions? options)
    {
        if (options is not null && options.BlockSize != 4096)
        {
            return false;
        }

        ulong sectorSize = device.BlockSize;
        if (sectorSize == 0 || device.BlockCount > MaxVolumeBytes / sectorSize)
        {
            return false;
        }

        var profile = new Ext2FormatOptions { BlockSize = 4096, VolumeLabel = options?.VolumeLabel ?? "" };
        return new Ext2FileSystemType(new CosmosLockedBlockDevice(device)).TryFormat(ReadOnlySpan<char>.Empty, profile);
    }
}
