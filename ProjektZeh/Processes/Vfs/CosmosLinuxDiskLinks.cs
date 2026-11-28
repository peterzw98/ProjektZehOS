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
/// als verweigert (-EACCES), damit kein Mount unter der Wurzel aus ihr herausführt. Während einer Auflösung hält sie die gefundenen
/// Ordner (N2-Z, siehe <see cref="BeginResolveStorageLocked"/>).
/// </summary>
internal sealed class CosmosLinuxDiskLinks : ILinuxDiskLinks
{
    private readonly string _root;

    // Während einer Auflösung (BeginResolveStorageLocked .. EndResolveStorageLocked, StorageLock durchgehend gehalten): gefundene
    // Ordner je vollem Pfad, damit der nächste Pfadteil im Elternordner nachschlägt statt VfsManager.TryOpenDirectory ab der
    // Einhängewurzel (Cosmos-FAT liest je Lookup das ganze Verzeichnis, N2-Z). Außerhalb der Sitzung leer: Ohne durchgehende Sperre
    // könnte ein anderer Weg einen gehaltenen Ordner entfernen und seinen Cluster neu vergeben. Ein Eintrag gilt für den Pfad, unter
    // dem der Aufrufer ihn angefragt hat; ein Fehltreffer kostet nur die Auflösung ab der Wurzel wie bisher.
    private readonly Dictionary<string, IVfsInode> _directories = new(StringComparer.Ordinal);

    // In derselben Sitzung: Einhängepunkte und der Einhängepunkt der Prozesswurzel, einmal gelesen statt je Abfrage.
    private readonly List<string> _mountPoints = new();
    private string? _rootMount;
    private bool _resolving;

    public CosmosLinuxDiskLinks(string root)
    {
        _root = root;
    }

    public void BeginResolveStorageLocked()
    {
        _directories.Clear();
        FillMountPointsStorageLocked(_mountPoints);
        _rootMount = PathGuard.MountOf(_mountPoints, _root);
        _resolving = true;
    }

    public void EndResolveStorageLocked()
    {
        _resolving = false;
        _directories.Clear(); // keine Inodes über die Sperre hinaus halten
        _mountPoints.Clear();
        _rootMount = null;
    }

    public LinuxNodeKind KindStorageLocked(string parent, string name, out string found, out LinuxDiskEntry entry)
    {
        found = name;
        entry = default;
        string full = parent + "/" + name;
        if (_resolving)
        {
            if (!string.Equals(PathGuard.MountOf(_mountPoints, full), _rootMount, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Anderer Einhängepunkt unter der Prozesswurzel.");
            }
        }
        else
        {
            var mountPoints = new List<string>();
            FillMountPointsStorageLocked(mountPoints);
            if (!string.Equals(PathGuard.MountOf(mountPoints, full), PathGuard.MountOf(mountPoints, _root), StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Anderer Einhängepunkt unter der Prozesswurzel.");
            }
        }

        IVfsInode inode = DirectoryStorageLocked(parent);
        if (!inode.InodeOperations.Lookup(inode, name, out IVfsInode? child))
        {
            return LinuxNodeKind.Missing;
        }

        // FAT findet auch über den 8.3-Kurznamen (FatInodeOperations.Lookup): den geführten Namen melden, DirectoryFileStore vergleicht.
        found = child.Name;

        // Unlesbare Attribute: ablehnen statt einen Symlink zu übersehen.
        if (!child.InodeOperations.GetAttr(child, out VfsStat stat))
        {
            throw new IOException("Attribute nicht lesbar.");
        }

        if (stat.IsSymbolicLink)
        {
            return LinuxNodeKind.Symlink;
        }

        if (stat.IsDirectory)
        {
            if (_resolving)
            {
                _directories[full] = child; // nächster Pfadteil schlägt hier nach
            }

            return LinuxNodeKind.Directory;
        }

        entry = new LinuxDiskEntry((long)stat.Size, ReadOnlyLikeFileInfo(stat), LastWriteTimeUtc(stat));
        return LinuxNodeKind.File;
    }

    /// <summary>
    /// Ordner <paramref name="parent"/>: in einer Auflösung zuerst der schon gefundene, sonst über <see cref="VfsManager.TryOpenDirectory"/>
    /// (folgt Cosmos-Symlinks, der Pfad ist aber schon Teil für Teil als Nicht-Symlink geprüft). Der Griff hält nichts
    /// (VfsDirectoryHandle.Dispose ist leer); das Inode-Objekt bleibt gültig, solange die Sperre gehalten wird.
    /// </summary>
    private IVfsInode DirectoryStorageLocked(string parent)
    {
        if (_resolving && _directories.TryGetValue(parent, out IVfsInode? held))
        {
            return held;
        }

        IVfsInode inode;
        using (IVfsDirectoryHandle directory = OpenDirectoryStorageLocked(parent))
        {
            inode = directory.Inode;
        }

        if (_resolving)
        {
            _directories[parent] = inode;
        }

        return inode;
    }

    private static void FillMountPointsStorageLocked(List<string> mountPoints)
    {
        mountPoints.Clear();
        foreach (VfsMount mount in VfsManager.Mounts)
        {
            mountPoints.Add(mount.MountPoint);
        }
    }

    /// <summary>
    /// Schreibschutz wie <see cref="FileAttributes.ReadOnly"/> über System.IO (FileStatus.IsModeReadOnlyCore): lesbar, aber nicht
    /// schreibbar für die passende Klasse; Cosmos meldet euid 0 und als Gruppe nur gid 0 (InteropSysFilePlug.GetEUid, IsMemberOfGroup).
    /// </summary>
    internal static bool ReadOnlyLikeFileInfo(in VfsStat stat)
    {
        VfsMode mode = stat.Mode;
        bool user = (mode & VfsMode.OwnerRead) != 0 && (mode & VfsMode.OwnerWrite) == 0;
        bool group = (mode & VfsMode.GroupRead) != 0 && (mode & VfsMode.GroupWrite) == 0;
        bool other = (mode & VfsMode.OtherRead) != 0 && (mode & VfsMode.OtherWrite) == 0;
        if (user == group && group == other)
        {
            return user;
        }

        if (stat.Uid == 0)
        {
            return user;
        }

        if (group == user)
        {
            return group;
        }

        return stat.Gid == 0 ? group : other;
    }

    /// <summary>Änderungszeit wie <see cref="FileSystemInfo.LastWriteTimeUtc"/> (FileStatus: Sekunden seit 1970 plus Nanosekunden in Ticks).</summary>
    private static DateTime LastWriteTimeUtc(in VfsStat stat) =>
        DateTimeOffset.FromUnixTimeSeconds(stat.ModificationTime.Seconds).AddTicks(stat.ModificationTime.Nanoseconds / 100).UtcDateTime;

    public string ReadLinkStorageLocked(string full)
    {
        (string parent, string name) = Split(full);
        IVfsInode inode = DirectoryStorageLocked(parent); // in einer Auflösung der eben gefundene Elternordner
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
        _directories.Clear(); // Änderung: gehaltene Ordner nicht weiterverwenden
        (string parent, string name) = Split(full);
        using IVfsDirectoryHandle directory = OpenDirectoryStorageLocked(parent);
        IVfsInode inode = directory.Inode;
        return inode.InodeOperations.Symlink(inode, name, target, out _);
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
