// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

namespace Cosmos.Kernel.System.FileSystem.Ext2;

/// <summary>
/// Parser and mutator for ext2 directory blocks. Operates over raw byte
/// spans read through the superblock; no I/O is performed here beyond the
/// block reads the superblock exposes.
/// </summary>
internal static class Ext2DirectoryHelper
{
    /// <summary>Upper bound on a directory's parsed size: larger sizes come from corrupt inodes.</summary>
    internal const ulong MaxDirectorySize = 16 * 1024 * 1024;

    /// <summary>Upper bound on the blocks a single directory spans.</summary>
    internal const uint MaxDirectoryBlocks = 4096;

    /// <summary>
    /// Parse the directory's entries. Stops on the first malformed record
    /// (zero or undersized rec_len, a name longer than its record, or a
    /// record leaving the data) instead of walking into the next block.
    /// The walk ends by the record checks and the data length; free records do not count against a limit.
    /// </summary>
    /// <param name="sb">The mounted volume.</param>
    /// <param name="dir">Directory inode to list.</param>
    /// <param name="entries">Living entries; empty for an empty or non-directory inode.</param>
    /// <returns>false when the directory exceeds <see cref="MaxDirectorySize"/> or <see cref="MaxDirectoryBlocks"/>
    /// and cannot be listed (callers must not treat it as empty).</returns>
    public static bool TryParseDirectory(Ext2Superblock sb, Ext2Inode dir, out List<Ext2DirEntry> entries)
    {
        // ZehOS patch ext2-directory-limit, see PATCHES.md
        List<Ext2DirEntry> result = [];
        entries = result;
        if (!dir.IsDirectory)
        {
            return true;
        }

        ulong size = dir.FullSize;
        if (size == 0)
        {
            return true;
        }

        if (size > MaxDirectorySize)
        {
            return false;
        }

        uint blocks = (uint)((size + sb.BlockSize - 1) / sb.BlockSize);
        if (blocks > MaxDirectoryBlocks)
        {
            return false;
        }

        byte[] data = new byte[blocks * sb.BlockSize];
        uint offset = 0;
        for (uint i = 0; i < blocks; i++)
        {
            uint blk = sb.GetBlockPointer(dir, i, false, out _);
            if (blk == 0)
            {
                // ZehOS patch ext2-rmdir, see PATCHES.md
                // A hole inside i_size would hide every entry behind it; a
                // partial list must not pass for the whole directory (Rmdir
                // would take a non-empty directory for empty).
                return false;
            }

            Span<byte> slice = data.AsSpan((int)offset, (int)sb.BlockSize);
            sb.ReadBlocks(blk, 1, slice);
            offset += sb.BlockSize;
        }

        int pos = 0;
        while (pos + Ext2InodeLayout.DirEntryMinRecLen <= data.Length && (ulong)pos < size)
        {
            if (pos + 8 > data.Length)
            {
                break;
            }

            uint inode = BitConverter.ToUInt32(data.AsSpan(pos + Ext2InodeLayout.DirEntryInodeOffset, 4));
            ushort recLen = BitConverter.ToUInt16(data.AsSpan(pos + Ext2InodeLayout.DirEntryRecLenOffset, 2));
            byte nameLen = data[pos + Ext2InodeLayout.DirEntryNameLenOffset];
            byte fileType = data[pos + Ext2InodeLayout.DirEntryFileTypeOffset];

            // ZehOS patch ext2-rmdir: a corrupt record ends parsing with a
            // failure instead of a silently truncated list.
            if (recLen == 0 || recLen < Ext2InodeLayout.DirEntryMinRecLen || pos + recLen > data.Length)
            {
                return false;
            }

            // A record crossing a block boundary would skip the entries at
            // the start of the next block (Linux: "directory entry across blocks").
            if ((pos % sb.BlockSize) + recLen > sb.BlockSize)
            {
                return false;
            }

            if (nameLen > recLen - Ext2InodeLayout.DirEntryNameOffset)
            {
                return false;
            }

            if (inode != 0)
            {
                int nameLenClamped = Math.Min((int)nameLen, recLen - Ext2InodeLayout.DirEntryNameOffset);
                string name;
                try
                {
                    name = global::System.Text.Encoding.UTF8.GetString(data, pos + Ext2InodeLayout.DirEntryNameOffset, nameLenClamped);
                }
                catch
                {
                    return false;
                }

                result.Add(new Ext2DirEntry(inode, recLen, nameLen, fileType, name, pos));
            }

            pos += recLen;
        }

        return true;
    }

