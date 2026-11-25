using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Packages;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class PackageCatalog2Tests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    private static readonly string Full = string.Join('\n',
        "zehos-catalog 2",
        "generation=7",
        $"package=hello 1.2.3-r4 {HashA} 1234",
        "depends=libcxx>=17.0.6-r1",
        "depends=musl>=1.2.5-r1",
        "provides=so:libhello.so.1",
        "provides=so:libhello_x+y.so",
        $"package=musl 1.2.5-r2 {HashB} 0",
        "provides=so:libc.so",
        "end") + "\n";

    private static ResultObject<PackageCatalog2> Parse(string text) => PackageCatalog2.Parse(Encoding.UTF8.GetBytes(text));

    private static void Rejects(string text, string fragment)
    {
        var result = Parse(text);
        Assert.IsFalse(result.Status.IsOK(), text);
        Assert.AreEqual(ResultCode.InvalidArgument, result.Status.Code);
        StringAssert.Contains(result.Status.Message, fragment);
    }

    private static string With(string from, string to) => Full.Replace(from, to, StringComparison.Ordinal);

    [TestMethod]
    public void ParsesValidCatalog()
    {
        var result = Parse(Full);
        Assert.IsTrue(result.Status.IsOK(), result.Status.Message);
        var catalog = result.Value!;
        Assert.AreEqual(7, catalog.Generation);
        Assert.AreEqual(2, catalog.Entries.Length);
        var hello = catalog.Find("hello")!;
        Assert.AreEqual("1.2.3-r4", hello.Version.ToString());
        Assert.AreEqual(HashA, hello.ManifestSha256);
        Assert.AreEqual(1234, hello.ContentBytes);
        CollectionAssert.AreEqual(new[] { "libcxx>=17.0.6-r1", "musl>=1.2.5-r1" }, hello.Dependencies.Select(d => d.ToString()).ToArray());
        CollectionAssert.AreEqual(new[] { "so:libhello.so.1", "so:libhello_x+y.so" }, hello.Provides);
        Assert.AreEqual(0, catalog.Find("musl")!.ContentBytes);
        Assert.IsNull(catalog.Find("nope"));
        Assert.IsNull(catalog.Find(null!));
    }

    [TestMethod]
    public void ParsesCatalogWithoutEntries() =>
        Assert.AreEqual(0, Parse("zehos-catalog 2\ngeneration=1\nend\n").Value!.Entries.Length);

    [TestMethod]
    public void RejectsMalformedInput()
    {
        Assert.IsFalse(PackageCatalog2.Parse(null!).Status.IsOK());
        Rejects("", "Katalogformat");
        Rejects(With("catalog 2", "catalog 1"), "Katalogformat");
        Rejects(Full.Replace("\n", "\r\n", StringComparison.Ordinal), "CR");
        Rejects(Full.TrimEnd('\n'), "Katalogende");
        Rejects(Full + "x\n", "Text nach end");
        Rejects(With("generation=7", "generation=0"), "Generation");
        Rejects(With("generation=7", "generation=07"), "Generation");
        Rejects(With("generation=7", "generation=-1"), "Generation");
        Rejects(With("generation=7", "generation=99999999999"), "Generation");
        Rejects(With("generation=7\n", ""), "Generation");
        Assert.IsFalse(PackageCatalog2.Parse([0xFF, 0xFE, 0x0A]).Status.IsOK());
    }

    [TestMethod]
    public void RejectsInvalidEntryLines()
    {
        Rejects(With("hello 1.2.3-r4", "Hello 1.2.3-r4"), "Eintrag");
        Rejects(With("1.2.3-r4", "1.2.3"), "Eintrag");
        Rejects(With("1.2.3-r4", "1.2.3-r0"), "Eintrag");
        Rejects(With("1.2.3-r4", "1.2.3-r04"), "Eintrag");
        Rejects(With(HashA, new string('A', 64)), "Eintrag");
        Rejects(With(HashA, new string('a', 63)), "Eintrag");
        Rejects(With(" 1234", ""), "Eintrag");
        Rejects(With(" 1234", " 12 34"), "Eintrag");
        Rejects(With(" 1234", " 01234"), "Eintrag");
        Rejects(With(" 1234", " 1073741825"), "Eintrag");
        Rejects(With(" 1234", " -1"), "Eintrag");
        Rejects(With("package=hello", "package= hello"), "Eintrag");
        Rejects(With("package=hello", "package=hello  "), "Eintrag");
    }

    [TestMethod]
    public void AcceptsExactContentLimit()
    {
        Assert.IsTrue(Parse(With(" 1234", " 1073741824")).Status.IsOK());
    }

    [TestMethod]
    public void RejectsOrderAndDuplicates()
    {
        Rejects(With("package=musl", "package=hello"), "Doppelte Paketkennung");
        Rejects(With("package=hello", "package=zlib").Replace("package=musl", "package=aaa", StringComparison.Ordinal), "nicht nach Kennung sortiert");
        Rejects(With("depends=libcxx", "depends=musl"), "Doppelte Abhaengigkeit");
        Rejects(With("depends=libcxx>=17.0.6-r1\ndepends=musl>=1.2.5-r1", "depends=musl>=1.2.5-r1\ndepends=libcxx>=17.0.6-r1"), "nicht nach Kennung sortiert");
        Rejects(With("provides=so:libhello.so.1\nprovides=so:libhello_x+y.so", "provides=so:libhello_x+y.so\nprovides=so:libhello.so.1"), "nicht sortiert");
        Rejects(With("provides=so:libhello_x+y.so", "provides=so:libhello.so.1"), "Doppelte provides");
        Rejects(With("depends=musl>=1.2.5-r1\nprovides=so:libhello.so.1", "provides=so:libhello.so.1\ndepends=musl>=1.2.5-r1"), "depends steht nach provides");
    }

    [TestMethod]
    public void RejectsInvalidDependsAndProvides()
    {
        Rejects(With("depends=libcxx>=17.0.6-r1", "depends=libcxx>=17.0.6"), "Abhaengigkeit");
        Rejects(With("depends=libcxx>=17.0.6-r1", "depends=libcxx"), "Abhaengigkeit");
        Rejects(With("depends=libcxx>=17.0.6-r1", "depends=hello>=1.0.0-r1"), "sich selbst");
        Rejects(With("provides=so:libc.so", "provides=libc.so"), "provides");
        Rejects(With("provides=so:libc.so", "provides=so:"), "provides");
        Rejects(With("provides=so:libc.so", "provides=so:lib c.so"), "provides");
        Rejects(With("provides=so:libc.so", "provides=so:" + new string('a', 201)), "provides");
        Rejects(With("provides=so:libc.so", "unknown=1"), "Unbekannte Zeile");
        Rejects("zehos-catalog 2\ngeneration=1\ndepends=a>=1.0.0-r1\nend\n", "Unbekannte Zeile");
    }

    [TestMethod]
    public void EnforcesCountLimits()
    {
        string deps = string.Concat(Enumerable.Range(0, 65).Select(i => $"depends=d{i:D3}>=1.0.0-r1\n"));
        Rejects($"zehos-catalog 2\ngeneration=1\npackage=a 1.0.0-r1 {HashA} 1\n{deps}end\n", "64");
        string provides = string.Concat(Enumerable.Range(0, 65).Select(i => $"provides=so:l{i:D3}.so\n"));
        Rejects($"zehos-catalog 2\ngeneration=1\npackage=a 1.0.0-r1 {HashA} 1\n{provides}end\n", "64");
        string sixtyFour = string.Concat(Enumerable.Range(0, 64).Select(i => $"depends=d{i:D3}>=1.0.0-r1\n"));
        Assert.IsTrue(Parse($"zehos-catalog 2\ngeneration=1\npackage=a 1.0.0-r1 {HashA} 1\n{sixtyFour}end\n").Status.IsOK());

        Assert.IsTrue(Parse(Build(4096)).Status.IsOK());
        Rejects(Build(4097), "4096");
        Rejects(Full + new string('x', PackageCatalog2.MaximumBytes), "4 MiB");
    }

    private static string Build(int count)
    {
        var text = new StringBuilder("zehos-catalog 2\ngeneration=1\n");
        for (int i = 0; i < count; i++) text.Append(CultureInfo.InvariantCulture, $"package=p{i:D5} 1.0.0-r1 {HashA} 1\n");
        return text.Append("end\n").ToString();
    }

    [TestMethod]
    public void RejectsBomAndInvalidUtf8()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Full)];
        Assert.IsFalse(PackageCatalog2.Parse(bom).Status.IsOK());
        byte[] bad = Encoding.UTF8.GetBytes(Full);
        bad[Full.IndexOf("hello", StringComparison.Ordinal)] = 0xC0;
        var result = PackageCatalog2.Parse(bad);
        Assert.AreEqual(ResultCode.InvalidArgument, result.Status.Code);
        StringAssert.Contains(result.Status.Message, "UTF-8");
    }

    // Gueltiger Katalog mit genau total Bytes: lange provides-Zeilen, die letzten beiden fuellen den Rest.
    private static byte[] CatalogOfSize(int total)
    {
        var text = new StringBuilder("zehos-catalog 2\ngeneration=1\n");
        int entry = 0, inEntry = 64, counter = 0;
        int remaining = total - text.Length - 4;
        while (remaining > 0)
        {
            int line = remaining > 213 ? (remaining > 231 ? 213 : remaining - 18) : remaining;
            if (inEntry == 64)
            {
                string head = $"package=p{entry++:D5} 1.0.0-r1 {HashA} 1\n";
                // Der Paketkopf zaehlt zur Groesse; ohne Rest wuerde er die Rechnung stoeren, also zuerst abziehen.
                text.Append(head);
                remaining -= head.Length;
                inEntry = 0;
                continue;
            }
            string name = counter++.ToString("D5", CultureInfo.InvariantCulture);
            text.Append("provides=so:").Append(name).Append('x', line - 13 - name.Length).Append('\n');
            remaining -= line;
            inEntry++;
        }
        text.Append("end\n");
        Assert.AreEqual(total, text.Length);
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    [TestMethod]
    public void AcceptsExactlyFourMiB()
    {
        var exact = PackageCatalog2.Parse(CatalogOfSize(PackageCatalog2.MaximumBytes));
        Assert.IsTrue(exact.Status.IsOK(), exact.Status.Message);
        var over = PackageCatalog2.Parse([.. CatalogOfSize(PackageCatalog2.MaximumBytes), (byte)'\n']);
        Assert.IsFalse(over.Status.IsOK());
        StringAssert.Contains(over.Status.Message, "4 MiB");
    }

    [TestMethod]
    public void ErrorMessagesFilterInput()
    {
        string evil = "\u001b[31mä" + new string('x', 200);
        var result = Parse(With("depends=libcxx>=17.0.6-r1", "depends=" + evil));
        Assert.IsFalse(result.Status.IsOK());
        string message = result.Status.Message!;
        Assert.IsTrue(message.All(c => c is >= ' ' and <= '~'), message);
        Assert.IsTrue(message.Length < 250, message);
    }

    [TestMethod]
    public void NeverThrowsOnMutatedCatalogs()
    {
        var random = new Random(640);
        byte[] original = Encoding.UTF8.GetBytes(Full);
        int accepted = 0;
        for (int round = 0; round < 10000; round++)
        {
            byte[] copy = (byte[])original.Clone();
            for (int change = random.Next(1, 4); change > 0; change--)
            {
                switch (random.Next(3))
                {
                    case 0: copy[random.Next(copy.Length)] = (byte)random.Next(256); break;
                    case 1: copy = [.. copy.Take(random.Next(copy.Length))]; break;
                    default:
                        int at = random.Next(copy.Length);
                        copy = [.. copy.Take(at), .. copy.Skip(at).Skip(random.Next(1, 5))];
                        break;
                }
                if (copy.Length == 0) break;
            }
            var result = PackageCatalog2.Parse(copy);
            if (result.Status.IsOK()) accepted++;
            else Assert.AreEqual(ResultCode.InvalidArgument, result.Status.Code);
        }
        Assert.IsTrue(accepted < 10000);
    }

    // Repository aus scripts/package2.py (TestData/Package2, Testschluessel; der private Teil wurde nach dem Erzeugen geloescht).
    // scripts/tests/test_package2.py erzeugt Manifeste und Katalog mit einem neuen Schluessel erneut und vergleicht byteweise.
    private static byte[] Fixture(params string[] parts) =>
        File.ReadAllBytes(Path.Combine([AppContext.BaseDirectory, "TestData", "Package2", .. parts]));

    private static PackageTrust FixtureTrust() =>
        new(Convert.FromBase64String(Encoding.ASCII.GetString(Fixture("public.b64")).Trim()));

    private static byte[] FixtureManifest(CatalogEntry2 entry) => Fixture("repo", entry.Id, entry.Version.ToString(), "manifest");

    [TestMethod]
    public void ReadsSignedCatalogWrittenByTool()
    {
        var trust = FixtureTrust();
        byte[] bytes = Fixture("repo", "catalog2");
        // Wie beim Aufrufer: erst die Signatur ueber die Rohbytes, dann parsen.
        Assert.IsTrue(trust.Verify(bytes, Fixture("repo", "catalog2.sig")));
        var result = PackageCatalog2.Parse(bytes);
        Assert.IsTrue(result.Status.IsOK(), result.Status.Message);
        var catalog = result.Value!;
        Assert.AreEqual(3, catalog.Generation);
        CollectionAssert.AreEqual(new[] { "hello 1.2.3-r4", "libcxx 17.0.6-r2", "musl 1.2.5-r1" },
            catalog.Entries.Select(e => e.Id + " " + e.Version).ToArray());
        CollectionAssert.AreEqual(bytes, Encoding.UTF8.GetBytes(RenderCatalog(catalog)), "Katalog ist nicht kanonisch.");

        foreach (var entry in catalog.Entries)
        {
            byte[] manifestBytes = FixtureManifest(entry);
            Assert.IsTrue(trust.Verify(manifestBytes, Fixture("repo", entry.Id, entry.Version.ToString(), "manifest.sig")), entry.Id);
            Assert.AreEqual(entry.ManifestSha256, PackageTrust.Sha256Hex(manifestBytes), entry.Id);
            var parsed = PackageManifest2.Parse(manifestBytes);
            Assert.IsTrue(parsed.Status.IsOK(), parsed.Status.Message);
            var manifest = parsed.Value!;
            Assert.AreEqual(entry.Id, manifest.Id);
            Assert.AreEqual(entry.Version, manifest.Version);
            // Vor der Aktivierung muessen depends, provides und Inhaltssumme von Katalog und Manifest uebereinstimmen.
            CollectionAssert.AreEqual(entry.Dependencies, manifest.Dependencies, entry.Id);
            CollectionAssert.AreEqual(entry.Provides, manifest.Provides, entry.Id);
            Assert.AreEqual(entry.ContentBytes, manifest.Files.Sum(f => f.Size), entry.Id);
            CollectionAssert.AreEqual(manifestBytes, Encoding.UTF8.GetBytes(RenderManifest(manifest)), entry.Id + " ist nicht kanonisch.");
            foreach (var file in manifest.Files)
            {
                byte[] content = Fixture(["repo", entry.Id, entry.Version.ToString(), "files", .. file.Path.Split('/')]);
                Assert.AreEqual(file.Size, content.Length, file.Path);
                Assert.AreEqual(file.Sha256, PackageTrust.Sha256Hex(content), file.Path);
            }
        }
    }

    [TestMethod]
    public void ReadsManifestWrittenByTool()
    {
        var manifest = PackageManifest2.Parse(Fixture("repo", "hello", "1.2.3-r4", "manifest")).Value!;
        Assert.AreEqual("MIT OR Apache-2.0", manifest.License);
        Assert.AreEqual("Hallo Welt", manifest.Name);
        Assert.AreEqual("usr/bin/hello", manifest.Entry);
        Assert.AreEqual(256, manifest.MemoryMiB);
        Assert.AreEqual("0.5.0", manifest.MinimumKernel.ToString());
        // Die Spec nennt files vor net; das Werkzeug schreibt die feste Reihenfolge.
        Assert.IsTrue(manifest.AllowsNetwork && manifest.AllowsFiles && !manifest.AllowsStorage);
        CollectionAssert.AreEqual(new[] { "libcxx>=17.0.6-r1", "musl>=1.2.5-r1" }, manifest.Dependencies.Select(d => d.ToString()).ToArray());
        CollectionAssert.AreEqual(new[] { "usr/bin/hello:True", "usr/lib/libhello.so.1:True", "usr/share/hello/greeting.txt:False" },
            manifest.Files.Select(f => f.Path + ":" + f.Executable).ToArray());
        CollectionAssert.AreEqual(new[] { new PackageLink("usr/bin/hallo", "hello"), new PackageLink("usr/lib/libhello.so", "libhello.so.1") },
            manifest.Links);
    }

    [TestMethod]
    public void RejectsChangedToolOutput()
    {
        var trust = FixtureTrust();
        byte[] catalog = Fixture("repo", "catalog2");
        catalog[catalog.Length - 6] ^= 1;
        Assert.IsFalse(trust.Verify(catalog, Fixture("repo", "catalog2.sig")));
        byte[] manifest = Fixture("repo", "musl", "1.2.5-r1", "manifest");
        Assert.IsFalse(trust.Verify(manifest, Fixture("repo", "hello", "1.2.3-r4", "manifest.sig")));
    }

    // Kanonische Schreibweise aus den geparsten Feldern; muss die Bytes des Werkzeugs genau wiederergeben.
    private static string RenderCatalog(PackageCatalog2 catalog)
    {
        var lines = new List<string> { "zehos-catalog 2", "generation=" + catalog.Generation.ToString(CultureInfo.InvariantCulture) };
        foreach (var entry in catalog.Entries)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"package={entry.Id} {entry.Version} {entry.ManifestSha256} {entry.ContentBytes}"));
            lines.AddRange(entry.Dependencies.Select(d => "depends=" + d));
            lines.AddRange(entry.Provides.Select(p => "provides=" + p));
        }
        lines.Add("end");
        return string.Join('\n', lines) + "\n";
    }

    private static string RenderManifest(PackageManifest2 manifest)
    {
        var lines = new List<string>
        {
            "zehos-package 2", "id=" + manifest.Id, "version=" + manifest.Version.Version,
            "release=" + manifest.Version.Release.ToString(CultureInfo.InvariantCulture), "runtime=" + PackageManifest2.Runtime,
            "license=" + manifest.License,
        };
        if (manifest.Name != null) lines.Add("name=" + manifest.Name);
        if (manifest.Entry != null) lines.Add("entry=" + manifest.Entry);
        if (manifest.AllowsNetwork) lines.Add("capability=net");
        if (manifest.AllowsStorage) lines.Add("capability=storage");
        if (manifest.AllowsFiles) lines.Add("capability=files");
        if (manifest.MemoryMiB != null) lines.Add("memory-mib=" + manifest.MemoryMiB.Value.ToString(CultureInfo.InvariantCulture));
        if (manifest.MinimumKernel != null) lines.Add("min-kernel=" + manifest.MinimumKernel);
        lines.AddRange(manifest.Dependencies.Select(d => "depends=" + d));
        lines.AddRange(manifest.Provides.Select(p => "provides=" + p));
        lines.AddRange(manifest.Files.Select(f =>
            string.Create(CultureInfo.InvariantCulture, $"{(f.Executable ? "exec" : "file")}={f.Sha256} {f.Size} {f.Path}")));
        lines.AddRange(manifest.Links.Select(l => "link=" + l.Path + " " + l.Target));
        lines.Add("end");
        return string.Join('\n', lines) + "\n";
    }
}
