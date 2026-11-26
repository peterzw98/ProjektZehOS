using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.System.FileSystem;
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

    public LinuxNodeKind KindStorageLocked(string parent, string name)
    {
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
            return LinuxNodeKind.Missing;
        }

        // Unlesbare Attribute: ablehnen statt einen Symlink zu übersehen.
        if (!child.InodeOperations.GetAttr(child, out VfsStat stat))
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

    public void CreateSymlinkStorageLocked(string full, string target)
    {
        (string parent, string name) = Split(full);
        using IVfsDirectoryHandle directory = OpenDirectoryStorageLocked(parent);
        IVfsInode inode = directory.Inode;
        if (!inode.InodeOperations.Symlink(inode, name, target, out _))
        {
            throw new IOException("Symlink nicht angelegt.");
        }
    }

    private static IVfsDirectoryHandle OpenDirectoryStorageLocked(string parent)
    {
        if (!VfsManager.TryOpenDirectory(parent, out IVfsDirectoryHandle? directory))
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
