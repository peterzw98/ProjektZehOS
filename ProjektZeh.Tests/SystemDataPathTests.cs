using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class SystemDataPathTests
{
    private static string Root => Path.GetPathRoot(Path.GetTempPath())!;

    private static string P(params string[] parts) => Path.Combine(Root, Path.Combine(parts));

    [TestMethod]
    public void EtcZehosAndVarLibZehosAreNeverManagedOnTheFilesystemRoot()
    {
        var access = new FileAccessControl(Root, StringComparison.Ordinal);
        foreach (string path in new[] { P("etc", "zehos"), P("etc", "zehos", "update.conf"), P("etc", "zehos", "sub", "x"),
                     P("var", "lib", "zehos"), P("var", "lib", "zehos", "packages", "a") })
            Assert.IsFalse(access.IsManagedPath(path), path);
    }

    [TestMethod]
    public void NeighboursAndNestedLookAlikesStayManaged()
    {
        var access = new FileAccessControl(Root, StringComparison.Ordinal);
        foreach (string path in new[] { P("etc"), P("etc", "zehosx"), P("etc", "other"), P("var"), P("var", "lib"), P("var", "lib", "zehosx"),
                     P("var", "lib2", "zehos"), P("var", "zehos"), P("home", "a", "etc", "zehos"), P("ETC", "zehos") })
            Assert.IsTrue(access.IsManagedPath(path), path);
    }

    [TestMethod]
    public void CaseInsensitiveRootAlsoBlocksOtherSpellings()
    {
        var fat = new FileAccessControl(Root, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(fat.IsManagedPath(P("ETC", "Zehos", "x")));
        Assert.IsFalse(fat.IsManagedPath(P("Var", "LIB", "ZEHOS")));
    }

    [TestMethod]
    public void LegacyRootUnderDataIsUnchanged()
    {
        string data = Path.Combine(Path.GetTempPath(), "zehos-data");
        var access = new FileAccessControl(data, StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(access.IsManagedPath(Path.Combine(data, "etc", "zehos", "x")));
        Assert.IsTrue(access.IsManagedPath(Path.Combine(data, "var", "lib", "zehos")));
    }

    [TestMethod]
    public void AdministratorAndOwnerAreDeniedAndNothingCanBeAssignedThere()
    {
        var access = new FileAccessControl(Root, StringComparison.Ordinal);
        access.Initialize();
        access.Assign(Root, "0");
        access.Assign(P("etc"), "0");
        var admin = new UserSession("0", "root", true, Root);
        string secret = P("etc", "zehos", ".zehos-security.db");
        string other = P("etc", "zehos", "update.conf");
        foreach (string path in new[] { secret, other, P("etc", "zehos"), P("var", "lib", "zehos", "packages") })
            foreach (var permission in new[] { FilePermission.Read, FilePermission.Write })
            {
                Assert.IsFalse(access.Check(admin, path, permission).IsOK(), path);
                Assert.IsFalse(access.Check(admin, path, permission, creating: true).IsOK(), path);
            }

        Assert.ThrowsException<ArgumentException>(() => access.Assign(other, "0"));
        Assert.IsTrue(access.Check(admin, P("etc"), FilePermission.Read).IsOK(), "/etc selbst bleibt verwaltbar");
    }
}
