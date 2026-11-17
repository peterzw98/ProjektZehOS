using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Cosmos.Kernel.System.FileSystem;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Virtual;

namespace ProjektZeh.Storage;

/// <summary>
/// Cosmos-VFS-Treiber fuer einen der Baeume /proc, /sys oder /dev (Spec E5b/E6b §0.1, §2; nur Kernelbuild).
/// Baum, Rechte und Inhalte kommen aus <see cref="VirtualTree"/>, <see cref="DeviceFileSystem"/> und dem Erzeuger; der
/// Treiber lehnt jede Mutation selbst ab. Zugriffsrechte pro Konto prueft FileService, der VFS kennt keine ZehOS-Konten.
/// </summary>
internal sealed class CosmosVirtualFileSystemType(string root, Func<string, ResultObject<byte[]>> source) : IVfsFileSystemType
{
    public bool TryMount(ReadOnlySpan<char> mountSource, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        var node = VirtualTree.Find(root);
        superblock = node is { Kind: VirtualKind.Directory } ? new VirtualSuperblock(node, source) : null;
        return superblock != null;
    }

    public bool TryFormat(ReadOnlySpan<char> mountSource, IVfsFormatOptions? options) => false;

    public bool TryDestroy(ReadOnlySpan<char> mountSource) => false;
}

internal sealed class VirtualSuperblock : IVfsSuperblock, ISuperblockOperations
{
    public VirtualSuperblock(VirtualNode root, Func<string, ResultObject<byte[]>> source)
    {
        Operations = new VirtualInodeOperations(source);
        Root = new VirtualInode(root, Operations, null);
        // Nur /dev nimmt Schreibzugriffe an, und dort nur /dev/null (Spec §0.1).
        IsReadOnly = !string.Equals(root.Path, "/dev", StringComparison.Ordinal);
    }

    internal VirtualInodeOperations Operations { get; }
    public IVfsInode Root { get; }
    public ISuperblockOperations SuperOperations => this;
    public long BlockSize => 4096;
    public ulong MaxNameLength => 255;
    public bool IsReadOnly { get; }

    public bool Sync(IVfsSuperblock superblock) => true;

    /// <summary>0 Bloecke wie Linux-procfs; df und DiskSpace pruefen 0 vor jeder Division (huang P3 e).</summary>
    public bool StatFs(IVfsSuperblock superblock, out VfsStatFs statFs)
    {
        statFs = new VfsStatFs { BlockSize = 4096, MaxNameLength = 255 };
        return true;
    }

    public void Drop(IVfsSuperblock superblock) { }
}

/// <summary>Je Lookup ein frisches Objekt mit eigener Dateioperation: so haelt jedes Open seinen eigenen Snapshot (Spec §0.3).</summary>
internal sealed class VirtualInode(VirtualNode node, VirtualInodeOperations operations, IFileOperations? fileOperations) : IVfsInode
{
    public VirtualNode Node { get; } = node;
    public IInodeOperations InodeOperations { get; } = operations;
    public IFileOperations? FileOperations { get; } = fileOperations;
    public string Name { get; } = node.Path[(node.Path.LastIndexOf('/') + 1)..];
}

internal sealed class VirtualInodeOperations(Func<string, ResultObject<byte[]>> source) : IInodeOperations
{
    public bool Lookup(IVfsInode dir, ReadOnlySpan<char> name, [NotNullWhen(true)] out IVfsInode? child)
    {
        child = null;
        if (dir is not VirtualInode parent || name.IsEmpty || name.Contains('/')) return false;
        var node = VirtualTree.Find(parent.Node.Path + "/" + name.ToString());
        if (node is null) return false;
        child = Create(node);
        return true;
    }

    public bool ReadDir(IVfsInode dir, out IReadOnlyList<IVfsInode> entries)
    {
        entries = [];
        if (dir is not VirtualInode parent) return false;
        var names = VirtualTree.List(parent.Node.Path);
        if (!names.IsOK()) return false;
        var children = new List<IVfsInode>(names.Value!.Count);
        foreach (string name in names.Value) children.Add(Create(VirtualTree.Find(parent.Node.Path + "/" + name)!));
        entries = children;
        return true;
    }

    public bool GetAttr(IVfsInode inode, out VfsStat stat)
    {
        stat = default;
        if (inode is not VirtualInode virtualInode) return false;
        var type = virtualInode.Node.Kind switch
        {
            VirtualKind.Directory => VfsMode.Directory,
            VirtualKind.CharacterDevice => VfsMode.CharacterDevice,
            _ => VfsMode.RegularFile,
        };
        // Groesse 0 wie Linux: kein Snapshot beim stat, Leser lesen bis EOF (Spec §0.4).
        stat.Mode = type | ((VfsMode)virtualInode.Node.Mode & VfsMode.PermissionMask);
        stat.LinkCount = 1;
        stat.PreferredBlockSize = 4096;
        return true;
    }