    /// <summary>
    /// Find a child entry by exact (case-sensitive) name. The whole directory is checked even after a hit: callers
    /// that change it must not act on a corrupt one. The first of several equal names wins.
    /// </summary>
    /// <param name="sb">The mounted volume.</param>
    /// <param name="dir">Directory to search.</param>
    /// <param name="name">Child name, a single path component.</param>
    /// <param name="entry">Matching entry on success.</param>
    /// <returns>true when the name exists in <paramref name="dir"/> and the directory is sound.</returns>
    public static bool TryFind(Ext2Superblock sb, Ext2Inode dir, ReadOnlySpan<char> name, out Ext2DirEntry entry)
    {
        return TryFind(sb, dir, name, false, out entry);
    }

    /// <summary>
    /// Find a child entry for reading: ends at the first hit, so damage behind it goes unnoticed. A caller that
    /// changes the directory uses <see cref="TryFind(Ext2Superblock, Ext2Inode, ReadOnlySpan{char}, out Ext2DirEntry)"/>.
    /// </summary>
    /// <param name="sb">The mounted volume.</param>
    /// <param name="dir">Directory to search.</param>
    /// <param name="name">Child name, a single path component.</param>
    /// <param name="entry">Matching entry on success.</param>
    /// <returns>true when the name exists and the directory is sound up to it.</returns>
    public static bool TryFindFirst(Ext2Superblock sb, Ext2Inode dir, ReadOnlySpan<char> name, out Ext2DirEntry entry)
    {
        return TryFind(sb, dir, name, true, out entry);
    }

    // ZehOS patch ext2-dir-scan, see PATCHES.md
    // Compares name bytes in one block buffer: no string and no entry object per record, only for the hit.
    private static bool TryFind(Ext2Superblock sb, Ext2Inode dir, ReadOnlySpan<char> name, bool stopAtHit, out Ext2DirEntry entry)
    {
        entry = null!;
        if (!dir.IsDirectory || !TryGetScanBlocks(sb, dir, out uint blocks) || blocks == 0)
        {
            return false;
        }

        string target = name.ToString();
        byte[] nameBytes = global::System.Text.Encoding.UTF8.GetBytes(target);

        // ReadDir shows bytes that are not UTF-8 as U+FFFD; such an entry stays reachable under the listed name.
        bool compareDecoded = target.Contains('\uFFFD');

        int blockSize = (int)sb.BlockSize;
        ulong size = dir.FullSize;
        byte[] data = new byte[blockSize];
        Ext2DirEntry? found = null;
        for (uint i = 0; i < blocks; i++)
        {
            uint blk = sb.GetBlockPointer(dir, i, false, out _);
            if (blk == 0)
            {
                return false;
            }

            sb.ReadBlocks(blk, 1, data);
            ulong blockStart = (ulong)i * sb.BlockSize;
            int pos = 0;
            while (pos < blockSize && blockStart + (ulong)pos < size)
            {
                if (pos + Ext2InodeLayout.DirEntryMinRecLen > blockSize)
                {
                    // As in TryParseDirectory, a rest too short for a record is tolerated only at the end of the data.
                    if (i + 1 < blocks)
                    {
                        return false;
                    }

                    break;
                }

                if (!TryReadRecord(data, pos, out uint inode, out int recLen, out int nameLen))
                {
                    return false;
                }

                if (inode != 0 && found is null)
                {
                    ReadOnlySpan<byte> entryName = data.AsSpan(pos + Ext2InodeLayout.DirEntryNameOffset, nameLen);
                    if (entryName.SequenceEqual(nameBytes)
                        || (compareDecoded && global::System.Text.Encoding.UTF8.GetString(entryName) == target))
                    {
                        found = new Ext2DirEntry(inode, (ushort)recLen, (byte)nameLen,
                            data[pos + Ext2InodeLayout.DirEntryFileTypeOffset], target, (int)blockStart + pos);
                        if (stopAtHit)
                        {
                            entry = found;
                            return true;
                        }
                    }
                }

                pos += recLen;
            }
        }

        if (found is null)
        {
            return false;
        }

        entry = found;
        return true;
    }

