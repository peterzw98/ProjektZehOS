using System;

namespace ProjektZeh.Storage.Ext2;

/// <summary>Result of the pre-mount feature check of an ext2 superblock.</summary>
public enum Ext2MountDecision
{
    /// <summary>Every feature is known; read-write mount allowed.</summary>
    Ok,
    /// <summary>Only unknown compat/ro_compat bits: mount read-only at most.</summary>
    ReadOnly,
    /// <summary>Not mountable: not a supported ext2 volume or unknown incompat bits.</summary>
    Reject,
}

/// <summary>
/// Pure check on the 1024-byte superblock buffer (no Cosmos dependency). The allowlist is what our
/// formatter stamps (incompat FILETYPE, ro_compat SPARSE_SUPER) and nothing else; in particular
/// large_file (ro_compat 0x2) is not allowed because files are limited to 2 GiB - 1 for now.
/// Unknown compat bits (e.g. has_journal, resize_inode, dir_index) are not maintained by the driver,
/// so they only allow read-only, the conservative reading of the ext2 rules.
/// </summary>
public static class Ext2FeatureGate
{
    private const int SuperblockSize = 1024;
    private const int MagicOffset = 56;
    private const int StateOffset = 58;
    private const int LogBlockSizeOffset = 24;
    private const int RevLevelOffset = 76;
    private const int FeatureCompatOffset = 92;
    private const int FeatureIncompatOffset = 96;
    private const int FeatureRoCompatOffset = 100;

    private const ushort Magic = 0xEF53;
    private const uint RevDynamic = 1;
    private const uint LogBlockSize4096 = 2; // 1024 << 2
    private const uint KnownCompat = 0;
    private const uint KnownIncompat = 0x2; // FILETYPE
    private const uint KnownRoCompat = 0x1; // SPARSE_SUPER

    public static Ext2MountDecision Evaluate(ReadOnlySpan<byte> superblock)
    {
        if (superblock.Length < SuperblockSize
            || BitConverter.ToUInt16(superblock.Slice(MagicOffset, 2)) != Magic
            || BitConverter.ToUInt32(superblock.Slice(RevLevelOffset, 4)) != RevDynamic
            || BitConverter.ToUInt32(superblock.Slice(LogBlockSizeOffset, 4)) != LogBlockSize4096
            || (BitConverter.ToUInt32(superblock.Slice(FeatureIncompatOffset, 4)) & ~KnownIncompat) != 0)
        {
            return Ext2MountDecision.Reject;
        }

        ushort state = BitConverter.ToUInt16(superblock.Slice(StateOffset, 2));
        if (state > 2)
        {
            return Ext2MountDecision.Reject;
        }

        bool unknownCompat = (BitConverter.ToUInt32(superblock.Slice(FeatureCompatOffset, 4)) & ~KnownCompat) != 0;
        bool unknownRoCompat = (BitConverter.ToUInt32(superblock.Slice(FeatureRoCompatOffset, 4)) & ~KnownRoCompat) != 0;
        return state != 1 || unknownCompat || unknownRoCompat ? Ext2MountDecision.ReadOnly : Ext2MountDecision.Ok;
    }
}
