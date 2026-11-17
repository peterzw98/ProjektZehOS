using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cosmos.Build.API.Attributes;
using Cosmos.Kernel.System.FileSystem;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage;

// shortcut: Cosmos 3.0.97 Rmdir gibt nur Block[0] frei (Ext2InodeOperations.cs:477-482); ein Verzeichnis mit mehr Bloecken
// hinterliess sie belegt (E4.0 Lauf 9: e2fsck "Block bitmap differences"). Zuweisung agent-coordinator, Freigabe Peter
// 17.11. Nach einem korrigierten Cosmos-Release entfernen.
// Liegt ausserhalb von Storage/Ext2/, weil die Hostprobe jene Quellen ohne Cosmos-Build-API einbindet.
[Plug(TargetName = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2InodeOperations")]
public static class CosmosExt2RmdirPlug
{
    // Der Koerper wird nach Cosmos.Kernel.System kopiert und darf keinen Typ von dort nennen (vgl. CosmosExt2FormatterPlug):
    // mit IVfsInode in der Aufrufsignatur verwies die Assembly auf sich selbst ("library initializer ... cycle").
    // Die Plug-Signatur selbst nennt IVfsInode, damit der Patcher Rmdir nach vollem Typnamen findet.
    [PlugMember]
    public static bool Rmdir(object aThis, IVfsInode dir, ReadOnlySpan<char> name) => CosmosExt2DirectoryRmdir.Remove(aThis, dir, name);
}

/// <summary>
/// Ersatz fuer Cosmos Ext2InodeOperations.Rmdir mit demselben Ablauf, aber vollstaendiger Blockfreigabe: Truncate gibt alle
/// Datenbloecke frei, die Zeigerbloecke danach Ext2IndirectBlocks. Die Pruefungen nutzen die oeffentlichen
/// IInodeOperations (Lookup, ReadDir) statt der internen Verzeichnisparser.
/// </summary>
internal static class CosmosExt2DirectoryRmdir
{
    private const string Operations = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2InodeOperations, Cosmos.Kernel.System";
    private const string Superblock = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Superblock, Cosmos.Kernel.System";
    private const string Inode = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Inode, Cosmos.Kernel.System";
    private const string GroupDesc = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2GroupDesc, Cosmos.Kernel.System";

    internal static bool Remove(object operations, object directory, ReadOnlySpan<char> name)
    {
        var dir = (IVfsInode)directory;
        // Lookup loest "." und ".." strukturell zum Verzeichnis selbst bzw. zum Elternteil auf; das Original haette hier den
        // Punkteintrag entfernt. Wie unter Linux (EINVAL/ENOTEMPTY) wird beides abgelehnt.
        if (name is "." or "..") return false;
        var inodeOperations = (IInodeOperations)operations;
        // Lookup und ReadDir pruefen selbst, dass dir bzw. child ein ext2-Verzeichnis dieses Volumes ist.
        if (!inodeOperations.Lookup(dir, name, out IVfsInode? child)) return false;
        if (!inodeOperations.ReadDir(child, out IReadOnlyList<IVfsInode> entries) || entries.Count != 0) return false;

        object superblock = SuperblockOf(dir);
        if (!CosmosExt2DirectoryRemoval.Remove(superblock, dir, name.ToString())) return false;

        // Zeigerbloecke vor Truncate einsammeln: Truncate nullt nur die Zeiger auf Datenbloecke, der Baum bleibt lesbar.
        uint blockSize = GetBlockSize(superblock);
        List<uint> indirect = Ext2IndirectBlocks.Collect(GetBlock(child), blockSize, GetBlocksCount(superblock),
            block =>
            {
                byte[] buffer = new byte[blockSize];
                ReadBlocks(superblock, block, 1, buffer);
                return buffer;
            });
        Truncate(superblock, child, 0);
        foreach (uint block in indirect) FreeBlock(superblock, block);
        uint[] pointers = GetBlock(child);
        for (int index = Ext2IndirectBlocks.SingleIndirectIndex; index < pointers.Length; index++) pointers[index] = 0;
        SetSectors(child, 0);
        SetLinksCount(child, 0);
        // Die Freigabe ueber den ZehOS-FreeInode-Plug schreibt links_count 0 und dtime ins Inode auf der Platte.
        WriteInode(superblock, child);
        uint inodeNumber = GetInodeNumber(child);
        FreeInode(superblock, inodeNumber);

        // Wie das Original: ein Unterverzeichnis weniger heisst ein Link weniger am Elternteil und ein Verzeichnis weniger
        // in der Gruppe des entfernten Inodes.
        SetLinksCount(dir, (ushort)(GetLinksCount(dir) - 1));
        WriteInode(superblock, dir);
        uint group = GroupOfInode(superblock, inodeNumber);
        ref ushort usedDirs = ref UsedDirsCount(GetGroup(superblock, group));
        if (usedDirs > 0) usedDirs--;
        WriteUsedDirsCount(operations, group);
        return true;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Superblock")]
    [return: UnsafeAccessorType(Superblock)]
    private static extern object SuperblockOf([UnsafeAccessorType(Inode)] object inode);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WriteUsedDirsCount")]
    private static extern void WriteUsedDirsCount([UnsafeAccessorType(Operations)] object operations, uint group);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BlockSize")]
    private static extern uint GetBlockSize([UnsafeAccessorType(Superblock)] object superblock);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BlocksCount")]
    private static extern uint GetBlocksCount([UnsafeAccessorType(Superblock)] object superblock);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReadBlocks")]
    private static extern void ReadBlocks([UnsafeAccessorType(Superblock)] object superblock, uint ext2Block, uint count, Span<byte> destination);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Truncate")]
    private static extern void Truncate([UnsafeAccessorType(Superblock)] object superblock, [UnsafeAccessorType(Inode)] object inode, ulong newSize);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "FreeBlock")]
    private static extern void FreeBlock([UnsafeAccessorType(Superblock)] object superblock, uint blockNumber);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "FreeInode")]
    private static extern void FreeInode([UnsafeAccessorType(Superblock)] object superblock, uint inodeNumber);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WriteInode")]
    private static extern void WriteInode([UnsafeAccessorType(Superblock)] object superblock, [UnsafeAccessorType(Inode)] object inode);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GroupOfInode")]
    private static extern uint GroupOfInode([UnsafeAccessorType(Superblock)] object superblock, uint inodeNumber);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetGroup")]
    [return: UnsafeAccessorType(GroupDesc)]
    private static extern object GetGroup([UnsafeAccessorType(Superblock)] object superblock, uint groupIndex);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "UsedDirsCount")]
    private static extern ref ushort UsedDirsCount([UnsafeAccessorType(GroupDesc)] object descriptor);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Block")]
    private static extern uint[] GetBlock([UnsafeAccessorType(Inode)] object inode);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_InodeNumber")]
    private static extern uint GetInodeNumber([UnsafeAccessorType(Inode)] object inode);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LinksCount")]
    private static extern ushort GetLinksCount([UnsafeAccessorType(Inode)] object inode);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_LinksCount")]
    private static extern void SetLinksCount([UnsafeAccessorType(Inode)] object inode, ushort value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Blocks")]
    private static extern void SetSectors([UnsafeAccessorType(Inode)] object inode, uint value);
}
