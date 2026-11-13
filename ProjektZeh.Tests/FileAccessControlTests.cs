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
