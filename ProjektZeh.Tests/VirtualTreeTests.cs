using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;
using ProjektZeh.Shared;
using ProjektZeh.Storage.Virtual;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class VirtualTreeTests
{
    private static readonly UserSession User = new("2", "bob", false, "/home/bob");
    private static readonly UserSession Admin = new("1", "alice", true, "/home/alice");

    [TestMethod]
    public void EveryFileOfTheSpecIsFoundWithItsKind()
    {
        string[] files = ["/proc/version", "/proc/uptime", "/proc/meminfo", "/proc/mounts", "/proc/log",
            "/sys/kernel/components", "/sys/devices", "/sys/block"];
        foreach (string path in files) Assert.AreEqual(VirtualKind.File, VirtualTree.Find(path)?.Kind, path);
        Assert.AreEqual(VirtualKind.CharacterDevice, VirtualTree.Find("/dev/null")?.Kind);
        Assert.AreEqual(VirtualKind.CharacterDevice, VirtualTree.Find("/dev/zero")?.Kind);
        foreach (string path in new[] { "/proc", "/sys", "/sys/kernel", "/dev" })
            Assert.AreEqual(VirtualKind.Directory, VirtualTree.Find(path)?.Kind, path);
    }

    [TestMethod]
    public void UnknownNamesAndOtherCaseAreMissing()
    {
        foreach (string path in new[] { "/proc/Version", "/proc/threads", "/dev/random", "/sys/kernel/x", "/proc/version/", "/" })
            Assert.IsNull(VirtualTree.Find(path), path);
    }

    [TestMethod]
    public void ListReturnsDirectChildrenSortedAndRejectsFiles()
    {
        CollectionAssert.AreEqual(new[] { "log", "meminfo", "mounts", "uptime", "version" }, new List<string>(VirtualTree.List("/proc").Value!));
        CollectionAssert.AreEqual(new[] { "block", "devices", "kernel" }, new List<string>(VirtualTree.List("/sys").Value!));
        CollectionAssert.AreEqual(new[] { "components" }, new List<string>(VirtualTree.List("/sys/kernel").Value!));
        CollectionAssert.AreEqual(new[] { "null", "zero" }, new List<string>(VirtualTree.List("/dev").Value!));
        Assert.AreEqual(ResultCode.InvalidArgument, VirtualTree.List("/proc/version").Status.Code);
        Assert.AreEqual(ResultCode.NotFound, VirtualTree.List("/proc/none").Status.Code);
    }

    [TestMethod]
    public void NormalizeResolvesDotsSlashesAndRelativePaths()
    {
        foreach (string path in new[] { "/proc/./version", "//proc/version", "/proc/../proc/version", "/proc/version/" })
            Assert.AreEqual("/proc/version", VirtualTree.Normalize(path, "/home/x"), path);
        Assert.AreEqual("/proc/version", VirtualTree.Normalize("proc/version", "/"));
        Assert.AreEqual("/proc/version", VirtualTree.Normalize("../../proc/version", "/home/x"));
        Assert.AreEqual("/proc/version", VirtualTree.Normalize("/../proc/version", "/"));
        Assert.AreEqual("/etc/x", VirtualTree.Normalize("/proc/../etc/x", "/"));
    }

    [TestMethod]
    public void IsVirtualPathCoversTheThreeTreesOnly()
    {
        foreach (string path in new[] { "/proc", "/proc/version", "/proc/unknown", "/sys/kernel", "/dev/zero" })
            Assert.IsTrue(VirtualTree.IsVirtualPath(path), path);
        foreach (string path in new[] { "/", "/etc/x", "/process", "/devices", "/data/proc", "/Proc" })
            Assert.IsFalse(VirtualTree.IsVirtualPath(path), path);
    }

    [TestMethod]
    public void AccountsReadPublicFilesAndOnlyAdministratorsReadTheLog()
    {
        var version = VirtualTree.Find("/proc/version")!;
        var log = VirtualTree.Find("/proc/log")!;
        Assert.IsTrue(VirtualAccess.Check(version, User, write: false).IsOK());
        Assert.IsFalse(VirtualAccess.Check(log, User, write: false).IsOK());
        Assert.IsTrue(VirtualAccess.Check(log, Admin, write: false).IsOK());
    }

    [TestMethod]
    public void WithoutLoginNothingIsReadable()
    {
        var anonymous = new UserSession();
        foreach (string path in new[] { "/proc/version", "/dev/null", "/proc" })
            Assert.IsFalse(VirtualAccess.Check(VirtualTree.Find(path)!, anonymous, write: false).IsOK(), path);
        Assert.IsFalse(VirtualAccess.Check(VirtualTree.Find("/dev/null")!, anonymous, write: true).IsOK());
    }

    [TestMethod]
    public void OnlyDevNullAcceptsWritesEvenForAdministrators()
    {
        Assert.IsTrue(VirtualAccess.Check(VirtualTree.Find("/dev/null")!, User, write: true).IsOK());
        foreach (string path in new[] { "/dev/zero", "/proc/version", "/proc/log", "/sys/block", "/proc", "/dev" })
            Assert.IsFalse(VirtualAccess.Check(VirtualTree.Find(path)!, Admin, write: true).IsOK(), path);
    }
}