    /// <summary>
    /// Number of blocks a scan of <paramref name="dir"/> covers.
    /// </summary>
    /// <param name="sb">The mounted volume.</param>
    /// <param name="dir">Directory to scan.</param>
    /// <param name="blocks">Block count; 0 for an empty directory.</param>
    /// <returns>false when the directory exceeds <see cref="MaxDirectorySize"/> or <see cref="MaxDirectoryBlocks"/>.</returns>
    private static bool TryGetScanBlocks(Ext2Superblock sb, Ext2Inode dir, out uint blocks)
    {
        blocks = 0;
        ulong size = dir.FullSize;
        if (size > MaxDirectorySize)
        {
            return false;
        }

        blocks = (uint)((size + sb.BlockSize - 1) / sb.BlockSize);
        return blocks <= MaxDirectoryBlocks;
    }

    /// <summary>
    /// Read one record header with the same checks as <see cref="TryParseDirectory"/>.
    /// </summary>
    /// <param name="block">One directory block.</param>
    /// <param name="pos">Record start; at least <see cref="Ext2InodeLayout.DirEntryMinRecLen"/> bytes before the block end.</param>
    /// <param name="inode">Inode number; 0 for a free record.</param>
    /// <param name="recLen">Record length.</param>
    /// <param name="nameLen">Name length in bytes.</param>
    /// <returns>false when the record is corrupt.</returns>
    private static bool TryReadRecord(ReadOnlySpan<byte> block, int pos, out uint inode, out int recLen, out int nameLen)
    {
        inode = BitConverter.ToUInt32(block.Slice(pos + Ext2InodeLayout.DirEntryInodeOffset, 4));
        recLen = BitConverter.ToUInt16(block.Slice(pos + Ext2InodeLayout.DirEntryRecLenOffset, 2));
        nameLen = block[pos + Ext2InodeLayout.DirEntryNameLenOffset];
        return recLen >= Ext2InodeLayout.DirEntryMinRecLen
            && pos + recLen <= block.Length
            && nameLen <= recLen - Ext2InodeLayout.DirEntryNameOffset;
    }

    /// <summary>
    /// Insert an entry into a directory: splits the first entry with enough
    /// slack, reuses a free slot, or appends a fresh block. The directory
    /// size grows by whole blocks. Only the block that changes is written.
    /// </summary>
    /// <param name="sb">The mounted volume.</param>
    /// <param name="dir">Directory receiving the entry.</param>
    /// <param name="name">New entry name (UTF-8, at most 255 bytes).</param>
    /// <param name="inodeNumber">Inode number the entry points at.</param>
    /// <param name="fileType">File type value recorded in the entry.</param>
    /// <returns>true when the entry was inserted.</returns>
    public static bool AddEntry(Ext2Superblock sb, Ext2Inode dir, string name, uint inodeNumber, byte fileType)
    {
        // ZehOS patch ext2-dir-scan, see PATCHES.md
        // One pass checks the whole directory and remembers the first place with room.
        if (name.Length > 255 || !dir.IsDirectory)
        {
            return false;
        }

        byte[] nameBytes = global::System.Text.Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 255)
        {
            return false;
        }