    // Mutationen lehnt der Treiber selbst ab, auch wenn ein Mount schreibbar ist (/dev).
    public bool Create(IVfsInode dir, ReadOnlySpan<char> name, VfsMode mode, [NotNullWhen(true)] out IVfsInode? inode) { inode = null; return false; }
    public bool Mkdir(IVfsInode dir, ReadOnlySpan<char> name, VfsMode mode, [NotNullWhen(true)] out IVfsInode? inode) { inode = null; return false; }
    public bool Symlink(IVfsInode dir, ReadOnlySpan<char> name, ReadOnlySpan<char> target, [NotNullWhen(true)] out IVfsInode? inode) { inode = null; return false; }
    public bool Unlink(IVfsInode dir, ReadOnlySpan<char> name) => false;
    public bool Rmdir(IVfsInode dir, ReadOnlySpan<char> name) => false;
    public bool Rename(IVfsInode oldParent, ReadOnlySpan<char> oldName, IVfsInode newParent, ReadOnlySpan<char> newName) => false;
    public bool SetAttr(IVfsInode inode, SetAttrFlags flags, in VfsStat attributes) => false;

    private VirtualInode Create(VirtualNode node) => new(node, this, node.Kind switch
    {
        VirtualKind.Directory => null,
        VirtualKind.CharacterDevice => new VirtualDeviceOperations(node.Path),
        _ => new VirtualFileOperations(() => source(node.Path)),
    });
}

/// <summary>Snapshot beim ersten Read oder Seek(End), danach fest fuer dieses Open; die Position fuehrt VfsFileHandle.</summary>
internal sealed class VirtualFileOperations(Func<ResultObject<byte[]>> produce) : IFileOperations
{
    private ResultObject<SystemInfoFile>? snapshot;

    private ResultObject<SystemInfoFile> Snapshot() => snapshot ??= SystemInfoFile.Open(produce);

    // Fehler als 0 (EOF) wie die Cosmos-Treiber: VfsFileHandle addiert den Rueckgabewert auf die Position, -1 waere dort
    // eine gueltige Laenge. Die Ursache sieht FileService direkt im Erzeuger, nicht ueber diesen Weg.
    public long Read(IVfsOpenFile openFile, Span<byte> buffer)
    {
        var file = Snapshot();
        if (!file.IsOK()) return 0;
        var read = file.Value!.Read(openFile.Position, buffer);
        return read.IsOK() ? read.Value : 0;
    }

    // /proc und /sys sind schreibgeschuetzt eingehaengt; Cosmos lehnt das Schreiben dort schon vorher ab.
    public long Write(IVfsOpenFile openFile, ReadOnlySpan<byte> buffer) => 0;

    /// <summary>Jede nichtnegative Position ist erlaubt, auch hinter EOF; Read liefert dort 0 (Spec §2).</summary>
    public bool Seek(IVfsOpenFile openFile, long offset, SeekWhence whence, out long newPosition)
    {
        newPosition = openFile.Position;
        long basis;
        if (whence == SeekWhence.Set) basis = 0;
        else if (whence == SeekWhence.Cur) basis = openFile.Position;
        else
        {
            var file = Snapshot();
            if (!file.IsOK()) return false;
            basis = file.Value!.Length;
        }

        if ((offset > 0 && basis > long.MaxValue - offset) || basis + offset < 0) return false;
        newPosition = basis + offset;
        return true;
    }

    public bool Fsync(IVfsOpenFile openFile) => true;
    public void Release(IVfsOpenFile openFile) { }
}

/// <summary>/dev/null und /dev/zero ueber <see cref="DeviceFileSystem"/>; die Position hat fuer Geraete keine Bedeutung.</summary>
internal sealed class VirtualDeviceOperations(string path) : IFileOperations
{
    private readonly DeviceHandle? handle = DeviceFileSystem.Open(path["/dev/".Length..]).Value;

    public long Read(IVfsOpenFile openFile, Span<byte> buffer)
    {
        if (handle is null) return 0;
        var read = handle.Read(buffer);
        return read.IsOK() ? read.Value : 0;
    }

    /// <summary>Beide Geraete verwerfen wie unter Linux; Nutzerpfade auf /dev/zero lehnt FileService (Spec §1) schon vorher ab.</summary>
    public long Write(IVfsOpenFile openFile, ReadOnlySpan<byte> buffer)
    {
        if (handle is null) return 0;
        var written = handle.Write(buffer);
        return written.IsOK() ? written.Value : 0;
    }

    /// <summary>Geraete haben keine Laenge: End rechnet ab 0; jede nichtnegative Position ist erlaubt.</summary>
    public bool Seek(IVfsOpenFile openFile, long offset, SeekWhence whence, out long newPosition)
    {
        long basis = whence == SeekWhence.Cur ? openFile.Position : 0;
        newPosition = openFile.Position;
        if ((offset > 0 && basis > long.MaxValue - offset) || basis + offset < 0) return false;
        newPosition = basis + offset;
        return true;
    }

    public bool Fsync(IVfsOpenFile openFile) => true;
    // Ohne Wirkung wie bei Dateien (Spec §2): das Handle gehoert zu diesem Open, haelt aber keine Ressource.
    public void Release(IVfsOpenFile openFile) { }
}
