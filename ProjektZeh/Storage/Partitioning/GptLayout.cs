using System;
using System.Collections.Generic;
using System.Text;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Partitioning;

/// <summary>Ein belegter GPT-Eintrag; <paramref name="LastLba"/> ist wie in UEFI einschliesslich.</summary>
internal sealed record GptEntry(Guid Type, Guid Unique, ulong FirstLba, ulong LastLba, string Name);

/// <summary>Gepruefte Tabelle: Disk-GUID und die belegten Eintraege in Tabellenreihenfolge.</summary>
internal sealed record GptTable(Guid DiskGuid, IReadOnlyList<GptEntry> Entries);

/// <summary>
/// Layout der ZehOS-GPT (Etappe 7a, Spec 2026-11-17-storage-7a-installer-layout.md Abschnitt 3): 512-Byte-Sektoren,
/// 32 bis 128 GiB, 128 Eintraege zu 128 Byte, Primaerkopf LBA 1, Backup in den letzten 33 Sektoren. Writer und Verify
/// teilen die Bausteine hier, damit der Leser genau das prueft, was der Writer schreibt.
/// </summary>
internal static class GptLayout
{
    public const ulong SectorSize = 512;
    public const ulong MinBlockCount = 32UL * 1024 * 1024 * 1024 / SectorSize;
    /// <summary>128 GiB, zugleich die Obergrenze des korrigierten ext2-Formatters.</summary>
    public const ulong MaxBlockCount = 268435456;
    public const uint EntryCount = 128;
    public const uint EntrySize = 128;
    public const ulong EntrySectors = EntryCount * EntrySize / SectorSize;
    public const ulong PrimaryHeaderLba = 1;
    public const ulong PrimaryEntriesLba = 2;
    public const ulong FirstUsableLba = PrimaryEntriesLba + EntrySectors;

    public static readonly Guid EspType = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid LinuxFileSystemType = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    /// <summary>Microsoft Basic Data; so traegt eine GPT-Platte eine FAT-Datenpartition wie ZEHOSDATA.</summary>
    public static readonly Guid BasicDataType = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

    private const ulong Signature = 0x5452415020494645UL; // "EFI PART"
    private const uint Revision = 0x00010000;
    private const uint HeaderSize = 92;
    private const int HeaderCrcOffset = 16;
    private const int NameOffset = 56;
    private const int NameChars = 36;
    private const int MbrTableOffset = 446;
    private const byte ProtectiveType = 0xEE;

    public static ulong LastUsableLba(ulong blockCount) => blockCount - 1 - EntrySectors - 1;
    public static ulong BackupEntriesLba(ulong blockCount) => blockCount - 1 - EntrySectors;
    public static ulong BackupHeaderLba(ulong blockCount) => blockCount - 1;

    internal static bool SupportedGeometry(ulong blockSize, ulong blockCount) =>
        blockSize == SectorSize && blockCount >= MinBlockCount && blockCount <= MaxBlockCount;

