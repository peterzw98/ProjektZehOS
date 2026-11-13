using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class AccountServiceTests
{
    [TestMethod]
    public void UserCreationDoesNotAdoptExistingHomesAndRecoveryPreservesOversizedMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var accounts = new AccountService(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)));
            var admin = accounts.CreateSession();
            Assert.IsTrue(accounts.PrepareRecovery(admin, "admin", "admin-password-123")().IsOK());
            string home = Path.Combine(root, "home", "existing");
            Directory.CreateDirectory(home);
            Assert.IsFalse(accounts.PrepareCreateUser(admin, "existing", false, "user-password-123")().IsOK());
            Assert.IsFalse(accounts.Access.Contains(home));
            string corrupt = new('x', 70000);
            File.WriteAllText(Path.Combine(root, ".zehos-security.db"), corrupt);
            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsTrue(accounts.PrepareRecovery(admin, "rescued", "admin-password-456")().IsOK());
            string[] backups = Directory.GetFiles(root, ".zehos-security-recovery-*");
            Assert.AreEqual(1, backups.Length);
            Assert.AreEqual(corrupt, File.ReadAllText(backups[0]));
            Assert.IsTrue(accounts.Load().IsOK());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AccountsPermissionsAndIdsPersistAndCorruptionLocksEverySession()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var accounts = new AccountService(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)));
            var admin = accounts.CreateSession();
            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsTrue(accounts.PrepareRecovery(admin, "admin", "admin-password-123")().IsOK());
            Assert.IsTrue(accounts.PrepareCreateUser(admin, "alice", false, "alice-password-123")().IsOK());
            Assert.IsTrue(accounts.PrepareCreateUser(admin, "bob", false, "bob-password-123")().IsOK());
            var alice = accounts.CreateSession();
            var bob = accounts.CreateSession();
            Assert.IsTrue(accounts.PrepareLogin(alice, "alice", "alice-password-123")().IsOK());
            Assert.IsTrue(accounts.PrepareLogin(bob, "bob", "bob-password-123")().IsOK());
            string aliceId = alice.UserId!;
            var files = new FileService(alice.HomeDirectory!, access: accounts.Access, session: alice);
            Assert.IsTrue(files.WriteText("hello.txt", "hello").IsOK());
            string path = Path.Combine(alice.HomeDirectory!, "hello.txt");
            Assert.IsTrue(accounts.Grant(alice, path, "bob", FilePermission.Read).IsOK());
            Assert.IsTrue(accounts.Grant(admin, alice.HomeDirectory!, "bob", FilePermission.Read).IsOK());
            Assert.IsFalse(accounts.PrepareCreateUser(alice, "evil", true, "evil-password-123")().IsOK());
            var rebooted = new AccountService(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)));
            Assert.IsTrue(rebooted.Load().IsOK());
            var restored = rebooted.CreateSession();
            Assert.IsTrue(rebooted.PrepareLogin(restored, "alice", "alice-password-123")().IsOK());
            Assert.AreEqual(aliceId, restored.UserId);
            Assert.IsTrue(rebooted.Access.Check(bob, path, FilePermission.Read).IsOK());
            Assert.IsFalse(rebooted.Access.Check(bob, path, FilePermission.Write).IsOK());
            File.WriteAllText(Path.Combine(root, ".zehos-security.db"), "corrupt");
            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsNull(admin.UserId);
            Assert.IsNull(alice.UserId);
            Assert.IsFalse(files.ReadText("hello.txt").IsOK());
            Assert.IsFalse(accounts.PrepareLogin(bob, "bob", "bob-password-123")().IsOK());
        }
        finally { Directory.Delete(root, true); }
    }
}
