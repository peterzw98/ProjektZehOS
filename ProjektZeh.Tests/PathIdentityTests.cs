using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

/// <summary>Etappe 3.1: Pfadidentitaet und Rechte (Plan Etappe 3, docs/superpowers/specs/2026-11-17-storage-etappe3-design.md).</summary>
[TestClass]
public sealed class PathIdentityTests
{
    private static readonly string Temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

    private static (FileAccessControl Access, UserSession Alice) Prepared(string root, StringComparison comparison, params string[] owned)
    {
        var access = new FileAccessControl(root, comparison);
        access.Initialize();
        access.Assign(access.RootDirectory, "1");
        foreach (string path in owned) access.Assign(path, "1");
        return (access, new UserSession("1", "alice", false, root));
    }

    [TestMethod]
    public void Ext2Root_CaseDiffers_IsADifferentPath()
    {
        string root = Path.Combine(Temp, "zehos-ext2");
        var (access, alice) = Prepared(root, StringComparison.Ordinal, Path.Combine(root, "A"));
        Assert.IsTrue(access.Check(alice, Path.Combine(root, "A"), FilePermission.Read).IsOK());
        Assert.IsFalse(access.Check(alice, Path.Combine(root, "a"), FilePermission.Read).IsOK(), "a ist auf ext2 eine andere Datei");
        Assert.IsFalse(access.Contains(Path.Combine(root, "a")));
    }

    [TestMethod]
    public void FatRoot_CaseDiffers_IsTheSamePath()
    {
        string root = Path.Combine(Temp, "zehos-fat");
        var (access, alice) = Prepared(root, StringComparison.OrdinalIgnoreCase, Path.Combine(root, "A"));
        Assert.IsTrue(access.Check(alice, Path.Combine(root, "a"), FilePermission.Read).IsOK(), "FAT-Alias");
        Assert.IsTrue(access.Contains(Path.Combine(root, "a")));
    }

