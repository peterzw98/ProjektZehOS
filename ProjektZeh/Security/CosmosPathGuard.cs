using System;
using System.Collections.Generic;
using System.IO;
using Cosmos.Kernel.System.FileSystem;
using ProjektZeh.Shared;

namespace ProjektZeh.Security;

/// <summary>
/// Cosmos-Anbindung des Pfadwaechters (Etappe 3.3). Fragt jede Komponente ohne Folgen ab: Elternverzeichnis oeffnen,
/// IInodeOperations.Lookup und GetAttr auf dem Kind. Der Elternpfad wurde zuvor Komponente fuer Komponente als
/// Nicht-Symlink geprueft, deshalb folgt TryOpenDirectory dabei keinem Link. Einhaengepunkte aus VfsManager.Mounts.
/// </summary>
internal static class CosmosPathGuard
{
    public static Func<string, bool, ResultErr> For(string root) => (path, creating) =>
    {
        var mountPoints = new List<string>();
        foreach (VfsMount mount in VfsManager.Mounts) mountPoints.Add(mount.MountPoint);
        return PathGuard.Check(root, path, mountPoints, Lookup, creating);
    };

    private static PathNodeKind Lookup(string parent, string name)
    {
        // Der Elternpfad wurde schon als Verzeichnis geprueft; laesst er sich nicht oeffnen, ist das ein Fehler, kein fehlendes Blatt.
        if (!VfsManager.TryOpenDirectory(parent, out IVfsDirectoryHandle? directory))
            throw new IOException("Elternverzeichnis nicht lesbar: " + parent);
        using (directory)
        {
            IVfsInode inode = directory.Inode;
            if (!inode.InodeOperations.Lookup(inode, name, out IVfsInode? child)) return PathNodeKind.Missing;
            // Unlesbare Attribute: lieber ablehnen als einen Symlink uebersehen (Ausnahme sperrt im Aufrufer).
            if (!child.InodeOperations.GetAttr(child, out VfsStat stat)) throw new InvalidOperationException("GetAttr fehlgeschlagen.");
            return stat.IsSymbolicLink ? PathNodeKind.Symlink : stat.IsDirectory ? PathNodeKind.Directory : PathNodeKind.File;
        }
    }
}
