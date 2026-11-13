using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FilePermissionTests
{
    [TestMethod]
    public void OwnerAndExplicitReaderHaveSeparateWritePermissions()
    {
        var permissions = new FilePermissions("alice");
        Assert.IsTrue(permissions.Allows("alice", FilePermission.Write));
        Assert.IsFalse(permissions.Allows("bob", FilePermission.Read));
        permissions.Grant("bob", FilePermission.Read);
        Assert.IsTrue(permissions.Allows("bob", FilePermission.Read));
        Assert.IsFalse(permissions.Allows("bob", FilePermission.Write));
        permissions.Grant("bob", FilePermission.None);
        Assert.IsFalse(permissions.Allows("bob", FilePermission.Read));
    }

    [TestMethod]
    public void InvalidGrantsAreRejected()
    {
        var permissions = new FilePermissions("alice");
        Assert.ThrowsException<ArgumentException>(() => permissions.Grant("", FilePermission.Read));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => permissions.Grant("bob", (FilePermission)8));
    }
}
