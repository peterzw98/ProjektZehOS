using System;
using System.Runtime.CompilerServices;
using System.Text;
using Cosmos.Build.API.Attributes;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage;

// shortcut: Cosmos 3.0.97 RemoveEntry verschmilzt Records ueber Blockgrenzen (Ext2Directory.cs:341-345, E4.0 Lauf 9/10,
// Hostbeweis task/ext2-boundary-test); Freigabe Peter 17.11. Nach einem korrigierten Cosmos-Release entfernen.
// Liegt ausserhalb von Storage/Ext2/, weil die Hostprobe jene Quellen ohne Cosmos-Build-API einbindet.
[Plug(TargetName = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2DirectoryHelper")]
public static class CosmosExt2RemoveEntryPlug
{
    // Unlink, Rmdir und Rename rufen RemoveEntry; der Koerper nennt keinen Typ aus Cosmos.Kernel.System (vgl. CosmosExt2FreeInodePlug).
    [PlugMember]
    public static bool RemoveEntry(object sb, object dir, string name) => CosmosExt2DirectoryRemoval.Remove(sb, dir, name);
}

/// <summary>Verbindet Ext2DirectoryRemoval mit Cosmos' Ext2Superblock und Ext2Inode (Original: Ext2Directory.cs:302-365).</summary>
internal static class CosmosExt2DirectoryRemoval
{
    private const string Superblock = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Superblock, Cosmos.Kernel.System";
    private const string Inode = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Inode, Cosmos.Kernel.System";

    internal static bool Remove(object superblock, object directory, string name)
    {
        ulong size = GetFullSize(directory);
        if (size == 0) return false;
        uint blockSize = GetBlockSize(superblock);
        uint blocks = (uint)((size + blockSize - 1) / blockSize);
        byte[] data = new byte[blocks * blockSize];
        uint read = 0;
        for (; read < blocks; read++)
        {
            uint block = GetBlockPointer(superblock, directory, read, false, out _);
            // Wie das Original: ein Loch beendet das Verzeichnis; nur die gelesenen Bloecke werden durchsucht.
            if (block == 0) break;
            ReadBlocks(superblock, block, 1, data.AsSpan((int)(read * blockSize), (int)blockSize));
        }

        int changed = Ext2DirectoryRemoval.Remove(data.AsSpan(0, (int)(read * blockSize)), (int)blockSize, Encoding.UTF8.GetBytes(name));
        if (changed < 0) return false;
        // Nur der eine geaenderte Block wird geschrieben (das Original schrieb alle Bloecke des Verzeichnisses zurueck).
        uint target = GetBlockPointer(superblock, directory, (uint)changed, false, out _);
        WriteBlocks(superblock, target, 1, data.AsSpan((int)(changed * blockSize), (int)blockSize));
        return true;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FullSize")]
    private static extern ulong GetFullSize([UnsafeAccessorType(Inode)] object inode);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BlockSize")]
    private static extern uint GetBlockSize([UnsafeAccessorType(Superblock)] object superblock);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetBlockPointer")]
    private static extern uint GetBlockPointer([UnsafeAccessorType(Superblock)] object superblock, [UnsafeAccessorType(Inode)] object inode,
        uint logicalBlock, bool allocate, out bool isNew);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReadBlocks")]
    private static extern void ReadBlocks([UnsafeAccessorType(Superblock)] object superblock, uint ext2Block, uint count, Span<byte> destination);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WriteBlocks")]
    private static extern void WriteBlocks([UnsafeAccessorType(Superblock)] object superblock, uint ext2Block, uint count, ReadOnlySpan<byte> data);
}
