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
    /// Find a child entry by exact (case-sensitive) name.
    /// </summary>
    /// <param name="sb">The mounted volume.</param>
    /// <param name="dir">Directory to search.</param>
    /// <param name="name">Child name, a single path component.</param>
    /// <param name="entry">Matching entry on success.</param>
    /// <returns>true when the name exists in <paramref name="dir"/>.</returns>
    public static bool TryFind(Ext2Superblock sb, Ext2Inode dir, ReadOnlySpan<char> name, out Ext2DirEntry entry)
    {
        entry = null!;
        string target = name.ToString();
        if (!TryParseDirectory(sb, dir, out List<Ext2DirEntry> entries))
        {
            return false;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Name == target)
            {
                entry = entries[i];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Insert an entry into a directory: splits the first entry with enough
    /// slack, reuses a free slot, or appends a fresh block. The directory
    /// size grows by whole blocks.
    /// </summary>
    /// <param name="sb">The mounted volume.</param>
    /// <param name="dir">Directory receiving the entry.</param>
    /// <param name="name">New entry name (UTF-8, at most 255 bytes).</param>
    /// <param name="inodeNumber">Inode number the entry points at.</param>
    /// <param name="fileType">File type value recorded in the entry.</param>
    /// <returns>true when the entry was inserted.</returns>
    public static bool AddEntry(Ext2Superblock sb, Ext2Inode dir, string name, uint inodeNumber, byte fileType)
    {
        if (name.Length > 255)
        {
            return false;
        }

        byte[] nameBytes = global::System.Text.Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 255)
        {
            return false;
        }

        int needed = Ext2InodeLayout.DirEntryNameOffset + nameBytes.Length;
        needed = (needed + 3) & ~3;

        uint blocks = (uint)((dir.FullSize + sb.BlockSize - 1) / sb.BlockSize);
        if (blocks == 0)
        {
            // Need at least one block for directory.
            if (!AllocateDirBlock(sb, dir))
            {
                return false;
            }

            blocks = 1;
        }

        byte[] data = new byte[blocks * sb.BlockSize];
        for (uint i = 0; i < blocks; i++)
        {
            uint blk = sb.GetBlockPointer(dir, i, false, out _);
            if (blk == 0)
            {
                break;
            }

            sb.ReadBlocks(blk, 1, data.AsSpan((int)i * (int)sb.BlockSize, (int)sb.BlockSize));
        }

        // Search for free space.
        int pos = 0;
        while (pos < data.Length)
        {
            uint inode = BitConverter.ToUInt32(data.AsSpan(pos + Ext2InodeLayout.DirEntryInodeOffset, 4));
            ushort recLen = BitConverter.ToUInt16(data.AsSpan(pos + Ext2InodeLayout.DirEntryRecLenOffset, 2));
            if (recLen == 0)
            {
                break;
            }

            int idealLen = 0;
            if (inode != 0)
            {
                byte entryNameLen = data[pos + Ext2InodeLayout.DirEntryNameLenOffset];
                idealLen = (Ext2InodeLayout.DirEntryNameOffset + entryNameLen + 3) & ~3;
            }

            int remaining = recLen - idealLen;
            if (inode != 0 && remaining >= needed)
            {
                // Shrink the live entry to its ideal length; the new entry takes the slack behind it.
                ushort newRecLen = (ushort)idealLen;
                BitConverter.TryWriteBytes(data.AsSpan(pos + Ext2InodeLayout.DirEntryRecLenOffset, 2), newRecLen);
                int newPos = pos + idealLen;
                BitConverter.TryWriteBytes(data.AsSpan(newPos + Ext2InodeLayout.DirEntryInodeOffset, 4), inodeNumber);
                BitConverter.TryWriteBytes(data.AsSpan(newPos + Ext2InodeLayout.DirEntryRecLenOffset, 2), (ushort)(recLen - idealLen));
                data[newPos + Ext2InodeLayout.DirEntryNameLenOffset] = (byte)nameBytes.Length;
                data[newPos + Ext2InodeLayout.DirEntryFileTypeOffset] = fileType;
                nameBytes.CopyTo(data.AsSpan(newPos + Ext2InodeLayout.DirEntryNameOffset, nameBytes.Length));
                for (int z = nameBytes.Length; z < (recLen - idealLen) - Ext2InodeLayout.DirEntryNameOffset; z++)
                {
                    data[newPos + Ext2InodeLayout.DirEntryNameOffset + z] = 0;
                }

                for (uint i = 0; i < blocks; i++)
                {
                    uint blk = sb.GetBlockPointer(dir, i, false, out _);
                    sb.WriteBlocks(blk, 1, data.AsSpan((int)i * (int)sb.BlockSize, (int)sb.BlockSize));
                }

                return true;
            }

            if (inode == 0 && recLen >= needed)
            {
                // Reuse the free slot in place: its rec_len stays, so the chain is unchanged.
                BitConverter.TryWriteBytes(data.AsSpan(pos + Ext2InodeLayout.DirEntryInodeOffset, 4), inodeNumber);
                data[pos + Ext2InodeLayout.DirEntryNameLenOffset] = (byte)nameBytes.Length;
                data[pos + Ext2InodeLayout.DirEntryFileTypeOffset] = fileType;
                nameBytes.CopyTo(data.AsSpan(pos + Ext2InodeLayout.DirEntryNameOffset, nameBytes.Length));
                for (int z = nameBytes.Length; z < recLen - Ext2InodeLayout.DirEntryNameOffset; z++)
                {
                    data[pos + Ext2InodeLayout.DirEntryNameOffset + z] = 0;
                }

                for (uint i = 0; i < blocks; i++)
                {
                    uint blk = sb.GetBlockPointer(dir, i, false, out _);
                    sb.WriteBlocks(blk, 1, data.AsSpan((int)i * (int)sb.BlockSize, (int)sb.BlockSize));
                }

                return true;
            }

            pos += recLen;
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
