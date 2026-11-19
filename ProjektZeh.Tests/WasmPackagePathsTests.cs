using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WasmPackagePathsTests
{
    private string root = "";

    [TestInitialize]
    public void CreatePackage()
    {
        root = Path.Combine(Path.GetTempPath(), "zehos-wasm-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "res"));
        File.WriteAllBytes(Path.Combine(root, "res", "Messages"), new byte[5]);
        // Ausbruchsziel neben dem Paket, damit ein fehlerhafter Ausbruch tatsaechlich eine Datei faende.
        File.WriteAllText(root + "-nachbar", "x");
    }

    [TestCleanup]
    public void RemovePackage()
    {
        Directory.Delete(root, true);
        File.Delete(root + "-nachbar");
    }

    [TestMethod]
    public void FilesAndDirectoriesReportSizeAndType()
    {
        Assert.AreEqual(WasmPackagePaths.Success, WasmPackagePaths.Stat(root, "res/Messages", out ulong size, out int type));
        Assert.AreEqual(5UL, size);
        Assert.AreEqual(WasmPackagePaths.TypeFile, type);
        Assert.AreEqual(WasmPackagePaths.Success, WasmPackagePaths.Stat(root, "res", out size, out type));
        Assert.AreEqual(0UL, size);
        Assert.AreEqual(WasmPackagePaths.TypeDirectory, type);
    }

    [TestMethod]
    public void MissingFileOrParentIsNotFound()
    {
        Assert.AreEqual(WasmPackagePaths.NotFound, WasmPackagePaths.Stat(root, "res/fehlt.css", out _, out _));
        Assert.AreEqual(WasmPackagePaths.NotFound, WasmPackagePaths.Stat(root, "fehlt/Messages", out _, out _));
    }

    [TestMethod]
    public void PathsOutsideThePackageAreDenied()
    {
        string sibling = "../" + Path.GetFileName(root) + "-nachbar";
        Assert.AreEqual(WasmPackagePaths.AccessDenied, WasmPackagePaths.Stat(root, sibling, out ulong size, out int type));
        Assert.AreEqual(0UL, size);
        Assert.AreEqual(0, type);
        Assert.AreEqual(WasmPackagePaths.AccessDenied, WasmPackagePaths.Stat(root, "res/../..", out _, out _));
        Assert.ThrowsException<UnauthorizedAccessException>(() => WasmPackagePaths.Resolve(root, sibling));
    }

    [TestMethod]
    public void PackageRootItselfAndTrailingSeparatorAreInside()
    {
        Assert.AreEqual(Path.GetFullPath(root), WasmPackagePaths.Resolve(root + Path.DirectorySeparatorChar, "."));
        Assert.AreEqual(Path.Combine(Path.GetFullPath(root), "res"), WasmPackagePaths.Resolve(root, "res"));
    }

    [TestMethod]
    public void SymbolicLinkAsFinalComponentIsDenied()
    {
        // Geprueft wird nur das letzte Pfadglied; Zwischenverweise folgt GetAttributes (FAT kennt keine Verweise).
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "res", "Verweis"), Path.Combine(root, "res", "Messages"));
            Directory.CreateSymbolicLink(Path.Combine(root, "Ordner"), Path.Combine(root, "res"));
        }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        {
            Assert.Inconclusive("Windows erlaubt diesem Benutzer keine symbolischen Links.");
        }
        Assert.AreEqual(WasmPackagePaths.AccessDenied, WasmPackagePaths.Stat(root, "res/Verweis", out ulong size, out int type));
        Assert.AreEqual(0UL, size);
        Assert.AreEqual(0, type);
        Assert.AreEqual(WasmPackagePaths.AccessDenied, WasmPackagePaths.Stat(root, "Ordner", out _, out _));
    }

    [TestMethod]
    public void NoRunningAppIsDenied()
    {
        Assert.AreEqual(WasmPackagePaths.AccessDenied, WasmPackagePaths.Stat(null, "res/Messages", out _, out _));
    }

    [TestMethod]
    public void InvalidPathBecomesIoErrorInsteadOfException()
    {
        Assert.AreEqual(WasmPackagePaths.Io, WasmPackagePaths.Stat(root, "res/\0Messages", out _, out _));
    }

    [TestMethod]
    public void List_Root_ReturnsSortedEntries()
    {
        File.WriteAllBytes(Path.Combine(root, "b.txt"), []);
        File.WriteAllBytes(Path.Combine(root, "B.txt"), []);
        Assert.AreEqual(WasmPackagePaths.Success, WasmPackagePaths.List(root, "", out var entries));
        CollectionAssert.AreEqual(new[] { "B.txt", "b.txt", "res" }, Array.ConvertAll(entries, entry => entry.Name));
        Assert.IsTrue(entries[2].Directory);
        Assert.IsFalse(entries[0].Directory);
    }

    [TestMethod]
    public void List_Subdirectory_ReturnsOnlyChildren()
    {
        Assert.AreEqual(WasmPackagePaths.Success, WasmPackagePaths.List(root, "res", out var entries));
        Assert.AreEqual(1, entries.Length);
        Assert.AreEqual("Messages", entries[0].Name);
    }

    [TestMethod]
    public void List_DotDot_ReturnsEacces()
    {
        Assert.AreEqual(WasmPackagePaths.AccessDenied, WasmPackagePaths.List(root, "..", out var entries));
        Assert.AreEqual(0, entries.Length);
        Assert.AreEqual(WasmPackagePaths.AccessDenied, WasmPackagePaths.List(root, "res/../..", out _));
    }

    [TestMethod]
    public void List_File_ReturnsEnotdir()
    {
        Assert.AreEqual(WasmPackagePaths.NotDirectory, WasmPackagePaths.List(root, "res/Messages", out _));
        Assert.AreEqual(WasmPackagePaths.NotDirectory, WasmPackagePaths.Stat(root, "res/Messages/x", out _, out _));
    }

    [TestMethod]
    public void List_ReparsePointInMiddleComponent_IsRejected()
    {
        try { Directory.CreateSymbolicLink(Path.Combine(root, "Ordner"), Path.Combine(root, "res")); }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
        {
            Assert.Inconclusive("Windows erlaubt diesem Benutzer keine symbolischen Links.");
        }
        Assert.AreEqual(WasmPackagePaths.AccessDenied, WasmPackagePaths.Stat(root, "Ordner/Messages", out _, out _));
        Assert.AreEqual(WasmPackagePaths.AccessDenied, WasmPackagePaths.List(root, "Ordner", out _));
        Assert.ThrowsException<UnauthorizedAccessException>(() => WasmPackagePaths.ResolveChecked(root, "Ordner/Messages"));
        // Verweise fehlen in der Auflistung.
        Assert.AreEqual(WasmPackagePaths.Success, WasmPackagePaths.List(root, "", out var entries));
        CollectionAssert.AreEqual(new[] { "res" }, Array.ConvertAll(entries, entry => entry.Name));
    }

    [TestMethod]
    public void Join_LongerThan255_ReturnsEnametoolong()
    {
        string name = new('a', 256);
        Assert.AreEqual(WasmPackagePaths.NameTooLong, WasmPackagePaths.List(root, WasmPackagePaths.Join("res", name), out _));
        Assert.AreEqual(WasmPackagePaths.NameTooLong, WasmPackagePaths.Stat(root, name, out _, out _));
        Assert.AreEqual("res/x", WasmPackagePaths.Join("res", "x"));
        Assert.ThrowsException<UnauthorizedAccessException>(() => WasmPackagePaths.Join("res", "/etc"));
    }
}
