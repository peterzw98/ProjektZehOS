// This code is licensed under the BSD 3-Clause license (see LICENSE-Cosmos.txt for details)
// Abgeleitet von Cosmos src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Formatter.cs
// (Commit efcf20cc62884b042a0adadffe9fc4540491fd89, Paket 3.0.97). Änderungen siehe README.md in diesem Ordner.

using System;
using Cosmos.Kernel.HAL.Devices.Storage;
using Cosmos.Kernel.System.FileSystem.Ext2;

namespace ProjektZeh.Storage.Ext2;

/// <summary>
/// Lays down a fresh ext2 volume on an <see cref="IBlockDevice"/>: writes
/// the superblock, group descriptors with bitmaps and inode tables, and a
/// root directory holding "." and "..". Pure I/O against the device — no
/// superblock or VFS state.
/// </summary>
internal static class CosmosExt2Formatter
{
    /// <summary>Smallest volume (in filesystem blocks) <see cref="Format"/> will attempt.</summary>
    private const uint MinTotalBlocks = 16;

    /// <summary>Blocks per group for 1024-byte volumes (8 MiB groups).</summary>
    private const uint BlocksPerGroup1K = 8192u;

    /// <summary>Blocks per group for 2048-byte volumes (8 MiB groups).</summary>
    private const uint BlocksPerGroup2K = 4096u;

    /// <summary>Blocks per group for 4096-byte volumes (8 MiB groups).</summary>
    private const uint BlocksPerGroup4K = 2048u;

    /// <summary>Inodes per group, derived as one quarter of the blocks per group.</summary>
    private const uint InodesPerBlockRatio = 4;

    /// <summary>Smallest inode count per group; keeps tiny volumes usable.</summary>
    private const uint MinInodesPerGroup = 16;

    /// <summary>Inodes 1 through 10 are reserved; the root takes inode 2.</summary>
    private const uint ReservedInodeCount = 10;

    /// <summary>Filesystem state stamped at format time: cleanly unmounted.</summary>
    private const ushort ValidState = 1;

    /// <summary>Error behavior stamped at format time: continue on errors.</summary>
    private const ushort ErrorsContinue = 1;

    /// <summary>Creator OS stamped at format time: Linux.</summary>
    private const uint CreatorLinux = 0;

    /// <summary>Inode size stamped at format time (128 bytes).</summary>
    private const int Rev0InodeSize = 128;

    /// <summary>Incompatible feature flag stamped at format time: directory entries carry a file type.</summary>
    private const uint FeatureIncompatFiletype = 2;

    // Änderung: Sicherungskopien nur in Gruppe 0, 1 und Potenzen von 3, 5, 7 (wie mke2fs).
    /// <summary>Read-only compatible feature flag stamped at format time: sparse superblock backups.</summary>
    private const uint FeatureRoCompatSparseSuper = 1;

    /// <summary>Largest device the formatter accepts (128 GiB, qualified target range).</summary>
    private const ulong MaxVolumeBytes = 128UL * 1024 * 1024 * 1024;

    /// <summary>Longest volume label in characters.</summary>
    private const int MaxVolumeLabelLength = 16;

    /// <summary>
    /// Produktionsprofil für Kernel und Plug: nur 4096-Byte-Blöcke (null =&gt; 4096). Andere Blockgrößen
    /// werden vor jedem Schreibzugriff abgelehnt, weil der Cosmos-3.0.97-Treiber 1K-Volumes falsch adressiert
    /// und 2K nicht qualifiziert ist.
    /// </summary>
    /// <param name="device">Block device to format.</param>
    /// <param name="options">Label and block size; the block size must be 4096 when given.</param>
    /// <returns>true when the volume was written.</returns>
    public static bool FormatForKernel(IBlockDevice device, Ext2FormatOptions? options)
    {
        if (options != null && options.BlockSize != 4096)
        {
            return false;
        }

        return Format(device, options);
    }

