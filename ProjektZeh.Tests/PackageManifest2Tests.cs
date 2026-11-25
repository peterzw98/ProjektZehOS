using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Packages;
using ProjektZeh.Shared;
using ProjektZeh.Update;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class PackageManifest2Tests
{
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    // Vollstaendiges gueltiges Manifest mit jedem Feld; die Tests veraendern daraus jeweils genau eine Regel.
    private static readonly string Full = string.Join('\n',
        "zehos-package 2",
        "id=hello",
        "version=1.2.3",
        "release=4",
        "runtime=linux-x64-v1",
        "license=MIT OR (Apache-2.0 AND BSD-3-Clause)",
        "name=Hallo Welt",
        "entry=usr/bin/hello",
        "capability=net",
        "capability=storage",
        "capability=files",
        "memory-mib=256",
        "min-kernel=0.5.0",
        "depends=libcxx>=17.0.6-r1",
        "depends=musl>=1.2.5-r1",
        "provides=so:libhello.so.1",
        "provides=so:libhello_extra+x.so",
        $"exec={HashA} 10 usr/bin/hello",
        $"file={HashB} 20 usr/lib/libhello.so.1",
        "link=lib/libhello.so ../usr/lib/libhello.so.1",
        "link=usr/lib/libhello.so libhello.so.1",
        "end",
        "");

    // Bibliothek: nur Pflichtfelder und eine Datei.
    private static readonly string Library = string.Join('\n',
        "zehos-package 2", "id=musl", "version=1.2.5", "release=1", "runtime=linux-x64-v1", "license=MIT",
        $"file={HashA} 0 lib/ld-musl-x86_64.so.1", "end", "");

    private static ResultObject<PackageManifest2> Parse(string text) => PackageManifest2.Parse(Encoding.UTF8.GetBytes(text));

    private static void AssertValid(string text)
    {
        var parsed = Parse(text);
        Assert.IsTrue(parsed.IsOK(), parsed + "\n" + text);
    }

    private static void AssertInvalid(string text)
    {
        var parsed = Parse(text);
        Assert.IsFalse(parsed.IsOK(), text);
        Assert.AreEqual(ResultCode.InvalidArgument, parsed.Status.Code, text);
        Assert.IsNull(parsed.Value, text);
    }

    private static string Replace(string text, string line, string replacement)
    {
        string needle = "\n" + line + "\n";
        Assert.IsTrue(text.Contains(needle, StringComparison.Ordinal), line);
        return text.Replace(needle, replacement.Length == 0 ? "\n" : "\n" + replacement + "\n", StringComparison.Ordinal);
    }

    private static string Remove(string text, string line) => Replace(text, line, "");

    private static string InsertBefore(string text, string line, string inserted) => Replace(text, line, inserted + "\n" + line);

    private static string Content(int count, long size, Func<int, string> path) =>
        string.Concat(Enumerable.Range(0, count).Select(index =>
            string.Create(CultureInfo.InvariantCulture, $"file={HashA} {size} {path(index)}\n")));

    private static string WithLibraryFiles(string files) =>
        Library.Replace($"file={HashA} 0 lib/ld-musl-x86_64.so.1\n", files, StringComparison.Ordinal);

    [TestMethod]
    public void Parse_FullManifest_ReadsEveryField()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Full);
        var parsed = PackageManifest2.Parse(bytes);
        Assert.IsTrue(parsed.IsOK(), parsed.ToString());
        var manifest = parsed.Value!;
        Assert.AreEqual("hello", manifest.Id);
        Assert.AreEqual(new PackageVersion2(new UpdateVersion(1, 2, 3), 4), manifest.Version);
        Assert.AreEqual("MIT OR (Apache-2.0 AND BSD-3-Clause)", manifest.License);
        Assert.AreEqual("Hallo Welt", manifest.Name);
        Assert.AreEqual("usr/bin/hello", manifest.Entry);
        Assert.AreEqual(256, manifest.MemoryMiB);
        Assert.IsTrue(manifest.AllowsNetwork && manifest.AllowsStorage && manifest.AllowsFiles);
        Assert.AreEqual(new UpdateVersion(0, 5, 0), manifest.MinimumKernel);
        CollectionAssert.AreEqual(new[] { "libcxx>=17.0.6-r1", "musl>=1.2.5-r1" }, manifest.Dependencies.Select(d => d.ToString()).ToArray());
        CollectionAssert.AreEqual(new[] { "so:libhello.so.1", "so:libhello_extra+x.so" }, manifest.Provides);
        CollectionAssert.AreEqual(new[]
        {
            new PackageFile2("usr/bin/hello", 10, HashA, true),
            new PackageFile2("usr/lib/libhello.so.1", 20, HashB, false),
        }, manifest.Files);
        CollectionAssert.AreEqual(new[]
        {
            new PackageLink("lib/libhello.so", "../usr/lib/libhello.so.1"),
            new PackageLink("usr/lib/libhello.so", "libhello.so.1"),
        }, manifest.Links);
        // Die Signatur deckt die Originalbytes; das Manifest behaelt sie unveraendert.
        CollectionAssert.AreEqual(bytes, manifest.Bytes);
    }

    [TestMethod]
    public void Parse_LibraryWithoutOptionalFields_HasDefaults()
    {
        var parsed = Parse(Library);
        Assert.IsTrue(parsed.IsOK(), parsed.ToString());
        var manifest = parsed.Value!;
        Assert.IsNull(manifest.Name);
        Assert.IsNull(manifest.Entry);
        Assert.IsNull(manifest.MemoryMiB);
        Assert.IsNull(manifest.MinimumKernel);
        Assert.IsFalse(manifest.AllowsNetwork || manifest.AllowsStorage || manifest.AllowsFiles);
        Assert.AreEqual(0, manifest.Dependencies.Length);
        Assert.AreEqual(0, manifest.Provides.Length);
        Assert.AreEqual(0, manifest.Links.Length);
        Assert.AreEqual(1, manifest.Files.Length);
        Assert.IsFalse(manifest.Files[0].Executable);
    }

    [TestMethod]
    public void Parse_FormatsAreSeparate_V1AndV2RejectEachOther()
    {
        string v1 = $"zehos-package 1\nid=doom\nversion=1.0.0\nruntime=zehos-wasm32-v1\nname=Doom\nentry=app.wasm\nfile={HashA} 1 app.wasm\nend\n";
        Assert.IsTrue(PackageManifest.Parse(Encoding.UTF8.GetBytes(v1)).IsOK());
        AssertInvalid(v1);
        Assert.IsFalse(PackageManifest.Parse(Encoding.UTF8.GetBytes(Full)).IsOK());
        Assert.IsFalse(PackageManifest.Parse(Encoding.UTF8.GetBytes(Library)).IsOK());
    }

    [TestMethod]
    public void PackageVersion2_TryParse_AcceptsOnlyCanonicalForm()
    {
        Assert.IsTrue(PackageVersion2.TryParse("1.2.3-r4", out var version));
        Assert.AreEqual(new PackageVersion2(new UpdateVersion(1, 2, 3), 4), version);
        Assert.AreEqual("1.2.3-r4", version.ToString());
        Assert.IsTrue(PackageVersion2.TryParse("0.0.0-r9999", out version));
        Assert.AreEqual("0.0.0-r9999", version.ToString());
        foreach (string invalid in new[]
        {
            "", "1.2.3", "1.2.3-r", "1.2.3-r0", "1.2.3-r10000", "1.2.3-r04", "1.2-r1", "01.2.3-r1", "1.2.3-R1", "1.2.3-r+1",
            "1.2.3-r-1", "1.2.3 -r1", "1.2.3-r1 ", "1.2.3-r99999999999999999999", "99999999999.0.0-r1", "-r1",
        }) Assert.IsFalse(PackageVersion2.TryParse(invalid, out _), invalid);
        Assert.IsFalse(PackageVersion2.TryParse(null!, out _));
    }

    [TestMethod]
    public void PackageVersion2_CompareTo_OrdersVersionThenRelease()
    {
        static PackageVersion2 V(string text) => PackageVersion2.TryParse(text, out var value) ? value : throw new AssertFailedException(text);
        Assert.IsTrue(V("1.2.3-r9").CompareTo(V("1.2.4-r1")) < 0);
        Assert.IsTrue(V("1.2.3-r2").CompareTo(V("1.2.3-r10")) < 0);
        Assert.IsTrue(V("2.0.0-r1").CompareTo(V("1.99.99-r9999")) > 0);
        Assert.AreEqual(0, V("1.2.3-r4").CompareTo(V("1.2.3-r4")));
    }

    [TestMethod]
    public void PackageDependency_TryParse_RequiresIdAndCanonicalVersion()
    {
        Assert.IsTrue(PackageDependency.TryParse("musl>=1.2.5-r1", out var dependency));
        Assert.AreEqual("musl", dependency!.Id);
        Assert.AreEqual("1.2.5-r1", dependency.Minimum.ToString());
        Assert.AreEqual("musl>=1.2.5-r1", dependency.ToString());
        foreach (string invalid in new[]
        {
            "", "musl", "musl>1.2.5-r1", "musl=1.2.5-r1", "musl>=1.2.5", ">=1.2.5-r1", "Musl>=1.2.5-r1", "musl >=1.2.5-r1",
            "musl>= 1.2.5-r1", "musl>=1.2.5-r1>=1.0.0-r1", "-musl>=1.2.5-r1",
        })
        {
            Assert.IsFalse(PackageDependency.TryParse(invalid, out var rejected), invalid);
            Assert.IsNull(rejected, invalid);
        }
    }

    [TestMethod]
    public void Parse_Header_MustBeFormatTwo()
    {
        AssertInvalid(Full.Replace("zehos-package 2\n", "zehos-package 3\n", StringComparison.Ordinal));
        AssertInvalid(Full.Replace("zehos-package 2\n", "zehos-package 2 \n", StringComparison.Ordinal));
        AssertInvalid("\uFEFF" + Full);
        AssertInvalid("");
        AssertInvalid("zehos-package 2\n");
        AssertInvalid("zehos-package 2\nend\n");
    }

    [TestMethod]
    public void Parse_LineEndings_OnlyLfAllowed()
    {
        AssertValid(Full);
        AssertInvalid(Full.Replace("\n", "\r\n", StringComparison.Ordinal));
        AssertInvalid(Full.Replace("license=MIT OR", "license=MIT\rOR", StringComparison.Ordinal));
        AssertInvalid(Full[..^1]);                      // letzte Zeile ohne LF
        AssertInvalid(Full + "\n");                     // Leerzeile nach end
        AssertInvalid(Full + "end\n");                  // Text nach end
        AssertInvalid(InsertBefore(Full, "release=4", ""));
    }

    [TestMethod]
    public void Parse_Encoding_StrictUtf8AndSizeLimit()
    {
        AssertInvalid(Full.Replace("name=Hallo Welt", "name=Hällo", StringComparison.Ordinal));
        byte[] bytes = Encoding.UTF8.GetBytes(Full);
        bytes[^3] = 0xff;
        Assert.IsFalse(PackageManifest2.Parse(bytes).IsOK());
        Assert.IsFalse(PackageManifest2.Parse([]).IsOK());
        Assert.IsFalse(PackageManifest2.Parse(null!).IsOK());
        Assert.IsFalse(PackageManifest2.Parse(new byte[PackageManifest2.MaximumBytes + 1]).IsOK());
        string files = Content(4096, 1, index => "usr/share/f/" + index.ToString("D5", CultureInfo.InvariantCulture) + new string('x', 180));
        string large = WithLibraryFiles(files);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(large) > PackageManifest2.MaximumBytes);
        AssertInvalid(large);
    }

    [TestMethod]
    public void Parse_FieldOrder_IsFixed()
    {
        // Jede Vertauschung zweier benachbarter Zeilen verschiedener Schluessel ist ungueltig.
        string[] lines = Full.Split('\n');
        for (int index = 1; index < lines.Length - 3; index++)
        {
            string first = lines[index][..lines[index].IndexOf('=', StringComparison.Ordinal)];
            string second = lines[index + 1][..lines[index + 1].IndexOf('=', StringComparison.Ordinal)];
            if (first == second || (first is "exec" or "file" && second is "exec" or "file")) continue;
            string[] swapped = (string[])lines.Clone();
            (swapped[index], swapped[index + 1]) = (swapped[index + 1], swapped[index]);
            AssertInvalid(string.Join('\n', swapped));
        }
        foreach (string required in new[] { "id=hello", "version=1.2.3", "release=4", "runtime=linux-x64-v1", "license=MIT OR (Apache-2.0 AND BSD-3-Clause)" })
            AssertInvalid(Remove(Full, required));
        AssertInvalid(InsertBefore(Full, "end", "id=hello"));
        AssertInvalid(InsertBefore(Full, "end", "min-kernel=0.5.0"));
        AssertInvalid(InsertBefore(Full, "end", "unknown=1"));
        AssertInvalid(InsertBefore(Full, "end", "end"));
        AssertInvalid(InsertBefore(Full, "end", "kein Gleichheitszeichen"));
        AssertInvalid(Replace(Full, "release=4", "release=4\nrelease=4"));
    }

    [TestMethod]
    public void Parse_Id_LikeVersionOne()
    {
        AssertValid(Replace(Full, "id=hello", "id=a" + new string('b', 47)));
        AssertInvalid(Replace(Full, "id=hello", "id=a" + new string('b', 48)));
        AssertInvalid(Replace(Full, "id=hello", "id=Hello"));
        AssertInvalid(Replace(Full, "id=hello", "id=-hello"));
        AssertInvalid(Replace(Full, "id=hello", "id="));
        AssertInvalid(Replace(Full, "id=hello", "id=hel_lo"));
    }

    [TestMethod]
    public void Parse_VersionAndRelease_AreCanonical()
    {
        AssertValid(Replace(Full, "version=1.2.3", "version=0.0.0"));
        AssertInvalid(Replace(Full, "version=1.2.3", "version=1.2"));
        AssertInvalid(Replace(Full, "version=1.2.3", "version=01.2.3"));
        AssertInvalid(Replace(Full, "version=1.2.3", "version=1.2.3-r4"));
        AssertValid(Replace(Full, "release=4", "release=1"));
        AssertValid(Replace(Full, "release=4", "release=9999"));
        foreach (string release in new[] { "0", "10000", "04", "-1", "+1", "", "1 ", "99999999999999999999" })
            AssertInvalid(Replace(Full, "release=4", "release=" + release));
    }

    [TestMethod]
    public void Parse_Runtime_OnlyLinuxX64V1()
    {
        AssertInvalid(Replace(Full, "runtime=linux-x64-v1", "runtime=zehos-wasm32-v1"));
        AssertInvalid(Replace(Full, "runtime=linux-x64-v1", "runtime=linux-x64-v2"));
        AssertInvalid(Replace(Full, "runtime=linux-x64-v1", "runtime=LINUX-X64-V1"));
    }

    [TestMethod]
    public void Parse_License_CharsetAndLength()
    {
        string line = "license=MIT OR (Apache-2.0 AND BSD-3-Clause)";
        AssertValid(Replace(Full, line, "license=" + new string('A', 64)));
        AssertValid(Replace(Full, line, "license=GPL-2.0+"));
        AssertInvalid(Replace(Full, line, "license=" + new string('A', 65)));
        AssertInvalid(Replace(Full, line, "license="));
        AssertInvalid(Replace(Full, line, "license=MIT/BSD"));
        AssertInvalid(Replace(Full, line, "license=MIT\tOR BSD"));
        AssertInvalid(Replace(Full, line, "license=LicenseRef_x"));
    }

    [TestMethod]
    public void Parse_NameAndEntry_NameNeedsEntry()
    {
        AssertValid(Remove(Full, "name=Hallo Welt"));                 // Programm ohne Startmenueeintrag
        AssertInvalid(Remove(Full, "entry=usr/bin/hello"));           // name ohne entry
        AssertInvalid(Replace(Full, "name=Hallo Welt", "name= Hallo"));
        AssertInvalid(Replace(Full, "name=Hallo Welt", "name=" + new string('x', 33)));
        AssertValid(Replace(Full, "name=Hallo Welt", "name=" + new string('x', 32)));
        AssertInvalid(Replace(Full, "name=Hallo Welt", "name="));
        AssertInvalid(Replace(Full, "name=Hallo Welt", "name=Hallo\u0001"));
    }

    [TestMethod]
    public void Parse_Entry_MustBeListedExecLine()
    {
        AssertInvalid(Replace(Full, "entry=usr/bin/hello", "entry=usr/lib/libhello.so.1"));   // file statt exec
        AssertInvalid(Replace(Full, "entry=usr/bin/hello", "entry=usr/lib/libhello.so"));     // Link
        AssertInvalid(Replace(Full, "entry=usr/bin/hello", "entry=usr/bin/missing"));
        AssertInvalid(Replace(Full, "entry=usr/bin/hello", "entry=usr/bin/HELLO"));
        AssertInvalid(Replace(Full, "entry=usr/bin/hello", "entry=bin/hello"));
        AssertInvalid(Replace(Full, "entry=usr/bin/hello", "entry="));
    }

    [TestMethod]
    public void Parse_Capabilities_OnceEachInFixedOrder()
    {
        string none = Remove(Remove(Remove(Full, "capability=net"), "capability=storage"), "capability=files");
        var parsed = Parse(none);
        Assert.IsTrue(parsed.IsOK(), parsed.ToString());
        Assert.IsFalse(parsed.Value!.AllowsNetwork || parsed.Value.AllowsStorage || parsed.Value.AllowsFiles);
        parsed = Parse(Remove(Remove(Full, "capability=net"), "capability=files"));
        Assert.IsTrue(parsed.IsOK() && !parsed.Value!.AllowsNetwork && parsed.Value.AllowsStorage && !parsed.Value.AllowsFiles);
        AssertInvalid(Replace(Full, "capability=net", "capability=net\ncapability=net"));
        AssertInvalid(Replace(Full, "capability=files", "capability=files\ncapability=storage"));
        AssertInvalid(Replace(Full, "capability=net", "capability=camera"));
        AssertInvalid(Replace(Full, "capability=net", "capability=NET"));
        AssertInvalid(Replace(Full, "capability=net", "capability="));
    }

    [TestMethod]
    public void Parse_MemoryMiB_RangeAndNeedsEntry()
    {
        AssertValid(Replace(Full, "memory-mib=256", "memory-mib=1"));
        AssertValid(Replace(Full, "memory-mib=256", "memory-mib=4096"));
        foreach (string value in new[] { "0", "4097", "0256", "", "-1", "99999999999999999999" })
            AssertInvalid(Replace(Full, "memory-mib=256", "memory-mib=" + value));
        AssertInvalid(InsertBefore(Library, $"file={HashA} 0 lib/ld-musl-x86_64.so.1", "memory-mib=16"));
    }

    [TestMethod]
    public void Parse_MinimumKernel_Optional()
    {
        Assert.IsNull(Parse(Remove(Full, "min-kernel=0.5.0")).Value!.MinimumKernel);
        AssertInvalid(Replace(Full, "min-kernel=0.5.0", "min-kernel=0.5"));
        AssertInvalid(Replace(Full, "min-kernel=0.5.0", "min-kernel=0.5.0-r1"));
    }

    [TestMethod]
    public void Parse_Dependencies_SortedUniqueNotSelf()
    {
        AssertValid(Remove(Remove(Full, "depends=libcxx>=17.0.6-r1"), "depends=musl>=1.2.5-r1"));
        AssertInvalid(Replace(Full, "depends=libcxx>=17.0.6-r1", "depends=zlib>=1.0.0-r1"));      // nicht sortiert
        AssertInvalid(Replace(Full, "depends=libcxx>=17.0.6-r1", "depends=musl>=1.0.0-r1"));      // doppelte id
        AssertInvalid(Replace(Full, "depends=libcxx>=17.0.6-r1", "depends=hello>=1.0.0-r1"));     // sich selbst
        AssertInvalid(Replace(Full, "depends=libcxx>=17.0.6-r1", "depends=libcxx>=17.0.6"));
        AssertInvalid(Replace(Full, "depends=libcxx>=17.0.6-r1", "depends=libcxx"));
        string many(int count) => string.Join('\n', Enumerable.Range(0, count).Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"depends=d{i:D3}>=1.0.0-r1")));
        string withoutDependencies = Remove(Remove(Full, "depends=libcxx>=17.0.6-r1"), "depends=musl>=1.2.5-r1");
        AssertValid(InsertBefore(withoutDependencies, "provides=so:libhello.so.1", many(64)));
        AssertInvalid(InsertBefore(withoutDependencies, "provides=so:libhello.so.1", many(65)));
    }

    [TestMethod]
    public void Parse_Provides_SortedUniqueSonames()
    {
        string line = "provides=so:libhello.so.1";
        AssertInvalid(Replace(Full, line, "provides=libhello.so.1"));
        AssertInvalid(Replace(Full, line, "provides=bin:hello"));
        AssertInvalid(Replace(Full, line, "provides=so:"));
        AssertInvalid(Replace(Full, line, "provides=so:lib/hello.so"));
        AssertInvalid(Replace(Full, line, "provides=so:lib hello.so"));
        AssertInvalid(Replace(Full, line, "provides=so:libhello_extra+x.so"));     // doppelt
        AssertInvalid(Replace(Full, line, "provides=so:libz.so"));                 // nicht sortiert
        AssertValid(Replace(Full, line, "provides=so:" + new string('a', 200)));
        AssertInvalid(Replace(Full, line, "provides=so:" + new string('a', 201)));
        string many(int count) => string.Join('\n', Enumerable.Range(0, count).Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"provides=so:lib{i:D3}.so")));
        string withoutProvides = Remove(Remove(Full, line), "provides=so:libhello_extra+x.so");
        AssertValid(InsertBefore(withoutProvides, $"exec={HashA} 10 usr/bin/hello", many(64)));
        AssertInvalid(InsertBefore(withoutProvides, $"exec={HashA} 10 usr/bin/hello", many(65)));
    }

    [TestMethod]
    public void Parse_ContentLines_HashSizeAndSyntax()
    {
        string exec = $"exec={HashA} 10 usr/bin/hello";
        AssertInvalid(Replace(Full, exec, $"exec={HashA.ToUpperInvariant()} 10 usr/bin/hello"));
        AssertInvalid(Replace(Full, exec, $"exec={HashA[1..]} 10 usr/bin/hello"));
        AssertInvalid(Replace(Full, exec, $"exec={HashA} 010 usr/bin/hello"));
        AssertInvalid(Replace(Full, exec, $"exec={HashA} -1 usr/bin/hello"));
        AssertInvalid(Replace(Full, exec, $"exec={HashA}  10 usr/bin/hello"));
        AssertInvalid(Replace(Full, exec, $"exec={HashA} 10 usr/bin/hello x"));
        AssertInvalid(Replace(Full, exec, $"exec={HashA} 10"));
        AssertInvalid(Replace(Full, exec, $"exec={HashA} 99999999999999999999 usr/bin/hello"));
        AssertInvalid(Replace(Full, exec, $"exec={HashA} 9223372036854775808 usr/bin/hello"));
        AssertValid(Replace(Full, exec, $"exec={HashA} 0 usr/bin/hello"));
        AssertInvalid(WithLibraryFiles(""));                                    // ohne Inhalt
    }

    [TestMethod]
    public void Parse_ContentSize_SumLimitedWithoutOverflow()
    {
        const long limit = PackageManifest2.MaximumContentBytes;
        AssertValid(WithLibraryFiles($"file={HashA} {limit} lib/a\n"));
        AssertInvalid(WithLibraryFiles($"file={HashA} {limit + 1} lib/a\n"));
        AssertValid(WithLibraryFiles($"file={HashA} {limit - 1} lib/a\nfile={HashA} 1 lib/b\n"));
        AssertInvalid(WithLibraryFiles($"file={HashA} {limit} lib/a\nfile={HashA} 1 lib/b\n"));
        // Jede Zeile einzeln an der Grenze, die Summe weit darueber; eine einzelne zehnstellige Groesse liegt ebenfalls darueber.
        AssertInvalid(WithLibraryFiles(Content(4096, limit, index => "lib/f" + index.ToString("D4", CultureInfo.InvariantCulture))));
        AssertInvalid(WithLibraryFiles($"file={HashA} 9999999999 lib/a\n"));
    }

    [TestMethod]
    public void Parse_ContentCount_AtMost4096()
    {
        string name(int index) => "usr/share/f" + index.ToString("D4", CultureInfo.InvariantCulture);
        var parsed = Parse(WithLibraryFiles(Content(4096, 1, name)));
        Assert.IsTrue(parsed.IsOK(), parsed.ToString());
        Assert.AreEqual(4096, parsed.Value!.Files.Length);
        AssertInvalid(WithLibraryFiles(Content(4097, 1, name)));
    }

    [TestMethod]
    public void Parse_ContentOrder_ExecAndFileSortedTogether()
    {
        AssertValid(WithLibraryFiles($"file={HashA} 1 usr/a\nexec={HashA} 1 usr/b\nfile={HashA} 1 usr/c\n"));
        AssertInvalid(WithLibraryFiles($"exec={HashA} 1 usr/b\nfile={HashA} 1 usr/a\n"));
        AssertInvalid(WithLibraryFiles($"file={HashA} 1 usr/a\nexec={HashA} 1 usr/a\n"));      // doppelt
        AssertInvalid(WithLibraryFiles($"file={HashA} 1 usr/A\nfile={HashA} 1 usr/a\n"));      // nur Grossschreibung
        AssertValid(WithLibraryFiles($"file={HashA} 1 usr/B\nfile={HashA} 1 usr/a\n"));        // ordinal: B vor a
        AssertInvalid(WithLibraryFiles($"file={HashA} 1 usr/a\nfile={HashA} 1 usr/B\n"));
    }

    [TestMethod]
    public void Parse_Paths_UnderUsrLibEtcWithPlus()
    {
        foreach (string valid in new[] { "usr/bin/g++", "lib/x", "etc/ssl/cert.pem", "usr/" + new string('a', 196) })
            AssertValid(WithLibraryFiles($"file={HashA} 1 {valid}\n"));
        foreach (string invalid in new[]
        {
            "bin/x", "usr", "usr/", "/usr/x", "Usr/x", "var/x", "x", "usr//x", "usr/./x", "usr/../etc/x", "usr/x.", "usr/x/", "usr/a b",
            "usr/a\\b", "usr/ä", "usr/a:b", "usr/" + new string('a', 197), "usrx/a", "../usr/x",
        }) AssertInvalid(WithLibraryFiles($"file={HashA} 1 {invalid}\n"));
    }

    [TestMethod]
    public void Parse_Paths_NoPathIsFolderOfAnother()
    {
        AssertValid(WithLibraryFiles($"file={HashA} 1 usr/lib/a\nfile={HashA} 1 usr/lib/b\n"));
        AssertInvalid(WithLibraryFiles($"file={HashA} 1 usr/lib\nfile={HashA} 1 usr/lib/a\n"));
        AssertInvalid(WithLibraryFiles($"file={HashA} 1 usr/LIB\nfile={HashA} 1 usr/lib/a\n"));
        AssertValid(WithLibraryFiles($"file={HashA} 1 usr/lib.d\nfile={HashA} 1 usr/lib/a\n"));
        // Ein Link als Ordner einer Datei wuerde Dateien beim Auspacken durch den Link umlenken.
        string file = $"file={HashA} 1 usr/lib/a/b\n";
        AssertValid(WithLibraryFiles(file + "link=usr/lib/c a/b\n"));
        AssertInvalid(WithLibraryFiles(file + "link=usr/lib/a x\n"));
        AssertInvalid(WithLibraryFiles(file + "link=usr/lib x\n"));
        AssertInvalid(WithLibraryFiles(file + "link=usr/lib/A x\n"));
        AssertInvalid(WithLibraryFiles(file + "link=usr/lib/a/b/c x\n"));
    }

    [TestMethod]
    public void Parse_Links_SortedAndDistinctFromFiles()
    {
        string links = "link=lib/libhello.so ../usr/lib/libhello.so.1\nlink=usr/lib/libhello.so libhello.so.1\n";
        AssertValid(Full);
        AssertInvalid(Full.Replace(links, "link=usr/lib/libhello.so libhello.so.1\nlink=lib/libhello.so ../usr/lib/libhello.so.1\n", StringComparison.Ordinal));
        AssertInvalid(Full.Replace(links, links + "link=usr/lib/libhello.so libhello.so.1\n", StringComparison.Ordinal));
        AssertInvalid(Full.Replace(links, links + "link=usr/lib/libhello.so.1 x\n", StringComparison.Ordinal));     // Pfad einer Datei
        AssertInvalid(Full.Replace(links, links + "link=usr/lib/LIBHELLO.SO x\n", StringComparison.Ordinal));
        AssertInvalid(Full.Replace(links, links + "link=usr/lib/x\n", StringComparison.Ordinal));
        AssertInvalid(Full.Replace(links, links + "link=usr/lib/x y z\n", StringComparison.Ordinal));
        AssertInvalid(Full.Replace(links, links + "link=bin/x ../usr/bin/hello\n", StringComparison.Ordinal));
        string many(int count) => string.Concat(Enumerable.Range(0, count).Select(i =>
            string.Create(CultureInfo.InvariantCulture, $"link=usr/share/l{i:D4} ../bin/hello\n")));
        AssertValid(Full.Replace(links, links + many(4094), StringComparison.Ordinal));
        AssertInvalid(Full.Replace(links, links + many(4095), StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parse_LinkTarget_StaysUnderRoot()
    {
        string links = "link=lib/libhello.so ../usr/lib/libhello.so.1\nlink=usr/lib/libhello.so libhello.so.1\n";
        string With(string link) => Full.Replace(links, link + "\n", StringComparison.Ordinal);
        foreach (string valid in new[]
        {
            "link=usr/lib/x libhello.so.1", "link=usr/lib/x ../bin/hello", "link=usr/lib/x ../../etc/hosts", "link=lib/x ../etc/y",
            "link=usr/lib/x sub/dir/y", "link=usr/lib/x ../../usr/lib/libhello.so.1", "link=usr/lib/x g++",
        }) AssertValid(With(valid));
        foreach (string invalid in new[]
        {
            "link=usr/lib/x ../../..",                 // genau die Wurzel ueberschritten
            "link=usr/lib/x ../../../etc/passwd",      // Ausbruch
            "link=lib/x ../../y",
            "link=usr/lib/x /etc/passwd",              // absolut
            "link=usr/lib/x ../..",                    // die Wurzel selbst
            "link=usr/lib/x ..",
            "link=usr/lib/x a/../../../../y",          // .. nach einer Komponente ist nicht kanonisch
            "link=usr/lib/x a/..",
            "link=usr/lib/x ./y",
            "link=usr/lib/x y/.",
            "link=usr/lib/x a//b",
            "link=usr/lib/x y/",
            "link=usr/lib/x ",
            "link=usr/lib/x x",                        // auf sich selbst
            "link=usr/lib/x ../lib/X",                 // auf sich selbst (FAT32)
            "link=usr/lib/x a\\..\\..\\..\\y",
            "link=usr/lib/x y.",
            "link=usr/lib/x " + new string('a', 201),
            "link=usr/lib/x a\0b", "link=usr/lib/x a\tb", "link=usr/lib/x ä", "link=usr/lib/x \uFF0E\uFF0E/y",
            "link=usr/lib/x ../", "link=usr/lib/x ../.", "link=usr/lib/x ../a/../b", "link=usr/lib/x ...",
        }) AssertInvalid(With(invalid));
        AssertValid(With("link=usr/lib/x " + new string('a', 192)));     // aufgeloest genau 200 Zeichen
        AssertInvalid(With("link=usr/lib/x " + new string('a', 193)));
    }

    [TestMethod]
    public void Parse_RandomMutations_NeverThrow()
    {
        // Feste Saat, damit ein Fund reproduzierbar ist. Jede Mutation trifft ein gueltiges Manifest an zufaelliger Stelle.
        var random = new Random(20261124);
        byte[] fullBytes = Encoding.UTF8.GetBytes(Full);
        byte[] libraryBytes = Encoding.UTF8.GetBytes(Library);
        byte[] alphabet = Encoding.ASCII.GetBytes("\n\r =>-./+:0123456789abfrzAZ\0\x7f");
        int accepted = 0;
        for (int round = 0; round < 10000; round++)
        {
            var bytes = (round % 2 == 0 ? fullBytes : libraryBytes).ToList();
            int edits = 1 + random.Next(4);
            for (int edit = 0; edit < edits && bytes.Count > 0; edit++)
            {
                int position = random.Next(bytes.Count);
                switch (random.Next(5))
                {
                    case 0: bytes[position] = (byte)random.Next(256); break;
                    case 1: bytes[position] = alphabet[random.Next(alphabet.Length)]; break;
                    case 2: bytes.Insert(position, alphabet[random.Next(alphabet.Length)]); break;
                    case 3: bytes.RemoveAt(position); break;
                    default:
                        int length = Math.Min(bytes.Count - position, 1 + random.Next(40));
                        int target = random.Next(bytes.Count - length + 1);
                        var slice = bytes.GetRange(position, length);
                        bytes.RemoveRange(position, length);
                        bytes.InsertRange(Math.Min(target, bytes.Count), slice);
                        break;
                }
            }
            byte[] mutated = [.. bytes];
            ResultObject<PackageManifest2> parsed;
            try { parsed = PackageManifest2.Parse(mutated); }
            catch (Exception exception) { throw new AssertFailedException("Runde " + round + ": " + exception, exception); }
            if (parsed.IsOK())
            {
                accepted++;
                Assert.IsNotNull(parsed.Value);
                CollectionAssert.AreEqual(mutated, parsed.Value.Bytes);
                AssertInvariants(parsed.Value, round);
            }
            else Assert.AreEqual(ResultCode.InvalidArgument, parsed.Status.Code);
        }
        // Mutationen, die nur einen Hash- oder Namensbuchstaben aendern, bleiben gueltig; die meisten muessen scheitern.
        Assert.IsTrue(accepted < 5000, accepted.ToString(CultureInfo.InvariantCulture));
    }

    // Was der Parser annimmt, muss die Formatregeln erfuellen, egal wie es entstanden ist.
    private static void AssertInvariants(PackageManifest2 manifest, int round)
    {
        string context = "Runde " + round.ToString(CultureInfo.InvariantCulture);
        Assert.IsTrue(PackageManifest.IsValidId(manifest.Id), context);
        Assert.IsTrue(manifest.Version.Release is >= 1 and <= 9999, context);
        Assert.IsTrue(manifest.Files.Length is >= 1 and <= PackageManifest2.MaximumFiles, context);
        Assert.IsTrue(manifest.Links.Length <= PackageManifest2.MaximumLinks, context);
        Assert.IsTrue(manifest.Dependencies.Length <= 64 && manifest.Provides.Length <= 64, context);
        long total = 0;
        for (int index = 0; index < manifest.Files.Length; index++)
        {
            var file = manifest.Files[index];
            Assert.IsTrue(PackageManifest2.IsValidPath(file.Path) && file.Size >= 0 && file.Sha256.Length == 64, context);
            if (index > 0) Assert.IsTrue(string.CompareOrdinal(manifest.Files[index - 1].Path, file.Path) < 0, context);
            total += file.Size;
        }
        Assert.IsTrue(total <= PackageManifest2.MaximumContentBytes, context);
        for (int index = 1; index < manifest.Links.Length; index++)
            Assert.IsTrue(string.CompareOrdinal(manifest.Links[index - 1].Path, manifest.Links[index].Path) < 0, context);
        for (int index = 1; index < manifest.Dependencies.Length; index++)
            Assert.IsTrue(string.CompareOrdinal(manifest.Dependencies[index - 1].Id, manifest.Dependencies[index].Id) < 0, context);
        Assert.IsTrue(manifest.Dependencies.All(dependency => dependency.Id != manifest.Id), context);
        Assert.IsTrue(manifest.Name == null || manifest.Entry != null, context);
        Assert.IsTrue(manifest.MemoryMiB == null || manifest.Entry != null, context);
        Assert.IsTrue(manifest.Entry == null || manifest.Files.Any(file => file.Executable && file.Path == manifest.Entry), context);
        var paths = manifest.Files.Select(file => file.Path).Concat(manifest.Links.Select(link => link.Path)).ToList();
        Assert.AreEqual(paths.Count, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), context);
        Assert.IsFalse(paths.Any(path => paths.Any(other => other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))), context);
    }

    [TestMethod]
    public void TryParse_RandomStrings_NeverThrow()
    {
        var random = new Random(639);
        const string alphabet = "0123456789.-r>=abcmusl \0ä";
        for (int round = 0; round < 10000; round++)
        {
            var text = new string(Enumerable.Range(0, random.Next(24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            if (PackageVersion2.TryParse(text, out var version)) Assert.AreEqual(text, version.ToString());
            if (PackageDependency.TryParse(text, out var dependency)) Assert.AreEqual(text, dependency!.ToString());
        }
    }

    private static string LinkLibrary(params string[] links) =>
        WithLibraryFiles($"file={HashA} 1 usr/bin/tool\n" + string.Concat(links.Select(link => "link=" + link + "\n")));

    [TestMethod]
    public void Parse_LinkTarget_MustStayInPackageTrees()
    {
        AssertValid(LinkLibrary("usr/lib/x ../../etc/hosts"));
        AssertValid(LinkLibrary("lib/x ../usr/bin/tool"));
        foreach (string target in new[] { "../../dev/zehos", "../../data/secret", "../../save", "../../save/x", "../../tmp/x", "../../usr" })
            AssertInvalid(LinkLibrary("usr/lib/x " + target));
        AssertInvalid(LinkLibrary("lib/x ../var/y"));
        // Das aufgeloeste Ziel unterliegt derselben Laengengrenze wie jeder Pfad.
        string deep = "usr/" + string.Join('/', Enumerable.Repeat("d", 60)) + "/x";
        AssertValid(LinkLibrary(deep + " " + new string('a', 70)));
        AssertInvalid(LinkLibrary(deep + " " + new string('a', 90)));
    }

    [TestMethod]
    public void Parse_LinkCycles_AreRejected()
    {
        AssertInvalid(LinkLibrary("usr/lib/l l/x"));                          // Ziel unter dem Link selbst
        AssertInvalid(LinkLibrary("usr/lib/x ../lib"));                       // Ziel ist Ordner des Links
        AssertInvalid(LinkLibrary("usr/lib/x ../../usr/lib"));
        AssertInvalid(LinkLibrary("usr/lib/a b", "usr/lib/b a"));             // Kreis aus zwei Links
        AssertInvalid(LinkLibrary("usr/lib/a b", "usr/lib/b c", "usr/lib/c a"));
        AssertInvalid(LinkLibrary("usr/d lib", "usr/lib/x ../d"));            // ueber einen anderen Link auf den eigenen Ordner
        AssertInvalid(LinkLibrary("usr/lib/a b/x", "usr/lib/b a"));           // wachsender Kreis
        AssertValid(LinkLibrary("usr/lib/a b", "usr/lib/b c"));               // Kette ohne Kreis
        AssertValid(LinkLibrary("usr/lib/d ../share", "usr/lib/y d/x"));      // durch einen Link als Ordner
    }

    [TestMethod]
    public void Parse_LinkChains_AtMost40HopsAnd200Characters()
    {
        string Chain(int hops) => LinkLibrary(Enumerable.Range(0, hops + 1).Select(i =>
            "usr/share/l" + i.ToString("D2", CultureInfo.InvariantCulture) + " " +
            (i == hops ? "../bin/tool" : "l" + (i + 1).ToString("D2", CultureInfo.InvariantCulture))).ToArray());
        AssertValid(Chain(40));
        AssertInvalid(Chain(41));
        string longTarget = "usr/s " + new string('a', 180);      // aufgeloest 184 Zeichen
        AssertValid(LinkLibrary("usr/q s/" + new string('b', 10), longTarget));
        AssertInvalid(LinkLibrary("usr/q s/" + new string('b', 100), longTarget));
    }

    [TestMethod]
    public void Parse_ErrorMessage_IsShortAndPrintable()
    {
        string hostile = "\u001b[2J\u001b]0;pwn\u0007" + new string('Q', 900_000);
        foreach (string text in new[]
        {
            LinkLibrary("usr/lib/x " + hostile),
            Replace(Full, "capability=net", "capability=" + hostile),
            Replace(Full, "depends=libcxx>=17.0.6-r1", "depends=" + hostile),
            Replace(Full, "provides=so:libhello.so.1", "provides=" + hostile),
            Replace(Full, $"exec={HashA} 10 usr/bin/hello", "exec=" + hostile),
            WithLibraryFiles($"file={HashA} 1 usr/" + hostile + "\n"),
        })
        {
            string message = Parse(text).Status.Message;
            Assert.IsTrue(message.Length < 300, message.Length.ToString(CultureInfo.InvariantCulture));
            Assert.IsTrue(message.All(c => c is >= ' ' and <= '~'), message);
        }
        Assert.AreEqual("\"a?b\"", PackageManifest2.Quote("a\u001bb"));
        Assert.AreEqual("\"" + new string('x', 64) + "\"...", PackageManifest2.Quote(new string('x', 65)));
    }

    [TestMethod]
    public void Parse_Bytes_AreCopiedFromInput()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Full);
        var manifest = PackageManifest2.Parse(bytes).Value!;
        Assert.AreNotSame(bytes, manifest.Bytes);
        bytes[20] ^= 1;
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Full), manifest.Bytes);
    }

    [TestMethod]
    public void Parse_InvalidUtf8AndTruncation_AreRejected()
    {
        byte[] valid = Encoding.UTF8.GetBytes(Full);
        Assert.IsFalse(PackageManifest2.Parse([0xEF, 0xBB, 0xBF, .. valid]).IsOK());       // BOM
        byte[] overlong = Encoding.UTF8.GetBytes(Replace(Full, "name=Hallo Welt", "name=Hallo XX"));
        int at = Array.IndexOf(overlong, (byte)'X');
        overlong[at] = 0xC0;
        overlong[at + 1] = 0xAE;                                                            // ueberlanges '.'
        Assert.IsFalse(PackageManifest2.Parse(overlong).IsOK());
        byte[] surrogate = Encoding.UTF8.GetBytes(Replace(Full, "name=Hallo Welt", "name=Hallo XXX"));
        at = Array.IndexOf(surrogate, (byte)'X');
        (surrogate[at], surrogate[at + 1], surrogate[at + 2]) = ((byte)0xED, (byte)0xA0, (byte)0x80);
        Assert.IsFalse(PackageManifest2.Parse(surrogate).IsOK());
        AssertInvalid(Full[..^4]);                                                         // ohne end
        AssertInvalid(Full[..Full.IndexOf("provides=", StringComparison.Ordinal)]);
        AssertInvalid(Replace(Full, "name=Hallo Welt", "name=Hallo\0Welt"));
        AssertInvalid(Replace(Full, "name=Hallo Welt", "name=Hallo\tWelt"));
    }

    [TestMethod]
    public void Parse_DeepPaths_AllocateLittle()
    {
        // Nahe 1 MiB, 3868 Dateien in 95 Ebenen: Die Ordnerpruefung darf nicht je Ebene eine Zeichenkette anlegen.
        string directory = "usr/" + string.Join('/', Enumerable.Repeat("a", 93));
        string text = WithLibraryFiles(Content(3868, 1, index => directory + "/f" + index.ToString("D4", CultureInfo.InvariantCulture)));
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        Assert.IsTrue(bytes.Length is > 1_000_000 and <= PackageManifest2.MaximumBytes, bytes.Length.ToString(CultureInfo.InvariantCulture));
        Assert.IsTrue(PackageManifest2.Parse(bytes).IsOK());
        long before = GC.GetAllocatedBytesForCurrentThread();
        var parsed = PackageManifest2.Parse(bytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(parsed.IsOK(), parsed.ToString());
        Console.WriteLine("Parse_DeepPaths_AllocateLittle: " + allocated.ToString(CultureInfo.InvariantCulture) + " Bytes");
        Assert.IsTrue(allocated < 10L * 1024 * 1024, allocated.ToString(CultureInfo.InvariantCulture));
    }
}
