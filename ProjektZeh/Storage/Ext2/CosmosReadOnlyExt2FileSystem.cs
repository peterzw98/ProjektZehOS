using System;
using System.Diagnostics.CodeAnalysis;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.FileSystem.Ext2;

namespace ProjektZeh.Storage.Ext2;

/// <summary>Expliziter Lesemount; noch keine Boot-, Shell- oder /data-Anbindung.</summary>
internal sealed class CosmosReadOnlyExt2FileSystem(IBlockDevice device) : IVfsFileSystemType
{
    internal const string FileSystemName = "zehos-ext2-ro";
    internal const string MountPoint = "/mnt/ext2-ro";
    private bool registered;

    public bool TryMount([NotNullWhen(true)] out VfsMount? mount) => TryMount(MountPoint, out mount);

    /// <summary>Etappe 4.3: im Fall RootUnclean haengt der Kernel die Lesekomponente direkt auf / ein.</summary>
    public bool TryMount(string mountPoint, [NotNullWhen(true)] out VfsMount? mount)
    {
        VfsMount? mounted = null;
        bool success = StorageLock.Run(() => TryMountStorageLocked(mountPoint, out mounted));
        mount = mounted;
        return success;
    }

    private bool TryMountStorageLocked(string mountPoint, [NotNullWhen(true)] out VfsMount? mount)
    {
        mount = null;
        foreach (VfsMount existing in VfsManager.Mounts)
        {
            if (existing.MountPoint == mountPoint || ReferenceEquals(existing.Partition, device)) return false;
        }

        if (!registered)
        {
            if (!VfsManager.RegisterFileSystem(FileSystemName, this)) return false;
            registered = true;
        }

        return VfsManager.TryMount(FileSystemName, "", MountFlags.ReadOnly, mountPoint, out mount);
    }

    public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        superblock = null;
        // Eine Source würde im Cosmos-Treiber das injizierte Lesegerät umgehen.
        if (!source.IsEmpty || (flags & MountFlags.ReadOnly) == 0 || device.BlockSize != 512
            || device.BlockCount < 4 || device.BlockCount > (ulong)Ext2FeatureGate.MaximumBlocks * 8) return false;
        byte[] header = new byte[1024];
        try { using (StorageLock.Enter()) device.ReadBlock(2, 2, header); }
        catch (Exception) { return false; }
        if (Ext2FeatureGate.Evaluate(header) == Ext2MountDecision.Reject) return false;

        var native = new Ext2FileSystemType(new CosmosReadOnlyBlockDevice(device));
        if (!native.TryMount(ReadOnlySpan<char>.Empty, flags, out IVfsSuperblock? inner)) return false;
        superblock = CosmosReadOnlyVfs.Wrap(inner);
        return true;
    }

    public bool TryFormat(ReadOnlySpan<char> source, IVfsFormatOptions? options) => false;
    public bool TryDestroy(ReadOnlySpan<char> source) => false;
}
