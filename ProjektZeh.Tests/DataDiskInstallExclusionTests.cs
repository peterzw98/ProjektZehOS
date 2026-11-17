using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Partitioning;

namespace ProjektZeh.Tests;

/// <summary>
/// Etappe 7a, Folgearbeit Installer-Ausschluss (Spec 7a, Abschnitt 5 "disk init" und Offener Punkt 4): disk init verweigert
/// Platten mit GPT-Spuren unabhaengig von Bestaetigungen, der Installer bietet ZEHOSDATA-Platten auch mit GPT nicht an.
/// </summary>
[TestClass]
public sealed class DataDiskInstallExclusionTests
{
    // 1 GiB: innerhalb der disk-init-Grenzen, aber ausserhalb der 32..128 GiB, die GptLayout.Verify kennt.
    private const ulong SmallDisk = 2097152;
    private const ulong LargeDisk = GptLayout.MinBlockCount;

    /// <summary>Duennes Plattenabbild: nur beschriebene Sektoren werden gehalten, alle anderen lesen sich als Nullen.</summary>
    private sealed class SparseDisk(ulong blockCount)
    {
        private readonly Dictionary<ulong, byte[]> sectors = [];
        public int Reads { get; private set; }

        public void Write(ulong lba, byte[] data) => sectors[lba] = data;

