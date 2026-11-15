using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FileAccessControlTests
{
    [TestMethod]
    public void MissingOrCorruptMetadataNeverAllowsAccess()
    {
        var access = new FileAccessControl(Path.GetTempPath());
        var user = new UserSession();
        Assert.IsFalse(access.Check(user, Path.GetTempPath(), FilePermission.Read).IsOK());
        Assert.IsFalse(access.Restore("ZEHOS-SECURITY-1\ninvalid").IsOK());
        Assert.IsFalse(access.Check(user, Path.GetTempPath(), FilePermission.Read).IsOK());
    }

    [TestMethod]
    public void FullMetadataRejectsOnlyTheNewEntry()
    {
        string root = Path.Combine(Path.GetTempPath(), "zehos-full");
        var access = new FileAccessControl(root);
        access.Initialize();
        access.Assign(root, "1");
        access.Assign(Path.Combine(root, "alt.txt"), "1");
        var alice = new UserSession("1", "alice", false, root);
        access.Persist = () => ProjektZeh.Shared.ResultErr.CreateFailure(ProjektZeh.Shared.ResultCode.Failure, FileAccessControl.FullMessage);
        Assert.AreEqual(FileAccessControl.FullMessage, access.RegisterCreated(alice, Path.Combine(root, "neu.txt")).Message);
        Assert.AreEqual(FileAccessControl.FullMessage, access.PrepareMove(Path.Combine(root, "alt.txt"), Path.Combine(root, "ziel.txt")).Message);
        Assert.IsTrue(access.IsReady, "Eine volle Rechtedatei darf das System nicht sperren.");
        Assert.IsFalse(access.Contains(Path.Combine(root, "neu.txt")));
        Assert.IsFalse(access.Contains(Path.Combine(root, "ziel.txt")));
        Assert.IsTrue(access.Check(alice, Path.Combine(root, "alt.txt"), FilePermission.Write).IsOK());
        // Andere Schreibfehler sperren weiterhin, damit Speicher und Platte nicht auseinanderlaufen.
        access.Persist = () => ProjektZeh.Shared.ResultErr.CreateFailure(ProjektZeh.Shared.ResultCode.Failure, "E/A-Fehler");
        Assert.IsFalse(access.RegisterCreated(alice, Path.Combine(root, "neu.txt")).IsOK());
        Assert.IsFalse(access.IsReady);
    }

    [TestMethod]
    public void UnmappedPathsAndForeignWritesFailWhileExplicitReadSurvivesRestore()
    {
        string root = Path.Combine(Path.GetTempPath(), "zehos-security");
        var access = new FileAccessControl(root);
        access.Initialize();
        var alice = new UserSession("1", "alice", false, root);
        var bob = new UserSession("2", "bob", false, root);
        string path = Path.Combine(root, "alice.txt");
        access.Assign(root, "1");
        access.Grant(root, "*", FilePermission.Read);
        access.Assign(path, "1");
        Assert.IsTrue(access.Check(alice, path, FilePermission.Write).IsOK());
        Assert.IsFalse(access.Check(bob, path, FilePermission.Read).IsOK());
        access.Grant(path, "2", FilePermission.Read);
        var restored = new FileAccessControl(root);
        Assert.IsTrue(restored.Restore(access.Serialize()).IsOK());
        Assert.IsTrue(restored.Check(bob, path, FilePermission.Read).IsOK());
        Assert.IsFalse(restored.Check(bob, path, FilePermission.Write).IsOK());
        Assert.IsFalse(restored.Check(alice, Path.Combine(root, "unmapped"), FilePermission.Read).IsOK());
        Assert.IsFalse(restored.Check(alice, Path.GetFullPath(Path.Combine(root, "..", "outside")), FilePermission.Read).IsOK());
        Assert.IsFalse(restored.Check(alice, Path.Combine(root, ".zehos-hidden.tmp"), FilePermission.Read).IsOK());
        bob.Logout();
        Assert.IsFalse(restored.Check(bob, path, FilePermission.Read).IsOK());
        Assert.AreEqual("1", alice.UserId);
    }
}
