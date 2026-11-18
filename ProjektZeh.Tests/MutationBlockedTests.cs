using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;

namespace ProjektZeh.Tests;

/// <summary>
/// Etappe 7a Schritt 3b: Sperre bei Rootfehlern sitzt in AccountService und wird vor jeder Mutation neu ausgewertet
/// (Spec 7a, Abschnitt 5, huang P2-2): nicht ruecksetzbar ueber SetupBlockedReason, gilt fuer Ersteinrichtung und Recovery.
/// </summary>
[DoNotParallelize]
[TestClass]
public sealed class MutationBlockedTests
{
    private const string Reason = "cause='RootMissing' message='Kein GPT-Eintrag mit der Root-GUID des Markers. Keine Ersteinrichtung.'";

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "zehos-7a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static AccountService Accounts(string root, Func<string?> blocked) =>
        new(root, new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes))) { MutationBlockedReason = blocked, IsDataMounted = () => true, SetupWindowOpen = true };

    private static SortedDictionary<string, string> Metadata(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(root, ".zehos-*")) files[Path.GetFileName(file)] = Convert.ToBase64String(File.ReadAllBytes(file));
        return files;
    }

    [TestMethod]
    public void Blocked_NoSetupOffered_EvenAfterSetupBlockedReasonReset()
    {
        string root = NewRoot();
        try
        {
            var accounts = Accounts(root, () => Reason);
            var loaded = accounts.Load();
            Assert.IsFalse(accounts.NeedsSetup);
            StringAssert.Contains(loaded.Message, "RootMissing");
            // Kernel.cs setzt nach disk use/mount/init SetupBlockedReason = null und laedt neu: die Sperre bleibt.
            accounts.SetupBlockedReason = null;
            accounts.Load();
            Assert.IsFalse(accounts.NeedsSetup);
            Assert.IsFalse(accounts.SetupWork(accounts.CreateSession(), "admin", "admin-password-123")()().IsOK());
            Assert.AreEqual(0, Metadata(root).Count, "keine Konten angelegt");
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "home")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Blocked_RecoveryNeverMovesOrReplacesAccounts()
    {
        string root = NewRoot();
        try
        {
            string? blocked = null;
            var accounts = Accounts(root, () => blocked);
            var admin = accounts.CreateSession();
            Assert.IsTrue(accounts.PrepareRecovery(admin, "admin", "admin-password-123")().IsOK());
            var before = Metadata(root);

            // Weg "Boot blockiert -> disk use -> security recover": die Sperre gilt beim Aufruf, nicht nur beim Boot.
            blocked = Reason;
            var result = accounts.PrepareRecovery(accounts.CreateSession(), "neu", "admin-password-456")();
            Assert.IsFalse(result.IsOK());
            StringAssert.Contains(result.Message, "RootMissing");
            CollectionAssert.AreEqual(before, Metadata(root), "Kontendatei byteidentisch, keine .zehos-security-recovery-*.db");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Blocked_EvaluatedAtCommitNotAtPrepare()
    {
        string root = NewRoot();
        try
        {
            string? blocked = null;
            var accounts = Accounts(root, () => blocked);
            // Vorbereitet (Kennwort gehasht) solange frei, gesperrt vor dem Festschreiben: nichts wird geschrieben.
            var commit = accounts.RecoveryWork(accounts.CreateSession(), "admin", "admin-password-123")();
            blocked = Reason;
            Assert.IsFalse(commit().IsOK());
            Assert.AreEqual(0, Metadata(root).Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ThrowingCheck_FailsClosedWithTypeNameOnly()
    {
        string root = NewRoot();
        try
        {
            var accounts = Accounts(root, () => throw new InvalidOperationException("geheime Details"));
            var loaded = accounts.Load();
            Assert.IsFalse(accounts.NeedsSetup, "eine Ausnahme der Pruefung sperrt, statt den Boot zu werfen");
            StringAssert.Contains(loaded.Message, "cause='IdentityCheckFailed'");
            StringAssert.Contains(loaded.Message, "InvalidOperationException");
            Assert.IsFalse(loaded.Message.Contains("geheime Details", StringComparison.Ordinal));
            Assert.IsFalse(accounts.PrepareRecovery(accounts.CreateSession(), "admin", "admin-password-123")().IsOK());
            Assert.AreEqual(0, Metadata(root).Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NotBlocked_SetupAsBefore()
    {
        string root = NewRoot();
        try
        {
            int calls = 0;
            var accounts = Accounts(root, () => { calls++; return null; });
            accounts.Load();
            Assert.IsTrue(accounts.NeedsSetup);
            Assert.IsTrue(accounts.SetupWork(accounts.CreateSession(), "admin", "admin-password-123")()().IsOK());
            Assert.IsTrue(File.Exists(Path.Combine(root, AccountService.MetadataFile)));
            Assert.IsTrue(calls >= 2, "beim Laden und vor dem Festschreiben neu ausgewertet");
        }
        finally { Directory.Delete(root, true); }
    }
}
