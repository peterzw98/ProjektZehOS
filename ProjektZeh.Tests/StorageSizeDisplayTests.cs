using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using ProjektZeh.Apps.FileManager;
using ProjektZeh.Installer;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

/// <summary>Groessenanzeigen duerfen ab 4 GiB (ueber uint) bis 128 GiB nicht abschneiden oder ueberlaufen.</summary>
[TestClass]
public sealed class StorageSizeDisplayTests
{
    private const ulong GiB = 1UL << 30;

    [TestMethod]
    public void DiskList_ShowsExactBytesAboveUintRange()
    {
        ulong[] sizes = [uint.MaxValue, 1UL << 32, 32 * GiB, 64 * GiB, 128 * GiB];
        var devices = new StorageDeviceInfo[sizes.Length];
        for (int index = 0; index < sizes.Length; index++) devices[index] = new($"sata{index}", sizes[index], 1);
        var storage = new Mock<IStorageService>();
        storage.Setup(target => target.GetDevices()).Returns(ResultObjectFactory.CreateSuccess(devices));
        var lines = new List<string>();
        var output = new Mock<ITextOutput>();
        output.Setup(target => target.WriteLine(It.IsAny<string>())).Callback<string>(lines.Add);

        var result = new ShellService(new StorageShellCommands(storage.Object).Commands).ExecuteLine("disk list", output.Object);

        Assert.IsTrue(result.IsOK());
        CollectionAssert.AreEqual(new[]
        {
            "sata0: 4294967295 Bytes, 1 Partition(en)", "sata1: 4294967296 Bytes, 1 Partition(en)",
            "sata2: 34359738368 Bytes, 1 Partition(en)", "sata3: 68719476736 Bytes, 1 Partition(en)",
            "sata4: 137438953472 Bytes, 1 Partition(en)"
        }, lines);
    }

    [DataTestMethod]
    [DataRow(4294967295UL, "3,9 GB")] // abgeschnitten auf Zehntel, nicht aufgerundet
    [DataRow(1UL << 32, "4,0 GB")]
    [DataRow(32UL << 30, "32,0 GB")]
    [DataRow(64UL << 30, "64,0 GB")]
    [DataRow(128UL << 30, "128,0 GB")]
    public void SetupDiskList_FormatSize_HandlesLargeDisks(ulong bytes, string expected) =>
        Assert.AreEqual(expected, SetupDiskList.FormatSize(bytes));

    [DataTestMethod]
    [DataRow(4294967295L, "4 GB")]
    [DataRow(1L << 32, "4 GB")]
    [DataRow(32L << 30, "32 GB")]
    [DataRow(64L << 30, "64 GB")]
    [DataRow(128L << 30, "128 GB")]
    public void FileManager_FormatSize_HandlesLargeDisks(long bytes, string expected) =>
        Assert.AreEqual(expected, FileManagerApp.FormatSize(bytes));
}
