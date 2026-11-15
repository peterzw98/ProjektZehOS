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
    public void SymbolicLinksInsideThePackageAreDenied()
    {
        File.CreateSymbolicLink(Path.Combine(root, "res", "Verweis"), Path.Combine(root, "res", "Messages"));
        Directory.CreateSymbolicLink(Path.Combine(root, "Ordner"), Path.Combine(root, "res"));
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
}
