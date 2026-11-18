using System;
using System.Collections.Generic;
using Cosmos.Kernel.System.FileSystem;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage;

/// <summary>Cosmos-Seite von <see cref="RootPowerSequence"/>; jeder Plattenzugriff einzeln unter der Speicher-Sperre.</summary>
internal sealed class CosmosRootPowerPlatform(IRootVolume root, Func<string?> busyReason) : IRootPowerPlatform
{
    // Zum Wiedereinhaengen: Cosmos verweigert das Aushaengen offener Dateien nicht, es nimmt nur den Mount heraus (Spec 7b §2.4).
    private readonly Dictionary<string, VfsMount> removed = new(StringComparer.Ordinal);

    public string? BusyReason() => busyReason();
    public bool TryBlockWrites() => AppStorageGate.TryBlock();
    public void ReleaseWrites() => AppStorageGate.Release();

    public IReadOnlyList<string> PersistentMounts() => StorageLock.Run(PersistentMountsStorageLocked);

    // Ohne Partition sind es virtuelle (/proc, /sys, /dev) oder Lese-Wrapper-Mounts; sie halten keine Daten (Spec 7b §2.6).
    private static List<string> PersistentMountsStorageLocked()
    {
        var mounts = new List<string>();
        foreach (VfsMount mount in VfsManager.Mounts)
            if (mount.Partition != null && mount.MountPoint != "/" && !mount.IsReadOnly) mounts.Add(mount.MountPoint);
        return mounts;
    }

    public bool IsMounted(string mountPoint) => StorageLock.Run(() => VfsManager.TryGetMount(mountPoint, out _));

    public bool Sync(string mountPoint) => StorageLock.Run(() => SyncStorageLocked(mountPoint));

    // FAT-Sync ruft Device.Flush; vorgezogen, damit ein Fehler sichtbar wird, solange der Mount noch besteht (Spec 7b §2.5).
    private static bool SyncStorageLocked(string mountPoint) =>
        VfsManager.TryGetMount(mountPoint, out VfsMount? mount) && mount.Superblock.SuperOperations.Sync(mount.Superblock);

    public bool Unmount(string mountPoint) => StorageLock.Run(() => UnmountStorageLocked(mountPoint));

    private bool UnmountStorageLocked(string mountPoint)
    {
        if (!VfsManager.TryGetMount(mountPoint, out VfsMount? mount)) return false;
        removed[mountPoint] = mount;
        return VfsManager.TryUnmount(mountPoint);
    }

    public bool Remount(string mountPoint) => StorageLock.Run(() => RemountStorageLocked(mountPoint));

    private bool RemountStorageLocked(string mountPoint)
    {
        if (!removed.TryGetValue(mountPoint, out VfsMount? mount) || mount.Partition == null) return false;
        if (!VfsManager.TryMount(mount.Name, mount.Partition, mount.Flags, mountPoint, out _)) return false;
        removed.Remove(mountPoint);
        return true;
    }

    public ResultErr CloseRoot() => StorageLock.Run(() => RootVolumeState.Close(root));
    public ResultErr ReopenRoot() => StorageLock.Run(() => RootVolumeState.Open(root));
}
