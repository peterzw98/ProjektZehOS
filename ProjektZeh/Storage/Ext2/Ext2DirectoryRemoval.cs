// ZehOS-eigener Code; ersetzt Cosmos Ext2DirectoryHelper.RemoveEntry (efcf20cc, 3.0.97, Ext2Directory.cs:302-365).
using System;

namespace ProjektZeh.Storage.Ext2;

/// <summary>
/// Entfernt einen Verzeichniseintrag ohne Cosmos-Abhaengigkeit und haelt die ext2-Regel ein, dass kein Record eine
/// Blockgrenze ueberschreitet: verschmolzen wird nur mit dem Vorgaenger im selben Block, der erste Record eines Blocks wird
/// nur freigegeben (inode 0, rec_len bleibt; Cosmos AddEntry nutzt solche Plaetze wieder). Das Original trug den Vorgaenger
/// ueber Blockgrenzen weiter und lief bei vielen Loeschungen in den 16-Bit-rec_len ueber (E4.0 Lauf 9/10).
/// </summary>
internal static class Ext2DirectoryRemoval
{
    public const int NotFound = -1;
    /// <summary>Verzeichnis schon beschaedigt (Record ausserhalb des Blocks, rec_len ungueltig): nichts wird veraendert.</summary>
    public const int Malformed = -2;
    private const int InodeOffset = 0;
    private const int RecLenOffset = 4;
    private const int NameLenOffset = 6;
    private const int NameOffset = 8;

    /// <summary>
    /// Entfernt <paramref name="name"/> (UTF-8, exakter Bytevergleich wie Cosmos) aus den Verzeichnisdaten. Ergebnis: Index
    /// des einzigen geaenderten Blocks, den der Aufrufer zurueckschreibt, sonst <see cref="NotFound"/> oder <see cref="Malformed"/>.
    /// Ein Block wird erst geaendert, wenn sein Aufbau bis zum Treffer gueltig ist.
    /// </summary>
    public static int Remove(Span<byte> data, int blockSize, ReadOnlySpan<byte> name)
    {
        if (blockSize < NameOffset || data.Length % blockSize != 0) return Malformed;
        for (int block = 0; block < data.Length; block += blockSize)
        {
            int end = block + blockSize;
            int previous = -1;
            for (int pos = block; pos < end;)
            {
                if (pos + NameOffset > end) return Malformed;
                ushort recLen = BitConverter.ToUInt16(data.Slice(pos + RecLenOffset, 2));
                if (recLen < NameOffset || recLen % 4 != 0 || pos + recLen > end) return Malformed;
                uint inode = BitConverter.ToUInt32(data.Slice(pos + InodeOffset, 4));
                int nameLength = data[pos + NameLenOffset];
                if (inode != 0 && nameLength == name.Length && nameLength <= recLen - NameOffset
                    && data.Slice(pos + NameOffset, nameLength).SequenceEqual(name))
                {
                    if (previous >= 0)
                    {
                        ushort merged = (ushort)(BitConverter.ToUInt16(data.Slice(previous + RecLenOffset, 2)) + recLen);
                        BitConverter.TryWriteBytes(data.Slice(previous + RecLenOffset, 2), merged);
                    }
                    else
                    {
                        BitConverter.TryWriteBytes(data.Slice(pos + InodeOffset, 4), 0u);
                    }

                    return block / blockSize;
                }

                previous = pos;
                pos += recLen;
            }
        }

        return NotFound;
    }
}
