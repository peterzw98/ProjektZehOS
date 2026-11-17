using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Packages;
using ProjektZeh.Security;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Migration;
using ProjektZeh.Update;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class MigrationSourceWriterTests
{
    private string root = null!;
    private readonly MigrationWriteGate gate = new();

    [TestInitialize]
    public void Setup()
    {
        root = Path.Combine(Path.GetTempPath(), "ZehOS-MigrationWriters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        File.WriteAllText(Path.Combine(root, "one"), "original");
        File.WriteAllText(Path.Combine(root, "source"), "input");
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(root, true);

    [TestMethod]
    [DataRow("write")]
    [DataRow("copy")]
    [DataRow("move")]
    [DataRow("delete")]
    [DataRow("mkdir")]
    [DataRow("rmdir")]
    [DataRow("touch")]
    public void MigrationRefusesEveryFileMutationButReadingContinues(string operation)
    {
        var files = new FileService(root, tryBeginWrite: gate.TryBeginWrite);
        using var migration = gate.TryLockSource();
        Assert.IsNotNull(migration);
        var result = operation switch
        {
            "write" => files.WriteText("one", "changed"),
            "copy" => files.CopyFile("source", "new"),
            "move" => files.MoveFile("source", "new"),
            "delete" => files.DeleteFile("one"),
            "mkdir" => files.CreateDirectory("new"),
            "rmdir" => files.DeleteDirectory("folder"),
            _ => files.Touch("one")
        };
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "StorageBusy");
        Assert.AreEqual("original", files.ReadText("one").Value);
        Assert.AreEqual("input", File.ReadAllText(Path.Combine(root, "source")));
        Assert.IsTrue(Directory.Exists(Path.Combine(root, "folder")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "new")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "new")));
    }

    [TestMethod]
    public void FileLeaseCoversReplacementAndRestorationAndIsReleasedAfterFailure()
    {
        var files = new FileService(root, moveFile: (from, to, overwrite) =>
        {
            Assert.IsNull(gate.TryLockSource());
            if (from.EndsWith(".tmp", StringComparison.Ordinal)) throw new IOException("rename failed");
            File.Move(from, to, overwrite);
        }, tryBeginWrite: gate.TryBeginWrite);
        Assert.IsFalse(files.WriteText("one", "changed").IsOK());
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "one")));
        using var migration = gate.TryLockSource();
        Assert.IsNotNull(migration);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedWriteCleansTemporaryFilesBeforeReleasingItsLease(bool copy)
    {
        var files = new FileService(root, moveFile: (from, to, overwrite) =>
        {
            if (from.EndsWith(".tmp", StringComparison.Ordinal)) throw new IOException("rename failed");
            File.Move(from, to, overwrite);
        }, tryBeginWrite: () => new CheckOnDispose(() =>
            Assert.AreEqual(0, Directory.GetFiles(root, ".zehos-*.tmp").Length)));
        Assert.IsFalse((copy ? files.CopyFile("source", "new") : files.WriteText("one", "changed")).IsOK());
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(root, "one")));
    }

    private sealed class CheckOnDispose(Action check) : IDisposable
    {
        public void Dispose() => check();
    }

    // Review peter-review P3-b (4.5): eine Gate-Ablehnung ist "Laufwerk gesperrt", nicht "Zugriff verweigert".
    [TestMethod]
    public void BlockedWriteCheckReportsStorageBusyNotDenied()
    {
        var accounts = new AccountService(root, new PasswordHasher(bytes => Array.Fill(bytes, (byte)7)),
            StringComparison.OrdinalIgnoreCase, null, tryBeginWrite: gate.TryBeginWrite);
        var admin = accounts.CreateSession();
        Assert.IsTrue(accounts.PrepareRecovery(admin, "admin", "admin-password-123")().IsOK());
        string path = Path.Combine(root, "managed");
        Assert.IsTrue(accounts.Access.Check(admin, path, FilePermission.Write, creating: true).IsOK());
        using var migration = gate.TryLockSource();

        var blocked = accounts.Access.Check(admin, path, FilePermission.Write, creating: true);

        StringAssert.Contains(blocked.Message, "StorageBusy");
    }

    [TestMethod]
    public void BlockedAccountMutationsPreserveMetadataAndExistingSessions()
    {
        var accounts = new AccountService(root, new PasswordHasher(bytes => Array.Fill(bytes, (byte)7)),
            StringComparison.OrdinalIgnoreCase, null, tryBeginWrite: gate.TryBeginWrite);
        var admin = accounts.CreateSession();
        Assert.IsTrue(accounts.PrepareRecovery(admin, "admin", "admin-password-123")().IsOK());
        Assert.IsTrue(accounts.PrepareCreateUser(admin, "other", false, "user-password-123")().IsOK());
        var other = accounts.CreateSession();
        Assert.IsTrue(accounts.PrepareLogin(other, "other", "user-password-123")().IsOK());
        var files = new FileService(root, access: accounts.Access, session: admin);
        Assert.IsTrue(files.WriteText("managed", "data").IsOK());
        string metadata = File.ReadAllText(Path.Combine(root, AccountService.MetadataFile));
        var passwordApply = accounts.PasswordWork(admin, "admin", "changed-password-123")();
        using var migration = gate.TryLockSource();
        Assert.IsNotNull(migration);
        Assert.IsFalse(accounts.DeleteUser(admin, "other").IsOK());
        Assert.IsFalse(accounts.SetCommandAccess(admin, "fetch", true).IsOK());
        Assert.IsFalse(passwordApply().IsOK());
        Assert.IsFalse(accounts.PrepareCreateUser(admin, "new", false, "user-password-123")().IsOK());
        Assert.IsFalse(accounts.Grant(admin, Path.Combine(root, "managed"), "other", FilePermission.Read).IsOK());
        Assert.IsFalse(accounts.Adopt(admin, Path.Combine(root, "source"), "admin").IsOK());
        Assert.IsFalse(accounts.PrepareRecovery(admin, "rescued", "admin-password-456")().IsOK());
        Assert.IsFalse(files.DeleteFile("managed").IsOK());
        Assert.IsTrue(accounts.IsAdmin(admin));
        Assert.IsNotNull(other.UserId);
        Assert.IsTrue(accounts.IsReady);
        Assert.AreEqual(metadata, File.ReadAllText(Path.Combine(root, AccountService.MetadataFile)));
        Assert.AreEqual("data", files.ReadText("managed").Value);
        var login = accounts.CreateSession();
        Assert.IsTrue(accounts.PrepareLogin(login, "admin", "admin-password-123")().IsOK());
    }

    [TestMethod]
    public void PackageAndUpdateWritersCannotChangeTheirStateWhileMigrationOwnsTheSource()
    {
        string packagesRoot = Path.Combine(root, ".zehos-packages");
        var packages = new PackageStore(packagesRoot, null, new UpdateVersion(1, 0, 0), tryBeginWrite: gate.TryBeginWrite);
        var update = new FileUpdateTarget(root, tryBeginWrite: gate.TryBeginWrite);
        Directory.CreateDirectory(Path.Combine(root, "boot"));
        File.WriteAllText(Path.Combine(root, "boot", "ProjektZeh.elf"), "old kernel");
        using var migration = gate.TryLockSource();
        Assert.IsNotNull(migration);
        Assert.IsFalse(packages.WriteSource("https://example.invalid/").IsOK());
        Assert.IsFalse(packages.Reconcile().IsOK());
        Assert.IsFalse(update.ReplaceKernel([1, 2, 3], rotateBackup: true).IsOK());
        Assert.IsFalse(update.WriteVersion("2.0.0").IsOK());
        Assert.IsFalse(Directory.Exists(packagesRoot));
        Assert.AreEqual("old kernel", File.ReadAllText(Path.Combine(root, "boot", "ProjektZeh.elf")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "boot", "ProjektZeh.elf.bak")));
    }
}
