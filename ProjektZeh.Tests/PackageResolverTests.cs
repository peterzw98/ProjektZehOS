using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Packages;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class PackageResolverTests
{
    private static readonly string Hash = new('c', 64);

    // Eintrag als "id version deps... | provides..."; deps als "id>=version".
    private static PackageCatalog2 Catalog(params string[] specs)
    {
        var text = new StringBuilder("zehos-catalog 2\ngeneration=1\n");
        foreach (string spec in specs.OrderBy(s => s, StringComparer.Ordinal))
        {
            string[] sides = spec.Split('|');
            string[] head = sides[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            text.Append(CultureInfo.InvariantCulture, $"package={head[0]} {head[1]} {Hash} 10\n");
            foreach (string dep in head.Skip(2).OrderBy(s => s, StringComparer.Ordinal)) text.Append("depends=").Append(dep).Append('\n');
            if (sides.Length > 1)
                foreach (string name in sides[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(s => s, StringComparer.Ordinal))
                    text.Append("provides=so:").Append(name).Append('\n');
        }
        var result = PackageCatalog2.Parse(Encoding.UTF8.GetBytes(text.Append("end\n").ToString()));
        Assert.IsTrue(result.Status.IsOK(), result.Status.Message);
        return result.Value!;
    }

    private static InstalledPackage2 Have(string id, string version, bool requested = true, params string[] deps) =>
        new(id, V(version), requested, [.. deps.Select(D)]);

    private static PackageVersion2 V(string text)
    {
        Assert.IsTrue(PackageVersion2.TryParse(text, out var version), text);
        return version;
    }

    private static PackageDependency D(string text)
    {
        Assert.IsTrue(PackageDependency.TryParse(text, out var dependency), text);
        return dependency!;
    }

    private static string[] Ids(ResultObject<CatalogEntry2[]> result)
    {
        Assert.IsTrue(result.Status.IsOK(), result.Status.Message);
        return [.. result.Value!.Select(entry => entry.Id)];
    }

    private static void Fails(ResultObject<CatalogEntry2[]> result, ResultCode code, string fragment)
    {
        Assert.IsFalse(result.Status.IsOK());
        Assert.AreEqual(code, result.Status.Code, result.Status.Message);
        StringAssert.Contains(result.Status.Message, fragment);
    }

    private static readonly InstalledPackage2[] None = [];

    [TestMethod]
    public void ChainInstallsDependenciesFirst()
    {
        var catalog = Catalog("app 1.0.0-r1 lib>=1.0.0-r1", "lib 1.0.0-r1 base>=1.0.0-r1", "base 1.0.0-r1");
        CollectionAssert.AreEqual(new[] { "base", "lib", "app" }, Ids(PackageResolver.Install(catalog, None, ["app"])));
    }

    [TestMethod]
    public void DiamondListsSharedDependencyOnce()
    {
        var catalog = Catalog("top 1.0.0-r1 left>=1.0.0-r1 right>=1.0.0-r1", "left 1.0.0-r1 base>=1.0.0-r1",
            "right 1.0.0-r1 base>=1.0.0-r1", "base 1.0.0-r1");
        CollectionAssert.AreEqual(new[] { "base", "left", "right", "top" }, Ids(PackageResolver.Install(catalog, None, ["top"])));
    }

    [TestMethod]
    public void MissingDependencyFails()
    {
        var catalog = Catalog("app 1.0.0-r1 gone>=1.0.0-r1");
        Fails(PackageResolver.Install(catalog, None, ["app"]), ResultCode.NotFound, "gone");
        Fails(PackageResolver.Install(catalog, None, ["unbekannt"]), ResultCode.NotFound, "unbekannt");
    }

    [TestMethod]
    public void TooOldDependencyFails()
    {
        var catalog = Catalog("app 1.0.0-r1 lib>=2.0.0-r1", "lib 1.9.9-r9");
        Fails(PackageResolver.Install(catalog, None, ["app"]), ResultCode.Failure, "zu alt");
        // Gleiche Version, hoehere Revision verlangt.
        Fails(PackageResolver.Install(Catalog("app 1.0.0-r1 lib>=1.0.0-r2", "lib 1.0.0-r1"), None, ["app"]), ResultCode.Failure, "zu alt");
        Assert.IsTrue(PackageResolver.Install(Catalog("app 1.0.0-r1 lib>=1.0.0-r2", "lib 1.0.0-r2"), None, ["app"]).Status.IsOK());
    }

    [TestMethod]
    public void TooOldIsFoundEvenWhenDependencyIsAlreadyPlanned()
    {
        var catalog = Catalog("a 1.0.0-r1 lib>=1.0.0-r1 mid>=1.0.0-r1", "mid 1.0.0-r1 lib>=2.0.0-r1", "lib 1.0.0-r1");
        Fails(PackageResolver.Install(catalog, None, ["a"]), ResultCode.Failure, "zu alt");
    }

    [TestMethod]
    public void CycleThroughTargetFails()
    {
        var catalog = Catalog("a 1.0.0-r1 b>=1.0.0-r1", "b 1.0.0-r1 a>=1.0.0-r1");
        Fails(PackageResolver.Install(catalog, None, ["a"]), ResultCode.Failure, "\"a\" -> \"b\" -> \"a\"");
    }

    [TestMethod]
    public void CycleBesideTargetFails()
    {
        // Das Ziel gehoert nicht zum Zyklus, erreicht ihn aber.
        var catalog = Catalog("t 1.0.0-r1 b>=1.0.0-r1", "b 1.0.0-r1 c>=1.0.0-r1", "c 1.0.0-r1 b>=1.0.0-r1");
        Fails(PackageResolver.Install(catalog, None, ["t"]), ResultCode.Failure, "zyklus");
    }

    [TestMethod]
    public void UnreachableCycleDoesNotBlock()
    {
        var catalog = Catalog("ok 1.0.0-r1", "x 1.0.0-r1 y>=1.0.0-r1", "y 1.0.0-r1 x>=1.0.0-r1");
        CollectionAssert.AreEqual(new[] { "ok" }, Ids(PackageResolver.Install(catalog, None, ["ok"])));
    }

    [TestMethod]
    public void CycleSatisfiedByInstalledIsNotATargetCycle()
    {
        var catalog = Catalog("a 1.0.0-r1 b>=1.0.0-r1", "b 1.0.0-r1 a>=1.0.0-r1");
        var installed = new[] { Have("b", "1.0.0-r1", false) };
        CollectionAssert.AreEqual(new[] { "a" }, Ids(PackageResolver.Install(catalog, installed, ["a"])));
    }

    [TestMethod]
    public void DuplicateProvidesInTargetFails()
    {
        var catalog = Catalog("a 1.0.0-r1 b>=1.0.0-r1 c>=1.0.0-r1", "b 1.0.0-r1|libx.so", "c 1.0.0-r1|libx.so");
        Fails(PackageResolver.Install(catalog, None, ["a"]), ResultCode.Failure, "libx.so");
        // Ohne beide in der Zielmenge ist es kein Fehler.
        Assert.IsTrue(PackageResolver.Install(catalog, None, ["b"]).Status.IsOK());
        Assert.IsTrue(PackageResolver.Install(catalog, None, ["c"]).Status.IsOK());
    }

    [TestMethod]
    public void InstalledNewerOrEqualVersionIsLeftAlone()
    {
        var catalog = Catalog("app 1.0.0-r2");
        Assert.AreEqual(0, Ids(PackageResolver.Install(catalog, [Have("app", "1.0.0-r2")], ["app"])).Length);
        Assert.AreEqual(0, Ids(PackageResolver.Install(catalog, [Have("app", "1.0.0-r3")], ["app"])).Length);
        Assert.AreEqual(0, Ids(PackageResolver.Install(catalog, [Have("app", "2.0.0-r1")], ["app"])).Length);
        CollectionAssert.AreEqual(new[] { "app" }, Ids(PackageResolver.Install(catalog, [Have("app", "1.0.0-r1")], ["app"])));
    }

    [TestMethod]
    public void InstalledNewerDependencySatisfiesAndIsNotDowngraded()
    {
        var catalog = Catalog("app 1.0.0-r1 lib>=1.0.0-r1", "lib 1.0.0-r1");
        var ids = Ids(PackageResolver.Install(catalog, [Have("lib", "3.0.0-r1", false)], ["app"]));
        CollectionAssert.AreEqual(new[] { "app" }, ids);
    }

    [TestMethod]
    public void OlderInstalledDependencyIsRenewed()
    {
        var catalog = Catalog("app 1.0.0-r1 lib>=1.1.0-r1", "lib 1.2.0-r1");
        CollectionAssert.AreEqual(new[] { "lib", "app" }, Ids(PackageResolver.Install(catalog, [Have("lib", "1.0.0-r1", false)], ["app"])));
    }

    [TestMethod]
    public void RequestedDuplicatesAndOrderAreStable()
    {
        var catalog = Catalog("zed 1.0.0-r1", "alpha 1.0.0-r1", "mid 1.0.0-r1 zed>=1.0.0-r1");
        string[] expected = ["alpha", "zed", "mid"];
        CollectionAssert.AreEqual(expected, Ids(PackageResolver.Install(catalog, None, ["mid", "alpha", "zed", "alpha"])));
        CollectionAssert.AreEqual(expected, Ids(PackageResolver.Install(catalog, None, ["zed", "alpha", "mid"])));
        Assert.AreEqual(0, Ids(PackageResolver.Install(catalog, None, [])).Length);
    }

    [TestMethod]
    public void InstalledOrderDoesNotChangeResult()
    {
        var catalog = Catalog("a 2.0.0-r1", "b 2.0.0-r1", "c 2.0.0-r1");
        InstalledPackage2[] one = [Have("c", "1.0.0-r1"), Have("a", "1.0.0-r1"), Have("b", "1.0.0-r1")];
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, Ids(PackageResolver.Upgrade(catalog, one)));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, Ids(PackageResolver.Upgrade(catalog, one.Reverse().ToArray())));
    }

    [TestMethod]
    public void UpgradeNeverDowngradesAndSkipsCurrent()
    {
        var catalog = Catalog("a 1.0.0-r1", "b 2.0.0-r1", "c 1.0.0-r2");
        InstalledPackage2[] installed = [Have("a", "5.0.0-r1"), Have("b", "2.0.0-r1"), Have("c", "1.0.0-r1"), Have("local", "1.0.0-r1")];
        CollectionAssert.AreEqual(new[] { "c" }, Ids(PackageResolver.Upgrade(catalog, installed)));
        Assert.AreEqual(0, Ids(PackageResolver.Upgrade(catalog, [Have("a", "5.0.0-r1")])).Length);
    }

    [TestMethod]
    public void UpgradePullsNewDependenciesFirst()
    {
        var catalog = Catalog("app 2.0.0-r1 newlib>=1.0.0-r1", "newlib 1.0.0-r1");
        CollectionAssert.AreEqual(new[] { "newlib", "app" }, Ids(PackageResolver.Upgrade(catalog, [Have("app", "1.0.0-r1")])));
    }

    [TestMethod]
    public void UpgradeFailsWhenMinimumOfNewVersionCannotBeMet()
    {
        var catalog = Catalog("app 2.0.0-r1 lib>=2.0.0-r1", "lib 1.5.0-r1");
        Fails(PackageResolver.Upgrade(catalog, [Have("app", "1.0.0-r1"), Have("lib", "1.0.0-r1", false)]), ResultCode.Failure, "zu alt");
    }

    [TestMethod]
    public void UpgradeSatisfiesMinimumOfOtherPackageByUpgradingIt()
    {
        var catalog = Catalog("app 2.0.0-r1 lib>=2.0.0-r1", "lib 2.1.0-r1");
        CollectionAssert.AreEqual(new[] { "lib", "app" },
            Ids(PackageResolver.Upgrade(catalog, [Have("app", "1.0.0-r1"), Have("lib", "1.0.0-r1", false)])));
    }

    [TestMethod]
    public void InvalidInstalledListIsRejected()
    {
        var catalog = Catalog("a 1.0.0-r1");
        Fails(PackageResolver.Install(catalog, [Have("a", "1.0.0-r1"), Have("a", "1.0.0-r1")], ["a"]), ResultCode.InvalidArgument, "doppelt");
        Fails(PackageResolver.Install(catalog, [null!], ["a"]), ResultCode.InvalidArgument, "Bestandseintrag");
        Fails(PackageResolver.Install(null!, None, ["a"]), ResultCode.MissingArgument, "fehlt");
        Fails(PackageResolver.Install(catalog, None, null!), ResultCode.MissingArgument, "fehlt");
        Fails(PackageResolver.Upgrade(catalog, [Have("a", "0.1.0-r1"), Have("a", "0.1.0-r1")]), ResultCode.InvalidArgument, "doppelt");
        Fails(PackageResolver.Install(catalog, None, [null!]), ResultCode.NotFound, "\"\"");
    }

    [TestMethod]
    public void ErrorMessagesFilterCallerInput()
    {
        var catalog = Catalog("a 1.0.0-r1");
        string evil = "\u001b[2Jä" + new string('x', 300);
        var result = PackageResolver.Install(catalog, None, [evil]);
        string message = result.Status.Message!;
        Assert.IsTrue(message.All(c => c is >= ' ' and <= '~'), message);
        Assert.IsTrue(message.Length < 200, message);
        var removal = PackageResolver.CheckRemove([Have("a", "1.0.0-r1")], evil);
        Assert.IsTrue(removal.Message.All(c => c is >= ' ' and <= '~'));
        Assert.IsTrue(removal.Message.Length < 200);
    }

    [TestMethod]
    public void LongChainResolvesWithoutStackOverflow()
    {
        const int count = 4096;
        var text = new StringBuilder("zehos-catalog 2\ngeneration=1\n");
        for (int i = 0; i < count; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"package=p{i:D5} 1.0.0-r1 {Hash} 1\n");
            if (i + 1 < count) text.Append(CultureInfo.InvariantCulture, $"depends=p{i + 1:D5}>=1.0.0-r1\n");
        }
        var catalog = PackageCatalog2.Parse(Encoding.UTF8.GetBytes(text.Append("end\n").ToString())).Value!;
        var ids = Ids(PackageResolver.Install(catalog, None, ["p00000"]));
        Assert.AreEqual(count, ids.Length);
        Assert.AreEqual("p04095", ids[0]);
        Assert.AreEqual("p00000", ids[^1]);

        // Dieselbe Kette als Bestand fuer CheckRemove und Unused.
        var installed = Enumerable.Range(0, count)
            .Select(i => new InstalledPackage2($"p{i:D5}", V("1.0.0-r1"), i == 0, i + 1 < count ? [D($"p{i + 1:D5}>=1.0.0-r1")] : []))
            .ToArray();
        Assert.AreEqual(0, PackageResolver.Unused(installed).Length);
        Assert.IsFalse(PackageResolver.CheckRemove(installed, "p04095").IsOK());
        // Ohne das angeforderte Wurzelpaket haengt die ganze Restkette in der Luft.
        Assert.AreEqual(count - 1, PackageResolver.Unused([.. installed.Skip(1)]).Length);
    }

    [TestMethod]
    public void PlannedDependencyComesFirstEvenWhenInstalledOneSatisfiesMinimum()
    {
        var catalog = Catalog("a 2.0.0-r1 b>=1.0.0-r1", "b 2.0.0-r1");
        InstalledPackage2[] installed = [Have("a", "1.0.0-r1"), Have("b", "1.0.0-r1", false)];
        CollectionAssert.AreEqual(new[] { "b", "a" }, Ids(PackageResolver.Upgrade(catalog, installed)));
        var other = Catalog("x 1.0.0-r1 b>=1.0.0-r1", "y 1.0.0-r1", "b 2.0.0-r1");
        CollectionAssert.AreEqual(new[] { "b", "x", "y" }, Ids(PackageResolver.Install(other, [Have("b", "1.0.0-r1", false)], ["x", "b", "y"])));
    }

    [TestMethod]
    public void CycleInPlanIsNotHiddenByInstalledVersions()
    {
        var catalog = Catalog("a 2.0.0-r1 b>=1.0.0-r1", "b 2.0.0-r1 a>=1.0.0-r1");
        InstalledPackage2[] installed = [Have("a", "1.0.0-r1"), Have("b", "1.0.0-r1")];
        Fails(PackageResolver.Upgrade(catalog, installed), ResultCode.Failure, "zyklus");
    }

    [TestMethod]
    public void NullDependencyElementIsRejectedNotThrown()
    {
        var broken = new InstalledPackage2("a", V("1.0.0-r1"), true, [null!]);
        Assert.AreEqual(ResultCode.InvalidArgument, PackageResolver.CheckRemove([broken], "a").Code);
        Assert.AreEqual(0, PackageResolver.Unused([broken]).Length);
        Fails(PackageResolver.Install(Catalog("a 1.0.0-r1"), [broken], ["a"]), ResultCode.InvalidArgument, "Bestandseintrag");
    }

    [TestMethod]
    public void RandomGraphsKeepDependenciesBeforeDependents()
    {
        var random = new Random(640);
        int successes = 0, cycles = 0;
        for (int round = 0; round < 3000; round++)
        {
            int count = random.Next(1, 10);
            string[] names = [.. Enumerable.Range(0, count).Select(i => "p" + i.ToString(CultureInfo.InvariantCulture))];
            var specs = new List<string>();
            var installed = new List<InstalledPackage2>();
            for (int i = 0; i < count; i++)
            {
                var deps = new List<string>();
                for (int j = 0; j < count; j++)
                    if (j != i && random.Next(4) == 0) deps.Add(names[j] + ">=" + random.Next(1, 3).ToString(CultureInfo.InvariantCulture) + ".0.0-r1");
                specs.Add(names[i] + " " + random.Next(1, 4).ToString(CultureInfo.InvariantCulture) + ".0.0-r1 " + string.Join(' ', deps));
                if (random.Next(2) == 0)
                    installed.Add(Have(names[i], random.Next(1, 4).ToString(CultureInfo.InvariantCulture) + ".0.0-r1", random.Next(2) == 0,
                        [.. deps.Where(_ => random.Next(2) == 0)]));
            }
            var catalog = Catalog([.. specs]);
            var roots = names.Where(_ => random.Next(3) == 0).ToArray();
            foreach (var result in new[] { PackageResolver.Install(catalog, installed, roots), PackageResolver.Upgrade(catalog, installed) })
            {
                if (!result.Status.IsOK())
                {
                    if (result.Status.Message.Contains("zyklus", StringComparison.Ordinal)) cycles++;
                    continue;
                }
                successes++;
                var plan = result.Value!;
                Assert.AreEqual(plan.Length, plan.Select(e => e.Id).Distinct().Count());
                for (int i = 0; i < plan.Length; i++)
                {
                    var have = installed.Find(p => p.Id == plan[i].Id);
                    Assert.IsTrue(have == null || plan[i].Version.CompareTo(have.Version) > 0, "kein Wechsel auf gleich oder kleiner");
                    foreach (var dependency in plan[i].Dependencies)
                    {
                        int at = Array.FindIndex(plan, e => e.Id == dependency.Id);
                        if (at >= 0)
                        {
                            Assert.IsTrue(at < i, "Abhaengigkeit vor Abhaengigem");
                            Assert.IsTrue(plan[at].Version.CompareTo(dependency.Minimum) >= 0);
                        }
                        else
                        {
                            var present = installed.Find(p => p.Id == dependency.Id);
                            Assert.IsTrue(present != null && present.Version.CompareTo(dependency.Minimum) >= 0, "Mindestversion erfuellt");
                        }
                    }
                }
            }
        }
        Assert.IsTrue(successes > 500 && cycles > 20, successes + " " + cycles);
    }

    [TestMethod]
    public void LongCycleIsReportedShort()
    {
        const int count = 4096;
        var text = new StringBuilder("zehos-catalog 2\ngeneration=1\n");
        for (int i = 0; i < count; i++)
            text.Append(CultureInfo.InvariantCulture, $"package=p{i:D5} 1.0.0-r1 {Hash} 1\ndepends=p{(i + 1) % count:D5}>=1.0.0-r1\n");
        var catalog = PackageCatalog2.Parse(Encoding.UTF8.GetBytes(text.Append("end\n").ToString())).Value!;
        var result = PackageResolver.Install(catalog, None, ["p00000"]);
        Fails(result, ResultCode.Failure, "zyklus");
        Assert.IsTrue(result.Status.Message.Length < 300);
    }

    [TestMethod]
    public void CheckRemoveNamesDependents()
    {
        InstalledPackage2[] installed = [Have("lib", "1.0.0-r1", false), Have("b", "1.0.0-r1", true, "lib>=1.0.0-r1"),
            Have("a", "1.0.0-r1", true, "lib>=1.0.0-r1"), Have("solo", "1.0.0-r1")];
        var blocked = PackageResolver.CheckRemove(installed, "lib");
        Assert.AreEqual(ResultCode.Failure, blocked.Code);
        StringAssert.Contains(blocked.Message, "\"a\" \"b\"");
        Assert.IsTrue(PackageResolver.CheckRemove(installed, "solo").IsOK());
        Assert.IsTrue(PackageResolver.CheckRemove(installed, "a").IsOK());
        Assert.AreEqual(ResultCode.NotFound, PackageResolver.CheckRemove(installed, "gone").Code);
        Assert.AreEqual(ResultCode.MissingArgument, PackageResolver.CheckRemove(installed, null!).Code);
    }

    [TestMethod]
    public void CheckRemoveLimitsListedDependents()
    {
        var installed = new List<InstalledPackage2> { Have("lib", "1.0.0-r1", false) };
        for (int i = 0; i < 20; i++) installed.Add(Have($"d{i:D2}", "1.0.0-r1", true, "lib>=1.0.0-r1"));
        string message = PackageResolver.CheckRemove(installed, "lib").Message;
        StringAssert.Contains(message, "12 weitere");
        Assert.IsFalse(message.Contains("d19", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnusedFindsOnlyUnreachablePackages()
    {
        InstalledPackage2[] installed =
        [
            Have("app", "1.0.0-r1", true, "lib>=1.0.0-r1"),
            Have("lib", "1.0.0-r1", false, "base>=1.0.0-r1"),
            Have("base", "1.0.0-r1", false),
            Have("orphan", "1.0.0-r1", false),
            Have("chain-a", "1.0.0-r1", false, "chain-b>=1.0.0-r1"),
            Have("chain-b", "1.0.0-r1", false),
            Have("cycle-x", "1.0.0-r1", false, "cycle-y>=1.0.0-r1"),
            Have("cycle-y", "1.0.0-r1", false, "cycle-x>=1.0.0-r1", "missing>=1.0.0-r1"),
        ];
        CollectionAssert.AreEqual(new[] { "chain-a", "chain-b", "cycle-x", "cycle-y", "orphan" }, PackageResolver.Unused(installed));
        Assert.AreEqual(0, PackageResolver.Unused([]).Length);
        Assert.AreEqual(0, PackageResolver.Unused([Have("a", "1.0.0-r1"), Have("a", "1.0.0-r1", false)]).Length);
        Assert.AreEqual(0, PackageResolver.Unused(null!).Length);
    }
}
