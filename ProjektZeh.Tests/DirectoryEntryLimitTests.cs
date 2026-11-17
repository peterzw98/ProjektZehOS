using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DirectoryEntryLimitTests
{
    private string temporary = null!;
    private string directory = null!;

    [TestInitialize]
    public void SetUp()
    {
        temporary = Path.Combine(Path.GetTempPath(), "ZehOS-DirectoryLimit-" + Guid.NewGuid().ToString("N"));
        directory = Path.Combine(temporary, "limited");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "one.txt"), "original");
        File.WriteAllText(Path.Combine(directory, "two.txt"), "second");
        File.WriteAllText(Path.Combine(temporary, "source.txt"), "source");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(temporary, true);

    private FileService Limited() => new(directory, directoryEntryLimit: _ => 2);

    [TestMethod]
    [DataRow("touch")]
    [DataRow("write")]
    [DataRow("mkdir")]
    [DataRow("copy")]
    [DataRow("move")]
    public void NewEntryAtCapacityFailsWithoutChangingExistingData(string operation)
    {
        var files = Limited();
        string source = Path.Combine(temporary, "source.txt");
        ResultErr result = operation switch
        {
            "touch" => files.Touch("new.txt"),
            "write" => files.WriteText("new.txt", "new"),
            "mkdir" => files.CreateDirectory("new/deep"),
            "copy" => files.CopyFile(source, "new.txt"),
            _ => files.MoveFile(source, "new.txt")
        };
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "cause='DirectoryEntryLimit'");
        Assert.AreEqual(2, Directory.GetFileSystemEntries(directory).Length);
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(directory, "one.txt")));
        Assert.AreEqual("source", File.ReadAllText(source));
    }

    [TestMethod]
    public void ExistingTouchAndSameDirectoryRenameWorkAtCapacity()
    {
        var files = Limited();
        Assert.IsTrue(files.Touch("one.txt").IsOK());
        Assert.IsTrue(files.MoveFile("one.txt", "renamed.txt").IsOK());
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(directory, "renamed.txt")));
        Assert.AreEqual(2, Directory.GetFileSystemEntries(directory).Length);
    }

    [TestMethod]
    public void ExistingDirectoryDoesNotNeedAnotherEntry()
    {
        File.Delete(Path.Combine(directory, "two.txt"));
        Directory.CreateDirectory(Path.Combine(directory, "existing"));
        Assert.IsTrue(Limited().CreateDirectory("existing").IsOK());
    }

    [TestMethod]
    public void FailedReplacementKeepsOriginalAndBackupAndLeavesNoTemporaryFile()
    {
        string target = Path.Combine(directory, "one.txt");
        File.Move(Path.Combine(directory, "two.txt"), target + ".bak");
        var result = Limited().WriteBytes(target, [1, 2, 3], keepBackup: true);
        StringAssert.Contains(result.Message, "cause='DirectoryEntryLimit'");
        Assert.AreEqual("original", File.ReadAllText(target));
        Assert.AreEqual("second", File.ReadAllText(target + ".bak"));
        Assert.AreEqual(2, Directory.GetFileSystemEntries(directory).Length);
    }

    [TestMethod]
    public void LastAvailableEntryCanBeWrittenViaTemporaryFile()
    {
        File.Delete(Path.Combine(directory, "two.txt"));
        var result = Limited().WriteText("last.txt", "last");
        Assert.IsTrue(result.IsOK(), result.Message);
        Assert.AreEqual("last", File.ReadAllText(Path.Combine(directory, "last.txt")));
        Assert.AreEqual(2, Directory.GetFileSystemEntries(directory).Length);
    }

    [TestMethod]
    public void UnlimitedFileSystemKeepsItsBehavior()
    {
        var files = new FileService(directory, directoryEntryLimit: _ => null);
        Assert.IsTrue(files.WriteText("third.txt", "third").IsOK());
        Assert.AreEqual(3, Directory.GetFileSystemEntries(directory).Length);
    }

    [TestMethod]
    public void CapacityQueryFailurePreservesOriginalCause()
    {
        var files = new FileService(directory, directoryEntryLimit: _ => throw new IOException("statfs failed"));
        var result = files.Touch("new.txt");
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "statfs failed");
        Assert.AreEqual(2, Directory.GetFileSystemEntries(directory).Length);
    }
}
