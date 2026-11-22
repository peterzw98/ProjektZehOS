// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

using System.Diagnostics.CodeAnalysis;
using Cosmos.Kernel.HAL.Devices.Storage;
using SchedSpinLock = Cosmos.Kernel.Core.Scheduler.SpinLock;

namespace Cosmos.Kernel.System.FileSystem.Ext2;

/// <summary>
/// Per-mount ext2 filesystem instance: parsed superblock, group descriptors,
/// block and inode allocators, and the logical-to-physical block mapper.
/// Block numbers coming from on-disk metadata are untrusted: mapping treats
/// anything outside the volume as a hole and never lets it drive I/O.
/// </summary>
internal sealed class Ext2Superblock : IVfsSuperblock
{
    /// <summary>Group descriptor table (flat array).</summary>
    private readonly Ext2GroupDesc[] _groups;

    // ZehOS patch ext2-alloc-cache, see PATCHES.md
    // On-disk group descriptor table as read at mount. UpdateGroupDesc patches
    // one descriptor here and writes only the block holding it, instead of
    // reading and writing the whole table for every allocated block.
    private readonly byte[] _gdTable;

    /// <summary>First block of the group descriptor table.</summary>
    private readonly uint _gdStartBlock;

    /// <summary>Group whose block bitmap is held in <see cref="_blockBitmap"/>; uint.MaxValue = none.</summary>
    private uint _blockBitmapGroup = uint.MaxValue;

    /// <summary>Cached block bitmap of <see cref="_blockBitmapGroup"/>; always equal to or ahead of the disk.</summary>
    private byte[]? _blockBitmap;

    /// <summary>All bits below this index in <see cref="_blockBitmap"/> are known to be set.</summary>
    private uint _blockBitmapScanFrom;

    /// <summary>The cached block bitmap differs from the disk (only inside an allocation batch).</summary>
    private bool _blockBitmapDirty;

    /// <summary>Nesting depth of <see cref="BeginAllocationBatch"/>.</summary>
    private int _batchDepth;

    /// <summary>Groups whose descriptor changed inside the current batch.</summary>
    private readonly List<uint> _dirtyGroups = new();

    /// <summary>Pointer block held back inside a batch; uint.MaxValue = none.</summary>
    private uint _leafBlk = uint.MaxValue;

    /// <summary>Contents of <see cref="_leafBlk"/>, ahead of the disk while a batch runs.</summary>
    private byte[]? _leafData;

    // ZehOS patch ext2-alloc-cache, see PATCHES.md
    // Devices with a live writable mount. The bitmap and descriptor caches
    // assume this mount is the only writer of its device: a second writable
    // mount would hand out blocks the first one already uses. TryCreate
    // refuses it; Drop releases the claim. A read-only mount is still
    // allowed next to a writable one but may see stale data.
    private static readonly List<IBlockDevice> s_writableDevices = new();

    /// <summary>Guards <see cref="s_writableDevices"/>; mount and unmount only, never I/O inside.</summary>
    private static SchedSpinLock s_writableLock;

    /// <summary>This mount holds the write claim on <see cref="Device"/>.</summary>
    private bool _ownsWriteClaim;

    /// <summary>Live inodes by number; keeps open handles coherent with metadata writes.</summary>
    private readonly Dictionary<uint, Ext2Inode> _inodeCache;

    // ZehOS patch ext2-inode-cache, see PATCHES.md
    // Upper bound for presizing the inode cache at mount. Growing a large
    // dictionary later copies it into a new, larger array, which fails on a
    // fragmented kernel heap (about 17,500 cached inodes per mount). Beyond
    // the presize (rounded up to the prime 75,431) the dictionary grows as
    // before; the up-front cost is about 2 MiB per mount.
    internal const int InodeCachePresize = 65_536;

    /// <summary>Initial inode cache size for a volume with <paramref name="inodesCount"/> inodes.</summary>
    internal static int InitialInodeCacheSize(uint inodesCount) => (int)Math.Min(inodesCount, (uint)InodeCachePresize);

    /// <summary>Current capacity of the inode cache (test hook).</summary>
    internal int InodeCacheCapacity => _inodeCache.EnsureCapacity(0);

    /// <summary>Reusable buffer for indirect block reads (single and double first level).</summary>
    private byte[]? _indirBuf1;

    /// <summary>Reusable second-level buffer for the double-indirect path.</summary>
    private byte[]? _indirBuf2;

    /// <summary>Block number held by <see cref="_indirCacheData"/>; uint.MaxValue = invalid.</summary>
    private uint _indirCacheBlk = uint.MaxValue;

    /// <summary>Last-read indirect block contents; sequential reads through the same indirect block skip the disk.</summary>
    private byte[]? _indirCacheData;

    /// <summary>Reusable buffer for inode table reads.</summary>
    private byte[]? _inodeBuf;

    /// <summary>Total inodes in the volume (s_inodes_count).</summary>
    public uint InodesCount { get; }

    /// <summary>Total blocks in the volume (s_blocks_count).</summary>
    public uint BlocksCount { get; }

    /// <summary>Free blocks (s_free_blocks_count).</summary>
    public uint FreeBlocksCount { get; internal set; }

    /// <summary>Free inodes (s_free_inodes_count).</summary>
    public uint FreeInodesCount { get; internal set; }

    /// <summary>First block usable for data (s_first_data_block).</summary>
    public uint FirstDataBlock { get; }

    /// <summary>Block size shift (s_log_block_size).</summary>
    public uint LogBlockSize { get; }

    /// <summary>Fragment size shift (s_log_frag_size).</summary>
    public uint LogFragSize { get; }

    /// <summary>Blocks per group (s_blocks_per_group).</summary>
    public uint BlocksPerGroup { get; }

    /// <summary>Fragments per group (s_frags_per_group).</summary>
    public uint FragsPerGroup { get; }

    /// <summary>Inodes per group (s_inodes_per_group).</summary>
    public uint InodesPerGroup { get; }

    /// <summary>First non-reserved inode (s_first_ino).</summary>
    public uint FirstIno { get; }

    /// <summary>Inode structure size in bytes (s_inode_size).</summary>
    public ushort InodeSize { get; }

    /// <summary>Filesystem revision (s_rev_level).</summary>
    public uint RevLevel { get; }

    /// <summary>Compatible feature flags (s_feature_compat).</summary>
    public uint FeatureCompat { get; }

    /// <summary>Incompatible feature flags (s_feature_incompat).</summary>
    public uint FeatureIncompat { get; }

    /// <summary>Read-only compatible feature flags (s_feature_ro_compat).</summary>
    public uint FeatureRoCompat { get; }

    /// <summary>Block size in bytes, derived from the log shift.</summary>
    public uint BlockSize { get; }

    // ZehOS patch ext2-readonly, see PATCHES.md
    /// <summary>
    /// True when mounted read-only, by request or because the volume uses
    /// features the driver does not maintain. Every write path of the driver
    /// then returns without touching the device.
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>
    /// Largest regular file size this volume can record: 2 GiB - 1 without
    /// LARGE_FILE. Revision 0 has no feature fields, so a raw bit there does
    /// not lift the limit.
    /// </summary>
    internal ulong MaxFileSize => RevLevel >= 1 && (FeatureRoCompat & Ext2SuperblockLayout.FeatureRoCompatLargeFile) != 0
        ? ulong.MaxValue
        : Ext2SuperblockLayout.MaxSizeWithoutLargeFile;

    /// <summary>Fragment size in bytes, derived from the log shift.</summary>
    public uint FragsSize { get; }

    /// <summary>Number of block groups in the volume.</summary>
    public uint GroupsCount { get; }

    /// <summary>Reserved; mirrors the on-disk blocks-per-group geometry.</summary>
    public uint BlocksPerGroupActual { get; }

    /// <summary>Directory and metadata operations for this volume.</summary>
    public Ext2InodeOperations InodeOps { get; }

    /// <summary>Open-file byte I/O for this volume.</summary>
    public Ext2FileOperations FileOps { get; }

    /// <summary>Superblock-level callbacks for this volume.</summary>
    public Ext2SuperblockOperations SuperOps { get; }

    /// <summary>Root directory inode of this mount.</summary>
    public IVfsInode Root { get; }

    /// <inheritdoc />
    public ISuperblockOperations SuperOperations => SuperOps;

    long IVfsSuperblock.BlockSize => BlockSize;