        if (!TryGetScanBlocks(sb, dir, out uint blocks))
        {
            return false;
        }

        int needed = Ext2InodeLayout.DirEntryNameOffset + nameBytes.Length;
        needed = (needed + 3) & ~3;

        int blockSize = (int)sb.BlockSize;
        ulong size = dir.FullSize;
        byte[] data = new byte[blockSize];
        byte[]? slotData = null;
        uint slotBlock = 0;
        int slotPos = 0;

        // ZehOS patch ext2-addentry-guard, see PATCHES.md
        // Callers check for an existing name with TryFind, which also fails
        // on a corrupt directory; adding then would create a duplicate. So
        // nothing is written before every block has passed, holes included.
        for (uint i = 0; i < blocks; i++)
        {
            uint blk = sb.GetBlockPointer(dir, i, false, out _);
            if (blk == 0)
            {
                return false;
            }

            sb.ReadBlocks(blk, 1, data);
            ulong blockStart = (ulong)i * sb.BlockSize;
            bool slotHere = false;
            int pos = 0;
            while (pos < blockSize && blockStart + (ulong)pos < size)
            {
                if (pos + Ext2InodeLayout.DirEntryMinRecLen > blockSize)
                {
                    if (i + 1 < blocks)
                    {
                        return false;
                    }

                    break;
                }

                if (!TryReadRecord(data, pos, out uint inode, out int recLen, out int nameLen))
                {
                    return false;
                }

                if (slotData is null && !slotHere && recLen - UsedLength(inode, nameLen) >= needed)
                {
                    slotHere = true;
                    slotBlock = blk;
                    slotPos = pos;
                }

                pos += recLen;
            }

            if (slotHere)
            {
                // Keep this block; the rest of the directory is checked in a second buffer.
                slotData = data;
                if (i + 1 < blocks)
                {
                    data = new byte[blockSize];
                }
            }
        }

        if (slotData is not null)
        {
            PlaceEntry(slotData, slotPos, nameBytes, inodeNumber, fileType);
            sb.WriteBlocks(slotBlock, 1, slotData);
            return true;
        }

        // No slot has room: append a block whose single entry spans all of it. Past the parse limit the new
        // entry would be invisible to every reader, so the directory refuses to grow.
        if (blocks >= MaxDirectoryBlocks || (blocks + 1UL) * sb.BlockSize > MaxDirectorySize)
        {
            return false;
        }

        if (!AllocateDirBlock(sb, dir))
        {
            return false;
        }

