# Entwurf: Upstream-Issue für Cosmos (ext2, 3.0.97)

**Nicht veröffentlichen** ohne Freigabe von laser oder Peter. Quellstand: CosmosOS/Cosmos
`efcf20cc62884b042a0adadffe9fc4540491fd89` (Paket `Cosmos.Kernel.System` 3.0.97). Belege: e2fsprogs 1.47.4
`e2fsck -fn` auf frischen Images (`experiments/StorageProbe`) und auf der Gastprobe `-p:ZehOSStorageProbe=true`.
ZehOS-Umgehungen: `ProjektZeh/Storage/Ext2/` (Formatter), `ProjektZeh/Storage/CosmosExt2FreeInodePlug.cs`.

---

**Title:** ext2: formatter output fails e2fsck; rmdir/unlink leave freed inodes live; block numbering ignores s_first_data_block

**1. Formatter (`src/Cosmos.Kernel.System/FileSystem/Ext2/Ext2Formatter.cs`)**

- No backup superblocks/descriptor tables are written, yet `sparse_super` is not set either; from two groups on,
  e2fsck reports `Corrupt group descriptor: bad block for block bitmap` (block/inode bitmaps sit where backups belong).
- With 1 KiB blocks (`s_first_data_block = 1`) groups are laid out from block 0; group-0 bitmap bit b is treated as
  block b instead of block 1 + b (`Block bitmap differences: -134` on a 4 MiB volume).
- Padding bits past the end of the block and inode bitmaps are left clear (`Padding at end of ... bitmap is not set`).
- Result: 18 of 18 fresh images (4 MiB … 128 GiB × 1/2/4 KiB) end with e2fsck exit 4.

**2. Rmdir (`Ext2InodeOperations.cs:442-500`)**

Lines 483-484 set `target.LinksCount = 0` only in memory and call `FreeInode`; the inode is never written back and
gets no `i_dtime`. On disk it stays a live directory referencing its (now free) block. e2fsck after `rmdir`:
`Unconnected directory inode 13`, `'..' in ... (13) is / (2), should be <The NULL inode>`, `Inode 2 ref count is 2,
should be 3`, `Inode 13 ref count is 2, should be 1`, `Block bitmap differences: +41`, `Inode bitmap differences: +13`.

**3. Unlink / FreeInode (`Ext2InodeOperations.cs:425-426`, `Ext2Superblock.cs:617-632`)**

`Truncate` writes the inode with `i_links_count = 0` (`Ext2Superblock.cs:1074`), but `FreeInode` never sets
`i_dtime`: e2fsck reports `Deleted inode 12 has zero dtime`. Suggested fix: `FreeInode` writes `i_links_count = 0`
and `i_dtime = now` before clearing the bitmap bit (covers rmdir, unlink and rename-over).

**4. Block numbering ignores `s_first_data_block` (`Ext2Superblock.cs`)**

`TryCreate` line 260 computes `groupsCount = ceil(blocksCount / blocksPerGroup)`, `TryAllocateBlock` uses
`blockNumber = gi * BlocksPerGroup + i` (line 675) and `BlocksCount % BlocksPerGroup` for the last group (line 657),
`FreeBlock` uses `blockNumber / BlocksPerGroup` (line 693). With 1 KiB blocks every group starts at
`1 + g * blocksPerGroup`, so on a correctly laid out volume allocation is off by one block and can hand out metadata.

**5. Feature bits are read but not checked on mount** (`Ext2Superblock.TryCreate`), so volumes with unsupported
incompat/ro_compat features (e.g. extents, large_file) mount writable.
