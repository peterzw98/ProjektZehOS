using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DiskEmptyCheckTests
{
    private const ulong Sector = 512;
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    /// <summary>Platte mit einzelnen Nicht-Null-Bytes; meldet jeden Zugriff ueber das Ende.</summary>
    private sealed class FakeDisk(ulong bytes, params ulong[] dirtyOffsets)
    {
        public ulong BlockCount { get; } = bytes / Sector;
        public int Reads { get; private set; }
        public ulong BytesRead { get; private set; }

        public void Read(ulong block, ulong count, Span<byte> buffer)
        {
            Assert.IsTrue(block + count <= BlockCount, "Zugriff ueber das Plattenende");
            Assert.AreEqual((int)(count * Sector), buffer.Length);
            Reads++;
            BytesRead += count * Sector;
            buffer.Clear();
            foreach (ulong offset in dirtyOffsets)
                if (offset >= block * Sector && offset < (block + count) * Sector) buffer[(int)(offset - block * Sector)] = 1;
        }
    }

    private static ResultErrView Run(FakeDisk disk, bool confirmed = false) =>
        new(DiskEmptyCheck.Check(Sector, disk.BlockCount, confirmed, disk.Read));

    private readonly record struct ResultErrView(ProjektZeh.Shared.ResultErr Result)
    {
        public bool Ok => Result.IsOK();
        public bool Is(string cause) => !Result.IsOK() && Result.Message.Contains(cause, StringComparison.Ordinal);
    }

    [TestMethod]
    public void SmallDisk_FullScan_FindsDataInLastSector()
    {
        var disk = new FakeDisk(64 * MiB, 64 * MiB - 1);

        Assert.IsTrue(Run(disk).Is("DiskNotEmpty"));
    }

    [TestMethod]
    public void SmallDisk_Empty_IsOk_AndOddSizeStaysInsideDisk()
    {
        Assert.IsTrue(Run(new FakeDisk(64 * MiB)).Ok);
        Assert.IsTrue(Run(new FakeDisk(64 * MiB + 7 * Sector)).Ok, "nicht durch den Stapel teilbare Groesse");
    }

    [TestMethod]
    public void ExactlyFourGiB_IsStillFullScan()
    {
        var disk = new FakeDisk(4 * GiB, 2 * GiB + 1);

        Assert.IsTrue(Run(disk).Is("DiskNotEmpty"), "Daten mitten auf der Platte, nicht in einer Stichprobe");
    }

    [TestMethod]
    public void LargeDisk_WithoutConfirmation_ReadsNothing()
    {
        var disk = new FakeDisk(4 * GiB + Sector);

        Assert.IsTrue(Run(disk).Is("LargeDiskNeedsConfirmation"));
        Assert.AreEqual(0, disk.Reads);
    }

    [TestMethod]
    public void LargeDisk_Confirmed_Empty_IsOk_AndReadsOnlyEdgesAndSamples()
    {
        var disk = new FakeDisk(32 * GiB);

        Assert.IsTrue(Run(disk, true).Ok);
        Assert.AreEqual(2 * 64 * MiB + 511 * 4096, disk.BytesRead, "Kopf, Ende und 4 KiB alle 64 MiB");
    }

    [DataTestMethod]
    [DataRow(0UL)]
    [DataRow(64 * MiB - 1)]
    [DataRow(32 * GiB - 64 * MiB)]
    [DataRow(32 * GiB - 1)]
    [DataRow(3 * GiB + 256)] // Mitte der Stichprobe
    [DataRow(3 * GiB + 4095)] // letztes Byte der Stichprobe
    [DataRow(3 * GiB + 64 * MiB)] // dichter als 1 Stichprobe je GiB
    public void LargeDisk_Confirmed_DataInCheckedRegion_Aborts(ulong dirtyOffset)
    {
        Assert.IsTrue(Run(new FakeDisk(32 * GiB, dirtyOffset), true).Is("DiskNotEmpty"));
    }

    [TestMethod]
    public void LargeDisk_DataBetweenSamples_IsNotDetected_Documented()
    {
        // Bewusste Grenze: ueber 4 GiB werden nur 4 KiB je 64 MiB gelesen.
        Assert.IsTrue(Run(new FakeDisk(32 * GiB, 3 * GiB + 10 * MiB), true).Ok);
        Assert.IsTrue(Run(new FakeDisk(32 * GiB, 3 * GiB + 4096), true).Ok, "erstes Byte hinter der Stichprobe");
    }

    [TestMethod]
    public void LargeDisk_Confirmed_ReportsProgressBeforeAndAfterEachArea()
    {
        var disk = new FakeDisk(32 * GiB);
        var messages = new List<string>();

        Assert.IsTrue(new ResultErrView(DiskEmptyCheck.Check(Sector, disk.BlockCount, true, disk.Read, messages.Add)).Ok);

        ulong tailStart = disk.BlockCount - 64 * MiB / Sector;
        CollectionAssert.IsSubsetOf(new[]
        {
            "scan Kopf start LBA 0", "scan Kopf ok LBA 131072",
            "scan Stichprobe 16 start LBA 2097152", "scan Stichprobe 16 ok LBA 2097152",
            "scan Stichprobe 496 start LBA 65011712", "scan Stichprobe 496 ok LBA 65011712",
            $"scan Ende start LBA {tailStart}", $"scan Ende ok LBA {disk.BlockCount}"
        }, messages);
        Assert.AreEqual("scan Kopf start LBA 0", messages[0]);
        Assert.AreEqual($"scan Ende ok LBA {disk.BlockCount}", messages[^1]);
        Assert.AreEqual(31, messages.FindAll(m => m.StartsWith("scan Stichprobe") && m.Contains(" start ")).Count, "jede 16. von 511 Stichproben");
    }

    [TestMethod]
    public void SmallDisk_ReportsOneArea()
    {
        var messages = new List<string>();

        DiskEmptyCheck.Check(Sector, 64 * MiB / Sector, false, new FakeDisk(64 * MiB).Read, messages.Add);

        CollectionAssert.AreEqual(new[] { "scan Gesamt start LBA 0", "scan Gesamt ok LBA 131072" }, messages);
    }

    [TestMethod]
    public void FullScan_ReportsEvery256MiB()
    {
        var messages = new List<string>();

        DiskEmptyCheck.Check(Sector, GiB / Sector, false, new FakeDisk(GiB).Read, messages.Add);

        CollectionAssert.AreEqual(new[]
        {
            "scan Gesamt start LBA 0", "scan Gesamt gelesen bis LBA 524288", "scan Gesamt gelesen bis LBA 1048576",
            "scan Gesamt gelesen bis LBA 1572864", "scan Gesamt gelesen bis LBA 2097152", "scan Gesamt ok LBA 2097152"
        }, messages);
    }

    [TestMethod]
    public void LargeDisk_Confirmed_OddSizeStaysInsideDisk()
    {
        Assert.IsTrue(Run(new FakeDisk(5 * GiB + 3 * Sector), true).Ok);
    }
}
