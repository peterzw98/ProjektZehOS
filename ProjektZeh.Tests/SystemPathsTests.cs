using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class SystemPathsTests
{
    [TestMethod]
    public void LegacyYieldsExactlyTheCurrentPaths()
    {
        var paths = SystemPaths.Legacy;
        Assert.AreEqual(PathProfile.Legacy, paths.Profile);
        Assert.AreEqual("/data", paths.AdminRoot);
        Assert.AreEqual(StringComparison.OrdinalIgnoreCase, paths.Comparison);
        Assert.AreEqual("/data", paths.SecurityDirectory, ".zehos-security.db und .zehos-commands.db liegen direkt unter /data");
        Assert.AreEqual("/data/home/alice", paths.Home("alice"));
        Assert.AreEqual("/data/.zehos-packages", paths.PackagesDirectory);
        Assert.AreEqual("/data/.zehos-update.conf", paths.UpdateConfigPath);
        Assert.AreEqual("/data", paths.UpdateRoot);
        Assert.AreEqual("/data", paths.StartDirectory);
        Assert.AreEqual("/data", paths.InstallMount);
        Assert.AreEqual("/data/home/alice/zehos-desktop.conf", paths.DesktopSettings("/data/home/alice"));
        Assert.AreEqual("/data/hintergrund.bmp", paths.DefaultBackground("/data/home/alice"));
        CollectionAssert.AreEqual(new[] { "/data/home/alice", "/data" }, paths.Places("/data/home/alice", "/data/home/alice"));
    }

    [TestMethod]
    public void RootProfileFollowsTheTable()
    {
        var paths = SystemPaths.Root;
        Assert.AreEqual(PathProfile.Root, paths.Profile);
        Assert.AreEqual("/", paths.AdminRoot);
        Assert.AreEqual(StringComparison.Ordinal, paths.Comparison);
        Assert.AreEqual("/etc/zehos", paths.SecurityDirectory);
        Assert.AreEqual("/home/alice", paths.Home("alice"));
        Assert.AreEqual("/var/lib/zehos/packages", paths.PackagesDirectory);
        Assert.AreEqual("/etc/zehos/update.conf", paths.UpdateConfigPath);
        Assert.AreEqual("/boot/efi", paths.UpdateRoot);
        Assert.AreEqual("/", paths.StartDirectory);
        Assert.AreEqual("/mnt/install", paths.InstallMount);
        Assert.AreEqual("/home/alice/.config/zehos/desktop.conf", paths.DesktopSettings("/home/alice"));
        Assert.AreEqual("/home/alice/hintergrund.bmp", paths.DefaultBackground("/home/alice"));
        CollectionAssert.AreEqual(new[] { "/home/alice/docs", "/home/alice" }, paths.Places("/home/alice/docs", "/home/alice"));
    }

    [TestMethod]
    public void TrailingSeparatorOfHomeDoesNotDoubleUp()
    {
        Assert.AreEqual("/home/a/.config/zehos/desktop.conf", SystemPaths.Root.DesktopSettings("/home/a/"));
        Assert.AreEqual("/data/home/a/zehos-desktop.conf", SystemPaths.Legacy.DesktopSettings("/data/home/a/"));
    }

    [TestMethod]
    public void AppStateIsPerPackageAndAccountAndRejectsBadComponents()
    {
        Assert.AreEqual("/var/lib/zehos/apps/classicube/7", SystemPaths.Root.AppState("classicube", "7").Value);
        Assert.AreEqual("/data/.zehos-apps/classicube/7", SystemPaths.Legacy.AppState("classicube", "7").Value);
        foreach (string bad in new[] { "", ".", "..", "a/b", "a\\b", "a:b", "a|b", ".zehos-x", "a b", "a\nb", "x.", "../etc" })
        {
            Assert.IsFalse(SystemPaths.Root.AppState(bad, "7").IsOK(), "Paket '" + bad + "'");
            Assert.IsFalse(SystemPaths.Legacy.AppState("p", bad).IsOK(), "Konto '" + bad + "'");
        }
    }

    [TestMethod]
    public void ProfilesAreFixedSingletons()
    {
        Assert.AreSame(SystemPaths.Legacy, SystemPaths.Legacy);
        Assert.AreNotSame(SystemPaths.Legacy, SystemPaths.Root);
    }
}
