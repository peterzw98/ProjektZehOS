using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Tests;

/// <summary>Etappe 7a Schritt 1: GPT-Writer, Verify und CRC32 (Spec 2026-11-17-storage-7a-installer-layout.md, Abschnitt 3 und 7).</summary>
[TestClass]
public sealed class GptLayoutTests
{
    private const ulong Sector = 512;
    private const ulong GiB = 1024UL * 1024 * 1024;
    private const ulong Blocks32 = 32 * GiB / Sector;
    private const ulong Blocks64 = 64 * GiB / Sector;
    private const ulong Blocks128 = 128 * GiB / Sector;

    /// <summary>Duenn belegte Platte: nur beschriebene Sektoren existieren; bricht auf Wunsch vor dem n-ten Schreibaufruf ab.</summary>
    private sealed class SparseDisk(ulong blockCount, int failAtWrite = 0)
    {
        private readonly Dictionary<ulong, byte[]> sectors = [];
        public ulong BlockCount { get; } = blockCount;
        public int Writes { get; private set; }
        public int Flushes { get; private set; }
        public List<string> Log { get; } = [];

        public void Write(ulong block, ReadOnlySpan<byte> data)
        {
            Assert.AreEqual(0, data.Length % (int)Sector, "nur ganze Sektoren");
            Assert.IsTrue(block + (ulong)data.Length / Sector <= BlockCount, "Zugriff ueber das Plattenende");
            if (++Writes == failAtWrite) throw new IOException("Abbruch im Test");
            for (int i = 0; i < data.Length / (int)Sector; i++)
                sectors[block + (ulong)i] = data.Slice(i * (int)Sector, (int)Sector).ToArray();
            Log.Add($"W{block}+{data.Length / (int)Sector}");
        }

        public void Flush() { Flushes++; Log.Add("F"); }

        public void Read(ulong block, ulong count, Span<byte> buffer)
        {
            Assert.IsTrue(block + count <= BlockCount, "Zugriff ueber das Plattenende");
            for (ulong i = 0; i < count; i++)
            {
                var target = buffer.Slice((int)(i * Sector), (int)Sector);
                if (sectors.TryGetValue(block + i, out byte[]? data)) data.CopyTo(target);
                else target.Clear();
            }
        }

        public byte[] ReadSector(ulong block)
        {
            var buffer = new byte[Sector];
            Read(block, 1, buffer);
            return buffer;
        }

        public void Put(ulong block, byte[] data)
        {
            for (int i = 0; i < data.Length / (int)Sector; i++) sectors[block + (ulong)i] = data.AsSpan(i * (int)Sector, (int)Sector).ToArray();
        }

        public SparseDisk CopyTo(ulong blockCount)
        {
            var copy = new SparseDisk(blockCount);
            foreach (var pair in sectors) copy.sectors[pair.Key] = (byte[])pair.Value.Clone();
            return copy;
        }
    }

