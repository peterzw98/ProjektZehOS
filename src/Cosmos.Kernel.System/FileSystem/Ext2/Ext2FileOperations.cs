// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Text;

namespace Cosmos.Kernel.System.FileSystem.Ext2;

/// <summary>
/// Open-file byte I/O for ext2 inodes. Reads resolve logical blocks through
/// the superblock and copy them into the caller's buffer; writes allocate
/// blocks on demand and persist the inode size. Position advancement is the
/// caller's responsibility (matches the Linux VFS convention).
/// </summary>
internal sealed class Ext2FileOperations : IFileOperations
{
    private readonly Ext2Superblock _superblock;

    /// <summary>
    /// Reusable block buffer for the read/write loops; avoids one
    /// allocation per block on large sequential transfers.
    /// </summary>
    private byte[]? _blockBuffer;

    /// <summary>
    /// Creates file operations bound to a mounted volume.
    /// </summary>
    /// <param name="superblock">The mounted volume.</param>
    public Ext2FileOperations(Ext2Superblock superblock)
    {
        _superblock = superblock;
    }

    /// <summary>
    /// Read bytes at the open file's position. Unallocated blocks (holes)
    /// read as zeros; reads past end-of-file return 0.
    /// </summary>
    /// <param name="openFile">Open file to read from.</param>
    /// <param name="buffer">Destination for the bytes.</param>
    /// <returns>Bytes read; <c>0</c> indicates end-of-file.</returns>
    public long Read(IVfsOpenFile openFile, Span<byte> buffer)
    {
        if (openFile.Inode is not Ext2Inode inode)
        {
            return 0;
        }

        long position = openFile.Position;

        // Short targets live inline in i_block, not in data blocks.
        if (inode.IsSymlink)
        {
            string target = ReadSymlinkTarget(inode);
            byte[] bytes = Encoding.UTF8.GetBytes(target);
            if (position >= bytes.Length)
            {
                return 0;
            }

            long toCopy = Math.Min(buffer.Length, bytes.Length - position);
            bytes.AsSpan((int)position, (int)toCopy).CopyTo(buffer);
            return toCopy;
        }

        ulong size = inode.FullSize;
        if (position < 0 || (ulong)position >= size)
        {
            return 0;
        }

        long remaining = (long)(size - (ulong)position);
        long toRead = Math.Min(buffer.Length, remaining);
        if (toRead <= 0)
        {
            return 0;
        }

        long copied = 0;
        uint blockSize = _superblock.BlockSize;
        while (copied < toRead)
        {
            long fileOffset = position + copied;
            uint logicalBlock = (uint)(fileOffset / blockSize);
            uint blockOffset = (uint)(fileOffset % blockSize);
            uint physicalBlock = _superblock.GetBlockPointer(inode, logicalBlock, false, out _);
            long chunk = Math.Min(blockSize - blockOffset, toRead - copied);
            if (physicalBlock == 0)
            {
                // Hole reads as zeros.
                buffer.Slice((int)copied, (int)chunk).Clear();
                copied += chunk;
                continue;
            }

            if (_blockBuffer is null || _blockBuffer.Length != blockSize)
            {
                _blockBuffer = new byte[blockSize];
            }

            byte[] blockBuffer = _blockBuffer;
            _superblock.ReadBlocks(physicalBlock, 1, blockBuffer);
            blockBuffer.AsSpan((int)blockOffset, (int)chunk).CopyTo(buffer.Slice((int)copied, (int)chunk));
            copied += chunk;
        }

        return copied;
    }