    [TestMethod]
    public void Ext2Root_RestoreAndMove_UseTheSameComparison()
    {
        string root = Path.Combine(Temp, "zehos-ext2-restore");
        var (access, _) = Prepared(root, StringComparison.Ordinal, Path.Combine(root, "A"), Path.Combine(root, "a"));
        var restored = new FileAccessControl(root, StringComparison.Ordinal);
        Assert.IsTrue(restored.Restore(access.Serialize()).IsOK(), "A und a sind auf ext2 zwei Eintraege");
        Assert.IsTrue(restored.Contains(Path.Combine(root, "A")) && restored.Contains(Path.Combine(root, "a")));
        Assert.IsFalse(restored.PrepareMove(Path.Combine(root, "A"), Path.Combine(root, "a")).IsOK(), "Ziel a existiert schon");
        var fat = new FileAccessControl(root, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(fat.Restore(access.Serialize()).IsOK(), "auf FAT waeren A und a derselbe Schluessel");
    }

    [TestMethod]
    public void PrefixNeighbour_IsNotManaged()
    {
        string root = Path.Combine(Temp, "home", "a");
        var access = new FileAccessControl(root, StringComparison.Ordinal);
        Assert.IsTrue(access.IsManagedPath(Path.Combine(root, "x")));
        Assert.IsFalse(access.IsManagedPath(Path.Combine(Temp, "home", "ab", "x")));
        Assert.IsFalse(access.IsManagedPath(root + "b"));
    }

    [DataTestMethod]
    [DataRow("relativ/x")]
    [DataRow("x")]
    [DataRow("")]
    public void RelativePaths_AreNotManaged(string path)
    {
        var access = new FileAccessControl(Path.Combine(Temp, "zehos-rel"), StringComparison.Ordinal);
        Assert.IsFalse(access.IsManagedPath(path));
    }

    [TestMethod]
    public void FilesystemRoot_ManagesEtcAndHomeAndChecksParentsUpToRoot()
    {
        // Wurzel "/" (unter Windows das Laufwerk): bisher ergab das Praefix "//" und nichts war verwaltet.
        string root = Path.GetPathRoot(Temp)!;
        string etc = Path.Combine(root, "etc");
        string home = Path.Combine(root, "home");
        string alice = Path.Combine(home, "alice");
        var access = new FileAccessControl(root, StringComparison.Ordinal);
        Assert.AreEqual(root, access.RootDirectory, "die Wurzel selbst behaelt ihren Trenner");
        Assert.IsTrue(access.IsManagedPath(root));
        Assert.IsTrue(access.IsManagedPath(etc) && access.IsManagedPath(Path.Combine(alice, "notiz.txt")));
        Assert.IsFalse(access.IsManagedPath(root + Path.DirectorySeparatorChar + "etc"), "doppelter Trenner");

        access.Initialize();
        access.Assign(root, "0");
        access.Assign(home, "0");
        access.Assign(alice, "1");
        access.Assign(Path.Combine(alice, "notiz.txt"), "1");
        var user = new UserSession("1", "alice", false, alice);
        Assert.IsFalse(access.Check(user, Path.Combine(alice, "notiz.txt"), FilePermission.Read).IsOK(), "/ und /home ohne Leserecht");
        access.Grant(root, "1", FilePermission.Read);
        Assert.IsFalse(access.Check(user, Path.Combine(alice, "notiz.txt"), FilePermission.Read).IsOK(), "/home fehlt noch");
        access.Grant(home, "1", FilePermission.Read);
        Assert.IsTrue(access.Check(user, Path.Combine(alice, "notiz.txt"), FilePermission.Read).IsOK(), "Elternpruefung endet an /");
        Assert.IsFalse(access.Check(user, etc, FilePermission.Write).IsOK());
    }

    [TestMethod]
    public void FilesystemRoot_SurvivesRestore()
    {
        string root = Path.GetPathRoot(Temp)!;
        var access = new FileAccessControl(root, StringComparison.Ordinal);
        access.Initialize();
        access.Assign(root, "0");
        access.Assign(Path.Combine(root, "etc"), "0");
        var restored = new FileAccessControl(root, StringComparison.Ordinal);
        Assert.IsTrue(restored.Restore(access.Serialize()).IsOK());
        Assert.IsTrue(restored.Contains(Path.Combine(root, "etc")));
    }

    [TestMethod]
    public void Guard_IsAskedBeforeEveryDecision()
    {
        string root = Path.Combine(Temp, "zehos-guard");
        var calls = new List<(string Path, bool Creating)>();
        var access = new FileAccessControl(root, StringComparison.Ordinal, (path, creating) =>
        {
            calls.Add((path, creating));
            return FileAccessControl.Denied();
        });
        access.Initialize();
        access.Assign(root, "1");
        access.Assign(Path.Combine(root, "x"), "1");
        var alice = new UserSession("1", "alice", false, root);
        Assert.IsFalse(access.Check(alice, Path.Combine(root, "x"), FilePermission.Read).IsOK(), "Waechter verweigert trotz Recht");
        Assert.IsFalse(access.Check(alice, Path.Combine(root, "neu"), FilePermission.Write, creating: true).IsOK());
        CollectionAssert.AreEqual(new[] { (Path.Combine(root, "x"), false), (Path.Combine(root, "neu"), true) }, calls);
    }

    [TestMethod]
    public void FileService_EveryOperationReachesTheGuardAndChangesNothing()
    {
        string directory = Path.Combine(Temp, "zehos-guard-io-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string existing = Path.Combine(directory, "a.txt");
            File.WriteAllText(existing, "x");
            var calls = new List<(string Path, bool Creating)>();
            var files = new FileService(directory, pathGuard: (path, creating) =>
            {
                calls.Add((path, creating));
                return FileAccessControl.Denied();
            });
            Assert.IsFalse(files.ReadText("a.txt").IsOK(), "Lesen");
            Assert.IsFalse(files.WriteText("b.txt", "y").IsOK(), "Schreiben/Ersetzen");
            Assert.IsFalse(files.ListEntries(".").IsOK(), "Listen");
            Assert.IsFalse(files.MoveFile("a.txt", "c.txt").IsOK(), "Verschieben");
            Assert.IsFalse(files.CopyFile("a.txt", "d.txt").IsOK(), "Kopieren");
            Assert.IsFalse(files.DeleteFile("a.txt").IsOK(), "Loeschen");
            Assert.IsFalse(files.CreateDirectory("neu").IsOK(), "Verzeichnis");
            Assert.IsFalse(files.GetFileSize("a.txt").IsOK(), "Metadaten");
            Assert.AreEqual("x", File.ReadAllText(existing));
            CollectionAssert.AreEquivalent(new[] { "a.txt" }, Array.ConvertAll(Directory.GetFileSystemEntries(directory), Path.GetFileName));
            CollectionAssert.Contains(calls, (Path.Combine(directory, "b.txt"), true), "Schreibziel mit creating");
            CollectionAssert.Contains(calls, (existing, false), "Lesequelle ohne creating");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void FileService_GuardException_Denies()
    {
        var files = new FileService(Temp, pathGuard: (_, _) => throw new InvalidOperationException("kaputt"));
        Assert.IsFalse(files.ReadText("egal.txt", allowMissing: true).IsOK());
    }

    // ---- PathGuard (VFS-Pfade mit '/') ----

    private static readonly string[] Mounts = ["/", "/data", "/proc", "/mnt/ext2-ro"];

    private static Func<string, string, PathNodeKind> Tree(params (string Path, PathNodeKind Kind)[] nodes)
    {
        var map = new Dictionary<string, PathNodeKind>(StringComparer.Ordinal);
        foreach (var node in nodes) map[node.Path] = node.Kind;
        return (parent, name) => map.TryGetValue(parent == "/" ? "/" + name : parent + "/" + name, out var kind) ? kind : PathNodeKind.Missing;
    }

    private static readonly Func<string, string, PathNodeKind> Home = Tree(
        ("/data", PathNodeKind.Directory), ("/data/home", PathNodeKind.Directory), ("/data/home/a", PathNodeKind.Directory),
        ("/data/home/a/x.txt", PathNodeKind.File), ("/data/home/link", PathNodeKind.Symlink),
        ("/data/home/a/blatt", PathNodeKind.Symlink), ("/data/schleife", PathNodeKind.Symlink));

    [DataTestMethod]
    [DataRow("/data/home/a/x.txt", false, true)]
    [DataRow("/data/home/a/neu.txt", true, true)]
    [DataRow("/data/home/a/neu.txt", false, false)]
    [DataRow("/data/home/link/x.txt", false, false)]
    [DataRow("/data/home/a/blatt", false, false)]
    [DataRow("/data/schleife/x", false, false)]
    [DataRow("/data/fehlt/neu.txt", true, false)]
    [DataRow("/data/home/../home/a/x.txt", false, false)]
    [DataRow("/data/home//a/x.txt", false, false)]
    [DataRow("/datax/y", false, false)]
    public void Guard_Ext2Paths(string path, bool creating, bool allowed)
    {
        Assert.AreEqual(allowed, PathGuard.Check("/data", path, Mounts, Home, creating).IsOK(), path);
    }

    [DataTestMethod]
    [DataRow("/proc/1/status")]
    [DataRow("/mnt/ext2-ro/probe.bin")]
    [DataRow("/data/x")]
    public void Guard_RootSlash_RejectsPathsInOtherMounts(string path)
    {
        var lookup = Tree(("/etc", PathNodeKind.Directory), ("/data", PathNodeKind.Directory), ("/data/x", PathNodeKind.File));
        Assert.IsFalse(PathGuard.Check("/", path, Mounts, lookup, false).IsOK(), path);
    }

    [TestMethod]
    public void Guard_RootSlash_AllowsItsOwnFilesystem()
    {
        var lookup = Tree(("/etc", PathNodeKind.Directory), ("/etc/hosts", PathNodeKind.File));
        Assert.IsTrue(PathGuard.Check("/", "/etc/hosts", Mounts, lookup, false).IsOK());
        Assert.IsTrue(PathGuard.Check("/", "/", Mounts, lookup, false).IsOK());
    }

    [TestMethod]
    public void Guard_RootThatIsNoMountPoint_Denies()
    {
        Assert.IsFalse(PathGuard.Check("/data/home", "/data/home/a/x.txt", Mounts, Home, false).IsOK(), "Wurzel muss Einhaengepunkt sein");
    }

    [TestMethod]
    public void AccessControl_GuardExceptionDeniesEvenAdministrator()
    {
        string root = Path.Combine(Temp, "zehos-guard-throw");
        var access = new FileAccessControl(root, StringComparison.Ordinal, (_, _) => throw new IOException("kaputt"));
        access.Initialize();
        access.Assign(root, "1");
        access.Assign(Path.Combine(root, "x"), "1");
        var admin = new UserSession("1", "admin", true, root);
        Assert.IsFalse(access.Check(admin, Path.Combine(root, "x"), FilePermission.Read).IsOK());
        Assert.IsFalse(access.Guard(Path.Combine(root, "x"), false).IsOK());
    }

    [TestMethod]
    public void Guard_WithoutMountForRoot_Denies()
    {
        Assert.IsFalse(PathGuard.Check("/data", "/data/x", ["/proc"], Home, false).IsOK());
    }

    [DataTestMethod]
    [DataRow("/", "/")]
    [DataRow("/data", "/data")]
    [DataRow("/data/home/a", "/data")]
    [DataRow("/datax", "/")]
    [DataRow("/mnt/ext2-ro/x", "/mnt/ext2-ro")]
    [DataRow("/mnt", "/")]
    [DataRow("/DATA/x", "/")]
    public void MountOf_FollowsCosmosRule(string path, string expected)
    {
        Assert.AreEqual(expected, PathGuard.MountOf(Mounts, path));
    }

    [TestMethod]
    public void MountOf_WithoutCoveringMount_IsNull()
    {
        Assert.IsNull(PathGuard.MountOf(["/data"], "/etc/x"));
    }
}
