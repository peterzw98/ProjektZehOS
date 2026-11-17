// FreeInode-Ablauf nach Cosmos src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Superblock.cs:617-632 (efcf20cc, 3.0.97),
// BSD-3-Clause, siehe Storage/Ext2/LICENSE-Cosmos.txt. Ergänzt um das Löschen des Inodes auf der Platte.
using System;
using System.Collections;
using System.Runtime.CompilerServices;
using Cosmos.Build.API.Attributes;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage;

// shortcut: Cosmos 3.0.97 Rmdir schreibt das freigegebene Inode nie und Unlink setzt kein dtime (Storage/Ext2/README.md,
// docs/COSMOS_EXT2_UPSTREAM_ISSUE.md); nach einem korrigierten Cosmos-Release entfernen.
[Plug(TargetName = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Superblock")]
public static class CosmosExt2FreeInodePlug
{
    // Der Körper wird nach Cosmos.Kernel.System kopiert und darf keinen Typ von dort nennen (vgl. CosmosExt2FormatterPlug).
    [PlugMember]
    public static void FreeInode(object aThis, uint inodeNumber) => CosmosExt2InodeRelease.Free(aThis, inodeNumber);
}

internal static class CosmosExt2InodeRelease
{
    private const string Superblock = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Superblock, Cosmos.Kernel.System";
    private const string GroupDesc = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2GroupDesc, Cosmos.Kernel.System";
    private const string InodeCacheType = "System.Collections.Generic.Dictionary`2[[System.UInt32, System.Private.CoreLib],"
        + "[Cosmos.Kernel.System.FileSystem.Ext2.Ext2Inode, Cosmos.Kernel.System]], System.Private.CoreLib";

    internal static void Free(object superblock, uint inodeNumber)
    {
        uint group = GroupOfInode(superblock, inodeNumber);
        uint index = IndexInGroup(superblock, inodeNumber);
        object descriptor = GetGroup(superblock, group);
        uint blockSize = GetBlockSize(superblock);

        // Änderung: Inode zuerst auf der Platte als gelöscht markieren (links_count 0, dtime), wie e2fsck es erwartet.
        // Rmdir hat es bis hierhin nur im Speicher geändert, Unlink ohne dtime geschrieben.
        uint inodeSize = GetInodeSize(superblock);
        uint inodesPerBlock = blockSize / inodeSize;
        uint tableBlock = InodeTable(descriptor) + index / inodesPerBlock;
        int offset = (int)(index % inodesPerBlock * inodeSize);
        byte[] table = new byte[blockSize];
        ReadBlocks(superblock, tableBlock, 1, table);
        BitConverter.TryWriteBytes(table.AsSpan(offset + CosmosExt2InodeLayout.LinksCountOffset, 2), (ushort)0);
        // dtime 0 hiesse für e2fsck "nicht gelöscht"; ohne Uhr wenigstens 1.
        uint now = (uint)Math.Max(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        BitConverter.TryWriteBytes(table.AsSpan(offset + CosmosExt2InodeLayout.DtimeOffset, 4), now);
        WriteBlocks(superblock, tableBlock, 1, table);

        // Ab hier unverändert wie Cosmos: Bitmapbit löschen, Zähler und Deskriptor fortschreiben, Cache leeren.
        byte[] bitmap = new byte[blockSize];
        ReadBlocks(superblock, InodeBitmap(descriptor), 1, bitmap);
        bitmap[index / 8] &= (byte)~(1u << (int)(index % 8));
        WriteBlocks(superblock, InodeBitmap(descriptor), 1, bitmap);
        FreeInodesInGroup(descriptor)++;
        UpdateGroupDesc(superblock, group);
        SetFreeInodesCount(superblock, GetFreeInodesCount(superblock) + 1);
        ((IDictionary)InodeCache(superblock)).Remove(inodeNumber);
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

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_inodeCache")]
    [return: UnsafeAccessorType(InodeCacheType)]
    private static extern ref object InodeCache([UnsafeAccessorType(Superblock)] object superblock);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "InodeBitmap")]
    private static extern ref uint InodeBitmap([UnsafeAccessorType(GroupDesc)] object descriptor);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "InodeTable")]
    private static extern ref uint InodeTable([UnsafeAccessorType(GroupDesc)] object descriptor);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "FreeInodesCount")]
    private static extern ref ushort FreeInodesInGroup([UnsafeAccessorType(GroupDesc)] object descriptor);
}