    /// <summary>
    /// Write bytes at the open file's position, allocating blocks as needed
    /// and growing the inode size. Seeks past end-of-file leave holes that
    /// later reads report as zeros.
    /// </summary>
    /// <param name="openFile">Open file to write to.</param>
    /// <param name="buffer">Bytes to write.</param>
    /// <returns>Bytes written.</returns>
    public long Write(IVfsOpenFile openFile, ReadOnlySpan<byte> buffer)
    {
        if (openFile.Inode is not Ext2Inode inode)
        {
            return 0;
        }

        if (inode.IsSymlink)
        {
            return 0;
        }

        if (buffer.Length == 0)
        {
            return 0;
        }

        long position = openFile.Position;
        if (position < 0)
        {
            return 0;
        }

        // ZehOS patch ext2-readonly, see PATCHES.md
        // ZehOS patch ext2-inode-cache-evict: blocks allocated for a released
        // inode would never be recorded anywhere and leak.
        if (_superblock.IsReadOnly || inode.Detached)
        {
            return 0;
        }

        long endPosition = position + buffer.Length;
        if (endPosition < 0)
        {
            return 0;
        }

        // Without LARGE_FILE i_size_high is not part of the size: writes stop
        // short at 2 GiB - 1 instead of recording a size the volume cannot hold.
        ulong maxSize = _superblock.MaxFileSize;
        if ((ulong)position >= maxSize)
        {
            return 0;
        }

        if ((ulong)endPosition > maxSize)
        {
            buffer = buffer[..(int)(maxSize - (ulong)position)];
        }

        // Seeks past end-of-file leave holes: the write loop below
        // allocates the touched blocks, and untouched gaps read as zeros.
        long written = 0;
        uint blockSize = _superblock.BlockSize;
        // ZehOS patch ext2-alloc-cache, see PATCHES.md
        // Bitmap, group descriptor and superblock are written once at the
        // end of this call instead of once per allocated block.
        _superblock.BeginAllocationBatch();
        try
        {
            while (written < buffer.Length)
            {
                long fileOffset = position + written;
                uint logicalBlock = (uint)(fileOffset / blockSize);
                uint blockOffset = (uint)(fileOffset % blockSize);
                // ZehOS patch ext2-direct-runs, see PATCHES.md
                // Whole blocks go out as runs of consecutive physical blocks,
                // straight from the caller's buffer; a partial head or tail
                // block keeps the read-modify-write path below.
                if (blockOffset == 0 && buffer.Length - written >= blockSize)
                {
                    long full = WriteFullBlocks(inode, logicalBlock, buffer.Slice((int)written));
                    written += full;
                    if (full == 0)
                    {
                        break;
                    }

                    continue;
                }

                // A new data block is not zeroed on disk: this loop writes the
                // whole block, zero-filled in memory where the caller has no data.
                uint physicalBlock = _superblock.GetBlockPointer(inode, logicalBlock, true, false, out bool isNew);
                if (physicalBlock == 0)
                {
                    break;
                }

                if (_blockBuffer is null || _blockBuffer.Length != blockSize)
                {
                    _blockBuffer = new byte[blockSize];
                }

                byte[] blockBuffer = _blockBuffer;
                if (isNew)
                {
                    blockBuffer.AsSpan(0, (int)blockSize).Clear();
                }
                else
                {
                    _superblock.ReadBlocks(physicalBlock, 1, blockBuffer);
                }

                long chunk = Math.Min(blockSize - blockOffset, buffer.Length - written);
                buffer.Slice((int)written, (int)chunk).CopyTo(blockBuffer.AsSpan((int)blockOffset, (int)chunk));
                try
                {
                    _superblock.WriteBlocks(physicalBlock, 1, blockBuffer);
                }
                catch
                {
                    // The batch end below writes the bitmap and the held-back
                    // pointer block. Without this a new block whose data never
                    // reached the disk would be published with whatever the
                    // disk held there before, inside the file's size.
                    if (isNew)
                    {
                        RevertQuietly(inode, logicalBlock, physicalBlock);
                    }

                    throw;
                }

                written += chunk;
            }
        }
        finally
        {
            _superblock.EndAllocationBatch();
        }

        if (written == 0)
        {
            return 0;
        }

        long newEnd = position + written;
        if ((ulong)newEnd > inode.FullSize)
        {
            // GetBlockPointer already added each block it allocated to
            // i_blocks, so only the size and mtime change here.
            inode.Size = (uint)(newEnd & 0xFFFF_FFFF);
            inode.SizeHigh = (uint)(newEnd >> 32);
            inode.Mtime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        _superblock.WriteInode(inode);
        return written;
    }

    // ZehOS patch ext2-direct-runs, see PATCHES.md
    /// <summary>Most blocks one window resolves before its data goes out (1 MiB at 4 KiB blocks).</summary>
    internal const int RunWindowBlocks = 256;

    /// <summary>Physical blocks of the current window; reused across calls.</summary>
    private uint[]? _runPhysical;

    /// <summary>Which blocks of the current window this call allocated.</summary>
    private bool[]? _runIsNew;

    /// <summary>
    /// Write whole blocks starting at a block-aligned file position: resolve
    /// (and allocate) one window of blocks, then write each run of
    /// consecutive physical blocks with one device call straight from the
    /// caller's buffer. A window never crosses a pointer block boundary:
    /// the held-back pointer block is written when the next window switches
    /// to another one, after the device calls for every block it points at.
    /// This orders the calls only; without a flush in between, a device with
    /// a reordering volatile cache may still persist the pointer first.
    /// </summary>
    /// <param name="inode">File inode.</param>
    /// <param name="logicalBlock">First logical block, aligned with the data.</param>
    /// <param name="data">Remaining caller bytes, at least one block.</param>
    /// <returns>Bytes written (whole blocks), 0 when no block could be resolved.</returns>
    private long WriteFullBlocks(Ext2Inode inode, uint logicalBlock, ReadOnlySpan<byte> data)
    {
        uint blockSize = _superblock.BlockSize;
        long wanted = Math.Min(data.Length / blockSize, RunWindowBlocks);
        uint boundary = NextPointerBoundary(logicalBlock, blockSize / 4);
        if (boundary - logicalBlock < wanted)
        {
            wanted = boundary - logicalBlock;
        }

        int count = (int)wanted;
        _runPhysical ??= new uint[RunWindowBlocks];
        _runIsNew ??= new bool[RunWindowBlocks];
        uint[] physical = _runPhysical;
        bool[] isNew = _runIsNew;

        // Blocks [done, resolved) are linked into the file (inode or the
        // held-back pointer block) but hold no data yet.
        int resolved = 0;
        int done = 0;
        try
        {
            while (resolved < count)
            {
                uint blk = _superblock.GetBlockPointer(inode, logicalBlock + (uint)resolved, true, false, out bool fresh);
                if (blk == 0)
                {
                    break;
                }

                physical[resolved] = blk;
                isNew[resolved] = fresh;
                resolved++;
            }

            while (done < resolved)
            {
                int end = done + 1;
                while (end < resolved && physical[end] == physical[end - 1] + 1)
                {
                    end++;
                }

                int runBytes = (end - done) * (int)blockSize;
                _superblock.WriteDataRun(physical[done], (uint)(end - done), data.Slice(done * (int)blockSize, runBytes));
                done = end;
            }
        }
        catch
        {
            // Same rule as for a single block: a new block whose data did
            // not reach the disk must not be published by the batch end.
            // A failed run may have written part of its blocks; all of them
            // count as unwritten.
            for (int i = resolved - 1; i >= done; i--)
            {
                if (isNew[i])
                {
                    RevertQuietly(inode, logicalBlock + (uint)i, physical[i]);
                }
            }

            throw;
        }

        return (long)resolved * blockSize;
    }

    /// <summary>
    /// First logical block past the pointer block that maps
    /// <paramref name="logicalBlock"/>: 12 for the direct blocks, then the
    /// end of the single indirect block, then the end of each second-level
    /// block of the double indirect tree.
    /// </summary>
    /// <param name="logicalBlock">Zero-based block index within the file.</param>
    /// <param name="perBlock">Pointers per block.</param>
    /// <returns>Exclusive boundary, saturated at uint.MaxValue.</returns>
    internal static uint NextPointerBoundary(uint logicalBlock, uint perBlock)
    {
        ulong direct = Ext2InodeLayout.DirectBlockCount;
        if (logicalBlock < direct)
        {
            return (uint)direct;
        }

        ulong single = direct + perBlock;
        if (logicalBlock < single)
        {
            return (uint)single;
        }

        ulong next = single + ((logicalBlock - single) / perBlock + 1) * perBlock;
        return next > uint.MaxValue ? uint.MaxValue : (uint)next;
    }

    /// <summary>
    /// Take back a new data block whose write failed. The revert may fail
    /// itself (FreeBlock reads another group's bitmap); the caller must see
    /// the original write error, and a block left allocated is only a leak
    /// e2fsck reclaims.
    /// </summary>
    /// <param name="inode">File inode.</param>
    /// <param name="logicalBlock">Logical block of the failed write.</param>
    /// <param name="physicalBlock">The block GetBlockPointer allocated.</param>
    private void RevertQuietly(Ext2Inode inode, uint logicalBlock, uint physicalBlock)
    {
        try
        {
            _superblock.RevertNewDataBlock(inode, logicalBlock, physicalBlock);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Compute and apply a new file position. Positions beyond end-of-file
    /// are valid; a later write zero-fills the gap.
    /// </summary>
    /// <param name="openFile">Open file whose position is moved.</param>
    /// <param name="offset">Byte offset relative to <paramref name="whence"/>.</param>
    /// <param name="whence">Origin the offset is applied to.</param>
    /// <param name="newPosition">Resulting absolute position; 0 on failure.</param>
    /// <returns>true on success; false when the resulting position would be negative.</returns>
    public bool Seek(IVfsOpenFile openFile, long offset, SeekWhence whence, out long newPosition)
    {
        if (openFile.Inode is not Ext2Inode inode)
        {
            newPosition = 0;
            return false;
        }

        long basePosition = whence switch
        {
            SeekWhence.Set => 0,
            SeekWhence.Cur => openFile.Position,
            SeekWhence.End => (long)inode.FullSize,
            _ => -1,
        };

        if (basePosition < 0)
        {
            newPosition = 0;
            return false;
        }

        long target = basePosition + offset;
        if (target < 0)
        {
            newPosition = 0;
            return false;
        }

        openFile.Position = target;
        newPosition = target;
        return true;
    }

    /// <summary>
    /// Flush the file's data and metadata to the backing store.
    /// </summary>
    /// <param name="openFile">Open file to synchronize.</param>
    /// <returns>true on success.</returns>
    public bool Fsync(IVfsOpenFile openFile)
    {
        if (openFile.Inode is Ext2Inode inode)
        {
            _superblock.WriteInode(inode);
        }

        _superblock.Flush();
        return true;
    }

    /// <summary>
    /// Release driver-side state when the open file is closed (flushes first).
    /// </summary>
    /// <param name="openFile">Open file being closed.</param>
    public void Release(IVfsOpenFile openFile)
    {
        Fsync(openFile);
    }

    /// <summary>
    /// Read a symlink's target: inline from i_block for short targets, from
    /// data blocks for long ones.
    /// </summary>
    /// <param name="inode">Symlink inode.</param>
    internal static string ReadSymlinkTarget(Ext2Inode inode)
    {
        ulong size = inode.FullSize;
        if (size <= Ext2InodeLayout.InlineSymlinkMax)
        {
            // Short targets are packed into i_block itself (60 bytes).
            Span<byte> raw = stackalloc byte[Ext2InodeLayout.InlineSymlinkMax];
            for (int i = 0; i < Ext2InodeLayout.BlockCount; i++)
            {
                BitConverter.TryWriteBytes(raw.Slice(i * 4, 4), inode.Block[i]);
            }

            return Encoding.UTF8.GetString(raw[..(int)size]);
        }
        else
        {
            uint blockSize = inode.Superblock.BlockSize;
            byte[] targetBytes = new byte[size];
            uint logicalBlock = 0;
            int copied = 0;
            while ((ulong)copied < size)
            {
                uint physicalBlock = inode.Superblock.GetBlockPointer(inode, logicalBlock, false, out _);
                if (physicalBlock == 0)
                {
                    break;
                }

                byte[] blockBuffer = new byte[blockSize];
                inode.Superblock.ReadBlocks(physicalBlock, 1, blockBuffer);
                int toCopy = (int)Math.Min(blockSize, size - (ulong)copied);
                blockBuffer.AsSpan(0, toCopy).CopyTo(targetBytes.AsSpan(copied, toCopy));
                copied += toCopy;
                logicalBlock++;
            }

            return Encoding.UTF8.GetString(targetBytes);
        }
    }

    /// <summary>
    /// Pack a short symlink target into i_block (60 bytes, no data blocks).
    /// </summary>
    /// <param name="inode">Symlink inode to fill.</param>
    /// <param name="target">Link target; must fit in 60 bytes.</param>
    internal static void WriteInlineSymlink(Ext2Inode inode, string target)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(target);

        // The zeroed buffer covers every i_block slot, so the slots past the
        // target are cleared as the loop packs it.
        Span<byte> raw = stackalloc byte[Ext2InodeLayout.InlineSymlinkMax];
        bytes.AsSpan().CopyTo(raw);
        for (int i = 0; i < Ext2InodeLayout.BlockCount; i++)
        {
            inode.Block[i] = BitConverter.ToUInt32(raw.Slice(i * 4, 4));
        }

        inode.Size = (uint)bytes.Length;
        inode.SizeHigh = 0;
        inode.Blocks = 0;
    }
}