    /// <inheritdoc />
    public ulong MaxNameLength => 255;

    /// <summary>The block device holding the volume.</summary>
    public IBlockDevice Device { get; }

    private Ext2Superblock(
        IBlockDevice device,
        uint inodesCount,
        uint blocksCount,
        uint freeBlocksCount,
        uint freeInodesCount,
        uint firstDataBlock,
        uint logBlockSize,
        uint logFragSize,
        uint blocksPerGroup,
        uint fragsPerGroup,
        uint inodesPerGroup,
        uint firstIno,
        ushort inodeSize,
        uint revLevel,
        uint featureCompat,
        uint featureIncompat,
        uint featureRoCompat,
        Ext2GroupDesc[] groups,
        byte[] gdTable,
        uint gdStartBlock,
        bool readOnly)
    {
        Device = device;
        IsReadOnly = readOnly;
        _inodeCache = new Dictionary<uint, Ext2Inode>(InitialInodeCacheSize(inodesCount));
        InodesCount = inodesCount;
        BlocksCount = blocksCount;
        FreeBlocksCount = freeBlocksCount;
        FreeInodesCount = freeInodesCount;
        FirstDataBlock = firstDataBlock;
        LogBlockSize = logBlockSize;
        LogFragSize = logFragSize;
        BlocksPerGroup = blocksPerGroup;
        FragsPerGroup = fragsPerGroup;
        InodesPerGroup = inodesPerGroup;
        FirstIno = firstIno;
        InodeSize = inodeSize;
        RevLevel = revLevel;
        FeatureCompat = featureCompat;
        FeatureIncompat = featureIncompat;
        FeatureRoCompat = featureRoCompat;
        BlockSize = (uint)Ext2SuperblockLayout.BaseBlockSize << (int)logBlockSize;
        // A negative log shift halves the base size per step instead of doubling it.
        if ((int)logFragSize >= 0)
        {
            FragsSize = (uint)Ext2SuperblockLayout.BaseBlockSize << (int)logFragSize;
        }
        else
        {
            FragsSize = (uint)Ext2SuperblockLayout.BaseBlockSize >> (int)(-(int)logFragSize);
        }
        _groups = groups;
        _gdTable = gdTable;
        _gdStartBlock = gdStartBlock;
        GroupsCount = (uint)groups.Length;

        InodeOps = new Ext2InodeOperations(this);
        FileOps = new Ext2FileOperations(this);
        SuperOps = new Ext2SuperblockOperations();

        Root = ReadInode(Ext2SuperblockLayout.RootInodeNumber, "/");
    }

