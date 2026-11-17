using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Cosmos.Kernel.System.FileSystem;

namespace ProjektZeh.Storage;

/// <summary>
/// Lese-Wrapper um einen nativen Cosmos-Superblock (aus CosmosReadOnlyExt2FileSystem herausgeloest): alle Mutationen
/// liefern false, Fsync/Release/Sync/Drop schreiben nichts. Cosmos 3.0.97 ruft beim Schliessen jeder Datei Fsync auf,
/// auch bei reinen Lesehandles (FAT: Verzeichniseintrag und Flush); ein Mount mit MountFlags.ReadOnly allein reicht nicht.
/// </summary>
internal static class CosmosReadOnlyVfs
{
    public static IVfsSuperblock Wrap(IVfsSuperblock inner) => new ReadOnlySuperblock(inner);

    private sealed class ReadOnlySuperblock : IVfsSuperblock, ISuperblockOperations
    {
        private readonly IVfsSuperblock inner;
        public ReadOnlySuperblock(IVfsSuperblock inner)
        {
            this.inner = inner;
            Root = new ReadOnlyInode(inner.Root);
        }

        public IVfsInode Root { get; }
        public ISuperblockOperations SuperOperations => this;
        public long BlockSize => inner.BlockSize;
        public ulong MaxNameLength => inner.MaxNameLength;
        public bool IsReadOnly => true;
        public bool StatFs(IVfsSuperblock superblock, out VfsStatFs statFs) => inner.SuperOperations.StatFs(inner, out statFs);
        // Cosmos 3.0.97 schreibt auch bei Sync/Drop eines Lesemounts. Hier existieren nur verwaltete Lesecaches.
        public bool Sync(IVfsSuperblock superblock) => true;
        public void Drop(IVfsSuperblock superblock) { }
    }

    private sealed class ReadOnlyInode(IVfsInode inner) : IVfsInode, IInodeOperations, IFileOperations
    {
        public IInodeOperations InodeOperations => this;
        public IFileOperations? FileOperations => inner.FileOperations == null ? null : this;
        public string Name => inner.Name;

        public bool Lookup(IVfsInode dir, ReadOnlySpan<char> name, [NotNullWhen(true)] out IVfsInode? child)
        {
            child = null;
            if (!inner.InodeOperations.Lookup(inner, name, out IVfsInode? found)) return false;
            child = new ReadOnlyInode(found);
            return true;
        }

        public bool ReadDir(IVfsInode dir, out IReadOnlyList<IVfsInode> entries)
        {
            entries = Array.Empty<IVfsInode>();
            if (!inner.InodeOperations.ReadDir(inner, out IReadOnlyList<IVfsInode> found)) return false;
            var wrapped = new IVfsInode[found.Count];
            for (int i = 0; i < found.Count; i++) wrapped[i] = new ReadOnlyInode(found[i]);
            entries = wrapped;
            return true;
        }

        public bool GetAttr(IVfsInode inode, out VfsStat stat) => inner.InodeOperations.GetAttr(inner, out stat);
        public bool TryReadLink(IVfsInode symlink, out string? target) => inner.InodeOperations.TryReadLink(inner, out target);
        public bool Create(IVfsInode dir, ReadOnlySpan<char> name, VfsMode mode, [NotNullWhen(true)] out IVfsInode? inode) { inode = null; return false; }
        public bool Mkdir(IVfsInode dir, ReadOnlySpan<char> name, VfsMode mode, [NotNullWhen(true)] out IVfsInode? inode) { inode = null; return false; }
        public bool Symlink(IVfsInode dir, ReadOnlySpan<char> name, ReadOnlySpan<char> target, [NotNullWhen(true)] out IVfsInode? inode) { inode = null; return false; }
        public bool Unlink(IVfsInode dir, ReadOnlySpan<char> name) => false;
        public bool Rmdir(IVfsInode dir, ReadOnlySpan<char> name) => false;
        public bool Rename(IVfsInode oldParent, ReadOnlySpan<char> oldName, IVfsInode newParent, ReadOnlySpan<char> newName) => false;
        public bool SetAttr(IVfsInode inode, SetAttrFlags flags, in VfsStat attributes) => false;