        uint newBlockIdx = blocks;
        byte[] blockData = new byte[sb.BlockSize];
        BitConverter.TryWriteBytes(blockData.AsSpan(Ext2InodeLayout.DirEntryInodeOffset, 4), inodeNumber);
        BitConverter.TryWriteBytes(blockData.AsSpan(Ext2InodeLayout.DirEntryRecLenOffset, 2), (ushort)sb.BlockSize);
        blockData[Ext2InodeLayout.DirEntryNameLenOffset] = (byte)nameBytes.Length;
        blockData[Ext2InodeLayout.DirEntryFileTypeOffset] = fileType;
        nameBytes.CopyTo(blockData.AsSpan(Ext2InodeLayout.DirEntryNameOffset, nameBytes.Length));
        uint newBlock = sb.GetBlockPointer(dir, newBlockIdx, false, out _);
        sb.WriteBlocks(newBlock, 1, blockData);
        dir.Size = (uint)((newBlockIdx + 1) * sb.BlockSize);
        sb.WriteInode(dir);
        return true;
    }

    /// <summary>Bytes of a record its own entry needs; 0 for a free record.</summary>
    /// <param name="inode">Inode number of the record.</param>
    /// <param name="nameLen">Name length in bytes.</param>
    private static int UsedLength(uint inode, int nameLen)
    {
        return inode == 0 ? 0 : (Ext2InodeLayout.DirEntryNameOffset + nameLen + 3) & ~3;
    }

    /// <summary>
    /// Put an entry into the record at <paramref name="pos"/>: behind a live entry, which shrinks to its used
    /// length, or in place of a free record. The record chain of the block stays complete.
    /// </summary>
    /// <param name="block">Directory block to change.</param>
    /// <param name="pos">Record with enough room.</param>
    /// <param name="nameBytes">Name of the new entry.</param>
    /// <param name="inodeNumber">Inode number of the new entry.</param>
    /// <param name="fileType">File type of the new entry.</param>
    private static void PlaceEntry(byte[] block, int pos, byte[] nameBytes, uint inodeNumber, byte fileType)
    {
        uint inode = BitConverter.ToUInt32(block.AsSpan(pos + Ext2InodeLayout.DirEntryInodeOffset, 4));
        int recLen = BitConverter.ToUInt16(block.AsSpan(pos + Ext2InodeLayout.DirEntryRecLenOffset, 2));
        int used = UsedLength(inode, block[pos + Ext2InodeLayout.DirEntryNameLenOffset]);
        if (used > 0)
        {
            BitConverter.TryWriteBytes(block.AsSpan(pos + Ext2InodeLayout.DirEntryRecLenOffset, 2), (ushort)used);
            pos += used;
            recLen -= used;
            BitConverter.TryWriteBytes(block.AsSpan(pos + Ext2InodeLayout.DirEntryRecLenOffset, 2), (ushort)recLen);
        }

        BitConverter.TryWriteBytes(block.AsSpan(pos + Ext2InodeLayout.DirEntryInodeOffset, 4), inodeNumber);
        block[pos + Ext2InodeLayout.DirEntryNameLenOffset] = (byte)nameBytes.Length;
        block[pos + Ext2InodeLayout.DirEntryFileTypeOffset] = fileType;
        Span<byte> rest = block.AsSpan(pos + Ext2InodeLayout.DirEntryNameOffset, recLen - Ext2InodeLayout.DirEntryNameOffset);
        rest.Clear();
        nameBytes.CopyTo(rest);
    }

    /// <summary>
    /// Append one block to a directory and grow its size.
    /// </summary>
    /// <param name="sb">The mounted volume.</param>
    /// <param name="dir">Directory to grow.</param>
    /// <returns>true when a block was allocated and the size updated.</returns>
    private static bool AllocateDirBlock(Ext2Superblock sb, Ext2Inode dir)
    {
        uint idx = (uint)((dir.FullSize + sb.BlockSize - 1) / sb.BlockSize);
        uint blk = sb.GetBlockPointer(dir, idx, true, out _);
        if (blk == 0)
        {
            return false;
        }

        dir.Size = (uint)((idx + 1) * sb.BlockSize);
        sb.WriteInode(dir);
        return true;
    }

    /// <summary>
    /// Remove an entry. A record is merged only into its predecessor in the same directory block (no record may
    /// cross a block boundary); the first record of a block is only freed (inode 0, rec_len kept for reuse by
    /// AddEntry). Exactly the one changed block is written back.
    /// </summary>
    /// <param name="sb">The mounted volume.</param>
    /// <param name="dir">Directory holding the entry.</param>
    /// <param name="name">Name of the entry to remove (UTF-8, exact byte comparison).</param>
    /// <returns>true when the entry was found and removed; false when it is missing or the directory is malformed up to the hit (nothing is written then).</returns>
    public static bool RemoveEntry(Ext2Superblock sb, Ext2Inode dir, string name)
    {
        // ZehOS patch ext2-remove-entry, see PATCHES.md
        if (dir.FullSize == 0 || dir.FullSize > MaxDirectorySize || (dir.FullSize + sb.BlockSize - 1) / sb.BlockSize > MaxDirectoryBlocks)
        {
            // A corrupt inode size must not overflow the block count or request a huge buffer.
            return false;
        }

        int blockSize = (int)sb.BlockSize;
        byte[] nameBytes = global::System.Text.Encoding.UTF8.GetBytes(name);
        uint blocks = (uint)((dir.FullSize + sb.BlockSize - 1) / sb.BlockSize);
        uint[] pointers = new uint[blocks];
        byte[] data = new byte[blocks * sb.BlockSize];
        uint present = 0;
        for (uint i = 0; i < blocks; i++)
        {
            uint blk = sb.GetBlockPointer(dir, i, false, out _);
            if (blk == 0)
            {
                // A hole ends the scan; writing "block 0" of the device back would destroy the superblock area.
                break;
            }

            pointers[i] = blk;
            sb.ReadBlocks(blk, 1, data.AsSpan((int)i * blockSize, blockSize));
            present++;
        }

        for (int block = 0; block < present; block++)
        {
            int start = block * blockSize;
            int end = start + blockSize;
            int previous = -1;
            for (int pos = start; pos < end;)
            {
                if (pos + Ext2InodeLayout.DirEntryNameOffset > end)
                {
                    return false;
                }

                ushort recLen = BitConverter.ToUInt16(data.AsSpan(pos + Ext2InodeLayout.DirEntryRecLenOffset, 2));
                if (recLen < Ext2InodeLayout.DirEntryNameOffset || recLen % Ext2InodeLayout.DirEntryAlignment != 0 || pos + recLen > end)
                {
                    return false;
                }

                uint inode = BitConverter.ToUInt32(data.AsSpan(pos + Ext2InodeLayout.DirEntryInodeOffset, 4));
                int nameLen = data[pos + Ext2InodeLayout.DirEntryNameLenOffset];
                if (inode != 0 && nameLen == nameBytes.Length && nameLen <= recLen - Ext2InodeLayout.DirEntryNameOffset
                    && data.AsSpan(pos + Ext2InodeLayout.DirEntryNameOffset, nameLen).SequenceEqual(nameBytes))
                {
                    if (previous >= 0)
                    {
                        // The previous record in this block absorbs this one; its stale bytes stay behind the extended rec_len.
                        ushort merged = (ushort)(BitConverter.ToUInt16(data.AsSpan(previous + Ext2InodeLayout.DirEntryRecLenOffset, 2)) + recLen);
                        BitConverter.TryWriteBytes(data.AsSpan(previous + Ext2InodeLayout.DirEntryRecLenOffset, 2), merged);
                    }
                    else
                    {
                        // First record of the block: a zero inode frees the slot and keeps its rec_len for reuse.
                        BitConverter.TryWriteBytes(data.AsSpan(pos + Ext2InodeLayout.DirEntryInodeOffset, 4), 0u);
                    }

                    sb.WriteBlocks(pointers[block], 1, data.AsSpan(start, blockSize));
                    return true;
                }

                previous = pos;
                pos += recLen;
            }
        }

        return false;
    }

    /// <summary>
    /// Map an inode mode's file-type nibble to a directory entry file type.
    /// </summary>
    /// <param name="mode">Raw i_mode value.</param>
    public static byte FileTypeFromMode(ushort mode)
    {
        ushort type = (ushort)(mode & Ext2InodeLayout.IFMT);
        return type switch
        {
            Ext2InodeLayout.IFREG => Ext2InodeLayout.FileTypeRegFile,
            Ext2InodeLayout.IFDIR => Ext2InodeLayout.FileTypeDir,
            Ext2InodeLayout.IFLNK => Ext2InodeLayout.FileTypeSymlink,
            Ext2InodeLayout.IFCHR => Ext2InodeLayout.FileTypeChrDev,
            Ext2InodeLayout.IFBLK => Ext2InodeLayout.FileTypeBlkDev,
            Ext2InodeLayout.IFIFO => Ext2InodeLayout.FileTypeFifo,
            Ext2InodeLayout.IFSOCK => Ext2InodeLayout.FileTypeSock,
            _ => Ext2InodeLayout.FileTypeUnknown,
        };
    }
}
