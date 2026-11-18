// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using Cosmos.Kernel.HAL.Devices.Storage;

namespace Cosmos.Kernel.System.FileSystem.Ext2;

/// <summary>
/// Lays down a fresh ext2 volume on an <see cref="IBlockDevice"/>: writes
/// the superblock, group descriptors with bitmaps and inode tables, and a
/// root directory holding "." and "..". Pure I/O against the device — no
/// superblock or VFS state.
/// </summary>
internal static class Ext2Formatter
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

    /// <summary>Read-only compatible feature flag stamped at format time: sparse superblock backups.</summary>
    private const uint FeatureRoCompatSparseSuper = 1;

    /// <summary>Longest volume label in characters.</summary>
    private const int MaxVolumeLabelLength = 16;

    /// <summary>
    /// Format <paramref name="device"/> with a fresh ext2 volume.
    /// </summary>
    /// <param name="device">Block device to format.</param>
    /// <param name="options">Block size and label; null selects 4096-byte blocks.</param>
    /// <returns>true when the volume was written.</returns>
    public static bool Format(IBlockDevice device, Ext2FormatOptions? options)
    {
        // ZehOS patch ext2-formatter, see PATCHES.md
        // A null options object selects 4096: the driver addresses 1 KiB volumes without first_data_block, so a
        // default 1 KiB volume would be written to at the wrong blocks. 1 KiB stays available through explicit options.
        uint blockSize = options?.BlockSize ?? 4096;
        if (blockSize is not (1024 or 2048 or 4096))
        {
            return false;
        }

        // ZehOS patch ext2-formatter: the driver addresses blocks without first_data_block, which a correct 1 KiB
        // volume has (= 1). Refuse until the driver is fixed (optional package E2-P9) instead of creating a volume
        // that is written to at the wrong blocks.
        if (blockSize == 1024)
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

        ulong totalBytes = device.BlockCount * deviceBlockSize;
        if (totalBytes / blockSize > uint.MaxValue)
        {
            return false;
        }

        uint totalBlocks = (uint)(totalBytes / blockSize);
        if (totalBlocks < MinTotalBlocks)
        {
            return false;
        }

        // ZehOS patch ext2-formatter: Bei 1K-Blöcken liegt Block 0 vor der ersten Gruppe; Gruppe g beginnt bei
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
            // ZehOS patch ext2-formatter: e2fsck prüft das Bitmap-Ende byteweise; blocksPerGroup muss ein Vielfaches von 8 sein.
            // Bis zu 7 Restblöcke bleiben dafür ungenutzt.
            blocksPerGroup = (totalBlocks - firstDataBlock) & ~7u;
            totalBlocks = firstDataBlock + blocksPerGroup;
        }

        // ZehOS patch ext2-formatter: ganze Inode-Tabellenblöcke; Inodes je Block (8/16/32) sind zugleich Vielfache von 8.
        uint inodesPerBlock = blockSize / Rev0InodeSize;
        uint inodesPerGroup = Math.Max(blocksPerGroup / InodesPerBlockRatio, MinInodesPerGroup);
        inodesPerGroup = (inodesPerGroup + inodesPerBlock - 1) / inodesPerBlock * inodesPerBlock;
        uint inodeTableBlocksPerGroup = inodesPerGroup / inodesPerBlock;

        uint groups = (totalBlocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup;
        uint descriptorBlocks = DescriptorBlocks(groups, blockSize);

        // ZehOS patch ext2-formatter: Eine letzte Gruppe, die nicht einmal ihre Metadaten fasst, wird wie bei mke2fs
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

        byte[] superblock = new byte[Ext2SuperblockLayout.SuperblockSize];
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.InodesCountOffset, 4), totalInodes);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.BlocksCountOffset, 4), totalBlocks);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.RBlocksCountOffset, 4), (uint)0);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.FreeBlocksCountOffset, 4), freeBlocks);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.FreeInodesCountOffset, 4), freeInodes);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.FirstDataBlockOffset, 4), firstDataBlock);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.LogBlockSizeOffset, 4), logBlockSize);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.LogFragSizeOffset, 4), logBlockSize);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.BlocksPerGroupOffset, 4), blocksPerGroup);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.FragsPerGroupOffset, 4), blocksPerGroup);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.InodesPerGroupOffset, 4), inodesPerGroup);
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.MtimeOffset, 4), now);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.WtimeOffset, 4), now);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.MagicOffset, 2), Ext2SuperblockLayout.Magic);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.StateOffset, 2), ValidState);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.ErrorsOffset, 2), ErrorsContinue);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.CreatorOsOffset, 4), CreatorLinux);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.RevLevelOffset, 4), Ext2SuperblockLayout.RevDynamic);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.FirstInoOffset, 4), Ext2SuperblockLayout.DefaultFirstIno);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.InodeSizeOffset, 2), (ushort)Rev0InodeSize);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.FeatureCompatOffset, 4), (uint)0);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.FeatureIncompatOffset, 4), FeatureIncompatFiletype);
        BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.FeatureRoCompatOffset, 4), FeatureRoCompatSparseSuper);
        string volumeLabel = options?.VolumeLabel ?? "";
        if (volumeLabel.Length > MaxVolumeLabelLength)
        {
            volumeLabel = volumeLabel.Substring(0, MaxVolumeLabelLength);
        }

        global::System.Text.Encoding.ASCII.GetBytes(volumeLabel).CopyTo(superblock.AsSpan(Ext2SuperblockLayout.VolumeNameOffset, volumeLabel.Length));

        // ZehOS patch ext2-formatter: Metadaten liegen in jeder Gruppe zusammenhängend am Gruppenanfang:
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

            // ZehOS patch ext2-formatter: Bits hinter dem Gruppenende bis zum Ende des Bitmapblocks sind gesetzt (Padding).
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

            int descriptorOffset = (int)g * Ext2SuperblockLayout.GroupDescSize;
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + Ext2SuperblockLayout.GroupDescBlockBitmapOffset, 4), blockBitmapBlock);
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + Ext2SuperblockLayout.GroupDescInodeBitmapOffset, 4), inodeBitmapBlock);
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + Ext2SuperblockLayout.GroupDescInodeTableOffset, 4), inodeTableStart);
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + Ext2SuperblockLayout.GroupDescFreeBlocksCountOffset, 2), (ushort)(groupBlocks - usedBlocks));
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + Ext2SuperblockLayout.GroupDescFreeInodesCountOffset, 2), (ushort)(inodesPerGroup - reservedHere));
            BitConverter.TryWriteBytes(descriptorTable.AsSpan(descriptorOffset + Ext2SuperblockLayout.GroupDescUsedDirsCountOffset, 2), (ushort)(g == 0 ? 1 : 0));
        }

        // Inode 2 is the root: index 1 within group 0's table.
        uint rootIndex = 1;
        uint rootTable = inodeTableStarts[0];
        uint tableBlock = rootIndex / inodesPerBlock;
        uint offsetInBlock = (rootIndex % inodesPerBlock) * Rev0InodeSize;
        byte[] inodeBlock = new byte[blockSize];
        ReadBlocks(device, rootTable + tableBlock, 1, blockSize, inodeBlock);
        Span<byte> rootInode = inodeBlock.AsSpan((int)offsetInBlock, Rev0InodeSize);
        const ushort RootMode = (ushort)(Ext2InodeLayout.IFDIR | 0x1FF);
        const ushort RootLinks = 2;
        BitConverter.TryWriteBytes(rootInode.Slice(Ext2InodeLayout.ModeOffset, 2), RootMode);
        BitConverter.TryWriteBytes(rootInode.Slice(Ext2InodeLayout.UidOffset, 2), (ushort)0);
        BitConverter.TryWriteBytes(rootInode.Slice(Ext2InodeLayout.SizeOffset, 4), blockSize);
        BitConverter.TryWriteBytes(rootInode.Slice(Ext2InodeLayout.AtimeOffset, 4), now);
        BitConverter.TryWriteBytes(rootInode.Slice(Ext2InodeLayout.CtimeOffset, 4), now);
        BitConverter.TryWriteBytes(rootInode.Slice(Ext2InodeLayout.MtimeOffset, 4), now);
        BitConverter.TryWriteBytes(rootInode.Slice(Ext2InodeLayout.GidOffset, 2), (ushort)0);
        BitConverter.TryWriteBytes(rootInode.Slice(Ext2InodeLayout.LinksCountOffset, 2), RootLinks);
        BitConverter.TryWriteBytes(rootInode.Slice(Ext2InodeLayout.BlocksOffset, 4), blockSize / 512);
        uint rootBlock = inodeTableStarts[0] + inodeTableBlocksPerGroup;
        BitConverter.TryWriteBytes(rootInode.Slice(Ext2InodeLayout.BlockOffset, 4), rootBlock);
        WriteBlocks(device, rootTable + tableBlock, 1, blockSize, inodeBlock);

        // The root directory holds "." (itself) and ".." (itself).
        byte[] directoryBlock = new byte[blockSize];
        BitConverter.TryWriteBytes(directoryBlock.AsSpan(Ext2InodeLayout.DirEntryInodeOffset, 4), Ext2SuperblockLayout.RootInodeNumber);
        int dotLength = (Ext2InodeLayout.DirEntryNameOffset + 1 + 3) & ~3;
        BitConverter.TryWriteBytes(directoryBlock.AsSpan(Ext2InodeLayout.DirEntryRecLenOffset, 2), (ushort)dotLength);
        directoryBlock[Ext2InodeLayout.DirEntryNameLenOffset] = 1;
        directoryBlock[Ext2InodeLayout.DirEntryFileTypeOffset] = Ext2InodeLayout.FileTypeDir;
        directoryBlock[Ext2InodeLayout.DirEntryNameOffset] = (byte)'.';
        int dotDotOffset = dotLength;
        BitConverter.TryWriteBytes(directoryBlock.AsSpan(dotDotOffset + Ext2InodeLayout.DirEntryInodeOffset, 4), Ext2SuperblockLayout.RootInodeNumber);
        ushort dotDotLength = (ushort)(blockSize - dotLength);
        BitConverter.TryWriteBytes(directoryBlock.AsSpan(dotDotOffset + Ext2InodeLayout.DirEntryRecLenOffset, 2), dotDotLength);
        directoryBlock[dotDotOffset + Ext2InodeLayout.DirEntryNameLenOffset] = 2;
        directoryBlock[dotDotOffset + Ext2InodeLayout.DirEntryFileTypeOffset] = Ext2InodeLayout.FileTypeDir;
        directoryBlock[dotDotOffset + Ext2InodeLayout.DirEntryNameOffset] = (byte)'.';
        directoryBlock[dotDotOffset + Ext2InodeLayout.DirEntryNameOffset + 1] = (byte)'.';
        WriteBlocks(device, rootBlock, 1, blockSize, directoryBlock);

        // ZehOS patch ext2-formatter: Superblock und Deskriptortabellen zuletzt, erst wenn ihr Inhalt feststeht. Keine
        // Absturzsicherheit: eine ältere Signatur auf dem Gerät wird vorher nicht gelöscht.
        // Sicherungen tragen ihre Gruppennummer in s_block_group_nr.
        for (uint g = groups; g-- > 0;)
        {
            if (!HasSuperblockBackup(g))
            {
                continue;
            }

            uint groupStart = firstDataBlock + g * blocksPerGroup;
            BitConverter.TryWriteBytes(superblock.AsSpan(Ext2SuperblockLayout.BlockGroupNrOffset, 2), (ushort)g);
            WriteBlocks(device, groupStart + 1, descriptorBlocks, blockSize, descriptorTable);
            // Der primäre Superblock liegt immer bei Byte 1024, Sicherungen am Anfang ihres Gruppenblocks.
            ulong superblockOffset = g == 0 ? Ext2SuperblockLayout.SuperblockOffset : (ulong)groupStart * blockSize;
            WriteBytes(device, superblockOffset, superblock);
        }

        device.Flush();
        return true;
    }

    /// <summary>Blocks holding the group descriptor table.</summary>
    private static uint DescriptorBlocks(uint groups, uint blockSize)
        => (groups * (uint)Ext2SuperblockLayout.GroupDescSize + blockSize - 1) / blockSize;

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
    /// Wipe the filesystem signature so the volume no longer mounts. The
    /// underlying device is not zeroed in full.
    /// </summary>
    /// <param name="device">Block device holding the volume.</param>
    /// <returns>true when the signature was wiped.</returns>
    public static bool Destroy(IBlockDevice device)
    {
        try
        {
            WriteBytes(device, Ext2SuperblockLayout.SuperblockOffset + Ext2SuperblockLayout.MagicOffset, stackalloc byte[2]);
            device.Flush();
            return true;
        }
        catch
        {
            return false;
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
