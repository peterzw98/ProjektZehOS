using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.Core.Memory;
using Cosmos.Kernel.System.FileSystem;
using ProjektZeh.Processes;
using ProjektZeh.Security;

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
        if (!inode.InodeOperations.Lookup(inode, name, out IVfsInode? child))
        {
            ThrowIfMemoryExhausted();
            return LinuxNodeKind.Missing;
        }

        // FAT findet auch über den 8.3-Kurznamen (FatInodeOperations.Lookup): den geführten Namen melden, DirectoryFileStore vergleicht.
        found = child.Name;

        // Unlesbare Attribute: ablehnen statt einen Symlink zu übersehen.
        if (!child.InodeOperations.GetAttr(child, out VfsStat stat))
        {
            ThrowIfMemoryExhausted();
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
    /// V51: Cosmos-FAT meldet einen Allokationsfehler beim Lookup nur als false, ZehOS machte daraus -ENOENT (Ladybird 384M: "Spawn
    /// RequestServer ENOENT"). Nahe der Speicherreserve gilt false als fehlender Speicher (-ENOMEM). Gast belegt: nein (offen).
    /// </summary>
    private static void ThrowIfMemoryExhausted()
    {
        if (FrameReclaim.IsNearReserve(UserPageTable.FreePages, UserPageTable.ReservedPages))
        {
            throw new LinuxOutOfMemoryException("Speicher erschöpft.");
        }
    }

    private static IVfsDirectoryHandle OpenDirectoryStorageLocked(string parent)
    {
        if (!VfsManager.TryOpenDirectory(parent, out IVfsDirectoryHandle? directory))
        {
            ThrowIfMemoryExhausted();
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
