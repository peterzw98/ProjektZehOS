#pragma warning disable COSMOS0001 // UserPageTable gilt als Vorschau (wie in CosmosLinuxRun).
using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.Core.Memory;
using Cosmos.Kernel.System.FileSystem;
using ProjektZeh.Processes;
using ProjektZeh.Security;
using ProjektZeh.Storage;

namespace ProjektZeh.Processes.Vfs;

/// <summary>
/// Kernel-Fassung von <see cref="ILinuxDiskLinks"/> über die Cosmos-VFS, wie <see cref="CosmosPathGuard"/>: Elternverzeichnis
/// öffnen (dessen Pfad hat <see cref="LinuxFileSystem"/> schon Teil für Teil als Nicht-Symlink geprüft), Kind per
/// <c>Lookup</c> und <c>GetAttr</c> ohne Folgen abfragen. Ein Eintrag auf einem anderen Einhängepunkt als die Prozesswurzel gilt
/// als verweigert (-EACCES), damit kein Mount unter der Wurzel aus ihr herausführt. Im Gast noch nicht geprüft (V7).
/// </summary>
internal sealed class CosmosLinuxDiskLinks : ILinuxDiskLinks
{
    private readonly string _root;

    public CosmosLinuxDiskLinks(string root)
    {
        _root = root;
    }

    public LinuxNodeKind KindStorageLocked(string parent, string name, out string found)
    {
        found = name;
        var mountPoints = new List<string>();
        foreach (VfsMount mount in VfsManager.Mounts)
        {
            mountPoints.Add(mount.MountPoint);
        }

        if (!string.Equals(PathGuard.MountOf(mountPoints, parent + "/" + name), PathGuard.MountOf(mountPoints, _root), StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Anderer Einhängepunkt unter der Prozesswurzel.");
        }

        using IVfsDirectoryHandle directory = OpenDirectoryStorageLocked(parent);
        IVfsInode inode = directory.Inode;
        IVfsInode? child = null;
        if (!WithReclaim(() => inode.InodeOperations.Lookup(inode, name, out child)))
        {
            return LinuxNodeKind.Missing;
        }

        // FAT findet auch über den 8.3-Kurznamen (FatInodeOperations.Lookup): den geführten Namen melden, DirectoryFileStore vergleicht.
        found = child!.Name;

        // Unlesbare Attribute: ablehnen statt einen Symlink zu übersehen.
        VfsStat stat = default;
        IVfsInode attrNode = child!;
        if (!WithReclaim(() => attrNode.InodeOperations.GetAttr(attrNode, out stat)))
        {
            throw new IOException("Attribute nicht lesbar.");
        }

        return stat.IsSymbolicLink ? LinuxNodeKind.Symlink : stat.IsDirectory ? LinuxNodeKind.Directory : LinuxNodeKind.File;
    }

    public string ReadLinkStorageLocked(string full)
    {
        (string parent, string name) = Split(full);
        using IVfsDirectoryHandle directory = OpenDirectoryStorageLocked(parent);
        IVfsInode inode = directory.Inode;
        if (!inode.InodeOperations.Lookup(inode, name, out IVfsInode? child))
        {
            throw new FileNotFoundException("Eintrag fehlt.");
        }

        if (!child.InodeOperations.TryReadLink(child, out string? target) || target is null)
        {
            throw new IOException("Linkziel nicht lesbar.");
        }

        return target;
    }

    /// <summary>
    /// Cosmos meldet ein Scheitern nur als false; FAT kann keine Symlinks (FatInodeOperations.Symlink liefert immer false), daher
    /// gilt false als "nicht unterstützt" (-EPERM), nicht als Ein-/Ausgabefehler.
    /// </summary>
    public bool CreateSymlinkStorageLocked(string full, string target)
    {
        (string parent, string name) = Split(full);
        using IVfsDirectoryHandle directory = OpenDirectoryStorageLocked(parent);
        IVfsInode inode = directory.Inode;
        return inode.InodeOperations.Symlink(inode, name, target, out _);
    }

    /// <summary>
    /// V51: Cosmos-FAT meldet einen Allokationsfehler nur als false (Ladybird 384M: "Spawn RequestServer ENOENT"). Scheitert ein Versuch bei
    /// knappem Speicher, wird die Sperre kurz gelöst, einmal gesammelt (FrameReclaim.Retry, ohne Sperre, wie dessen Doku verlangt) und der
    /// Versuch genau einmal unter der Sperre wiederholt; das Ergebnis dieses zweiten Versuchs zählt.
    /// Keine Heuristik: eine wirklich fehlende Datei bleibt auch bei knappem Speicher -ENOENT. Gast belegt: siehe PR.
    /// </summary>
    private static bool WithReclaim(Func<bool> attempt)
    {
        if (attempt())
        {
            return true;
        }

        if (!FrameReclaim.IsNearReserve(UserPageTable.FreePages, UserPageTable.ReservedPages))
        {
            return false;
        }

        // Der erste Versuch lief schon; Retry bekommt deshalb einen leeren Versuch und liefert nur die gebremste Sammlung (Sturmbremse, Fehler geschluckt).
        StorageLock.RunUnlocked(static () => FrameReclaim.Retry(static () => false, static () => GC.Collect(),
            static () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency));
        return attempt();
    }

    private static IVfsDirectoryHandle OpenDirectoryStorageLocked(string parent)
    {
        IVfsDirectoryHandle? directory = null;
        if (!WithReclaim(() => VfsManager.TryOpenDirectory(parent, out directory)) || directory is null)
        {
            throw new DirectoryNotFoundException("Elternverzeichnis nicht lesbar.");
        }

        return directory;
    }

    private static (string Parent, string Name) Split(string full)
    {
        int slash = full.LastIndexOf('/');
        return (full.Substring(0, slash), full.Substring(slash + 1));
    }
}
