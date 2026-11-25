// This code is licensed under the BSD 3-Clause license (see LICENSE for details)

namespace Cosmos.Kernel.System.FileSystem.Ext2;

/// <summary>
/// VFS inode backed by an ext2 inode. Carries the parsed on-disk fields so
/// metadata updates (size, mode, link count) persist without a fresh inode
/// table read.
/// </summary>
internal sealed class Ext2Inode : IVfsInode
{
    /// <summary>The mounted volume owning this inode.</summary>
    public Ext2Superblock Superblock { get; }

    /// <summary>One-based inode number, unique within the volume.</summary>
    public uint InodeNumber { get; }

    /// <summary>Leaf name within the parent directory; updated on re-lookup.</summary>
    public string Name { get; internal set; }

    /// <summary>File type plus permission bits (i_mode).</summary>
    public ushort Mode { get; internal set { field = value; Dirty = true; } }

    /// <summary>Owner user id (i_uid).</summary>
    public ushort Uid { get; internal set { field = value; Dirty = true; } }

    /// <summary>Owner group id (i_gid).</summary>
    public ushort Gid { get; internal set { field = value; Dirty = true; } }

    /// <summary>File size in bytes, low 32 bits (i_size).</summary>
    public uint Size { get; internal set { field = value; Dirty = true; } }

    /// <summary>File size in bytes, high 32 bits (i_dir_acl for regular files).</summary>
    public uint SizeHigh { get; internal set { field = value; Dirty = true; } }

    /// <summary>Full 64-bit file size.</summary>
    public ulong FullSize => ((ulong)SizeHigh << 32) | Size;

    /// <summary>Last access time, Unix seconds (i_atime).</summary>
    public uint Atime { get; internal set { field = value; Dirty = true; } }

    /// <summary>Inode change time, Unix seconds (i_ctime).</summary>
    public uint Ctime { get; internal set { field = value; Dirty = true; } }

    /// <summary>Last modification time, Unix seconds (i_mtime).</summary>
    public uint Mtime { get; internal set { field = value; Dirty = true; } }

    /// <summary>Deletion time, Unix seconds (i_dtime).</summary>
    public uint Dtime { get; internal set { field = value; Dirty = true; } }

    /// <summary>Hard link count (i_links_count).</summary>
    public ushort LinksCount { get; internal set { field = value; Dirty = true; } }

    /// <summary>512-byte sectors allocated to the inode (i_blocks).</summary>
    public uint Blocks { get; internal set { field = value; Dirty = true; } }

    /// <summary>Inode flags (i_flags).</summary>
    public uint Flags { get; internal set { field = value; Dirty = true; } }

    // ZehOS patch ext2-truncate, see PATCHES.md
    /// <summary>Extended attribute block (i_file_acl); 0 when none. Read only, never written back.</summary>
    public uint FileAcl { get; internal set; }

    /// <summary>15 block pointers: 12 direct, then single, double and triple indirect.</summary>
    public uint[] Block { get; internal set; } = new uint[Ext2InodeLayout.BlockCount];

    // ZehOS patch ext2-inode-cache-evict, see PATCHES.md
    /// <summary>
    /// Fields are ahead of the disk; keeps the inode in the cache. Set by the
    /// setters above. Elements of <see cref="Block"/> do not set it: whoever
    /// changes one changes <see cref="Blocks"/> or <see cref="Size"/> too.
    /// </summary>
    internal bool Dirty { get; set; }

    /// <summary>The inode was released: its number may belong to another file, so it is never written again.</summary>
    internal bool Detached { get; set; }

    /// <summary>Slot in the <see cref="Ext2InodeCache"/>; valid while cached, under its lock.</summary>
    internal int CacheSlot { get; set; }

    /// <summary>Looked up since the cache last considered evicting it; under the cache's lock.</summary>
    internal bool CacheReferenced { get; set; }

    /// <inheritdoc/>
    public IInodeOperations InodeOperations => Superblock.InodeOps;

    /// <inheritdoc/>
    public IFileOperations? FileOperations
    {
        get
        {
            ushort type = (ushort)(Mode & Ext2InodeLayout.IFMT);
            // Directories have no file ops; reads go through ReadDir.
            if (type == Ext2InodeLayout.IFDIR)
            {
                return null;
            }

            // Regular files and symlinks (for reading the target) are readable.
            if (type is Ext2InodeLayout.IFREG or Ext2InodeLayout.IFLNK)
            {
                return Superblock.FileOps;
            }

            return null;
        }
    }

    /// <summary>True when the mode encodes a directory.</summary>
    public bool IsDirectory => (Mode & Ext2InodeLayout.IFMT) == Ext2InodeLayout.IFDIR;

    /// <summary>True when the mode encodes a symbolic link.</summary>
    public bool IsSymlink => (Mode & Ext2InodeLayout.IFMT) == Ext2InodeLayout.IFLNK;

    /// <summary>True when the mode encodes a regular file.</summary>
    public bool IsRegularFile => (Mode & Ext2InodeLayout.IFMT) == Ext2InodeLayout.IFREG;

    /// <summary>
    /// Creates an inode handle.
    /// </summary>
    /// <param name="superblock">The mounted volume owning the inode.</param>
    /// <param name="inodeNumber">One-based inode number.</param>
    /// <param name="name">Leaf name within the parent directory.</param>
    public Ext2Inode(Ext2Superblock superblock, uint inodeNumber, string name)
    {
        Superblock = superblock;
        InodeNumber = inodeNumber;
        Name = name;
    }
}
