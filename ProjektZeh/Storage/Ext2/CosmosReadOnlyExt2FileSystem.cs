using System;
using System.Collections.Generic;
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

    public bool TryMount([NotNullWhen(true)] out VfsMount? mount)
    {
        mount = null;
        foreach (VfsMount existing in VfsManager.Mounts)
        {
            if (existing.MountPoint == MountPoint || ReferenceEquals(existing.Partition, device)) return false;
        }

        if (!registered)
        {
            if (!VfsManager.RegisterFileSystem(FileSystemName, this)) return false;
            registered = true;
        }

        return VfsManager.TryMount(FileSystemName, "", MountFlags.ReadOnly, MountPoint, out mount);
    }

    public bool TryMount(ReadOnlySpan<char> source, MountFlags flags, [NotNullWhen(true)] out IVfsSuperblock? superblock)
    {
        superblock = null;
        // Eine Source würde im Cosmos-Treiber das injizierte Lesegerät umgehen.
        if (!source.IsEmpty || (flags & MountFlags.ReadOnly) == 0 || device.BlockSize != 512
            || device.BlockCount < 4 || device.BlockCount > (ulong)Ext2FeatureGate.MaximumBlocks * 8) return false;
        byte[] header = new byte[1024];
        device.ReadBlock(2, 2, header);
        if (Ext2FeatureGate.Evaluate(header) == Ext2MountDecision.Reject) return false;

        var native = new Ext2FileSystemType(new CosmosReadOnlyBlockDevice(device));
        if (!native.TryMount(ReadOnlySpan<char>.Empty, flags, out IVfsSuperblock? inner)) return false;
        superblock = new ReadOnlySuperblock(inner);
        return true;
    }

    public bool TryFormat(ReadOnlySpan<char> source, IVfsFormatOptions? options) => false;
    public bool TryDestroy(ReadOnlySpan<char> source) => false;

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
        public long Write(IVfsOpenFile openFile, ReadOnlySpan<byte> buffer) => throw new UnauthorizedAccessException("ext2-Lesemount: Schreiben gesperrt.");
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
