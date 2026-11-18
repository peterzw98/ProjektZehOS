using System;
using System.Runtime.CompilerServices;
using Cosmos.Build.API.Attributes;
using ProjektZeh.Storage.Ext2;

namespace ProjektZeh.Storage;

// shortcut: Cosmos 3.0.97 Truncate gibt nur Datenbloecke frei; Zeigerbloecke bleiben belegt ("Emptied indirect blocks
// leak", Ext2Superblock.cs:1043). E4.0 2G Lauf 1: ~520 Bloecke 'Block bitmap differences' nach dem Loeschen der
// 64-MiB-Fuelldateien. Betrifft Unlink, Rmdir und jede Verkuerzung (SetAttr Size). Zuweisung agent-coordinator 18.11.;
// Freigabe Peter steht aus. Nach einem korrigierten Cosmos-Release entfernen.
// Liegt ausserhalb von Storage/Ext2/, weil die Hostprobe jene Quellen ohne Cosmos-Build-API einbindet.
[Plug(TargetName = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Superblock")]
public static class CosmosExt2TruncatePlug
{
    // Der Patcher vergleicht Parametertypen nach vollem Namen (gleichnamige Attrappe Ext2Inode, CosmosExt2PlugTypes.cs);
    // der Rumpf reicht sie nur als object weiter und nennt keinen Typ aus Cosmos.Kernel.System.
    [PlugMember]
    public static void Truncate(object aThis, Cosmos.Kernel.System.FileSystem.Ext2.Ext2Inode inode, ulong newSize) =>
        CosmosExt2Truncate.Run(aThis, inode, newSize);
}

/// <summary>
/// Ersatz fuer Cosmos Ext2Superblock.Truncate (Ext2Superblock.cs:1049-1076): derselbe Ablauf fuer die Datenbloecke, danach
/// die Zeigerbloecke, die hinter der neuen Laenge ganz frei werden (<see cref="Ext2IndirectBlocks.PlanTruncate"/>).
/// </summary>
internal static class CosmosExt2Truncate
{
    private const string Superblock = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Superblock, Cosmos.Kernel.System";
    private const string Inode = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2Inode, Cosmos.Kernel.System";

    internal static void Run(object superblock, object inode, ulong newSize)
    {
        uint blockSize = GetBlockSize(superblock);
        uint oldBlocks = (uint)((GetFullSize(inode) + blockSize - 1) / blockSize);
        uint newBlocks = newSize == 0 ? 0 : (uint)((newSize + blockSize - 1) / blockSize);

        // Plan vor dem Freigeben der Datenbloecke: der Zeigerbaum ist dann noch unveraendert lesbar.
        uint[] pointers = GetBlock(inode);
        var plan = newBlocks < oldBlocks
            ? Ext2IndirectBlocks.PlanTruncate(pointers, newBlocks, blockSize, GetBlocksCount(superblock), block =>
            {
                byte[] buffer = new byte[blockSize];
                ReadBlocks(superblock, block, 1, buffer);
                return buffer;
            })
            : null;

        // Wie das Original: jeden Datenblock hinter der neuen Laenge freigeben und seinen Zeiger nullen.
        for (uint index = newBlocks; index < oldBlocks; index++)
        {
            uint block = GetBlockPointer(superblock, inode, index, false, out _);
            if (block == 0) continue;
            FreeBlock(superblock, block);
            SetBlockPointer(superblock, inode, index, 0);
            SetSectors(inode, GetSectors(inode) - blockSize / 512);
        }

        if (plan != null)
        {
            // Erst die Eintraege in verbleibenden Zeigerbloecken nullen, dann die Bloecke freigeben: ein Abbruch dazwischen
            // hinterlaesst hoechstens ein Platzleck, nie einen Zeiger auf einen freien Block.
            foreach (var (block, index) in plan.ClearEntries)
            {
                byte[] buffer = new byte[blockSize];
                ReadBlocks(superblock, block, 1, buffer);
                BitConverter.TryWriteBytes(buffer.AsSpan(index * 4, 4), 0u);
                WriteBlocks(superblock, block, 1, buffer);
            }

            foreach (int root in plan.ClearRoots) pointers[root] = 0;
            foreach (uint block in plan.Free)
            {
                FreeBlock(superblock, block);
                SetSectors(inode, GetSectors(inode) - blockSize / 512);
            }
        }

        SetSize(inode, (uint)(newSize & 0xFFFF_FFFF));
        SetSizeHigh(inode, (uint)(newSize >> 32));
        WriteInode(superblock, inode);
        InvalidateIndirCache(superblock);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BlockSize")]
    private static extern uint GetBlockSize([UnsafeAccessorType(Superblock)] object superblock);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BlocksCount")]
    private static extern uint GetBlocksCount([UnsafeAccessorType(Superblock)] object superblock);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetBlockPointer")]
    private static extern uint GetBlockPointer([UnsafeAccessorType(Superblock)] object superblock, [UnsafeAccessorType(Inode)] object inode,
        uint logicalBlock, bool allocate, out bool isNew);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetBlockPointer")]
    private static extern void SetBlockPointer([UnsafeAccessorType(Superblock)] object superblock, [UnsafeAccessorType(Inode)] object inode,
        uint logicalBlock, uint value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "FreeBlock")]
    private static extern void FreeBlock([UnsafeAccessorType(Superblock)] object superblock, uint blockNumber);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReadBlocks")]
    private static extern void ReadBlocks([UnsafeAccessorType(Superblock)] object superblock, uint ext2Block, uint count, Span<byte> destination);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WriteBlocks")]
    private static extern void WriteBlocks([UnsafeAccessorType(Superblock)] object superblock, uint ext2Block, uint count, ReadOnlySpan<byte> data);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "WriteInode")]
    private static extern void WriteInode([UnsafeAccessorType(Superblock)] object superblock, [UnsafeAccessorType(Inode)] object inode);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "InvalidateIndirCache")]
    private static extern void InvalidateIndirCache([UnsafeAccessorType(Superblock)] object superblock);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FullSize")]
    private static extern ulong GetFullSize([UnsafeAccessorType(Inode)] object inode);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Block")]
    private static extern uint[] GetBlock([UnsafeAccessorType(Inode)] object inode);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Blocks")]
    private static extern uint GetSectors([UnsafeAccessorType(Inode)] object inode);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Blocks")]
    private static extern void SetSectors([UnsafeAccessorType(Inode)] object inode, uint value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Size")]
    private static extern void SetSize([UnsafeAccessorType(Inode)] object inode, uint value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_SizeHigh")]
    private static extern void SetSizeHigh([UnsafeAccessorType(Inode)] object inode, uint value);
}