        public void Read(ulong block, ulong count, Span<byte> buffer)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(block + count, blockCount, nameof(block));
            Reads++;
            buffer.Clear();
            for (ulong index = 0; index < count; index++)
                if (sectors.TryGetValue(block + index, out var data)) data.CopyTo(buffer.Slice((int)(index * 512), 512));
        }
    }

    private static byte[] Header(ulong blockCount, ulong myLba, ulong alternateLba) =>
        GptLayout.BuildHeader(blockCount, Guid.NewGuid(), myLba, alternateLba, myLba == 1 ? 2 : GptLayout.BackupEntriesLba(blockCount), 0);

    private static ProjektZeh.Shared.ResultErr Check(SparseDisk disk, ulong blockCount) =>
        DataDiskSelector.CheckNoInstalledLayout(512, blockCount, disk.Read);

    [TestMethod]
    public void BlankDisk_Allowed()
    {
        var disk = new SparseDisk(SmallDisk);
        Assert.IsTrue(Check(disk, SmallDisk).IsOK());
    }

    [TestMethod]
    public void LegacyMbrDataDisk_AllowedHere()
    {
        // MBR mit 0x0C: diese Pruefung laesst sie durch, ExistingPartitions bzw. die Leerpruefung lehnen danach ab.
        var mbr = new byte[512];
        mbr[446 + 4] = DataDiskSelector.FatLbaType;
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        var disk = new SparseDisk(SmallDisk);
        disk.Write(0, mbr);
        Assert.IsTrue(Check(disk, SmallDisk).IsOK());
    }

    [TestMethod]
    public void FullInstalledLayout_Refused()
    {
        var disk = new SparseDisk(LargeDisk);
        disk.Write(0, GptLayout.BuildProtectiveMbr(LargeDisk));
        disk.Write(1, Header(LargeDisk, 1, LargeDisk - 1));
        disk.Write(LargeDisk - 1, Header(LargeDisk, LargeDisk - 1, 1));
        var result = Check(disk, LargeDisk);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "InstalledLayout");
    }

    [TestMethod]
    public void ProtectiveMbrOnly_Refused()
    {
        // Abbruch des Writers nach dem Schutz-MBR: keine gueltige GPT, aber eindeutig angefangen.
        var disk = new SparseDisk(SmallDisk);
        disk.Write(0, GptLayout.BuildProtectiveMbr(SmallDisk));
        StringAssert.Contains(Check(disk, SmallDisk).Message, "InstalledLayout");
    }

    [TestMethod]
    public void ProtectiveEntryInLaterSlot_Refused()
    {
        var mbr = new byte[512];
        mbr[446 + 3 * 16 + 4] = 0xEE;
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        var disk = new SparseDisk(SmallDisk);
        disk.Write(0, mbr);
        Assert.IsFalse(Check(disk, SmallDisk).IsOK());
    }

    [TestMethod]
    public void PrimaryHeaderOnly_Refused()
    {
        var disk = new SparseDisk(SmallDisk);
        disk.Write(1, Header(SmallDisk, 1, SmallDisk - 1));
        Assert.IsFalse(Check(disk, SmallDisk).IsOK());
    }

    [TestMethod]
    public void BackupHeaderOnly_Refused()
    {
        // Genullter Primaerbereich (Spec P3-a): die Firmware wuerde den Backup nehmen, also gilt die Platte als belegt.
        var disk = new SparseDisk(SmallDisk);
        disk.Write(SmallDisk - 1, Header(SmallDisk, SmallDisk - 1, 1));
        Assert.IsFalse(Check(disk, SmallDisk).IsOK());
    }

    [TestMethod]
    public void SignatureTextElsewhere_Allowed()
    {
        var sector = new byte[512];
        Encoding.ASCII.GetBytes("EFI PART").CopyTo(sector, 100);
        var disk = new SparseDisk(SmallDisk);
        disk.Write(1, sector);
        Assert.IsTrue(Check(disk, SmallDisk).IsOK());
    }

    [TestMethod]
    public void ReadsOnlyThreeSectors()
    {
        var disk = new SparseDisk(SmallDisk);
        Check(disk, SmallDisk);
        Assert.AreEqual(3, disk.Reads);
    }

    [TestMethod]
    public void TinyDisk_NoOutOfRangeRead()
    {
        // Groessenpruefung kommt in disk init zuerst; die Funktion selbst darf trotzdem nicht ausserhalb lesen.
        var disk = new SparseDisk(2);
        Assert.IsTrue(Check(disk, 2).IsOK());
    }

    [TestMethod]
    public void IsInstallExcluded_MbrDataPartition() =>
        Assert.IsTrue(DataDiskSelector.IsInstallExcluded(new(DataDiskSelector.FatLbaType, "ZEHOSDATA  ")));

    [TestMethod]
    public void IsInstallExcluded_GptBasicDataPartition() =>
        Assert.IsTrue(DataDiskSelector.IsInstallExcluded(new(0, "ZEHOSDATA  ", GptLayout.BasicDataType)));

    [TestMethod]
    public void IsInstallExcluded_GptEspLabelledData() =>
        Assert.IsTrue(DataDiskSelector.IsInstallExcluded(new(0, "zehosdata", GptLayout.EspType)));

    [TestMethod]
    public void IsInstallExcluded_InstalledSystemNotExcluded()
    {
        // Neuinstallation ueber ein installiertes System bleibt mit --erase moeglich.
        Assert.IsFalse(DataDiskSelector.IsInstallExcluded(new(0, "ZEHOSBOOT  ", GptLayout.EspType)));
        Assert.IsFalse(DataDiskSelector.IsInstallExcluded(new(0, null, GptLayout.LinuxFileSystemType)));
    }

    [TestMethod]
    public void IsInstallExcluded_OtherLabelNotExcluded() =>
        Assert.IsFalse(DataDiskSelector.IsInstallExcluded(new(0, "DATA       ", GptLayout.BasicDataType)));

    [TestMethod]
    public void GptBasicDataZehosData_NotMountedByDecide()
    {
        // Einbinden bleibt bei MBR 0x0C; eine GPT-ZEHOSDATA wird nur vom Installer ausgeschlossen, nicht eingebunden.
        var decision = DataDiskSelector.Decide([new("sata1", 512, 67108864, [new(0, "ZEHOSDATA  ", GptLayout.BasicDataType)])]);
        Assert.AreEqual(DataDiskAction.NoDataDisk, decision.Action);
    }

    [TestMethod]
    public void GptBasicDataZehosDataOnSata0_OldFallbackUnchanged()
    {
        // Unveraendert: sata0 mit genau einer Partition bindet der bisherige Rueckfall ein, unabhaengig von GPT/Label.
        var decision = DataDiskSelector.Decide([new("sata0", 512, 67108864, [new(0, "ZEHOSDATA  ", GptLayout.BasicDataType)])]);
        Assert.AreEqual(DataDiskAction.Mount, decision.Action);
    }
}