    /// <summary>Eintragstabelle (16 KiB); freie Eintraege bleiben vollstaendig null, wie UEFI es verlangt.</summary>
    internal static byte[] BuildEntries(IReadOnlyList<GptEntry> entries)
    {
        var array = new byte[EntryCount * EntrySize];
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = array.AsSpan(i * (int)EntrySize, (int)EntrySize);
            entries[i].Type.TryWriteBytes(entry[..16]);
            entries[i].Unique.TryWriteBytes(entry.Slice(16, 16));
            BitConverter.TryWriteBytes(entry.Slice(32, 8), entries[i].FirstLba);
            BitConverter.TryWriteBytes(entry.Slice(40, 8), entries[i].LastLba);
            // UTF-16LE von Hand: die Namen sind ASCII, und Encoding.Unicode ist im Kernel nicht belegt.
            for (int c = 0; c < Math.Min(entries[i].Name.Length, NameChars); c++)
                BitConverter.TryWriteBytes(entry.Slice(NameOffset + c * 2, 2), (ushort)entries[i].Name[c]);
        }
        return array;
    }

    /// <summary>Kopfsektor; die CRC laeuft ueber genau HeaderSize Byte mit genulltem CRC-Feld.</summary>
    internal static byte[] BuildHeader(ulong blockCount, Guid diskGuid, ulong myLba, ulong alternateLba, ulong entriesLba, uint entriesCrc)
    {
        var header = new byte[SectorSize];
        var span = header.AsSpan();
        BitConverter.TryWriteBytes(span[..8], Signature);
        BitConverter.TryWriteBytes(span.Slice(8, 4), Revision);
        BitConverter.TryWriteBytes(span.Slice(12, 4), HeaderSize);
        BitConverter.TryWriteBytes(span.Slice(24, 8), myLba);
        BitConverter.TryWriteBytes(span.Slice(32, 8), alternateLba);
        BitConverter.TryWriteBytes(span.Slice(40, 8), FirstUsableLba);
        BitConverter.TryWriteBytes(span.Slice(48, 8), LastUsableLba(blockCount));
        diskGuid.TryWriteBytes(span.Slice(56, 16));
        BitConverter.TryWriteBytes(span.Slice(72, 8), entriesLba);
        BitConverter.TryWriteBytes(span.Slice(80, 4), EntryCount);
        BitConverter.TryWriteBytes(span.Slice(84, 4), EntrySize);
        BitConverter.TryWriteBytes(span.Slice(88, 4), entriesCrc);
        BitConverter.TryWriteBytes(span.Slice(HeaderCrcOffset, 4), Crc32.Compute(span[..(int)HeaderSize]));
        return header;
    }

    /// <summary>Schutz-MBR: genau ein 0xEE-Eintrag ab LBA 1 ueber die ganze Platte, kein Hybrid-MBR.</summary>
    internal static byte[] BuildProtectiveMbr(ulong blockCount)
    {
        var mbr = new byte[SectorSize];
        var entry = mbr.AsSpan(MbrTableOffset, 16);
        entry[2] = 0x02; // CHS-Start 0/0/2 = LBA 1, wie UEFI 2.10 Tabelle 5-4
        entry[4] = ProtectiveType;
        entry[5] = entry[6] = entry[7] = 0xFF; // CHS-Ende nicht darstellbar
        BitConverter.TryWriteBytes(entry.Slice(8, 4), (uint)PrimaryHeaderLba);
        BitConverter.TryWriteBytes(entry.Slice(12, 4), (uint)Math.Min(blockCount - 1, uint.MaxValue));
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        return mbr;
    }

    /// <summary>
    /// Prueft eine Platte gegen genau dieses Layout. Gpt.Parse aus Cosmos 3.0.97 prueft weder CRCs noch Backup, die
    /// Firmware nimmt bei kaputtem Primaerkopf dagegen den Backup; deshalb gilt nur eine Platte, auf der Schutz-MBR,
    /// beide Koepfe und beide Tabellen zusammenpassen. Jede Abweichung ist ungueltig (Spec: dann Unclear).
    /// </summary>
    public static ResultObject<GptTable> Verify(ulong blockSize, ulong blockCount, BlockReader read)
    {
        if (!SupportedGeometry(blockSize, blockCount))
            return Failure("UnsupportedGeometry", "Nur 512-Byte-Sektoren und 32 bis 128 GiB.");

        var sector = new byte[SectorSize];
        read(0, 1, sector);
        if (!ProtectiveMbrValid(sector, blockCount))
            return Failure("ProtectiveMbrInvalid", "Schutz-MBR fehlt oder weicht ab.");

        var primary = new byte[SectorSize];
        read(PrimaryHeaderLba, 1, primary);
        var check = CheckHeader(primary, blockCount, PrimaryHeaderLba, BackupHeaderLba(blockCount), PrimaryEntriesLba, "Primary");
        if (!check.IsOK()) return ResultObjectFactory.CreateFailure<GptTable>(check);

        var backup = new byte[SectorSize];
        read(BackupHeaderLba(blockCount), 1, backup);
        check = CheckHeader(backup, blockCount, BackupHeaderLba(blockCount), PrimaryHeaderLba, BackupEntriesLba(blockCount), "Backup");
        if (!check.IsOK()) return ResultObjectFactory.CreateFailure<GptTable>(check);

        // Gleich sein muessen alle Felder ausser CRC, MyLBA, AlternateLBA und EntryLBA (die unterscheiden sich gewollt).
        if (!primary.AsSpan(40, 32).SequenceEqual(backup.AsSpan(40, 32)) || !primary.AsSpan(80, 8).SequenceEqual(backup.AsSpan(80, 8)))
            return Failure("HeadersDiffer", "Primaer- und Backupkopf beschreiben verschiedene Tabellen.");
        if (new Guid(primary.AsSpan(56, 16)) == Guid.Empty)
            return Failure("EmptyDiskGuid", "Die Disk-GUID fehlt.");

        var primaryEntries = new byte[EntryCount * EntrySize];
        read(PrimaryEntriesLba, EntrySectors, primaryEntries);
        if (Crc32.Compute(primaryEntries) != BitConverter.ToUInt32(primary, 88))
            return Failure("PrimaryEntriesCrc", "Primaere Eintragstabelle passt nicht zur Pruefsumme.");
        var backupEntries = new byte[EntryCount * EntrySize];
        read(BackupEntriesLba(blockCount), EntrySectors, backupEntries);
        if (Crc32.Compute(backupEntries) != BitConverter.ToUInt32(backup, 88))
            return Failure("BackupEntriesCrc", "Backup-Eintragstabelle passt nicht zur Pruefsumme.");
        if (!primaryEntries.AsSpan().SequenceEqual(backupEntries))
            return Failure("EntriesDiffer", "Primaer- und Backup-Eintragstabelle unterscheiden sich.");

        var entries = new List<GptEntry>();
        for (int i = 0; i < EntryCount; i++)
        {
            var raw = primaryEntries.AsSpan(i * (int)EntrySize, (int)EntrySize);
            var type = new Guid(raw[..16]);
            if (type == Guid.Empty) continue;
            var entry = new GptEntry(type, new Guid(raw.Slice(16, 16)), BitConverter.ToUInt64(raw.Slice(32, 8)),
                BitConverter.ToUInt64(raw.Slice(40, 8)), ReadName(raw[NameOffset..]));
            if (entry.FirstLba > entry.LastLba || entry.FirstLba < FirstUsableLba || entry.LastLba > LastUsableLba(blockCount))
                return Failure("EntryOutOfRange", $"Eintrag {i} liegt ausserhalb von FirstUsable..LastUsable.");
            if (entry.Unique == Guid.Empty)
                return Failure("EmptyUniqueGuid", $"Eintrag {i} hat keine Partitions-GUID.");
            foreach (var other in entries)
            {
                if (other.Unique == entry.Unique) return Failure("DuplicateUniqueGuid", $"Eintrag {i} wiederholt eine Partitions-GUID.");
                if (entry.FirstLba <= other.LastLba && other.FirstLba <= entry.LastLba)
                    return Failure("EntriesOverlap", $"Eintrag {i} ueberlappt einen anderen Eintrag.");
            }
            entries.Add(entry);
        }
        return ResultObjectFactory.CreateSuccess(new GptTable(new Guid(primary.AsSpan(56, 16)), entries));
    }

    /// <summary>
    /// Irgendeine GPT-Spur: ein 0xEE-Eintrag im MBR oder die Signatur EFI PART am Anfang von LBA 1 oder der letzten LBA.
    /// Anders als <see cref="Verify"/> unabhaengig von Geometrie und Gueltigkeit, damit auch ein abgebrochener oder
    /// beschaedigter Schreibvorgang als belegt gilt. Liest hoechstens drei Sektoren; erwartet Sektoren ab 512 Byte.
    /// </summary>
    internal static bool HasTraces(ulong blockSize, ulong blockCount, BlockReader read)
    {
        var sector = new byte[blockSize];
        read(0, 1, sector);
        for (int slot = 0; slot < 4; slot++)
            if (sector[MbrTableOffset + slot * 16 + 4] == ProtectiveType) return true;
        if (blockCount > PrimaryHeaderLba && HasSignature(read, PrimaryHeaderLba, sector)) return true;
        ulong last = blockCount - 1;
        return last > PrimaryHeaderLba && HasSignature(read, last, sector);
    }

    private static bool HasSignature(BlockReader read, ulong lba, byte[] sector)
    {
        read(lba, 1, sector);
        return BitConverter.ToUInt64(sector, 0) == Signature;
    }

    private static bool ProtectiveMbrValid(ReadOnlySpan<byte> mbr, ulong blockCount)
    {
        if (mbr[510] != 0x55 || mbr[511] != 0xAA) return false;
        var entry = mbr.Slice(MbrTableOffset, 16);
        if (entry[4] != ProtectiveType || BitConverter.ToUInt32(entry.Slice(8, 4)) != PrimaryHeaderLba ||
            BitConverter.ToUInt32(entry.Slice(12, 4)) != (uint)Math.Min(blockCount - 1, uint.MaxValue)) return false;
        foreach (byte value in mbr.Slice(MbrTableOffset + 16, 48)) if (value != 0) return false;
        return true;
    }

    /// <summary>Feldpruefung in fester Reihenfolge; Anzahl und Groesse der Eintraege sind vor jedem Tabellenlesen festgelegt.</summary>
    private static ResultErr CheckHeader(byte[] header, ulong blockCount, ulong myLba, ulong alternateLba, ulong entriesLba, string copy)
    {
        if (BitConverter.ToUInt64(header, 0) != Signature) return FailureErr($"{copy}HeaderSignature", "Signatur EFI PART fehlt.");
        if (BitConverter.ToUInt32(header, 8) != Revision) return FailureErr($"{copy}HeaderRevision", "Unbekannte GPT-Revision.");
        if (BitConverter.ToUInt32(header, 12) != HeaderSize) return FailureErr($"{copy}HeaderSize", "Kopfgroesse ist nicht 92 Byte.");
        var sealedPart = header.AsSpan(0, (int)HeaderSize).ToArray();
        uint stored = BitConverter.ToUInt32(sealedPart, HeaderCrcOffset);
        sealedPart.AsSpan(HeaderCrcOffset, 4).Clear();
        if (Crc32.Compute(sealedPart) != stored) return FailureErr($"{copy}HeaderCrc", "Kopf passt nicht zur Pruefsumme.");
        if (BitConverter.ToUInt32(header, 20) != 0 || BitConverter.ToUInt64(header, 24) != myLba ||
            BitConverter.ToUInt64(header, 32) != alternateLba || BitConverter.ToUInt64(header, 40) != FirstUsableLba ||
            BitConverter.ToUInt64(header, 48) != LastUsableLba(blockCount) || BitConverter.ToUInt64(header, 72) != entriesLba ||
            BitConverter.ToUInt32(header, 80) != EntryCount || BitConverter.ToUInt32(header, 84) != EntrySize)
            return FailureErr($"{copy}HeaderLayout", "Kopffelder passen nicht zu Plattengroesse und Layout.");
        return ResultErr.CreateSuccess();
    }

    private static string ReadName(ReadOnlySpan<byte> raw)
    {
        var name = new StringBuilder(NameChars);
        for (int c = 0; c < NameChars; c++)
        {
            char value = (char)BitConverter.ToUInt16(raw.Slice(c * 2, 2));
            if (value == '\0') break;
            name.Append(value);
        }
        return name.ToString();
    }

    private static ResultErr FailureErr(string cause, string message) =>
        ResultErr.CreateFailure(ResultCode.Failure, $"cause='{cause}' message='{message}'");

    internal static ResultObject<GptTable> Failure(string cause, string message) =>
        ResultObjectFactory.CreateFailure<GptTable>(FailureErr(cause, message));
}
