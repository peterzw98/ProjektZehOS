using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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

    // ---- Review c5b7ff9: Konten-/Home-Pfade und Listen ohne Rechteverwaltung ----

    private static AccountService Accounts(string root, Func<string, bool, ProjektZeh.Shared.ResultErr> guard) =>
        new(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)), StringComparison.Ordinal, guard);

    private static Func<string, bool, ProjektZeh.Shared.ResultErr> DenyWhen(Func<string, bool> denied) =>
        (path, _) => denied(path) ? FileAccessControl.Denied() : ProjektZeh.Shared.ResultErr.CreateSuccess();

    private static string NewRoot()
    {
        string root = Path.Combine(Temp, "zehos-e3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [TestMethod]
    public void Recovery_GuardDeniesMetadata_MovesAndSavesNothing()
    {
        string root = NewRoot();
        try
        {
            string metadata = Path.Combine(root, AccountService.MetadataFile);
            File.WriteAllText(metadata, "alt");
            var accounts = Accounts(root, DenyWhen(path => path.EndsWith(AccountService.MetadataFile, StringComparison.Ordinal)));
            Assert.IsFalse(accounts.PrepareRecovery(accounts.CreateSession(), "admin", "admin-password-123")().IsOK());
            Assert.AreEqual("alt", File.ReadAllText(metadata), "Metadaten bleiben am Platz");
            Assert.AreEqual(0, Directory.GetFiles(root, ".zehos-security-recovery-*").Length);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "home")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Recovery_GuardDeniesHomes_ChangesNoMetadata()
    {
        string root = NewRoot();
        try
        {
            var accounts = Accounts(root, DenyWhen(path => path.StartsWith(Path.Combine(root, "home"), StringComparison.Ordinal)));
            Assert.IsFalse(accounts.PrepareRecovery(accounts.CreateSession(), "admin", "admin-password-123")().IsOK());
            Assert.IsFalse(File.Exists(Path.Combine(root, AccountService.MetadataFile)), "ungueltige Home-Topologie vor dem Speichern abgewiesen");
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "home")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CreateUser_GuardDeniesHome_SavesNothing()
    {
        string root = NewRoot();
        try
        {
            string evil = Path.Combine(root, "home", "evil");
            var accounts = Accounts(root, DenyWhen(path => path.StartsWith(evil, StringComparison.Ordinal)));
            var admin = accounts.CreateSession();
            Assert.IsTrue(accounts.PrepareRecovery(admin, "admin", "admin-password-123")().IsOK());
            string metadata = Path.Combine(root, AccountService.MetadataFile);
            byte[] before = File.ReadAllBytes(metadata);
            Assert.IsFalse(accounts.PrepareCreateUser(admin, "evil", false, "user-password-123")().IsOK());
            CollectionAssert.AreEqual(before, File.ReadAllBytes(metadata), "Konten/Rechte unveraendert");
            Assert.IsFalse(accounts.Access.Contains(evil));
            Assert.IsFalse(Directory.Exists(evil));
            Assert.IsTrue(accounts.PrepareCreateUser(admin, "gut", false, "user-password-123")().IsOK(), "andere Konten weiter moeglich");
        }
        finally { Directory.Delete(root, true); }
    }

    // Alle reservierten Metadaten (Konten, Befehlsrechte, Recovery-Sicherungen) mit Inhalt.
    private static SortedDictionary<string, string> Metadata(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(root, ".zehos-*")) files[Path.GetFileName(file)] = Convert.ToBase64String(File.ReadAllBytes(file));
        return files;
    }

    private static AccountService PlainAccounts(string root) => new(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)));

    [TestMethod]
    public void Recovery_HomeIsAFile_ChangesNoMetadata()
    {
        string root = NewRoot();
        try
        {
            var accounts = PlainAccounts(root);
            Assert.IsTrue(accounts.PrepareRecovery(accounts.CreateSession(), "admin", "admin-password-123")().IsOK());
            string homes = Path.Combine(root, "home");
            Directory.Delete(homes, true);
            File.WriteAllText(homes, "hindernis");
            var before = Metadata(root);
            Assert.IsFalse(accounts.PrepareRecovery(accounts.CreateSession(), "neu", "admin-password-456")().IsOK());
            CollectionAssert.AreEqual(before, Metadata(root), "Konten/Commands byteidentisch, keine Recovery-Sicherung");
            Assert.AreEqual("hindernis", File.ReadAllText(homes));
            var reloaded = PlainAccounts(root);
            Assert.IsTrue(reloaded.Load().IsOK(), "bestehende Konten bleiben ladbar");
        }
        finally { File.Delete(Path.Combine(root, "home")); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Recovery_UserHomeIsAFile_ChangesNoMetadata()
    {
        string root = NewRoot();
        try
        {
            var accounts = PlainAccounts(root);
            Assert.IsTrue(accounts.PrepareRecovery(accounts.CreateSession(), "admin", "admin-password-123")().IsOK());
            string obstacle = Path.Combine(root, "home", "neu");
            File.WriteAllText(obstacle, "hindernis");
            var before = Metadata(root);
            Assert.IsFalse(accounts.PrepareRecovery(accounts.CreateSession(), "neu", "admin-password-456")().IsOK());
            CollectionAssert.AreEqual(before, Metadata(root));
            Assert.AreEqual("hindernis", File.ReadAllText(obstacle));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CreateUser_UserHomeIsAFile_ChangesNoMetadata()
    {
        string root = NewRoot();
        try
        {
            var accounts = PlainAccounts(root);
            var admin = accounts.CreateSession();
            Assert.IsTrue(accounts.PrepareRecovery(admin, "admin", "admin-password-123")().IsOK());
            string obstacle = Path.Combine(root, "home", "neu");
            File.WriteAllText(obstacle, "hindernis");
            var before = Metadata(root);
            Assert.IsFalse(accounts.PrepareCreateUser(admin, "neu", false, "user-password-123")().IsOK());
            CollectionAssert.AreEqual(before, Metadata(root));
            Assert.IsFalse(accounts.Access.Contains(obstacle));
            Assert.AreEqual("hindernis", File.ReadAllText(obstacle));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Setup_HomeIsAFile_CreatesNoMetadata()
    {
        string root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "home"), "hindernis");
            var accounts = PlainAccounts(root);
            accounts.IsDataMounted = () => true;
            accounts.SetupWindowOpen = true;
            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsTrue(accounts.NeedsSetup);
            Assert.IsFalse(accounts.SetupWork(accounts.CreateSession(), "admin", "admin-password-123")()().IsOK());
            Assert.AreEqual(0, Metadata(root).Count, "keine Metadaten angelegt");
            Assert.AreEqual("hindernis", File.ReadAllText(Path.Combine(root, "home")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FileServiceWithoutAccess_ListingsSkipGuardedChildren()
    {
        string root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "b.txt"), "b");
            var files = new FileService(root, pathGuard: DenyWhen(path => path.EndsWith("b.txt", StringComparison.Ordinal)));
            CollectionAssert.AreEqual(new[] { "a.txt" }, Array.ConvertAll(files.ListEntries(".").Value!, Path.GetFileName));
            CollectionAssert.AreEqual(new[] { "a.txt" }, Array.ConvertAll(files.ListDirectory(".").Value!, entry => Path.GetFileName(entry.Path)));
            CollectionAssert.AreEqual(new[] { "a.txt" }, Array.ConvertAll(files.FindEntries(".").Value!, entry => Path.GetFileName(entry.Path)));
        }
        finally { Directory.Delete(root, true); }
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
