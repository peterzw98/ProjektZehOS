using System;
using System.Runtime.CompilerServices;
using Cosmos.Build.API.Attributes;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage;

// shortcut: Cosmos 3.0.97 Rmdir schreibt das freigegebene Inode nie, Unlink setzt kein dtime (docs/COSMOS_EXT2_UPSTREAM_ISSUE.md);
// nach einem korrigierten Cosmos-Release entfernen.
[Plug(TargetName = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Superblock")]
public static class CosmosExt2FreeInodePlug
{
    // Der Koerper wird nach Cosmos.Kernel.System kopiert und darf keinen Typ von dort nennen (vgl. CosmosExt2FormatterPlug).
    [PlugMember]
    public static void FreeInode(object aThis, uint inodeNumber) => CosmosExt2InodeRelease.Free(aThis, inodeNumber);
}

/// <summary>Verbindet Ext2InodeRelease mit dem internen Zustand von Cosmos' Ext2Superblock (Original: Ext2Superblock.cs:617-632).</summary>
internal static class CosmosExt2InodeRelease
{
    private const string Superblock = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Superblock, Cosmos.Kernel.System";
    private const string GroupDesc = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2GroupDesc, Cosmos.Kernel.System";

    internal static void Free(object superblock, uint inodeNumber)
    {
        uint group = GroupOfInode(superblock, inodeNumber);
        object descriptor = GetGroup(superblock, group);
        uint blockSize = GetBlockSize(superblock);
        bool released = Ext2InodeRelease.Release(
            block =>
            {
                byte[] buffer = new byte[blockSize];
                ReadBlocks(superblock, block, 1, buffer);
                return buffer;
            },
            (block, buffer) => WriteBlocks(superblock, block, 1, buffer),
            InodeTable(descriptor), InodeBitmap(descriptor), IndexInGroup(superblock, inodeNumber),
            blockSize, GetInodeSize(superblock), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (released)
        {
            FreeInodesInGroup(descriptor)++;
            UpdateGroupDesc(superblock, group);
            SetFreeInodesCount(superblock, GetFreeInodesCount(superblock) + 1);
        }

        // shortcut: der Cache-Eintrag bleibt stehen (Cosmos: _inodeCache.Remove). Der Zugriff auf das generische
        // Dictionary per UnsafeAccessorType warf im Gast NotSupportedException (85b1484). Unschaedlich, weil ein
        // freigegebenes Inode keinen Verzeichniseintrag mehr hat und jede Neuvergabe (Create/Mkdir/Symlink) das Inode per
        // WriteInode in den Cache schreibt, bevor es gelesen wird (Ext2InodeOperations.cs:153-174, 209-256, 309-366).
        // Bei einem Cosmos-Fix des Treibers mit dem Plug entfernen.
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GroupOfInode")]
    private static extern uint GroupOfInode([UnsafeAccessorType(Superblock)] object superblock, uint inodeNumber);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "IndexInGroup")]
    private static extern uint IndexInGroup([UnsafeAccessorType(Superblock)] object superblock, uint inodeNumber);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetGroup")]
    [return: UnsafeAccessorType(GroupDesc)]
    private static extern object GetGroup([UnsafeAccessorType(Superblock)] object superblock, uint groupIndex);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BlockSize")]
    private static extern uint GetBlockSize([UnsafeAccessorType(Superblock)] object superblock);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_InodeSize")]
    private static extern ushort GetInodeSize([UnsafeAccessorType(Superblock)] object superblock);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReadBlocks")]
    private static extern void ReadBlocks([UnsafeAccessorType(Superblock)] object superblock, uint ext2Block, uint count, Span<byte> destination);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WriteBlocks")]
    private static extern void WriteBlocks([UnsafeAccessorType(Superblock)] object superblock, uint ext2Block, uint count, ReadOnlySpan<byte> data);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "UpdateGroupDesc")]
    private static extern void UpdateGroupDesc([UnsafeAccessorType(Superblock)] object superblock, uint groupIndex);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FreeInodesCount")]
    private static extern uint GetFreeInodesCount([UnsafeAccessorType(Superblock)] object superblock);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_FreeInodesCount")]
    private static extern void SetFreeInodesCount([UnsafeAccessorType(Superblock)] object superblock, uint value);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "InodeBitmap")]
    private static extern ref uint InodeBitmap([UnsafeAccessorType(GroupDesc)] object descriptor);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "InodeTable")]
    private static extern ref uint InodeTable([UnsafeAccessorType(GroupDesc)] object descriptor);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "FreeInodesCount")]
    private static extern ref ushort FreeInodesInGroup([UnsafeAccessorType(GroupDesc)] object descriptor);
}
