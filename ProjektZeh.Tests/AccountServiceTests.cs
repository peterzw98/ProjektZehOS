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
    public void MissingAccountFile_OffersSetupOnlyWhenNotBlockedByDiskSelection()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var accounts = new AccountService(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)));

            accounts.IsDataMounted = () => true;
            accounts.SetupWindowOpen = true;
            accounts.SetupBlockedReason = "cause='AmbiguousDataDisk'";
            var blocked = accounts.Load();
            Assert.IsFalse(blocked.IsOK());
            Assert.IsFalse(accounts.NeedsSetup, "kein Konto auf einem falsch gewaehlten Laufwerk");
            StringAssert.Contains(blocked.Message, "AmbiguousDataDisk");
            Assert.IsFalse(accounts.SetupWork(accounts.CreateSession(), "admin", "admin-password-123")()().IsOK());

            accounts.SetupBlockedReason = null;
            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsTrue(accounts.NeedsSetup, "ohne Sperre bleibt die Ersteinrichtung wie bisher");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SecurityDirectory_HoldsAccountFilesOutsideRoot()
    {
        // Etappe 4.3, Root-Profil: Konten unter /etc/zehos, Verwaltungswurzel / (Spec Etappe 4, Abschnitt 1).
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string security = Path.Combine(root, "etc", "zehos");
        Directory.CreateDirectory(security);
        try
        {
            var accounts = new AccountService(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)), StringComparison.Ordinal, null,
                securityDirectory: security)
            {
                IsDataMounted = () => true,
                SetupWindowOpen = true,
            };
            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsTrue(accounts.NeedsSetup);
            Assert.IsTrue(accounts.SetupWork(accounts.CreateSession(), "admin", "admin-password-123")()().IsOK());
            Assert.IsTrue(File.Exists(Path.Combine(security, AccountService.MetadataFile)));
            Assert.IsFalse(File.Exists(Path.Combine(root, AccountService.MetadataFile)));

            var reloaded = new AccountService(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)), StringComparison.Ordinal, null,
                securityDirectory: security);
            Assert.IsTrue(reloaded.Load().IsOK(), "Konten aus dem Sicherheitsverzeichnis geladen");
        }
        finally { Directory.Delete(root, true); }
    }

    [DataTestMethod]
    [DataRow(true, true, false, true)]
    [DataRow(false, true, false, false)]
    [DataRow(true, false, false, false)]
    [DataRow(true, true, true, false)]
    [DataRow(false, false, false, false)]
    public void MaySetUp_NeedsMountedBootAndNoAccountsFile(bool mounted, bool atBoot, bool hasAccountsFile, bool expected)
    {
        Assert.AreEqual(expected, AccountService.MaySetUp(mounted, atBoot, hasAccountsFile));
    }

    [TestMethod]
    public void MissingAccountFile_DefaultsNeverOfferSetup()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var accounts = new AccountService(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)));

            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsFalse(accounts.NeedsSetup, "ohne ausdrueckliches Bootfenster und eingebundenes /data nie eine Ersteinrichtung");
            accounts.SetupWindowOpen = true;
            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsFalse(accounts.NeedsSetup, "auch im Bootfenster gilt /data ohne ausdrueckliche Angabe als nicht eingebunden");
            accounts.SetupWindowOpen = false;
            accounts.IsDataMounted = () => true;
            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsFalse(accounts.NeedsSetup, "nur das geoeffnete Bootfenster erlaubt sie");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MissingAccountFile_NoSetupWhenDataNotMountedOrAfterBoot()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var accounts = new AccountService(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes)));
            bool mounted = false;
            accounts.IsDataMounted = () => mounted;
            accounts.SetupWindowOpen = true;

            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsFalse(accounts.NeedsSetup, "ohne eingebundenes /data keine Ersteinrichtung, auch wenn die Datei 'fehlt'");

            mounted = true;
            Assert.IsFalse(accounts.Load().IsOK());
            Assert.IsTrue(accounts.NeedsSetup, "beim Boot mit eingebundenem /data bleibt die Ersteinrichtung moeglich");

            accounts.SetupWindowOpen = false;
            var afterBoot = accounts.Load();
            Assert.IsFalse(afterBoot.IsOK());
            Assert.IsFalse(accounts.NeedsSetup, "im laufenden Betrieb gesperrt statt eingerichtet");
            StringAssert.Contains(afterBoot.Message, "nur beim Boot");
            Assert.IsFalse(accounts.SetupWork(accounts.CreateSession(), "admin", "admin-password-123")()().IsOK());
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
            var pendingLogin = accounts.PrepareLogin(alice, "alice", "alice-password-123");
            Assert.IsTrue(accounts.Load().IsOK());
            Assert.IsFalse(pendingLogin().IsOK(), "Ein vor dem Neuladen vorbereitetes Login darf Sitzungen nicht wiederbeleben.");
            Assert.IsNull(alice.UserId);
            Assert.IsTrue(accounts.PrepareLogin(alice, "alice", "alice-password-123")().IsOK());
            Assert.IsTrue(accounts.PrepareLogin(bob, "bob", "bob-password-123")().IsOK());
            Assert.IsTrue(accounts.PrepareLogin(admin, "admin", "admin-password-123")().IsOK());
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