    /// <summary>
    /// Format <paramref name="device"/> with a fresh ext2 volume.
    /// </summary>
    /// <param name="device">Block device to format.</param>
    /// <param name="options">Block size and label; null selects 4096-byte blocks.</param>
    /// <returns>true when the volume was written.</returns>
    public static bool Format(IBlockDevice device, Ext2FormatOptions? options)
    {
        // Änderung: Standard 4096 statt 1024. Der Cosmos-3.0.97-Treiber rechnet Blocknummern ohne
        // first_data_block und verträgt korrekt formatierte 1K-Volumes deshalb nicht (README.md).
        uint blockSize = options?.BlockSize ?? 4096;
        if (blockSize is not (1024 or 2048 or 4096))
        {
            return false;
        }

        uint logBlockSize = blockSize switch
        {
            2048 => 1u,
            4096 => 2u,
            _ => 0u,
        };

        ulong deviceBlockSize = device.BlockSize;
        if (deviceBlockSize == 0 || blockSize % deviceBlockSize != 0)
        {
            return false;
        }

        // Änderung: Zielumfang höchstens 128 GiB; die Division verhindert den Überlauf von BlockCount * BlockSize.
        if (device.BlockCount > MaxVolumeBytes / deviceBlockSize)
        {
            return false;
        }

        ulong totalBytes = device.BlockCount * deviceBlockSize;
        uint totalBlocks = (uint)(totalBytes / blockSize);
        if (totalBlocks < MinTotalBlocks)
        {
            return false;
        }

        // Änderung: Bei 1K-Blöcken liegt Block 0 vor der ersten Gruppe; Gruppe g beginnt bei
        // firstDataBlock + g * blocksPerGroup, und Bit b ihrer Bitmap steht für Block groupStart + b.
        uint firstDataBlock = blockSize == 1024 ? 1u : 0u;

        // Groups stay near 8 MiB regardless of block size; tiny devices
        // collapse to a single group.
        uint blocksPerGroup = blockSize switch
        {
            1024 => BlocksPerGroup1K,
            2048 => BlocksPerGroup2K,
            _ => BlocksPerGroup4K,
        };
        if (blocksPerGroup >= totalBlocks - firstDataBlock)
        {
            // Änderung: e2fsck prüft das Bitmap-Ende byteweise; blocksPerGroup muss ein Vielfaches von 8 sein.
            // Bis zu 7 Restblöcke bleiben dafür ungenutzt.
            blocksPerGroup = (totalBlocks - firstDataBlock) & ~7u;
            totalBlocks = firstDataBlock + blocksPerGroup;
        }

        // Änderung: ganze Inode-Tabellenblöcke; Inodes je Block (8/16/32) sind zugleich Vielfache von 8.
        uint inodesPerBlock = blockSize / Rev0InodeSize;
        uint inodesPerGroup = Math.Max(blocksPerGroup / InodesPerBlockRatio, MinInodesPerGroup);
        inodesPerGroup = (inodesPerGroup + inodesPerBlock - 1) / inodesPerBlock * inodesPerBlock;
        uint inodeTableBlocksPerGroup = inodesPerGroup / inodesPerBlock;

        uint groups = (totalBlocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup;
        uint descriptorBlocks = DescriptorBlocks(groups, blockSize);

        // Änderung: Eine letzte Gruppe, die nicht einmal ihre Metadaten fasst, wird wie bei mke2fs
        // abgeschnitten, statt das ganze Formatieren abzulehnen.
        uint lastStart = firstDataBlock + (groups - 1) * blocksPerGroup;
        if (groups > 1 && totalBlocks - lastStart <= MetadataBlocks(groups - 1, descriptorBlocks, inodeTableBlocksPerGroup))
        {
            totalBlocks = lastStart;
            groups--;
            descriptorBlocks = DescriptorBlocks(groups, blockSize);
        }

        uint totalInodes = inodesPerGroup * groups;
        uint freeBlocks = 0;
        for (uint g = 0; g < groups; g++)
        {
            uint used = MetadataBlocks(g, descriptorBlocks, inodeTableBlocksPerGroup);
            uint blocksInGroup = GroupBlocks(g, firstDataBlock, blocksPerGroup, totalBlocks);
            if (used > blocksInGroup)
            {
                return false;
            }

            freeBlocks += blocksInGroup - used;
        }

        uint freeInodes = totalInodes - ReservedInodeCount;
        if (freeBlocks == 0 || freeInodes == 0)
        {
            return false;
        }

        byte[] superblock = new byte[CosmosExt2SuperblockLayout.SuperblockSize];
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.InodesCountOffset, 4), totalInodes);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.BlocksCountOffset, 4), totalBlocks);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.RBlocksCountOffset, 4), (uint)0);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.FreeBlocksCountOffset, 4), freeBlocks);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.FreeInodesCountOffset, 4), freeInodes);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.FirstDataBlockOffset, 4), firstDataBlock);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.LogBlockSizeOffset, 4), logBlockSize);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.LogFragSizeOffset, 4), logBlockSize);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.BlocksPerGroupOffset, 4), blocksPerGroup);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.FragsPerGroupOffset, 4), blocksPerGroup);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.InodesPerGroupOffset, 4), inodesPerGroup);
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.MtimeOffset, 4), now);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.WtimeOffset, 4), now);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.MagicOffset, 2), CosmosExt2SuperblockLayout.Magic);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.StateOffset, 2), ValidState);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.ErrorsOffset, 2), ErrorsContinue);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.CreatorOsOffset, 4), CreatorLinux);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.RevLevelOffset, 4), CosmosExt2SuperblockLayout.RevDynamic);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.FirstInoOffset, 4), CosmosExt2SuperblockLayout.DefaultFirstIno);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.InodeSizeOffset, 2), (ushort)Rev0InodeSize);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.FeatureCompatOffset, 4), (uint)0);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.FeatureIncompatOffset, 4), FeatureIncompatFiletype);
        BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.FeatureRoCompatOffset, 4), FeatureRoCompatSparseSuper);
        string volumeLabel = options?.VolumeLabel ?? "";
        if (volumeLabel.Length > MaxVolumeLabelLength)
        {
            volumeLabel = volumeLabel.Substring(0, MaxVolumeLabelLength);
        }

        global::System.Text.Encoding.ASCII.GetBytes(volumeLabel).CopyTo(superblock.AsSpan(CosmosExt2SuperblockLayout.VolumeNameOffset, volumeLabel.Length));

        // Änderung: Metadaten liegen in jeder Gruppe zusammenhängend am Gruppenanfang:
        // [Superblock + Deskriptortabelle, nur in Gruppen mit Sicherung] Blockbitmap, Inodebitmap,
        // Inodetabelle, in Gruppe 0 danach der Wurzelverzeichnisblock.
        byte[] descriptorTable = new byte[descriptorBlocks * blockSize];
        uint[] inodeTableStarts = new uint[groups];
        byte[] zeroTable = new byte[inodeTableBlocksPerGroup * blockSize];
        for (uint g = 0; g < groups; g++)
        {
            uint groupStart = firstDataBlock + g * blocksPerGroup;
            uint groupBlocks = GroupBlocks(g, firstDataBlock, blocksPerGroup, totalBlocks);
            uint usedBlocks = MetadataBlocks(g, descriptorBlocks, inodeTableBlocksPerGroup);
            uint blockBitmapBlock = groupStart + (HasSuperblockBackup(g) ? 1 + descriptorBlocks : 0);
            uint inodeBitmapBlock = blockBitmapBlock + 1;
            uint inodeTableStart = inodeBitmapBlock + 1;
            inodeTableStarts[g] = inodeTableStart;

            // Änderung: Bits hinter dem Gruppenende bis zum Ende des Bitmapblocks sind gesetzt (Padding).
            byte[] blockBitmap = new byte[blockSize];
            SetBits(blockBitmap, 0, usedBlocks);
            SetBits(blockBitmap, groupBlocks, blockSize * 8);
            WriteBlocks(device, blockBitmapBlock, 1, blockSize, blockBitmap);

            // Reserved inodes 1..10 (including the root at 2) start allocated.
            byte[] inodeBitmap = new byte[blockSize];
            uint reservedHere = g == 0 ? ReservedInodeCount : 0;
            SetBits(inodeBitmap, 0, reservedHere);
            SetBits(inodeBitmap, inodesPerGroup, blockSize * 8);
            WriteBlocks(device, inodeBitmapBlock, 1, blockSize, inodeBitmap);

            WriteBlocks(device, inodeTableStart, inodeTableBlocksPerGroup, blockSize, zeroTable);

            int descriptorOffset = (int)g * CosmosExt2SuperblockLayout.GroupDescSize;
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + CosmosExt2SuperblockLayout.GroupDescBlockBitmapOffset, 4), blockBitmapBlock);
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + CosmosExt2SuperblockLayout.GroupDescInodeBitmapOffset, 4), inodeBitmapBlock);
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + CosmosExt2SuperblockLayout.GroupDescInodeTableOffset, 4), inodeTableStart);
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + CosmosExt2SuperblockLayout.GroupDescFreeBlocksCountOffset, 2), (ushort)(groupBlocks - usedBlocks));
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + CosmosExt2SuperblockLayout.GroupDescFreeInodesCountOffset, 2), (ushort)(inodesPerGroup - reservedHere));
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + CosmosExt2SuperblockLayout.GroupDescUsedDirsCountOffset, 2), (ushort)(g == 0 ? 1 : 0));
        }

        // Inode 2 is the root: index 1 within group 0's table.
        uint rootIndex = 1;
        uint rootTable = inodeTableStarts[0];
        uint tableBlock = rootIndex / inodesPerBlock;
        uint offsetInBlock = (rootIndex % inodesPerBlock) * Rev0InodeSize;
        byte[] inodeBlock = new byte[blockSize];
        ReadBlocks(device, rootTable + tableBlock, 1, blockSize, inodeBlock);
        Span<byte> rootInode = inodeBlock.AsSpan((int)offsetInBlock, Rev0InodeSize);
        const ushort RootMode = (ushort)(CosmosExt2InodeLayout.IFDIR | 0x1FF);
        const ushort RootLinks = 2;
        BitConverter.TryWriteBytes(rootInode.Slice(CosmosExt2InodeLayout.ModeOffset, 2), RootMode);
        BitConverter.TryWriteBytes(rootInode.Slice(CosmosExt2InodeLayout.UidOffset, 2), (ushort)0);
        BitConverter.TryWriteBytes(rootInode.Slice(CosmosExt2InodeLayout.SizeOffset, 4), blockSize);
        BitConverter.TryWriteBytes(rootInode.Slice(CosmosExt2InodeLayout.AtimeOffset, 4), now);
        BitConverter.TryWriteBytes(rootInode.Slice(CosmosExt2InodeLayout.CtimeOffset, 4), now);
        BitConverter.TryWriteBytes(rootInode.Slice(CosmosExt2InodeLayout.MtimeOffset, 4), now);
        BitConverter.TryWriteBytes(rootInode.Slice(CosmosExt2InodeLayout.GidOffset, 2), (ushort)0);
        BitConverter.TryWriteBytes(rootInode.Slice(CosmosExt2InodeLayout.LinksCountOffset, 2), RootLinks);
        BitConverter.TryWriteBytes(rootInode.Slice(CosmosExt2InodeLayout.BlocksOffset, 4), blockSize / 512);
        uint rootBlock = inodeTableStarts[0] + inodeTableBlocksPerGroup;
        BitConverter.TryWriteBytes(rootInode.Slice(CosmosExt2InodeLayout.BlockOffset, 4), rootBlock);
        WriteBlocks(device, rootTable + tableBlock, 1, blockSize, inodeBlock);

        // The root directory holds "." (itself) and ".." (itself).
        byte[] directoryBlock = new byte[blockSize];
        BitConverter.TryWriteBytes(directoryBlock.AsSpan(CosmosExt2InodeLayout.DirEntryInodeOffset, 4), CosmosExt2SuperblockLayout.RootInodeNumber);
        int dotLength = (CosmosExt2InodeLayout.DirEntryNameOffset + 1 + 3) & ~3;
        BitConverter.TryWriteBytes(directoryBlock.AsSpan(CosmosExt2InodeLayout.DirEntryRecLenOffset, 2), (ushort)dotLength);
        directoryBlock[CosmosExt2InodeLayout.DirEntryNameLenOffset] = 1;
        directoryBlock[CosmosExt2InodeLayout.DirEntryFileTypeOffset] = CosmosExt2InodeLayout.FileTypeDir;
        directoryBlock[CosmosExt2InodeLayout.DirEntryNameOffset] = (byte)'.';
        int dotDotOffset = dotLength;
        BitConverter.TryWriteBytes(directoryBlock.AsSpan(dotDotOffset + CosmosExt2InodeLayout.DirEntryInodeOffset, 4), CosmosExt2SuperblockLayout.RootInodeNumber);
        ushort dotDotLength = (ushort)(blockSize - dotLength);
        BitConverter.TryWriteBytes(directoryBlock.AsSpan(dotDotOffset + CosmosExt2InodeLayout.DirEntryRecLenOffset, 2), dotDotLength);
        directoryBlock[dotDotOffset + CosmosExt2InodeLayout.DirEntryNameLenOffset] = 2;
        directoryBlock[dotDotOffset + CosmosExt2InodeLayout.DirEntryFileTypeOffset] = CosmosExt2InodeLayout.FileTypeDir;
        directoryBlock[dotDotOffset + CosmosExt2InodeLayout.DirEntryNameOffset] = (byte)'.';
        directoryBlock[dotDotOffset + CosmosExt2InodeLayout.DirEntryNameOffset + 1] = (byte)'.';
        WriteBlocks(device, rootBlock, 1, blockSize, directoryBlock);

        // Änderung: Superblock und Deskriptortabellen zuletzt, erst wenn ihr Inhalt feststeht. Keine
        // Absturzsicherheit: eine ältere Signatur auf dem Gerät wird vorher nicht gelöscht.
        // Sicherungen tragen ihre Gruppennummer in s_block_group_nr.
        for (uint g = groups; g-- > 0;)
        {
            if (!HasSuperblockBackup(g))
            {
                continue;
            }

            uint groupStart = firstDataBlock + g * blocksPerGroup;
            BitConverter.TryWriteBytes(superblock.AsSpan(CosmosExt2SuperblockLayout.BlockGroupNrOffset, 2), (ushort)g);
            WriteBlocks(device, groupStart + 1, descriptorBlocks, blockSize, descriptorTable);
            // Der primäre Superblock liegt immer bei Byte 1024, Sicherungen am Anfang ihres Gruppenblocks.
            ulong superblockOffset = g == 0 ? CosmosExt2SuperblockLayout.SuperblockOffset : (ulong)groupStart * blockSize;
            WriteBytes(device, superblockOffset, superblock);
        }

        device.Flush();
        return true;
    }

    /// <summary>Blocks holding the group descriptor table.</summary>
    private static uint DescriptorBlocks(uint groups, uint blockSize)
        => (groups * (uint)CosmosExt2SuperblockLayout.GroupDescSize + blockSize - 1) / blockSize;

    /// <summary>
    /// Metadata blocks at the head of group <paramref name="group"/>: superblock and descriptor
    /// copies where present, both bitmaps, the inode table and, in group 0, the root directory.
    /// </summary>
    private static uint MetadataBlocks(uint group, uint descriptorBlocks, uint inodeTableBlocks)
        => (HasSuperblockBackup(group) ? 1 + descriptorBlocks : 0) + 2 + inodeTableBlocks + (group == 0 ? 1u : 0u);

    /// <summary>Blocks in group <paramref name="group"/>; only the last group may be shorter.</summary>
    private static uint GroupBlocks(uint group, uint firstDataBlock, uint blocksPerGroup, uint totalBlocks)
        => Math.Min(blocksPerGroup, totalBlocks - firstDataBlock - group * blocksPerGroup);

    /// <summary>sparse_super: groups 0, 1 and powers of 3, 5 and 7 carry superblock copies.</summary>
    private static bool HasSuperblockBackup(uint group)
        => group <= 1 || IsPowerOf(group, 3) || IsPowerOf(group, 5) || IsPowerOf(group, 7);

    private static bool IsPowerOf(uint value, uint radix)
    {
        while (value % radix == 0)
        {
            value /= radix;
        }

        return value == 1;
    }

    /// <summary>Set bits [<paramref name="from"/>, <paramref name="to"/>) of a bitmap.</summary>
    private static void SetBits(byte[] bitmap, uint from, uint to)
    {
        for (uint b = from; b < to; b++)
        {
            bitmap[b / 8] |= (byte)(1 << (int)(b % 8));
        }
    }

    /// <summary>
    /// Write bytes at an arbitrary byte offset, reading back the touched
    /// device blocks when the range is unaligned.
    /// </summary>
    /// <param name="device">Block device to write to.</param>
    /// <param name="byteOffset">Byte offset of the first byte.</param>
    /// <param name="data">Bytes to write.</param>
    private static void WriteBytes(IBlockDevice device, ulong byteOffset, ReadOnlySpan<byte> data)
    {
        ulong deviceBlockSize = device.BlockSize;
        ulong lba = byteOffset / deviceBlockSize;
        int offsetInBlock = (int)(byteOffset % deviceBlockSize);
        if (offsetInBlock == 0 && (ulong)data.Length % deviceBlockSize == 0)
        {
            ulong blocks = (ulong)data.Length / deviceBlockSize;
            device.WriteBlock(lba, blocks, data);
        }
        else
        {
            ulong start = lba;
            ulong endByte = byteOffset + (ulong)data.Length;
            ulong endLba = (endByte + deviceBlockSize - 1) / deviceBlockSize;
            ulong count = endLba - start;
            byte[] buffer = new byte[count * deviceBlockSize];
            device.ReadBlock(start, count, buffer);
            data.CopyTo(buffer.AsSpan(offsetInBlock, data.Length));
            device.WriteBlock(start, count, buffer);
        }
    }

    /// <summary>
    /// Write <paramref name="count"/> filesystem blocks.
    /// </summary>
    /// <param name="device">Block device to write to.</param>
    /// <param name="ext2Block">First filesystem block number.</param>
    /// <param name="count">Blocks to write.</param>
    /// <param name="blockSize">Filesystem block size in bytes.</param>
    /// <param name="data">Source bytes.</param>
    private static void WriteBlocks(IBlockDevice device, uint ext2Block, uint count, uint blockSize, ReadOnlySpan<byte> data)
    {
        ulong deviceBlockSize = device.BlockSize;
        ulong byteOffset = (ulong)ext2Block * blockSize;
        ulong lba = byteOffset / deviceBlockSize;
        ulong deviceBlocks = (ulong)count * blockSize / deviceBlockSize;
        device.WriteBlock(lba, deviceBlocks, data);
    }

    /// <summary>
    /// Read <paramref name="count"/> filesystem blocks.
    /// </summary>
    /// <param name="device">Block device to read from.</param>
    /// <param name="ext2Block">First filesystem block number.</param>
    /// <param name="count">Blocks to read.</param>
    /// <param name="blockSize">Filesystem block size in bytes.</param>
    /// <param name="destination">Destination buffer.</param>
    private static void ReadBlocks(IBlockDevice device, uint ext2Block, uint count, uint blockSize, Span<byte> destination)
    {
        ulong deviceBlockSize = device.BlockSize;
        ulong byteOffset = (ulong)ext2Block * blockSize;
        ulong lba = byteOffset / deviceBlockSize;
        ulong deviceBlocks = (ulong)count * blockSize / deviceBlockSize;
        device.ReadBlock(lba, deviceBlocks, destination);
    }
}
