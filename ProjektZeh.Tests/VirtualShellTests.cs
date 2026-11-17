using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

/// <summary>Shell auf /proc, /sys und /dev (Spec E5b/E6b §3a): Lesen ueber dieselbe Weiche, keine Navigation, Umleitung nur nach /dev/null.</summary>
[TestClass]
public sealed class VirtualShellTests
{
    private string directory = null!;
    private FileService files = null!;
    private ShellService shell = null!;
    private Output output = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        files = new FileService(directory, session: new UserSession("2", "bob", false, directory),
            virtualSource: path => ResultObjectFactory.CreateSuccess(Encoding.UTF8.GetBytes(path == "/proc/version" ? "version: 9.9.9\nzweite\n" : "x\n")));
        shell = new ShellService(new FileShellCommands(files).Commands, files: files);
        output = new Output();
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, true);

    [TestMethod]
    public void CatReadsVirtualFilesFromAnyWorkingDirectory()
    {
        Assert.IsTrue(shell.ExecuteLine("cat /proc/version", output).IsOK());
        Assert.AreEqual("version: 9.9.9\nzweite\n", output.Lines[0]);
    }

    [TestMethod]
    public void FiltersUseTheSameSwitch()
    {
        var result = shell.ExecuteLine("grep zweite /proc/version", output);
        Assert.IsTrue(result.IsOK(), result.ToString());
        CollectionAssert.Contains(output.Lines, "zweite");
    }

    [TestMethod]
    public void LsListsVirtualDirectoriesWithAbsolutePaths()
    {
        Assert.IsTrue(shell.ExecuteLine("ls /dev", output).IsOK());
        CollectionAssert.AreEqual(new[] { "/dev/null", "/dev/zero" }, output.Lines);
    }

    [TestMethod]
    public void LsOfAVirtualFilePrintsItsNameAndStillChecksRights()
    {
        Assert.IsTrue(shell.ExecuteLine("ls /proc/./version", output).IsOK());
        CollectionAssert.AreEqual(new[] { "/proc/version" }, output.Lines);
        Assert.IsFalse(shell.ExecuteLine("ls /proc/log", output).IsOK(), "Nur Administratoren duerfen /proc/log sehen.");
    }

    [TestMethod]
    public void CdIntoVirtualTreesIsRefusedAndKeepsTheDirectory()
    {
        string before = files.CurrentDirectory;
        var result = shell.ExecuteLine("cd /proc", output);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "VirtualReadOnly");
        Assert.AreEqual(before, files.CurrentDirectory);
    }

    [TestMethod]
    public void RedirectGoesOnlyToDevNull()
    {
        Assert.IsTrue(shell.ExecuteLine("echo x > /dev/null", output).IsOK());
        Assert.IsTrue(shell.ExecuteLine("echo x >> /dev/null", output).IsOK());
        Assert.IsFalse(shell.ExecuteLine("echo x > /proc/version", output).IsOK());
        Assert.IsFalse(shell.ExecuteLine("echo x > /dev/zero", output).IsOK());
        Assert.AreEqual(0, Directory.GetFileSystemEntries(directory).Length, "Nichts darf auf der Platte landen.");
    }

    [TestMethod]
    public void CatDevZeroEndsWithTheLimitAndDevNullIsEmpty()
    {
        Assert.IsFalse(shell.ExecuteLine("cat /dev/zero", output).IsOK());
        Assert.IsTrue(shell.ExecuteLine("cat /dev/null", output).IsOK());
        CollectionAssert.AreEqual(new[] { string.Empty }, output.Lines);
    }

    private sealed class Output : ITextOutput
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string text) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }
}