    /// <summary>Feste "Zufallsquelle" fuer reproduzierbare Bytes; zaehlt Aufrufe.</summary>
    private sealed class FixedRandom(byte seed)
    {
        public int Calls { get; private set; }
        public void Fill(byte[] bytes)
        {
            Calls++;
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i * 7);
        }
    }

    private static ResultObjectOf Write(SparseDisk disk, Action<byte[]>? random = null, ulong blockSize = Sector) =>
        new(GptLayoutWriter.Write(blockSize, disk.BlockCount, random ?? new FixedRandom(1).Fill, disk.Write, disk.Flush));

    private static ProjektZeh.Shared.ResultObject<GptTable> Verify(SparseDisk disk) => GptLayout.Verify(Sector, disk.BlockCount, disk.Read);

    /// <summary>Kurzform, damit die Tests Status und Tabelle lesbar pruefen.</summary>
    private sealed class ResultObjectOf(ProjektZeh.Shared.ResultObject<GptTable> result)
    {
        public bool IsOK => result.IsOK();
        public string Message => result.Status.Message;
        public GptTable Table => result.Value!;
    }

    private static void AssertCause(ProjektZeh.Shared.ResultObject<GptTable> result, string cause)
    {
        Assert.IsFalse(result.IsOK(), "erwartet ungueltig: " + cause);
        StringAssert.Contains(result.Status.Message, $"cause='{cause}'");
    }

    // --- CRC32 ---

    [TestMethod]
    public void Crc32_MatchesReferenceVectors()
    {
        // Referenzwerte aus zlib.crc32 (gleiches Polynom wie UEFI).
        Assert.AreEqual(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
        Assert.AreEqual(0u, Crc32.Compute([]));
        Assert.AreEqual(0xC2B526E7u, Crc32.Compute(new byte[92]));
        var pattern = new byte[16384];
        for (int i = 0; i < pattern.Length; i++) pattern[i] = (byte)i;
        Assert.AreEqual(0xE81722F0u, Crc32.Compute(pattern));
    }

    // --- Writer: Layout ---

    [TestMethod]
    [DataRow(Blocks32)]
    [DataRow(Blocks64)]
    [DataRow(Blocks128)]
    public void Write_TargetSizes_ProducesVerifiedLayout(ulong blockCount)
    {
        var disk = new SparseDisk(blockCount);
        var written = Write(disk);
        Assert.IsTrue(written.IsOK, written.Message);

        var verified = Verify(disk);
        Assert.IsTrue(verified.IsOK(), verified.Status.Message);
        Assert.AreEqual(written.Table.DiskGuid, verified.Value!.DiskGuid);
        Assert.AreEqual(2, verified.Value.Entries.Count);

        GptEntry esp = verified.Value.Entries[0], root = verified.Value.Entries[1];
        Assert.AreEqual(GptLayout.EspType, esp.Type);
        Assert.AreEqual(2048UL, esp.FirstLba);
        Assert.AreEqual(2048UL + 1048576 - 1, esp.LastLba, "ESP 512 MiB");
        Assert.AreEqual(GptLayoutWriter.EspName, esp.Name);
        Assert.AreEqual(GptLayout.LinuxFileSystemType, root.Type);
        Assert.AreEqual(GptLayoutWriter.RootName, root.Name);
        Assert.AreEqual(esp.LastLba + 1, root.FirstLba);
        Assert.AreEqual(0UL, root.FirstLba % 2048, "Root-Start 1-MiB-ausgerichtet");
        ulong rootSectors = root.LastLba - root.FirstLba + 1;
        Assert.AreEqual(0UL, rootSectors % 8, "Root-Ende auf 4 KiB abgerundet");
        Assert.IsTrue(root.LastLba <= blockCount - 34, "innerhalb LastUsable");
        Assert.IsTrue(blockCount - 34 - root.LastLba < 8, "hoechstens ein angebrochener 4-KiB-Block bleibt frei");
        Assert.IsTrue(rootSectors * Sector <= 128 * GiB);
    }

    [TestMethod]
    public void Write_Headers_FieldsAndCrcs()
    {
        var disk = new SparseDisk(Blocks32);
        Assert.IsTrue(Write(disk).IsOK);
        ulong n = Blocks32;
        byte[] primary = disk.ReadSector(1), backup = disk.ReadSector(n - 1);
        AssertHeader(primary, myLba: 1, alternateLba: n - 1, entriesLba: 2, n);
        AssertHeader(backup, myLba: n - 1, alternateLba: 1, entriesLba: n - 33, n);
        CollectionAssert.AreEqual(primary.AsSpan(56, 16).ToArray(), backup.AsSpan(56, 16).ToArray(), "Disk-GUID gleich");

        var primaryEntries = new byte[32 * Sector];
        var backupEntries = new byte[32 * Sector];
        disk.Read(2, 32, primaryEntries);
        disk.Read(n - 33, 32, backupEntries);
        CollectionAssert.AreEqual(primaryEntries, backupEntries, "Eintragstabellen gleich");
        uint entriesCrc = Crc32.Compute(primaryEntries);
        Assert.AreEqual(entriesCrc, BitConverter.ToUInt32(primary, 88));
        Assert.AreEqual(entriesCrc, BitConverter.ToUInt32(backup, 88));
        Assert.AreNotEqual(BitConverter.ToUInt32(primary, 16), BitConverter.ToUInt32(backup, 16), "Backup hat eigene Kopf-CRC");
    }

    private static void AssertHeader(byte[] header, ulong myLba, ulong alternateLba, ulong entriesLba, ulong n)
    {
        Assert.AreEqual("EFI PART", Encoding.ASCII.GetString(header, 0, 8));
        Assert.AreEqual(0x00010000u, BitConverter.ToUInt32(header, 8));
        Assert.AreEqual(92u, BitConverter.ToUInt32(header, 12));
        Assert.AreEqual(0u, BitConverter.ToUInt32(header, 20), "reserviert");
        Assert.AreEqual(myLba, BitConverter.ToUInt64(header, 24));
        Assert.AreEqual(alternateLba, BitConverter.ToUInt64(header, 32));
        Assert.AreEqual(34UL, BitConverter.ToUInt64(header, 40));
        Assert.AreEqual(n - 34, BitConverter.ToUInt64(header, 48));
        Assert.AreEqual(entriesLba, BitConverter.ToUInt64(header, 72));
        Assert.AreEqual(128u, BitConverter.ToUInt32(header, 80));
        Assert.AreEqual(128u, BitConverter.ToUInt32(header, 84));
        for (int i = 92; i < header.Length; i++) Assert.AreEqual(0, header[i], "Rest des Sektors null");
        // Kopf-CRC ueber genau 92 Byte mit genulltem CRC-Feld.
        var copy = header.AsSpan(0, 92).ToArray();
        uint stored = BitConverter.ToUInt32(copy, 16);
        copy[16] = copy[17] = copy[18] = copy[19] = 0;
        Assert.AreEqual(Crc32.Compute(copy), stored);
    }

    [TestMethod]
    public void Write_ProtectiveMbr_SingleEeEntry()
    {
        var disk = new SparseDisk(Blocks128);
        disk.Put(0, LegacyMbr());
        Assert.IsTrue(Write(disk).IsOK);
        byte[] mbr = disk.ReadSector(0);
        Assert.AreEqual(0x55, mbr[510]);
        Assert.AreEqual(0xAA, mbr[511]);
        Assert.AreEqual(0xEE, mbr[446 + 4]);
        Assert.AreEqual(1u, BitConverter.ToUInt32(mbr, 446 + 8));
        Assert.AreEqual((uint)(Blocks128 - 1), BitConverter.ToUInt32(mbr, 446 + 12));
        for (int i = 446 + 16; i < 510; i++) Assert.AreEqual(0, mbr[i], "Eintraege 2-4 null, alte 0xEF-Partition entfernt");
    }

    [TestMethod]
    public void TypeGuids_MixedEndianRawBytes()
    {
        CollectionAssert.AreEqual(Convert.FromHexString("28732AC11FF8D211BA4B00A0C93EC93B"), GptLayout.EspType.ToByteArray());
        CollectionAssert.AreEqual(Convert.FromHexString("AF3DC60F838472478E793D69D8477DE4"), GptLayout.LinuxFileSystemType.ToByteArray());
        var disk = new SparseDisk(Blocks32);
        Assert.IsTrue(Write(disk).IsOK);
        byte[] entries = disk.ReadSector(2);
        CollectionAssert.AreEqual(GptLayout.EspType.ToByteArray(), entries.AsSpan(0, 16).ToArray());
        CollectionAssert.AreEqual(GptLayout.LinuxFileSystemType.ToByteArray(), entries.AsSpan(128, 16).ToArray());
    }

    [TestMethod]
    public void Write_Order_WipeBackupEntriesPrimaryMbr()
    {
        var disk = new SparseDisk(Blocks32);
        Assert.IsTrue(Write(disk).IsOK);
        ulong n = Blocks32;
        CollectionAssert.AreEqual(
            new[] { "W0+2048", "F", $"W{n - 33}+32", $"W{n - 1}+1", "F", "W2+32", "F", "W1+1", "F", "W0+1", "F" },
            disk.Log);
    }

    // --- Writer: Grenzen vor jedem Schreibzugriff ---

    [TestMethod]
    [DataRow(31 * GiB / Sector)]
    [DataRow(Blocks32 - 1)]
    [DataRow(Blocks128 + 1)]
    [DataRow(2048 * GiB / Sector + 1)]
    public void Write_OutsideRange_RefusedBeforeAnyWrite(ulong blockCount)
    {
        var disk = new SparseDisk(blockCount);
        var random = new FixedRandom(1);
        var result = Write(disk, random.Fill);
        Assert.IsFalse(result.IsOK);
        StringAssert.Contains(result.Message, "cause='UnsupportedDiskSize'");
        Assert.AreEqual(0, disk.Writes);
        Assert.AreEqual(0, disk.Flushes);
        Assert.AreEqual(0, random.Calls, "keine Zufallsbytes fuer eine verweigerte Platte");
    }

    [TestMethod]
    [DataRow(4096UL)]
    [DataRow(0UL)]
    public void Write_SectorSizeNot512_RefusedBeforeAnyWrite(ulong blockSize)
    {
        var disk = new SparseDisk(Blocks32);
        var result = Write(disk, blockSize: blockSize);
        StringAssert.Contains(result.Message, "cause='UnsupportedSectorSize'");
        Assert.AreEqual(0, disk.Writes);
    }

    [TestMethod]
    public void Write_RandomUnavailable_RefusedBeforeAnyWrite()
    {
        var disk = new SparseDisk(Blocks32);
        var result = Write(disk, _ => throw new CryptographicException("RDSEED/RDRAND fehlt."));
        StringAssert.Contains(result.Message, "cause='RandomUnavailable'");
        Assert.AreEqual(0, disk.Writes);
        Assert.AreEqual(0, disk.Flushes);
    }

    [TestMethod]
    public void Write_Guids_Version4_DistinctAndNotEmpty()
    {
        var first = Write(new SparseDisk(Blocks32), bytes => RandomNumberGenerator.Fill(bytes));
        var second = Write(new SparseDisk(Blocks32), bytes => RandomNumberGenerator.Fill(bytes));
        var guids = new HashSet<Guid>();
        foreach (var table in new[] { first.Table, second.Table })
            foreach (Guid guid in new[] { table.DiskGuid, table.Entries[0].Unique, table.Entries[1].Unique })
            {
                Assert.AreNotEqual(Guid.Empty, guid);
                string text = guid.ToString();
                Assert.AreEqual('4', text[14], "Version 4: " + text);
                Assert.IsTrue("89ab".Contains(text[19], StringComparison.Ordinal), "RFC-4122-Variante: " + text);
                Assert.IsTrue(guids.Add(guid), "jede GUID nur einmal");
            }
        Assert.AreEqual(6, guids.Count, "gleich grosse Platten erhalten verschiedene GUIDs");
    }

    [TestMethod]
    public void Write_ConstantRandom_RefusedBeforeAnyWrite()
    {
        // Eine kaputte Quelle (lauter gleiche Bytes) ergaebe drei gleiche GUIDs; das darf nie auf die Platte.
        var disk = new SparseDisk(Blocks32);
        var result = Write(disk, bytes => Array.Fill(bytes, (byte)0x5A));
        StringAssert.Contains(result.Message, "cause='RandomRejected'");
        Assert.AreEqual(0, disk.Writes);
    }

    // --- Abbruch nach jedem Schritt ---

    [TestMethod]
    [DataRow(1, DisplayName = "Abbruch in Schritt 1 (Loeschen)")]
    [DataRow(2, DisplayName = "nach Schritt 1")]
    [DataRow(3, DisplayName = "nach Backup-Eintraegen")]
    [DataRow(4, DisplayName = "nach Schritt 2 (Backup)")]
    [DataRow(5, DisplayName = "nach Schritt 3 (Primaer-Eintraege)")]
    [DataRow(6, DisplayName = "nach Schritt 4 (Primaerkopf)")]
    public void Write_AbortAfterEachStep_NeverValidNeverLegacy(int failAtWrite)
    {
        var disk = new SparseDisk(Blocks32, failAtWrite);
        disk.Put(0, LegacyMbr());
        disk.Put(1, Encoding.ASCII.GetBytes("EFI PART".PadRight(512, '\0')));
        Assert.ThrowsException<IOException>(() => Write(disk));

        // Der Schutz-MBR kommt als letzter Schritt; vorher ist die Platte nie gueltig.
        AssertCause(Verify(disk), "ProtectiveMbrInvalid");
        bool wiped = failAtWrite > 1;
        if (!wiped) return; // Schritt 1 selbst abgebrochen: alter Zustand bleibt, der Installer meldet den Fehler.
        byte[] mbr = disk.ReadSector(0);
        Assert.IsFalse(mbr[510] == 0x55 && mbr[511] == 0xAA, "kein MBR mehr: weder Legacy-Partition noch Schutz-MBR sichtbar");
        bool primaryHeader = disk.ReadSector(1).AsSpan(0, 8).SequenceEqual("EFI PART"u8);
        Assert.AreEqual(failAtWrite == 6, primaryHeader, "Primaerkopf erst nach Schritt 4; davor sieht Cosmos keine GPT (0 Partitionen)");
    }

    // --- Verify: Negativfaelle ---

    [TestMethod]
    public void Verify_EmptyDisk_Invalid() => AssertCause(Verify(new SparseDisk(Blocks32)), "ProtectiveMbrInvalid");

    [TestMethod]
    public void Verify_PrimaryHeaderCrcBroken_Invalid()
    {
        var disk = Installed(Blocks32);
        Patch(disk, 1, 56, 0xFF); // Disk-GUID ohne neue CRC
        AssertCause(Verify(disk), "PrimaryHeaderCrc");
    }

    [TestMethod]
    public void Verify_BackupHeaderCrcBroken_Invalid()
    {
        var disk = Installed(Blocks32);
        Patch(disk, Blocks32 - 1, 56, 0xFF);
        AssertCause(Verify(disk), "BackupHeaderCrc");
    }

    [TestMethod]
    public void Verify_PrimaryEntriesChanged_Invalid()
    {
        var disk = Installed(Blocks32);
        Patch(disk, 2, 40, 0x01);
        AssertCause(Verify(disk), "PrimaryEntriesCrc");
    }

    [TestMethod]
    public void Verify_BackupEntriesChanged_Invalid()
    {
        var disk = Installed(Blocks32);
        Patch(disk, Blocks32 - 33, 40, 0x01);
        AssertCause(Verify(disk), "BackupEntriesCrc");
    }

    [TestMethod]
    public void Verify_PrimaryAndBackupDiffer_Invalid()
    {
        // Beide Kopien fuer sich gueltig versiegelt, aber mit verschiedenen Eintraegen bzw. Disk-GUIDs.
        var disk = Installed(Blocks32);
        var entries = Entries(Blocks32);
        Seal(disk, Blocks32, entries, primary: true);
        entries[1] = entries[1] with { LastLba = entries[1].LastLba - 8 };
        Seal(disk, Blocks32, entries, primary: false);
        AssertCause(Verify(disk), "EntriesDiffer");

        var other = Installed(Blocks32);
        Seal(other, Blocks32, Entries(Blocks32), primary: false, diskGuid: Guid.NewGuid());
        AssertCause(Verify(other), "HeadersDiffer");
    }

    [TestMethod]
    public void Verify_EntryOutsideUsableRange_Invalid()
    {
        var entries = Entries(Blocks32);
        entries[1] = entries[1] with { LastLba = Blocks32 - 33 }; // erster Sektor der Backup-Eintraege
        AssertCause(Verify(Sealed(Blocks32, entries)), "EntryOutOfRange");

        entries = Entries(Blocks32);
        entries[0] = entries[0] with { FirstLba = 33 }; // in der Primaer-Eintragstabelle
        AssertCause(Verify(Sealed(Blocks32, entries)), "EntryOutOfRange");

        entries = Entries(Blocks32);
        entries[1] = entries[1] with { FirstLba = entries[1].LastLba + 1 };
        AssertCause(Verify(Sealed(Blocks32, entries)), "EntryOutOfRange");
    }

    [TestMethod]
    public void Verify_OverlappingEntries_Invalid()
    {
        var entries = Entries(Blocks32);
        entries[1] = entries[1] with { FirstLba = entries[0].LastLba };
        AssertCause(Verify(Sealed(Blocks32, entries)), "EntriesOverlap");
    }

    [TestMethod]
    public void Verify_DuplicateUniqueGuid_Invalid()
    {
        var entries = Entries(Blocks32);
        entries[1] = entries[1] with { Unique = entries[0].Unique };
        AssertCause(Verify(Sealed(Blocks32, entries)), "DuplicateUniqueGuid");

        entries = Entries(Blocks32);
        entries[1] = entries[1] with { Unique = Guid.Empty };
        AssertCause(Verify(Sealed(Blocks32, entries)), "EmptyUniqueGuid");
    }

    [TestMethod]
    [DataRow(0, 8, (byte)'X', "PrimaryHeaderSignature")]
    [DataRow(8, 4, (byte)0x02, "PrimaryHeaderRevision")]
    [DataRow(12, 4, (byte)0x5C, "PrimaryHeaderSize")] // 92 -> 0x5C5C5C5C
    [DataRow(80, 4, (byte)0x81, "PrimaryHeaderLayout")] // 129 Eintraege und mehr
    [DataRow(84, 4, (byte)0x01, "PrimaryHeaderLayout")] // Eintragsgroesse != 128
    [DataRow(40, 8, (byte)0x00, "PrimaryHeaderLayout")] // FirstUsable 0
    [DataRow(24, 8, (byte)0x02, "PrimaryHeaderLayout")] // MyLBA falsch
    public void Verify_PrimaryHeaderFieldsResealed_Invalid(int offset, int length, byte value, string cause)
    {
        // Kopf mit neuer, passender CRC: nur die Feldpruefung darf ihn abweisen, nicht die Pruefsumme.
        var disk = Installed(Blocks32);
        byte[] header = disk.ReadSector(1);
        header.AsSpan(offset, length).Fill(value);
        Reseal(header);
        disk.Put(1, header);
        AssertCause(Verify(disk), cause);
    }

    [TestMethod]
    public void Verify_DiskEnlargedAfterInstall_Invalid()
    {
        // dd auf eine groessere Platte oder vergroesserte VM-Platte: Backup liegt nicht mehr bei N-1.
        ulong n = Blocks32 + 2048;
        var disk = Installed(Blocks32).CopyTo(n);
        AssertCause(Verify(disk), "ProtectiveMbrInvalid");
        // Auch mit angepasstem Schutz-MBR passen LastUsable und AlternateLBA nicht mehr zur Plattengroesse.
        disk.Put(0, GptLayout.BuildProtectiveMbr(n));
        AssertCause(Verify(disk), "PrimaryHeaderLayout");
    }

    [TestMethod]
    public void Verify_ProtectiveMbrWrong_Invalid()
    {
        var disk = Installed(Blocks32);
        byte[] mbr = disk.ReadSector(0);
        mbr[446 + 16 + 4] = 0x83; // zweiter Eintrag (Hybrid-MBR)
        disk.Put(0, mbr);
        AssertCause(Verify(disk), "ProtectiveMbrInvalid");
    }

    [TestMethod]
    [DataRow(4096UL, Blocks32)]
    [DataRow(Sector, Blocks128 + 1)]
    [DataRow(Sector, 100UL)]
    public void Verify_UnsupportedGeometry_InvalidWithoutRead(ulong blockSize, ulong blockCount)
    {
        var result = GptLayout.Verify(blockSize, blockCount, (_, _, _) => Assert.Fail("kein Lesezugriff"));
        AssertCause(result, "UnsupportedGeometry");
    }

    // --- Hilfen ---

    private static SparseDisk Installed(ulong blockCount)
    {
        var disk = new SparseDisk(blockCount);
        Assert.IsTrue(Write(disk).IsOK);
        return disk;
    }

    private static GptEntry[] Entries(ulong blockCount)
    {
        var table = GptLayoutWriter.Plan(Sector, blockCount, new FixedRandom(1).Fill);
        Assert.IsTrue(table.IsOK(), table.Status.Message);
        return [.. table.Value!.Entries];
    }

    /// <summary>Platte mit eigenen Eintraegen, beide Kopien ueber die produktiven Bausteine versiegelt.</summary>
    private static SparseDisk Sealed(ulong blockCount, GptEntry[] entries)
    {
        var disk = Installed(blockCount);
        Seal(disk, blockCount, entries, primary: true);
        Seal(disk, blockCount, entries, primary: false);
        return disk;
    }

    private static void Seal(SparseDisk disk, ulong n, GptEntry[] entries, bool primary, Guid? diskGuid = null)
    {
        Guid guid = diskGuid ?? new Guid(disk.ReadSector(1).AsSpan(56, 16));
        byte[] array = GptLayout.BuildEntries(entries);
        uint crc = Crc32.Compute(array);
        if (primary)
        {
            disk.Put(2, array);
            disk.Put(1, GptLayout.BuildHeader(n, guid, 1, n - 1, 2, crc));
        }
        else
        {
            disk.Put(n - 33, array);
            disk.Put(n - 1, GptLayout.BuildHeader(n, guid, n - 1, 1, n - 33, crc));
        }
    }

    private static void Reseal(byte[] header)
    {
        header[16] = header[17] = header[18] = header[19] = 0;
        BitConverter.TryWriteBytes(header.AsSpan(16, 4), Crc32.Compute(header.AsSpan(0, 92)));
    }

    private static void Patch(SparseDisk disk, ulong block, int offset, byte xor)
    {
        byte[] sector = disk.ReadSector(block);
        sector[offset] ^= xor;
        disk.Put(block, sector);
    }

    /// <summary>MBR der bisherigen Installation: eine 0xEF-Partition ab LBA 2048.</summary>
    private static byte[] LegacyMbr()
    {
        var mbr = new byte[Sector];
        mbr[446 + 4] = 0xEF;
        BitConverter.TryWriteBytes(mbr.AsSpan(446 + 8, 4), 2048u);
        BitConverter.TryWriteBytes(mbr.AsSpan(446 + 12, 4), 1000000u);
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        return mbr;
    }
}