    /// <summary>
    /// Probe <paramref name="device"/> for an ext2 volume and mount it.
    /// </summary>
    /// <param name="device">Block device holding the candidate volume.</param>
    /// <param name="readOnly">Mount read-only; set as well when the volume needs features the driver does not maintain.</param>
    /// <param name="superblock">Mounted volume on success; null on failure.</param>
    /// <returns>true when the device carries a valid, supported ext2 superblock.</returns>
    public static bool TryCreate(IBlockDevice device, bool readOnly, [NotNullWhen(true)] out Ext2Superblock? superblock)
    {
        superblock = null;
        try
        {
            // The superblock sits at byte 1024, which may straddle device
            // blocks: round the read up to whole device blocks.
            ulong devBlockSize = device.BlockSize;
            ulong sbByteOffset = Ext2SuperblockLayout.SuperblockOffset;
            ulong sbLba = sbByteOffset / devBlockSize;
            ulong blocksToRead = (Ext2SuperblockLayout.SuperblockSize + devBlockSize - 1) / devBlockSize;
            ulong totalBytes = blocksToRead * devBlockSize;
            byte[] buf = new byte[totalBytes];
            device.ReadBlock(sbLba, blocksToRead, buf);
            int offsetInBuf = (int)(sbByteOffset % devBlockSize);
            ReadOnlySpan<byte> sb = buf.AsSpan(offsetInBuf, Ext2SuperblockLayout.SuperblockSize);

            ushort magic = BitConverter.ToUInt16(sb.Slice(Ext2SuperblockLayout.MagicOffset, 2));
            if (magic != Ext2SuperblockLayout.Magic)
            {
                return false;
            }

            uint inodesCount = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.InodesCountOffset, 4));
            uint blocksCount = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.BlocksCountOffset, 4));
            uint freeBlocks = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.FreeBlocksCountOffset, 4));
            uint freeInodes = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.FreeInodesCountOffset, 4));
            uint firstDataBlock = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.FirstDataBlockOffset, 4));
            uint logBlockSize = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.LogBlockSizeOffset, 4));
            uint logFragSize = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.LogFragSizeOffset, 4));
            uint blocksPerGroup = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.BlocksPerGroupOffset, 4));
            uint fragsPerGroup = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.FragsPerGroupOffset, 4));
            uint inodesPerGroup = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.InodesPerGroupOffset, 4));
            uint revLevel = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.RevLevelOffset, 4));
            uint firstIno = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.FirstInoOffset, 4));
            ushort inodeSize = BitConverter.ToUInt16(sb.Slice(Ext2SuperblockLayout.InodeSizeOffset, 2));
            uint featureCompat = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.FeatureCompatOffset, 4));
            uint featureIncompat = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.FeatureIncompatOffset, 4));
            uint featureRoCompat = BitConverter.ToUInt32(sb.Slice(Ext2SuperblockLayout.FeatureRoCompatOffset, 4));

            if (logBlockSize > Ext2SuperblockLayout.MaxLogBlockSize)
            {
                return false;
            }

            // ZehOS patch ext2-readonly, see PATCHES.md
            // Feature fields exist from revision 1 on; revision 0 leaves them undefined.
            if (revLevel >= 1)
            {
                if ((featureIncompat & ~Ext2SuperblockLayout.SupportedFeatureIncompat) != 0)
                {
                    return false;
                }

                // Stricter than the specification, which allows writing with
                // unknown compat bits: the driver does not maintain dir_index
                // trees, the journal, resize_inode or shared xattr blocks
                // (Unlink never frees i_file_acl), so writing would leave them
                // inconsistent.
                if (featureCompat != 0 || (featureRoCompat & ~Ext2SuperblockLayout.SupportedFeatureRoCompat) != 0)
                {
                    readOnly = true;
                }
            }

            // Like Linux ext2_setup_super: a newer revision is only read.
            if (revLevel > Ext2SuperblockLayout.MaxRevLevel)
            {
                readOnly = true;
            }

            uint blockSize = (uint)Ext2SuperblockLayout.BaseBlockSize << (int)logBlockSize;
            // The parser never sees the device: reject block sizes the
            // device cannot address before any group-descriptor I/O.
            if (blockSize % devBlockSize != 0)
            {
                return false;
            }

            if (blocksPerGroup == 0 || inodesPerGroup == 0)
            {
                return false;
            }

            if (inodeSize == 0)
            {
                inodeSize = Ext2SuperblockLayout.DefaultInodeSize;
            }

            if (firstIno == 0)
            {
                firstIno = Ext2SuperblockLayout.DefaultFirstIno;
            }

            // Reject volumes whose claimed size leaves the device before
            // any group-descriptor I/O can hit the device's range guard.
            ulong devTotalBytes = device.BlockCount * devBlockSize;
            ulong fsBytes = (ulong)blocksCount * blockSize;
            if (fsBytes > devTotalBytes)
            {
                return false;
            }

            uint groupsCount = (blocksCount + blocksPerGroup - 1) / blocksPerGroup;
            uint groupsByInodes = (inodesCount + inodesPerGroup - 1) / inodesPerGroup;
            if (groupsByInodes > groupsCount)
            {
                groupsCount = groupsByInodes;
            }

            // The descriptor table starts in the block after the superblock:
            // block 2 for 1 KiB volumes (block 0 is boot, block 1 is the
            // superblock), block 1 otherwise.
            uint gdStartBlock = blockSize == 1024 ? 2u : 1u;
            uint gdBytes = groupsCount * (uint)Ext2SuperblockLayout.GroupDescSize;
            uint gdBlocks = (gdBytes + blockSize - 1) / blockSize;
            byte[] gdBuf = new byte[gdBlocks * blockSize];
            ReadBlocks(device, gdStartBlock, gdBlocks, blockSize, devBlockSize, gdBuf);

            Ext2GroupDesc[] groups = new Ext2GroupDesc[groupsCount];
            for (int i = 0; i < groupsCount; i++)
            {
                int off = i * Ext2SuperblockLayout.GroupDescSize;
                uint bBitmap = BitConverter.ToUInt32(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescBlockBitmapOffset, 4));
                uint iBitmap = BitConverter.ToUInt32(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescInodeBitmapOffset, 4));
                uint iTable = BitConverter.ToUInt32(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescInodeTableOffset, 4));
                ushort freeBlocksCount = BitConverter.ToUInt16(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescFreeBlocksCountOffset, 2));
                ushort freeInodesCount = BitConverter.ToUInt16(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescFreeInodesCountOffset, 2));
                ushort usedDirs = BitConverter.ToUInt16(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescUsedDirsCountOffset, 2));
                groups[i] = new Ext2GroupDesc(bBitmap, iBitmap, iTable, freeBlocksCount, freeInodesCount, usedDirs);
            }

            superblock = new Ext2Superblock(
                device,
                inodesCount, blocksCount, freeBlocks, freeInodes,
                firstDataBlock, logBlockSize, logFragSize,
                blocksPerGroup, fragsPerGroup, inodesPerGroup,
                firstIno, inodeSize, revLevel,
                featureCompat, featureIncompat, featureRoCompat,
                groups, gdBuf, gdStartBlock, readOnly);
            // ZehOS patch ext2-alloc-cache, see PATCHES.md
            if (!superblock.IsReadOnly && !superblock.TryClaimWrite())
            {
                superblock = null;
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Read <paramref name="count"/> filesystem blocks into <paramref name="dest"/>.
    /// Out-of-range or boot-sector reads report zeros instead of throwing.
    /// </summary>
    /// <param name="device">Block device holding the volume.</param>
    /// <param name="ext2Block">First filesystem block number.</param>
    /// <param name="count">Blocks to read.</param>
    /// <param name="ext2BlockSize">Filesystem block size in bytes.</param>
    /// <param name="devBlockSize">Device block size in bytes.</param>
    /// <param name="dest">Destination buffer.</param>
    internal static void ReadBlocks(IBlockDevice device, uint ext2Block, uint count, uint ext2BlockSize, ulong devBlockSize, Span<byte> dest)
    {
        if (dest.Length == 0)
        {
            return;
        }

        if (ext2Block == 0 && count != 0)
        {
            // Block 0 is the boot sector; ext2 data never lives there.
            dest.Clear();
            return;
        }

        ulong byteOffset = (ulong)ext2Block * ext2BlockSize;
        ulong lba = byteOffset / devBlockSize;
        ulong devBlocks = (ulong)count * ext2BlockSize / devBlockSize;
        if (lba + devBlocks > device.BlockCount)
        {
            dest.Clear();
            return;
        }

        if ((ulong)dest.Length < (ulong)count * ext2BlockSize)
        {
            return;
        }

        device.ReadBlock(lba, devBlocks, dest);
    }

    /// <summary>
    /// Read <paramref name="count"/> blocks of this volume into <paramref name="dest"/>.
    /// </summary>
    /// <param name="ext2Block">First filesystem block number.</param>
    /// <param name="count">Blocks to read.</param>
    /// <param name="dest">Destination buffer.</param>
    internal void ReadBlocks(uint ext2Block, uint count, Span<byte> dest)
    {
        if (dest.Length == 0)
        {
            return;
        }

        if (ext2Block >= BlocksCount)
        {
            dest.Clear();
            return;
        }

        ReadBlocks(Device, ext2Block, count, BlockSize, Device.BlockSize, dest);
    }

    /// <summary>
    /// Write <paramref name="count"/> blocks of this volume. Out-of-range
    /// writes are dropped instead of throwing.
    /// </summary>
    /// <param name="ext2Block">First filesystem block number.</param>
    /// <param name="count">Blocks to write.</param>
    /// <param name="data">Source bytes.</param>
    internal void WriteBlocks(uint ext2Block, uint count, ReadOnlySpan<byte> data)
    {
        // ZehOS patch ext2-readonly, see PATCHES.md
        if (IsReadOnly || data.Length == 0)
        {
            return;
        }

        if (ext2Block >= BlocksCount)
        {
            return;
        }

        ulong byteOffset = (ulong)ext2Block * BlockSize;
        ulong lba = byteOffset / Device.BlockSize;
        ulong devBlocks = (ulong)count * BlockSize / Device.BlockSize;
        if (lba + devBlocks > Device.BlockCount)
        {
            return;
        }

        Device.WriteBlock(lba, devBlocks, data);
    }

    /// <summary>
    /// Group descriptor for a block group index.
    /// </summary>
    /// <param name="groupIndex">Zero-based block group.</param>
    internal Ext2GroupDesc GetGroup(uint groupIndex)
    {
        return _groups[groupIndex];
    }

    /// <summary>
    /// Block group holding an inode number.
    /// </summary>
    /// <param name="inodeNumber">One-based inode number.</param>
    internal uint GroupOfInode(uint inodeNumber)
    {
        return (inodeNumber - 1) / InodesPerGroup;
    }

    /// <summary>
    /// Index of an inode number within its block group.
    /// </summary>
    /// <param name="inodeNumber">One-based inode number.</param>
    internal uint IndexInGroup(uint inodeNumber)
    {
        return (inodeNumber - 1) % InodesPerGroup;
    }

    /// <summary>
    /// Read an inode from its table, consulting the live-inode cache first.
    /// </summary>
    /// <param name="inodeNumber">One-based inode number.</param>
    /// <param name="name">Leaf name the caller resolved it under.</param>
    public Ext2Inode ReadInode(uint inodeNumber, string name)
    {
        if (inodeNumber == 0 || inodeNumber > InodesCount)
        {
            throw new ArgumentOutOfRangeException(nameof(inodeNumber));
        }

        if (_inodeCache.TryGetValue(inodeNumber, out Ext2Inode? cached))
        {
            // The cache is keyed by number, but a lookup may reach the same
            // inode under a new name (rename); refresh the leaf name.
            cached.Name = name;
            return cached;
        }

        uint group = GroupOfInode(inodeNumber);
        if (group >= GroupsCount)
        {
            throw new ArgumentOutOfRangeException(nameof(inodeNumber));
        }

        uint index = IndexInGroup(inodeNumber);
        Ext2GroupDesc gd = _groups[group];
        if (gd.InodeTable == 0 || gd.InodeTable >= BlocksCount)
        {
            throw new ArgumentOutOfRangeException(nameof(inodeNumber));
        }
        uint inodeTableBlock = gd.InodeTable;

        // Inode table is contiguous blocks.
        uint inodesPerBlock = BlockSize / InodeSize;
        uint blockOffset = index / inodesPerBlock;
        uint offsetInBlock = (index % inodesPerBlock) * InodeSize;

        if (_inodeBuf is null || _inodeBuf.Length != BlockSize)
        {
            _inodeBuf = new byte[BlockSize];
        }

        byte[] blockBuf = _inodeBuf;
        ReadBlocks(inodeTableBlock + blockOffset, 1, blockBuf);
        ReadOnlySpan<byte> raw = blockBuf.AsSpan((int)offsetInBlock, InodeSize);

        ushort mode = BitConverter.ToUInt16(raw.Slice(Ext2InodeLayout.ModeOffset, 2));
        ushort uid = BitConverter.ToUInt16(raw.Slice(Ext2InodeLayout.UidOffset, 2));
        uint size = BitConverter.ToUInt32(raw.Slice(Ext2InodeLayout.SizeOffset, 4));
        uint atime = BitConverter.ToUInt32(raw.Slice(Ext2InodeLayout.AtimeOffset, 4));
        uint ctime = BitConverter.ToUInt32(raw.Slice(Ext2InodeLayout.CtimeOffset, 4));
        uint mtime = BitConverter.ToUInt32(raw.Slice(Ext2InodeLayout.MtimeOffset, 4));
        uint dtime = BitConverter.ToUInt32(raw.Slice(Ext2InodeLayout.DtimeOffset, 4));
        ushort gid = BitConverter.ToUInt16(raw.Slice(Ext2InodeLayout.GidOffset, 2));
        ushort links = BitConverter.ToUInt16(raw.Slice(Ext2InodeLayout.LinksCountOffset, 2));
        uint blocks = BitConverter.ToUInt32(raw.Slice(Ext2InodeLayout.BlocksOffset, 4));
        uint flags = BitConverter.ToUInt32(raw.Slice(Ext2InodeLayout.FlagsOffset, 4));
        uint fileAcl = BitConverter.ToUInt32(raw.Slice(Ext2InodeLayout.FileAclOffset, 4)); // ZehOS patch ext2-truncate
        uint sizeHigh = 0;
        if (InodeSize >= Ext2InodeLayout.DirAclOrSizeHighOffset + 4)
        {
            sizeHigh = BitConverter.ToUInt32(raw.Slice(Ext2InodeLayout.DirAclOrSizeHighOffset, 4));
        }

        uint[] block = new uint[Ext2InodeLayout.BlockCount];
        for (int i = 0; i < Ext2InodeLayout.BlockCount; i++)
        {
            block[i] = BitConverter.ToUInt32(raw.Slice(Ext2InodeLayout.BlockOffset + i * 4, 4));
        }

        Ext2Inode inode = new(this, inodeNumber, name)
        {
            Mode = mode,
            Uid = uid,
            Gid = gid,
            Size = size,
            SizeHigh = sizeHigh,
            Atime = atime,
            Ctime = ctime,
            Mtime = mtime,
            Dtime = dtime,
            LinksCount = links,
            Blocks = blocks,
            Flags = flags,
            FileAcl = fileAcl,
            Block = block,
        };
        _inodeCache[inodeNumber] = inode;
        return inode;
    }

    /// <summary>
    /// Persist an inode's fields to its table slot (read-modify-write).
    /// </summary>
    /// <param name="inode">Inode to write back.</param>
    internal void WriteInode(Ext2Inode inode)
    {
        // ZehOS patch ext2-readonly, see PATCHES.md
        if (IsReadOnly)
        {
            return;
        }

        uint group = GroupOfInode(inode.InodeNumber);
        uint index = IndexInGroup(inode.InodeNumber);
        Ext2GroupDesc gd = _groups[group];
        uint inodeTableBlock = gd.InodeTable;
        uint inodesPerBlock = BlockSize / InodeSize;
        uint blockOffset = index / inodesPerBlock;
        uint offsetInBlock = (index % inodesPerBlock) * InodeSize;

        if (_inodeBuf is null || _inodeBuf.Length != BlockSize)
        {
            _inodeBuf = new byte[BlockSize];
        }

        byte[] blockBuf = _inodeBuf;
        ReadBlocks(inodeTableBlock + blockOffset, 1, blockBuf);
        Span<byte> raw = blockBuf.AsSpan((int)offsetInBlock, InodeSize);

        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.ModeOffset, 2), inode.Mode);
        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.UidOffset, 2), inode.Uid);
        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.SizeOffset, 4), inode.Size);
        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.AtimeOffset, 4), inode.Atime);
        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.CtimeOffset, 4), inode.Ctime);
        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.MtimeOffset, 4), inode.Mtime);
        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.DtimeOffset, 4), inode.Dtime);
        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.GidOffset, 2), inode.Gid);
        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.LinksCountOffset, 2), inode.LinksCount);
        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.BlocksOffset, 4), inode.Blocks);
        BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.FlagsOffset, 4), inode.Flags);
        if (InodeSize >= Ext2InodeLayout.DirAclOrSizeHighOffset + 4)
        {
            BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.DirAclOrSizeHighOffset, 4), inode.SizeHigh);
        }
        for (int i = 0; i < Ext2InodeLayout.BlockCount; i++)
        {
            BitConverter.TryWriteBytes(raw.Slice(Ext2InodeLayout.BlockOffset + i * 4, 4), inode.Block[i]);
        }

        WriteBlocks(inodeTableBlock + blockOffset, 1, blockBuf);
        _inodeCache[inode.InodeNumber] = inode;
    }

    /// <summary>
    /// Allocate a free inode, preferring <paramref name="groupHint"/>.
    /// </summary>
    /// <param name="groupHint">Block group to try first.</param>
    /// <param name="inodeNumber">Allocated one-based inode number on success.</param>
    /// <returns>true when an inode was allocated.</returns>
    internal bool TryAllocateInode(uint groupHint, out uint inodeNumber)
    {
        // ZehOS patch ext2-readonly, see PATCHES.md
        if (IsReadOnly)
        {
            inodeNumber = 0;
            return false;
        }

        for (uint g = 0; g < GroupsCount; g++)
        {
            uint gi = (groupHint + g) % GroupsCount;
            Ext2GroupDesc gd = _groups[gi];
            if (gd.FreeInodesCount == 0)
            {
                continue;
            }

            byte[] bmp = new byte[BlockSize];
            ReadBlocks(gd.InodeBitmap, 1, bmp);
            uint max = InodesPerGroup;
            if (gi == GroupsCount - 1)
            {
                uint rem = InodesCount % InodesPerGroup;
                if (rem != 0)
                {
                    max = rem;
                }
            }

            for (uint i = 0; i < max; i++)
            {
                uint byteIdx = i / 8;
                uint bitIdx = i % 8;
                if ((bmp[byteIdx] & (1u << (int)bitIdx)) == 0)
                {
                    bmp[byteIdx] |= (byte)(1u << (int)bitIdx);
                    WriteBlocks(gd.InodeBitmap, 1, bmp);
                    gd.FreeInodesCount--;
                    UpdateGroupDesc(gi);
                    FreeInodesCount--;
                    inodeNumber = gi * InodesPerGroup + i + 1;
                    return true;
                }
            }
        }

        inodeNumber = 0;
        return false;
    }

    /// <summary>
    /// Release an inode back to its group's bitmap. The on-disk slot is
    /// marked deleted (links_count 0, dtime set) before the bitmap bit is
    /// cleared. An inode that is already free is left untouched.
    /// </summary>
    /// <param name="inodeNumber">One-based inode number to free.</param>
    internal void FreeInode(uint inodeNumber)
    {
        uint group = GroupOfInode(inodeNumber);
        uint index = IndexInGroup(inodeNumber);
        Ext2GroupDesc gd = _groups[group];
        byte[] bmp = new byte[BlockSize];
        ReadBlocks(gd.InodeBitmap, 1, bmp);
        uint byteIdx = index / 8;
        uint bitIdx = index % 8;

        // ZehOS patch ext2-free-inode, see PATCHES.md
        // Freeing a free inode again would raise the free counters twice.
        if ((bmp[byteIdx] & (1u << (int)bitIdx)) == 0)
        {
            _inodeCache.Remove(inodeNumber);
            return;
        }

        // Write the slot directly instead of WriteInode, which would put the
        // inode back into the cache. dtime 0 means "not deleted" to e2fsck,
        // so store at least 1 without a valid clock.
        uint inodesPerBlock = BlockSize / InodeSize;
        uint tableBlock = gd.InodeTable + (index / inodesPerBlock);
        int slot = (int)((index % inodesPerBlock) * InodeSize);
        byte[] table = new byte[BlockSize];
        ReadBlocks(tableBlock, 1, table);
        uint dtime = Math.Max((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1u);
        BitConverter.TryWriteBytes(table.AsSpan(slot + Ext2InodeLayout.LinksCountOffset, 2), (ushort)0);
        BitConverter.TryWriteBytes(table.AsSpan(slot + Ext2InodeLayout.DtimeOffset, 4), dtime);
        WriteBlocks(tableBlock, 1, table);

        // A handle still holding the object (an open, unlinked file) may
        // call WriteInode later; it must not write dtime 0 back.
        if (_inodeCache.TryGetValue(inodeNumber, out Ext2Inode? cached))
        {
            cached.LinksCount = 0;
            cached.Dtime = dtime;
        }

        bmp[byteIdx] &= (byte)~(1u << (int)bitIdx);
        WriteBlocks(gd.InodeBitmap, 1, bmp);
        gd.FreeInodesCount++;
        UpdateGroupDesc(group);
        FreeInodesCount++;
        _inodeCache.Remove(inodeNumber);
    }

    /// <summary>
    /// Allocate a free block, preferring <paramref name="groupHint"/>. The
    /// block is zeroed before it is returned.
    /// </summary>
    /// <param name="groupHint">Block group to try first.</param>
    /// <param name="blockNumber">Allocated block number on success.</param>
    /// <returns>true when a block was allocated.</returns>
    internal bool TryAllocateBlock(uint groupHint, out uint blockNumber)
    {
        return TryAllocateBlock(groupHint, true, out blockNumber);
    }

    /// <summary>
    /// Allocate a free block, preferring <paramref name="groupHint"/>.
    /// </summary>
    /// <param name="groupHint">Block group to try first.</param>
    /// <param name="zero">Write zeros to the block. Callers that overwrite
    /// the whole block (or clear it in memory first) pass false.</param>
    /// <param name="blockNumber">Allocated block number on success.</param>
    /// <returns>true when a block was allocated.</returns>
    internal bool TryAllocateBlock(uint groupHint, bool zero, out uint blockNumber)
    {
        // ZehOS patch ext2-readonly, see PATCHES.md
        if (IsReadOnly)
        {
            blockNumber = 0;
            return false;
        }

        for (uint g = 0; g < GroupsCount; g++)
        {
            uint gi = (groupHint + g) % GroupsCount;
            Ext2GroupDesc gd = _groups[gi];
            if (gd.FreeBlocksCount == 0)
            {
                continue;
            }

            // ZehOS patch ext2-alloc-cache, see PATCHES.md
            byte[] bmp = LoadBlockBitmap(gi);
            uint max = BlocksPerGroup;
            if (gi == GroupsCount - 1)
            {
                uint rem = BlocksCount % BlocksPerGroup;
                if (rem != 0)
                {
                    max = rem;
                }
            }

            for (uint i = _blockBitmapScanFrom; i < max; i++)
            {
                uint byteIdx = i / 8;
                uint bitIdx = i % 8;
                if ((bmp[byteIdx] & (1u << (int)bitIdx)) == 0)
                {
                    bmp[byteIdx] |= (byte)(1u << (int)bitIdx);
                    _blockBitmapScanFrom = i + 1;
                    StoreBlockBitmap();
                    gd.FreeBlocksCount--;
                    UpdateGroupDesc(gi);
                    FreeBlocksCount--;
                    blockNumber = gi * BlocksPerGroup + i;
                    if (zero)
                    {
                        byte[] zeros = new byte[BlockSize];
                        WriteBlocks(blockNumber, 1, zeros);
                    }

                    return true;
                }
            }

            // Bits up to max are all set even though the descriptor counts
            // free blocks; remember that and try the next group.
            _blockBitmapScanFrom = max;
        }

        blockNumber = 0;
        return false;
    }

    /// <summary>
    /// Release a block back to its group's bitmap.
    /// </summary>
    /// <param name="blockNumber">Block number to free.</param>
    internal void FreeBlock(uint blockNumber)
    {
        uint group = blockNumber / BlocksPerGroup;
        uint index = blockNumber % BlocksPerGroup;
        if (group >= GroupsCount)
        {
            return;
        }

        Ext2GroupDesc gd = _groups[group];
        // ZehOS patch ext2-alloc-cache, see PATCHES.md
        byte[] bmp = LoadBlockBitmap(group);
        uint byteIdx = index / 8;
        uint bitIdx = index % 8;
        bmp[byteIdx] &= (byte)~(1u << (int)bitIdx);
        if (index < _blockBitmapScanFrom)
        {
            _blockBitmapScanFrom = index;
        }

        StoreBlockBitmap();
        gd.FreeBlocksCount++;
        UpdateGroupDesc(group);
        FreeBlocksCount++;
    }

    /// <summary>
    /// Undo the allocation of a data block whose first write failed, so the
    /// file keeps a hole there instead of pointing at stale disk contents.
    /// Only valid inside an allocation batch right after
    /// <see cref="GetBlockPointer(Ext2Inode, uint, bool, bool, out bool)"/>
    /// returned the block with isNew set: its pointer then sits in the
    /// inode (direct) or in the held-back pointer block, neither on disk yet.
    /// When the pointer is not found there the block stays allocated (a leak
    /// e2fsck reclaims) rather than freeing a block something references.
    /// </summary>
    /// <param name="inode">File inode.</param>
    /// <param name="logicalBlock">Zero-based block index within the file.</param>
    /// <param name="physicalBlock">The block GetBlockPointer allocated.</param>
    internal void RevertNewDataBlock(Ext2Inode inode, uint logicalBlock, uint physicalBlock)
    {
        // ZehOS patch ext2-alloc-cache, see PATCHES.md
        bool unlinked = false;
        if (logicalBlock < Ext2InodeLayout.DirectBlockCount)
        {
            if (inode.Block[logicalBlock] == physicalBlock)
            {
                inode.Block[logicalBlock] = 0;
                unlinked = true;
            }
        }
        else if (_batchDepth > 0 && _leafBlk != uint.MaxValue && _leafData is not null)
        {
            for (int off = 0; off + 4 <= (int)BlockSize; off += 4)
            {
                if (BitConverter.ToUInt32(_leafData.AsSpan(off, 4)) == physicalBlock)
                {
                    BitConverter.TryWriteBytes(_leafData.AsSpan(off, 4), 0u);
                    unlinked = true;
                    break;
                }
            }
        }

        if (!unlinked)
        {
            return;
        }

        // The indirect cache may hold the entry just cleared.
        InvalidateIndirCache();
        inode.Blocks -= BlockSize / 512;
        FreeBlock(physicalBlock);
    }

    /// <summary>
    /// Persist one group's descriptor and the volume's free counts.
    /// </summary>
    /// <param name="groupIndex">Zero-based block group.</param>
    private void UpdateGroupDesc(uint groupIndex)
    {
        // ZehOS patch ext2-alloc-cache, see PATCHES.md
        // Inside a batch the descriptor is written once at the batch end.
        if (_batchDepth > 0)
        {
            if (!_dirtyGroups.Contains(groupIndex))
            {
                _dirtyGroups.Add(groupIndex);
            }

            return;
        }

        WriteGroupDesc(groupIndex);
        UpdateSuperblock();
    }

    /// <summary>
    /// Serialize one group's descriptor into the cached table and write the
    /// table block holding it.
    /// </summary>
    /// <param name="groupIndex">Zero-based block group.</param>
    private void WriteGroupDesc(uint groupIndex)
    {
        byte[] gdBuf = _gdTable;
        int off = (int)groupIndex * Ext2SuperblockLayout.GroupDescSize;
        Ext2GroupDesc gd = _groups[groupIndex];
        BitConverter.TryWriteBytes(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescFreeBlocksCountOffset, 2), gd.FreeBlocksCount);
        BitConverter.TryWriteBytes(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescFreeInodesCountOffset, 2), gd.FreeInodesCount);
        BitConverter.TryWriteBytes(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescUsedDirsCountOffset, 2), gd.UsedDirsCount);
        BitConverter.TryWriteBytes(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescBlockBitmapOffset, 4), gd.BlockBitmap);
        BitConverter.TryWriteBytes(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescInodeBitmapOffset, 4), gd.InodeBitmap);
        BitConverter.TryWriteBytes(gdBuf.AsSpan(off + Ext2SuperblockLayout.GroupDescInodeTableOffset, 4), gd.InodeTable);
        uint tableBlock = (uint)off / BlockSize;
        WriteBlocks(_gdStartBlock + tableBlock, 1, gdBuf.AsSpan((int)(tableBlock * BlockSize), (int)BlockSize));
    }

    /// <summary>
    /// Block bitmap of <paramref name="group"/> from the one-group cache,
    /// reading it from disk on a group change (after writing back a dirty one).
    /// </summary>
    /// <param name="group">Zero-based block group.</param>
    private byte[] LoadBlockBitmap(uint group)
    {
        if (_blockBitmap is not null && _blockBitmapGroup == group)
        {
            return _blockBitmap;
        }

        WriteBackBlockBitmap();
        // The read reuses the buffer. A read that fills part of it and then
        // throws must not leave the old group marked valid over foreign
        // bytes: the next allocation there would write them back as its
        // bitmap. Until the read succeeds no group is cached.
        _blockBitmapGroup = uint.MaxValue;
        if (_blockBitmap is null || _blockBitmap.Length != BlockSize)
        {
            _blockBitmap = new byte[BlockSize];
        }

        ReadBlocks(_groups[group].BlockBitmap, 1, _blockBitmap);
        _blockBitmapGroup = group;
        _blockBitmapScanFrom = 0;
        return _blockBitmap;
    }

    /// <summary>
    /// Persist the cached block bitmap now, or mark it dirty inside a batch.
    /// </summary>
    private void StoreBlockBitmap()
    {
        if (_batchDepth > 0)
        {
            _blockBitmapDirty = true;
            return;
        }

        WriteBlocks(_groups[_blockBitmapGroup].BlockBitmap, 1, _blockBitmap!);
        _blockBitmapDirty = false;
    }

    /// <summary>Write the cached block bitmap if a batch left it dirty.</summary>
    private void WriteBackBlockBitmap()
    {
        if (_blockBitmapDirty && _blockBitmap is not null && _blockBitmapGroup < GroupsCount)
        {
            WriteBlocks(_groups[_blockBitmapGroup].BlockBitmap, 1, _blockBitmap);
        }

        _blockBitmapDirty = false;
    }

    /// <summary>
    /// Start a run of block allocations whose bitmap, descriptor and
    /// superblock writes are deferred to <see cref="EndAllocationBatch"/>.
    /// A crash inside the batch leaves blocks the bitmap marks free while an
    /// inode does not yet reference them on disk; e2fsck repairs both
    /// directions without data loss (same class as a crash between the
    /// bitmap and inode writes before this patch).
    /// </summary>
    internal void BeginAllocationBatch()
    {
        _batchDepth++;
    }

    /// <summary>
    /// End an allocation batch: write the dirty bitmap first, then the
    /// changed descriptors, then the superblock counters.
    /// </summary>
    internal void EndAllocationBatch()
    {
        if (_batchDepth == 0 || --_batchDepth > 0)
        {
            return;
        }

        CommitAllocationState();
    }

    /// <summary>
    /// Read a pointer block, from the batch's held-back copy when it is the
    /// same block.
    /// </summary>
    /// <param name="block">Pointer block number.</param>
    /// <param name="dest">Destination of one block.</param>
    private void LoadPointerBlock(uint block, byte[] dest)
    {
        if (_leafData is not null && _leafBlk == block)
        {
            _leafData.AsSpan(0, (int)BlockSize).CopyTo(dest);
            return;
        }

        ReadBlocks(block, 1, dest);
    }

    /// <summary>
    /// Write a pointer block that gained an entry. Outside a batch the
    /// bitmap is already on disk and the block is written now. Inside a
    /// batch it is held back and written by <see cref="WriteBackLeaf"/>,
    /// always after the bitmap that marks its new entries used, so the disk
    /// never points at a block its bitmap calls free.
    /// </summary>
    /// <param name="block">Pointer block number.</param>
    /// <param name="data">New contents of one block.</param>
    private void StorePointerBlock(uint block, byte[] data)
    {
        if (_batchDepth == 0)
        {
            WriteBlocks(block, 1, data);
            return;
        }

        if (_leafBlk != block)
        {
            WriteBackLeaf();
        }

        if (_leafData is null || _leafData.Length != BlockSize)
        {
            _leafData = new byte[BlockSize];
        }

        data.AsSpan(0, (int)BlockSize).CopyTo(_leafData);
        _leafBlk = block;
    }

    /// <summary>Write the held-back pointer block: bitmap first, then the block.</summary>
    private void WriteBackLeaf()
    {
        if (_leafBlk == uint.MaxValue || _leafData is null)
        {
            return;
        }

        WriteBackBlockBitmap();
        WriteBlocks(_leafBlk, 1, _leafData);
        _leafBlk = uint.MaxValue;
    }

    /// <summary>
    /// Write deferred state in dependency order: bitmap, held-back pointer
    /// block, descriptors, superblock counters. The caller writes the inode
    /// afterwards.
    /// </summary>
    private void CommitAllocationState()
    {
        WriteBackBlockBitmap();
        WriteBackLeaf();
        if (_dirtyGroups.Count == 0)
        {
            return;
        }

        for (int i = 0; i < _dirtyGroups.Count; i++)
        {
            WriteGroupDesc(_dirtyGroups[i]);
        }

        _dirtyGroups.Clear();
        UpdateSuperblock();
    }

    /// <summary>
    /// Persist the volume's free block and inode counts to the superblock.
    /// </summary>
    internal void UpdateSuperblock()
    {
        // ZehOS patch ext2-readonly, see PATCHES.md
        if (IsReadOnly)
        {
            return;
        }

        ulong devBlockSize = Device.BlockSize;
        ulong sbByteOffset = Ext2SuperblockLayout.SuperblockOffset;
        ulong sbLba = sbByteOffset / devBlockSize;
        ulong blocksToRead = (Ext2SuperblockLayout.SuperblockSize + devBlockSize - 1) / devBlockSize;
        ulong totalBytes = blocksToRead * devBlockSize;
        byte[] buf = new byte[totalBytes];
        Device.ReadBlock(sbLba, blocksToRead, buf);
        int off = (int)(sbByteOffset % devBlockSize);
        Span<byte> sb = buf.AsSpan(off, Ext2SuperblockLayout.SuperblockSize);
        BitConverter.TryWriteBytes(sb.Slice(Ext2SuperblockLayout.FreeBlocksCountOffset, 4), FreeBlocksCount);
        BitConverter.TryWriteBytes(sb.Slice(Ext2SuperblockLayout.FreeInodesCountOffset, 4), FreeInodesCount);
        Device.WriteBlock(sbLba, blocksToRead, buf);
    }

    /// <summary>
    /// Tear down this mount: drop cached inodes and flush.
    /// </summary>
    public void Drop()
    {
        _inodeCache.Clear();
        InvalidateIndirCache();
        try
        {
            Flush();
        }
        finally
        {
            // ZehOS patch ext2-alloc-cache, see PATCHES.md
            // The VFS has removed the mount even when the flush throws.
            ReleaseWriteClaim();
        }
    }

    /// <summary>
    /// Register this mount as the only writable one of its device.
    /// </summary>
    /// <returns>false when another writable mount of the device is live.</returns>
    private bool TryClaimWrite()
    {
        s_writableLock.Acquire();
        try
        {
            for (int i = 0; i < s_writableDevices.Count; i++)
            {
                if (ReferenceEquals(s_writableDevices[i], Device))
                {
                    return false;
                }
            }

            s_writableDevices.Add(Device);
            _ownsWriteClaim = true;
            return true;
        }
        finally
        {
            s_writableLock.Release();
        }
    }

    /// <summary>Give up the write claim taken by <see cref="TryClaimWrite"/>.</summary>
    private void ReleaseWriteClaim()
    {
        if (!_ownsWriteClaim)
        {
            return;
        }

        s_writableLock.Acquire();
        try
        {
            for (int i = 0; i < s_writableDevices.Count; i++)
            {
                if (ReferenceEquals(s_writableDevices[i], Device))
                {
                    s_writableDevices.RemoveAt(i);
                    break;
                }
            }
            _ownsWriteClaim = false;
        }
        finally
        {
            s_writableLock.Release();
        }
    }

    /// <summary>
    /// Flush the device's volatile write cache — the durability point for sync and unmount.
    /// </summary>
    public void Flush()
    {
        // ZehOS patch ext2-readonly, see PATCHES.md
        if (IsReadOnly)
        {
            return;
        }

        // ZehOS patch ext2-alloc-cache, see PATCHES.md
        CommitAllocationState();
        Device.Flush();
        UpdateSuperblock();
    }

    /// <summary>
    /// Resolve a file-logical block to a physical block number, allocating
    /// through direct, single-indirect and double-indirect pointers when
    /// <paramref name="allocate"/> is set. Numbers outside the volume read
    /// as holes (0), never as device I/O.
    /// </summary>
    /// <param name="inode">File inode.</param>
    /// <param name="logicalBlock">Zero-based block index within the file.</param>
    /// <param name="allocate">Allocate a missing block instead of reporting a hole.</param>
    /// <param name="isNew">True when the call allocated the returned block.</param>
    /// <returns>Physical block number, or 0 for a hole or on failure.</returns>
    internal uint GetBlockPointer(Ext2Inode inode, uint logicalBlock, bool allocate, out bool isNew)
    {
        return GetBlockPointer(inode, logicalBlock, allocate, true, out isNew);
    }

    /// <summary>
    /// Resolve a file-logical block like the overload above. Indirect blocks
    /// are always zeroed; a newly allocated data block only when
    /// <paramref name="zeroData"/> is set.
    /// </summary>
    /// <param name="inode">File inode.</param>
    /// <param name="logicalBlock">Zero-based block index within the file.</param>
    /// <param name="allocate">Allocate a missing block instead of reporting a hole.</param>
    /// <param name="zeroData">Zero a newly allocated data block on disk. A caller
    /// that writes the whole block itself (with zeros where it has no data)
    /// passes false and saves one block write.</param>
    /// <param name="isNew">True when the call allocated the returned block.</param>
    /// <returns>Physical block number, or 0 for a hole or on failure.</returns>
    internal uint GetBlockPointer(Ext2Inode inode, uint logicalBlock, bool allocate, bool zeroData, out bool isNew)
    {
        isNew = false;
        if (BlockSize == 0)
        {
            return 0;
        }

        uint perBlock = BlockSize / 4;
        if (perBlock == 0)
        {
            return 0;
        }

        if (logicalBlock < Ext2InodeLayout.DirectBlockCount)
        {
            uint blk = inode.Block[logicalBlock];
            if (blk >= BlocksCount)
            {
                return 0;
            }

            if (blk == 0 && allocate)
            {
                if (!TryAllocateBlock(GroupOfInode(inode.InodeNumber), zeroData, out uint nb))
                {
                    return 0;
                }

                inode.Block[logicalBlock] = nb;
                inode.Blocks += BlockSize / 512;
                isNew = true;
                return nb;
            }

            return blk;
        }

        logicalBlock -= Ext2InodeLayout.DirectBlockCount;

        if (logicalBlock < perBlock)
        {
            uint indir = inode.Block[Ext2InodeLayout.SingleIndirectIndex];
            if (indir == 0)
            {
                if (!allocate)
                {
                    return 0;
                }

                if (!TryAllocateBlock(GroupOfInode(inode.InodeNumber), false, out uint nb))
                {
                    return 0;
                }

                indir = nb;
                inode.Block[Ext2InodeLayout.SingleIndirectIndex] = nb;
                inode.Blocks += BlockSize / 512;
                byte[] zero = new byte[BlockSize];
                WriteBlocks(indir, 1, zero);
                isNew = true;
            }

            if (indir == 0 || indir >= BlocksCount)
            {
                return 0;
            }

            if (_indirBuf1 is null || _indirBuf1.Length != BlockSize)
            {
                _indirBuf1 = new byte[BlockSize];
            }

            byte[] blockBuf = _indirBuf1;
            if (_indirCacheData is not null && _indirCacheBlk == indir)
            {
                _indirCacheData.AsSpan(0, (int)BlockSize).CopyTo(blockBuf);
            }
            else
            {
                LoadPointerBlock(indir, blockBuf); // ZehOS patch ext2-alloc-cache
                if (_indirCacheData is null || _indirCacheData.Length != BlockSize)
                {
                    _indirCacheData = new byte[BlockSize];
                }

                blockBuf.AsSpan(0, (int)BlockSize).CopyTo(_indirCacheData);
                _indirCacheBlk = indir;
            }

            uint entry = BitConverter.ToUInt32(blockBuf.AsSpan((int)logicalBlock * 4, 4));
            if (entry >= BlocksCount)
            {
                return 0;
            }

            if (entry == 0 && allocate)
            {
                if (!TryAllocateBlock(GroupOfInode(inode.InodeNumber), zeroData, out uint nb))
                {
                    return 0;
                }

                BitConverter.TryWriteBytes(blockBuf.AsSpan((int)logicalBlock * 4, 4), nb);
                StorePointerBlock(indir, blockBuf); // ZehOS patch ext2-alloc-cache
                blockBuf.AsSpan(0, (int)BlockSize).CopyTo(_indirCacheData);
                _indirCacheBlk = indir;
                inode.Blocks += BlockSize / 512;
                // ZehOS patch ext2-alloc-cache: TryAllocateBlock zeroes the block when zeroData is set;
                // otherwise the caller relies on isNew to zero-fill it in memory.
                isNew = true;
                return nb;
            }

            return entry;
        }

        logicalBlock -= perBlock;

        uint perBlockSq = perBlock * perBlock;
        if (logicalBlock < perBlockSq)
        {
            uint dindir = inode.Block[Ext2InodeLayout.DoubleIndirectIndex];
            if (dindir == 0)
            {
                if (!allocate)
                {
                    return 0;
                }

                if (!TryAllocateBlock(GroupOfInode(inode.InodeNumber), false, out uint nb))
                {
                    return 0;
                }

                dindir = nb;
                inode.Block[Ext2InodeLayout.DoubleIndirectIndex] = nb;
                inode.Blocks += BlockSize / 512;
                byte[] zero = new byte[BlockSize];
                WriteBlocks(dindir, 1, zero);
            }

            uint firstIdx = logicalBlock / perBlock;
            uint secondIdx = logicalBlock % perBlock;

            if (dindir == 0 || dindir >= BlocksCount)
            {
                return 0;
            }

            if (_indirBuf1 is null || _indirBuf1.Length != BlockSize)
            {
                _indirBuf1 = new byte[BlockSize];
            }

            byte[] firstBuf = _indirBuf1;
            if (_indirCacheData is not null && _indirCacheBlk == dindir)
            {
                _indirCacheData.AsSpan(0, (int)BlockSize).CopyTo(firstBuf);
            }
            else
            {
                ReadBlocks(dindir, 1, firstBuf);
                if (_indirCacheData is null || _indirCacheData.Length != BlockSize)
                {
                    _indirCacheData = new byte[BlockSize];
                }

                firstBuf.AsSpan(0, (int)BlockSize).CopyTo(_indirCacheData);
                _indirCacheBlk = dindir;
            }

            uint firstBlk = BitConverter.ToUInt32(firstBuf.AsSpan((int)firstIdx * 4, 4));
            if (firstBlk == 0)
            {
                if (!allocate)
                {
                    return 0;
                }

                if (!TryAllocateBlock(GroupOfInode(inode.InodeNumber), false, out uint nb))
                {
                    return 0;
                }

                firstBlk = nb;
                // ZehOS patch ext2-alloc-cache, see PATCHES.md
                // Zero the new pointer block and put its bitmap bit on disk
                // before the double-indirect block points at it.
                byte[] zero = new byte[BlockSize];
                WriteBlocks(firstBlk, 1, zero);
                WriteBackBlockBitmap();
                BitConverter.TryWriteBytes(firstBuf.AsSpan((int)firstIdx * 4, 4), nb);
                WriteBlocks(dindir, 1, firstBuf);
                firstBuf.AsSpan(0, (int)BlockSize).CopyTo(_indirCacheData);
                _indirCacheBlk = dindir;
                inode.Blocks += BlockSize / 512;
            }

            if (_indirBuf2 is null || _indirBuf2.Length != BlockSize)
            {
                _indirBuf2 = new byte[BlockSize];
            }

            byte[] secondBuf = _indirBuf2;
            if (firstBlk == 0 || firstBlk >= BlocksCount)
            {
                return 0;
            }

            LoadPointerBlock(firstBlk, secondBuf); // ZehOS patch ext2-alloc-cache
            uint entry = BitConverter.ToUInt32(secondBuf.AsSpan((int)secondIdx * 4, 4));
            if (entry >= BlocksCount)
            {
                return 0;
            }

            if (entry == 0 && allocate)
            {
                if (!TryAllocateBlock(GroupOfInode(inode.InodeNumber), zeroData, out uint nb))
                {
                    return 0;
                }

                BitConverter.TryWriteBytes(secondBuf.AsSpan((int)secondIdx * 4, 4), nb);
                StorePointerBlock(firstBlk, secondBuf); // ZehOS patch ext2-alloc-cache
                inode.Blocks += BlockSize / 512;
                // ZehOS patch ext2-alloc-cache: TryAllocateBlock zeroes the block when zeroData is set;
                // otherwise the caller relies on isNew to zero-fill it in memory.
                isNew = true;
                return nb;
            }

            return entry;
        }

        // Triple indirect would address gigabytes past the double range;
        // files that large are out of scope for this driver.
        return 0;
    }

    /// <summary>
    /// List the physical blocks of a file in logical order, stopping at the
    /// first hole.
    /// </summary>
    /// <param name="inode">File inode.</param>
    internal List<uint> CollectBlocks(Ext2Inode inode)
    {
        List<uint> result = [];
        uint blocksNeeded = (uint)((inode.FullSize + BlockSize - 1) / BlockSize);
        for (uint i = 0; i < blocksNeeded; i++)
        {
            uint blk = GetBlockPointer(inode, i, false, out _);
            if (blk == 0)
            {
                break;
            }

            result.Add(blk);
        }

        return result;
    }

    /// <summary>
    /// Shrink a file to <paramref name="newSize"/>, freeing data blocks past
    /// the new end and the pointer blocks whose whole range lies past it.
    /// Triple-indirect data blocks are not freed: this driver never maps or
    /// allocates them (see <see cref="GetBlockPointer(Ext2Inode, uint, bool, out bool)"/>).
    /// </summary>
    /// <param name="inode">File inode.</param>
    /// <param name="newSize">New size in bytes.</param>
    internal void Truncate(Ext2Inode inode, ulong newSize)
    {
        uint oldBlocks = (uint)((inode.FullSize + BlockSize - 1) / BlockSize);
        uint newBlocks = (uint)((newSize + BlockSize - 1) / BlockSize);
        if (newSize == 0)
        {
            newBlocks = 0;
        }

        // ZehOS patch ext2-truncate, see PATCHES.md
        // i_block holds no block pointers for a fast symlink (target stored
        // inline, no blocks apart from an extended attribute block) or for
        // device, fifo and socket inodes; reading it as pointers would free
        // blocks owned by other files. Like Linux ext2_truncate_blocks and
        // ext2_inode_is_fast_symlink, release nothing for them.
        uint eaSectors = inode.FileAcl != 0 ? BlockSize / 512 : 0;
        bool hasBlockPointers = inode.IsRegularFile || inode.IsDirectory || (inode.IsSymlink && inode.Blocks > eaSectors);
        if (!hasBlockPointers)
        {
            oldBlocks = 0;
        }

        // Plan before freeing data blocks, while the pointer tree is intact.
        Ext2IndirectBlocks.TruncatePlan? plan = null;
        if (newBlocks < oldBlocks)
        {
            plan = Ext2IndirectBlocks.PlanTruncate(inode.Block, newBlocks, BlockSize, BlocksCount, block =>
            {
                byte[] buffer = new byte[BlockSize];
                ReadBlocks(block, 1, buffer);
                return buffer;
            });
        }

        if (newBlocks < oldBlocks)
        {
            for (uint i = newBlocks; i < oldBlocks; i++)
            {
                uint blk = GetBlockPointer(inode, i, false, out _);
                if (blk != 0)
                {
                    // ZehOS patch ext2-truncate: clear the pointer before
                    // freeing, so an interruption never leaves an indirect
                    // entry pointing to a free block.
                    SetBlockPointer(inode, i, 0);
                    FreeBlock(blk);
                    inode.Blocks -= BlockSize / 512;
                }
            }
        }

        if (plan is not null)
        {
            // Clear and persist the pointers to freed subtrees before freeing
            // the pointer blocks: an interruption then leaks space at worst,
            // never leaves a pointer to a free pointer block.
            byte[] buffer = new byte[BlockSize];
            foreach ((uint block, int index) in plan.ClearEntries)
            {
                ReadBlocks(block, 1, buffer);
                BitConverter.TryWriteBytes(buffer.AsSpan(index * 4, 4), 0u);
                WriteBlocks(block, 1, buffer);
            }

            foreach (int root in plan.ClearRoots)
            {
                inode.Block[root] = 0;
            }

            if (plan.ClearRoots.Count != 0)
            {
                WriteInode(inode);
            }

            foreach (uint block in plan.Free)
            {
                FreeBlock(block);
                inode.Blocks -= BlockSize / 512;
            }
        }

        inode.Size = (uint)(newSize & 0xFFFF_FFFF);
        inode.SizeHigh = (uint)(newSize >> 32);
        WriteInode(inode);
        InvalidateIndirCache();
    }

    /// <summary>
    /// Drops the indirect block cache. Called after any write that mutates
    /// indirect block contents outside <see cref="GetBlockPointer(Ext2Inode, uint, bool, out bool)"/>.
    /// </summary>
    private void InvalidateIndirCache()
    {
        _indirCacheBlk = uint.MaxValue;
    }

    /// <summary>
    /// Clear a logical block pointer (used when truncating). Indirect slots
    /// are updated with a read-modify-write through the reusable buffers.
    /// </summary>
    /// <param name="inode">File inode.</param>
    /// <param name="logicalBlock">Zero-based block index within the file.</param>
    /// <param name="value">Pointer value to store.</param>
    private void SetBlockPointer(Ext2Inode inode, uint logicalBlock, uint value)
    {
        uint perBlock = BlockSize / 4;
        if (logicalBlock < Ext2InodeLayout.DirectBlockCount)
        {
            inode.Block[logicalBlock] = value;
            return;
        }

        logicalBlock -= Ext2InodeLayout.DirectBlockCount;
        if (logicalBlock < perBlock)
        {
            uint indir = inode.Block[Ext2InodeLayout.SingleIndirectIndex];
            if (indir == 0)
            {
                return;
            }

            if (_indirBuf1 is null || _indirBuf1.Length != BlockSize)
            {
                _indirBuf1 = new byte[BlockSize];
            }

            byte[] buf = _indirBuf1;
            ReadBlocks(indir, 1, buf);
            BitConverter.TryWriteBytes(buf.AsSpan((int)logicalBlock * 4, 4), value);
            WriteBlocks(indir, 1, buf);
            InvalidateIndirCache();
            return;
        }

        logicalBlock -= perBlock;
        uint perSq = perBlock * perBlock;
        if (logicalBlock < perSq)
        {
            uint dindir = inode.Block[Ext2InodeLayout.DoubleIndirectIndex];
            if (dindir == 0)
            {
                return;
            }

            uint firstIdx = logicalBlock / perBlock;
            uint secondIdx = logicalBlock % perBlock;
            if (_indirBuf1 is null || _indirBuf1.Length != BlockSize)
            {
                _indirBuf1 = new byte[BlockSize];
            }

            byte[] firstBuf = _indirBuf1;
            ReadBlocks(dindir, 1, firstBuf);
            uint firstBlk = BitConverter.ToUInt32(firstBuf.AsSpan((int)firstIdx * 4, 4));
            if (firstBlk == 0)
            {
                return;
            }

            if (_indirBuf2 is null || _indirBuf2.Length != BlockSize)
            {
                _indirBuf2 = new byte[BlockSize];
            }

            byte[] secondBuf = _indirBuf2;
            ReadBlocks(firstBlk, 1, secondBuf);
            BitConverter.TryWriteBytes(secondBuf.AsSpan((int)secondIdx * 4, 4), value);
            WriteBlocks(firstBlk, 1, secondBuf);
            InvalidateIndirCache();
        }
    }
}

