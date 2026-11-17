// ZehOS-eigener Code; Ablauf nach Cosmos Ext2Superblock.FreeInode (efcf20cc, 3.0.97), ergaenzt um das Loeschen auf der Platte.
using System;

namespace ProjektZeh.Storage.Ext2;

/// <summary>
/// Plattenseite der Inode-Freigabe ohne Cosmos-Abhaengigkeit: erst das Inode mit links_count 0 und dtime schreiben,
/// dann das Bitmapbit loeschen. Cosmos 3.0.97 Rmdir schreibt das Inode sonst nie, Unlink setzt kein dtime.
/// Bloecke gibt diese Funktion nicht frei; das erledigen Rmdir/Truncate bereits selbst.
/// </summary>
internal static class Ext2InodeRelease
{
    private const int DtimeOffset = 20;
    private const int LinksCountOffset = 26;

    /// <summary>
    /// Gibt Inode <paramref name="index"/> (0-basiert in seiner Gruppe) frei. false: das Bit war schon frei; dann wird
    /// nichts geschrieben, und der Aufrufer darf die Freizaehler nicht ein zweites Mal erhoehen.
    /// </summary>
    public static bool Release(Func<uint, byte[]> readBlock, Action<uint, byte[]> writeBlock,
        uint inodeTable, uint inodeBitmap, uint index, uint blockSize, uint inodeSize, uint now)
    {
        byte[] bitmap = readBlock(inodeBitmap);
        byte mask = (byte)(1 << (int)(index % 8));
        if ((bitmap[index / 8] & mask) == 0) return false;

        uint inodesPerBlock = blockSize / inodeSize;
        uint tableBlock = inodeTable + index / inodesPerBlock;
        byte[] table = readBlock(tableBlock);
        int offset = (int)(index % inodesPerBlock * inodeSize);
        BitConverter.TryWriteBytes(table.AsSpan(offset + LinksCountOffset, 2), (ushort)0);
        // dtime 0 hiesse fuer e2fsck "nicht geloescht"; ohne gueltige Uhr wenigstens 1.
        BitConverter.TryWriteBytes(table.AsSpan(offset + DtimeOffset, 4), Math.Max(now, 1u));
        writeBlock(tableBlock, table);

        bitmap[index / 8] &= (byte)~mask;
        writeBlock(inodeBitmap, bitmap);
        return true;
    }
}