        public long Read(IVfsOpenFile openFile, Span<byte> buffer) => inner.FileOperations!.Read(new NativeOpenFile(openFile, inner), buffer);
        public bool Seek(IVfsOpenFile openFile, long offset, SeekWhence whence, out long newPosition) =>
            inner.FileOperations!.Seek(new NativeOpenFile(openFile, inner), offset, whence, out newPosition);
        public long Write(IVfsOpenFile openFile, ReadOnlySpan<byte> buffer) => throw new UnauthorizedAccessException("Lesemount: Schreiben gesperrt.");
        // Release ruft nativ Fsync mit WriteInode/Flush auf, selbst wenn die Datei nur gelesen wurde.
        public bool Fsync(IVfsOpenFile openFile) => true;
        public void Release(IVfsOpenFile openFile) { }
    }

    private sealed class NativeOpenFile(IVfsOpenFile outer, IVfsInode inode) : IVfsOpenFile
    {
        public IVfsInode Inode => inode;
        public IFileOperations Operations => inode.FileOperations!;
        public long Position { get => outer.Position; set => outer.Position = value; }
    }
}

/// <summary>
/// Generische Lesekomponente fuer beliebige Cosmos-Dateisystemtypen, etwa die FAT-Quelle /data waehrend der Migration
/// (Etappe 4.5, P3-g). Haengt nur lesend ein und reicht die Quelle unveraendert an den inneren Typ weiter.
/// </summary>
internal sealed class CosmosReadOnlyFileSystem(IVfsFileSystemType inner) : IVfsFileSystemType
{
    public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        superblock = null;
        if ((flags & MountFlags.ReadOnly) == 0 || !inner.TryMount(source, flags, out IVfsSuperblock? native)) return false;
        superblock = CosmosReadOnlyVfs.Wrap(native);
        return true;
    }

    public bool TryFormat(ReadOnlySpan<char> source, IVfsFormatOptions? options) => false;
    public bool TryDestroy(ReadOnlySpan<char> source) => false;
}

/// <summary>
/// Feste Registrierung mit austauschbarem Inneren (Etappe 4.5): Cosmos 3.0.97 kennt kein UnregisterFileSystem, die Migration
/// haengt aber nacheinander verschiedene Partitionen ein. Das Innere gilt nur waehrend des einen TryMount-Aufrufs. Die
/// oeffentliche Partitions-Ueberladung setzt VfsMount.Partition (Identitaetspruefung, IsPartitionMounted) und reicht als
/// Quelle den Partitionsindex weiter; den verwirft der Typ, damit das Innere sein injiziertes Lesegeraet verwendet.
/// </summary>
internal sealed class CosmosSwitchableFileSystem(string name) : IVfsFileSystemType
{
    private bool registered;
    private IVfsFileSystemType? inner;

    public bool TryMount(IVfsFileSystemType type, Cosmos.Kernel.System.Storage.Partition partition, MountFlags flags, string mountPoint)
    {
        if (!registered)
        {
            if (!VfsManager.RegisterFileSystem(name, this)) return false;
            registered = true;
        }

        inner = type;
        try
        {
            return VfsManager.TryMount(name, partition, flags, mountPoint, out _);
        }
        finally
        {
            inner = null;
        }
    }

    public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        superblock = null;
        // Mit dem Index wuerde FatFileSystemType das echte Geraet statt des injizierten Lesegeraets oeffnen.
        return inner != null && inner.TryMount(ReadOnlySpan<char>.Empty, flags, out superblock);
    }

    public bool TryFormat(ReadOnlySpan<char> source, IVfsFormatOptions? options) => false;
    public bool TryDestroy(ReadOnlySpan<char> source) => false;
}