/// <summary>
/// Parsed block group descriptor: bitmap and table locations plus the
/// group's free counters.
/// </summary>
internal sealed class Ext2GroupDesc
{
    /// <summary>Block holding the group's block bitmap.</summary>
    public uint BlockBitmap;

    /// <summary>Block holding the group's inode bitmap.</summary>
    public uint InodeBitmap;

    /// <summary>First block of the group's inode table.</summary>
    public uint InodeTable;

    /// <summary>Free blocks remaining in the group.</summary>
    public ushort FreeBlocksCount;

    /// <summary>Free inodes remaining in the group.</summary>
    public ushort FreeInodesCount;

    /// <summary>Directories in the group.</summary>
    public ushort UsedDirsCount;

    /// <summary>
    /// Creates a group descriptor.
    /// </summary>
    /// <param name="blockBitmap">Block holding the group's block bitmap.</param>
    /// <param name="inodeBitmap">Block holding the group's inode bitmap.</param>
    /// <param name="inodeTable">First block of the group's inode table.</param>
    /// <param name="freeBlocks">Free blocks remaining in the group.</param>
    /// <param name="freeInodes">Free inodes remaining in the group.</param>
    /// <param name="usedDirs">Directories in the group.</param>
    public Ext2GroupDesc(uint blockBitmap, uint inodeBitmap, uint inodeTable, ushort freeBlocks, ushort freeInodes, ushort usedDirs)
    {
        BlockBitmap = blockBitmap;
        InodeBitmap = inodeBitmap;
        InodeTable = inodeTable;
        FreeBlocksCount = freeBlocks;
        FreeInodesCount = freeInodes;
        UsedDirsCount = usedDirs;
    }
}
