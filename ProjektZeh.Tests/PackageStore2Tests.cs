using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using ProjektZeh.Packages;
using ProjektZeh.Update;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class PackageStore2Tests
{
    // Nur fuer Hosttests; nie in einen Kernelbuild uebernehmen.
    private static readonly Ed25519PrivateKeyParameters TestKey = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), 0);
    private static readonly Ed25519PrivateKeyParameters OtherKey = new(Enumerable.Range(50, 32).Select(i => (byte)i).ToArray(), 0);
    private static readonly PackageTrust Trust = new(TestKey.GeneratePublicKey().GetEncoded());
    private static readonly UpdateVersion Kernel = new(1, 0, 0);
    private string work = "";

    private sealed class PowerLoss : Exception;

    private sealed record Pkg(string Id, string Version, (string Path, byte[] Content, bool Exec)[] Files)
    {
        public string[] Depends { get; init; } = [];
        public string[] Provides { get; init; } = [];
        public (string Path, string Target)[] Links { get; init; } = [];
        public string? MinKernel { get; init; }
        public PackageVersion2 Parsed => PackageVersion2.TryParse(Version, out var value) ? value : throw new AssertFailedException(Version);
    }

    [TestInitialize]
    public void Create() => work = Directory.CreateTempSubdirectory("zehos-store2-").FullName;

    [TestCleanup]
    public void Delete() => Directory.Delete(work, true);

    private string Root => Path.Combine(work, "store");
    private string Repo => Path.Combine(work, "repo");

    private static byte[] Sign(byte[] content, Ed25519PrivateKeyParameters? key = null)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, key ?? TestKey);
        signer.BlockUpdate(content, 0, content.Length);
        return signer.GenerateSignature();
    }

    private static byte[] ManifestBytes(Pkg pkg)
    {
        string[] parts = pkg.Version.Split("-r");
        var text = new StringBuilder($"zehos-package 2\nid={pkg.Id}\nversion={parts[0]}\nrelease={parts[1]}\nruntime=linux-x64-v1\nlicense=MIT\n");
        if (pkg.MinKernel != null) text.Append("min-kernel=").Append(pkg.MinKernel).Append('\n');
        foreach (string dependency in pkg.Depends.Order(StringComparer.Ordinal)) text.Append("depends=").Append(dependency).Append('\n');
        foreach (string provides in pkg.Provides.Order(StringComparer.Ordinal)) text.Append("provides=").Append(provides).Append('\n');
        foreach (var file in pkg.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            text.Append(CultureInfo.InvariantCulture, $"{(file.Exec ? "exec" : "file")}={PackageTrust.Sha256Hex(file.Content)} {file.Content.Length} {file.Path}\n");
        foreach (var link in pkg.Links.OrderBy(link => link.Path, StringComparer.Ordinal)) text.Append("link=").Append(link.Path).Append(' ').Append(link.Target).Append('\n');
        return Encoding.UTF8.GetBytes(text.Append("end\n").ToString());
    }

    // Legt die Version im Repository wie das Paketwerkzeug ab (<id>/<version>-r<release>/...) und liefert den Katalogschluessel.
    private CatalogEntry2Key Publish(Pkg pkg, Ed25519PrivateKeyParameters? key = null)
    {
        string directory = Path.Combine(Repo, pkg.Id, pkg.Version);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        foreach (var file in pkg.Files)
        {
            string path = Path.Combine(directory, "files", file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Content);
        }
        byte[] manifest = ManifestBytes(pkg);
        File.WriteAllBytes(Path.Combine(directory, "manifest"), manifest);
        File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), Sign(manifest, key));
        return new CatalogEntry2Key(pkg.Id, pkg.Parsed, PackageTrust.Sha256Hex(manifest));
    }

    private static Pkg Lib(string id, string version, params string[] provides) =>
        new(id, version, [($"usr/lib/{id}.so", Encoding.ASCII.GetBytes(id + version), false), ($"usr/share/{id}/big.dat", Pattern(70000, version), false)])
        { Provides = provides };

    private static byte[] Pattern(int length, string seed)
    {
        var bytes = new byte[length];
        for (int index = 0; index < length; index++) bytes[index] = (byte)(index * 31 + seed.Length);
        return bytes;
    }

    private static readonly Pkg MuslR1 = Lib("musl", "1.2.5-r1", "so:libc.so");
    private static readonly Pkg MuslR2 = Lib("musl", "1.2.5-r2", "so:libc.so");
    private static readonly Pkg Hello = new("hello", "1.0.0-r1", [("usr/bin/hello", [1, 2, 3], true)])
    { Depends = ["musl>=1.2.5-r1"], Links = [("usr/bin/hi", "hello")] };

    private PackageStore2 Store(string? root = null, UpdateVersion? kernel = null, Action<string>? step = null) =>
        new(root ?? Root, Trust, kernel ?? Kernel) { FileStep = step };

    private DirectoryPackageSource Source() => new(Repo);

    private static (string, PackageVersion2, bool) Entry(Pkg pkg, bool requested = true) => (pkg.Id, pkg.Parsed, requested);

    private void Install(PackageStore2 store, params Pkg[] set)
    {
        foreach (var pkg in set)
        {
            var staged = store.Stage(Source(), Publish(pkg), CancellationToken.None);
            Assert.IsTrue(staged.IsOK(), staged.Status.Message);
        }
        var activated = store.Activate(set.Select(pkg => Entry(pkg)).ToArray(), CancellationToken.None);
        Assert.IsTrue(activated.IsOK(), activated.Message);
    }

    private static string Describe(PackageStore2 store)
    {
        var active = store.ReadActive();
        Assert.IsTrue(active.IsOK(), active.Status.Message);
        return string.Join(",", active.Value!.Select(package => $"{package.Manifest.Id} {package.Manifest.Version} {(package.Requested ? "requested" : "auto")}"));
    }

    private static string Describe(params Pkg[] set) =>
        string.Join(",", set.OrderBy(pkg => pkg.Id, StringComparer.Ordinal).Select(pkg => $"{pkg.Id} {pkg.Version} requested"));

    // v1-Bestand neben v2: Marker, Versionsordner, Katalog, Quelle samt Ersatzresten; muss byteweise unveraendert bleiben.
    private static void WriteV1(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "apps", "doom", "1.0.0", "files"));
        File.WriteAllText(Path.Combine(root, "apps", "doom", "active"), "1.0.0\n");
        File.WriteAllBytes(Path.Combine(root, "apps", "doom", "1.0.0", "manifest"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "apps", "doom", "1.0.0", "files", "doom.wasm"), Pattern(1000, "v1"));
        Directory.CreateDirectory(Path.Combine(root, "apps", "stray", "2.0.0"));
        File.WriteAllBytes(Path.Combine(root, "catalog.signed"), Pattern(300, "cat"));
        File.WriteAllBytes(Path.Combine(root, "catalog.signed.new"), Pattern(30, "cat"));
        File.WriteAllText(Path.Combine(root, "source"), "https://example.invalid/\n");
    }

    private static string TreeV1(string root) =>
        string.Join("\n", Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).StartsWith("v2", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + (File.Exists(path) ? " " + PackageTrust.Sha256Hex(File.ReadAllBytes(path)) : "/")));

    private static string[] VersionDirectories(string root) =>
        !Directory.Exists(Path.Combine(root, "v2")) ? [] :
        Directory.GetDirectories(Path.Combine(root, "v2")).SelectMany(Directory.GetDirectories)
            .Select(path => Path.GetRelativePath(Path.Combine(root, "v2"), path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(from)) CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private string ActiveText => File.ReadAllText(Path.Combine(Root, "v2", "active2"));

    [TestMethod]
    public void StageAndActivateWriteCanonicalActiveFile()
    {
        var store = Store();
        var musl = store.Stage(Source(), Publish(MuslR1), CancellationToken.None);
        Assert.IsTrue(musl.IsOK(), musl.Status.Message);
        Assert.IsTrue(store.Stage(Source(), Publish(Hello), CancellationToken.None).IsOK());
        Assert.AreEqual("", Describe(store), "Stufen allein aktiviert nichts.");
        var activated = store.Activate([Entry(Hello), Entry(MuslR1, requested: false)], CancellationToken.None);
        Assert.IsTrue(activated.IsOK(), activated.Message);
        Assert.AreEqual("hello 1.0.0-r1 requested,musl 1.2.5-r1 auto", Describe(store));
        Assert.AreEqual(
            "zehos-active 2\n" +
            $"package=hello 1.0.0-r1 requested {PackageTrust.Sha256Hex(ManifestBytes(Hello))}\n" +
            $"package=musl 1.2.5-r1 auto {PackageTrust.Sha256Hex(ManifestBytes(MuslR1))}\n" +
            "end\n", ActiveText);
        var active = store.ReadActive().Value!;
        Assert.AreEqual(Path.Combine(Root, "v2", "hello", "1.0.0-r1"), active[0].Directory);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(active[0].FilesDirectory, "usr", "bin", "hello")));
        foreach (var package in active) Assert.IsTrue(PackageStore2.VerifyFiles(package, CancellationToken.None).IsOK());
        Assert.IsFalse(File.Exists(Path.Combine(Root, "v2", "active2.new")) || File.Exists(Path.Combine(Root, "v2", "active2.old")));
    }

    [TestMethod]
    public void MissingActiveIsEmptyAndWithoutTrustEverythingIsLocked()
    {
        Assert.AreEqual("", Describe(Store()));
        Assert.IsTrue(Store().Reconcile().IsOK());
        Assert.IsFalse(Directory.Exists(Root), "Lesen und Aufraeumen eines leeren Bestands legen nichts an.");
        var locked = new PackageStore2(Root, null, Kernel);
        Assert.IsFalse(locked.ReadActive().IsOK());
        Assert.IsFalse(locked.Stage(Source(), Publish(MuslR1), CancellationToken.None).IsOK());
        Assert.IsFalse(locked.Activate([], CancellationToken.None).IsOK());
    }

    [TestMethod]
    public void EmptySetIsValidActiveState()
    {
        var store = Store();
        Install(store, MuslR1);
        Assert.IsTrue(store.Activate([], CancellationToken.None).IsOK());
        Assert.AreEqual("zehos-active 2\nend\n", ActiveText);
        Assert.AreEqual("", Describe(store));
    }

    [TestMethod]
    public void DamagedActiveFileIsAnErrorNeverEmpty()
    {
        var store = Store();
        Install(store, MuslR1, Hello);
        string valid = ActiveText;
        string hello = $"package=hello 1.0.0-r1 requested {PackageTrust.Sha256Hex(ManifestBytes(Hello))}\n";
        string musl = $"package=musl 1.2.5-r1 requested {PackageTrust.Sha256Hex(ManifestBytes(MuslR1))}\n";
        Assert.AreEqual("zehos-active 2\n" + hello + musl + "end\n", valid);
        string[] damaged =
        [
            "",
            "zehos-active 2\n",
            "zehos-active 2\n" + hello + musl,
            "zehos-active 2\n" + hello + musl + "end",
            "zehos-active 2\n" + hello + musl + "end\nend\n",
            "zehos-active 1\n" + hello + musl + "end\n",
            "zehos-active 2\n" + musl + hello + "end\n",
            "zehos-active 2\n" + hello + hello + musl + "end\n",
            "zehos-active 2\n\n" + hello + musl + "end\n",
            "zehos-active 2\r\n" + hello + musl + "end\r\n",
            "zehos-active 2\n" + hello.Replace("requested", "manual", StringComparison.Ordinal) + musl + "end\n",
            "zehos-active 2\n" + hello.Replace("1.0.0-r1", "1.0.0-r01", StringComparison.Ordinal) + musl + "end\n",
            "zehos-active 2\n" + hello.Replace(" requested ", " requested  ", StringComparison.Ordinal) + musl + "end\n",
            "zehos-active 2\n" + hello[..^2] + "\n" + musl + "end\n",
            "zehos-active 2\n" + hello.ToUpperInvariant().Replace("PACKAGE=HELLO", "package=hello", StringComparison.Ordinal).Replace("REQUESTED", "requested", StringComparison.Ordinal) + musl + "end\n",
            // Ohne musl fehlt die Abhaengigkeit von hello.
            "zehos-active 2\n" + hello + "end\n",
            // Hash eines anderen Manifests.
            "zehos-active 2\n" + hello + musl.Replace(PackageTrust.Sha256Hex(ManifestBytes(MuslR1)), new string('a', 64), StringComparison.Ordinal) + "end\n",
            // Version ohne Ordner.
            "zehos-active 2\n" + hello + musl.Replace("1.2.5-r1", "1.2.5-r3", StringComparison.Ordinal) + "end\n",
            "zehos-active 2\n" + hello + musl + "end\n" + new string('x', PackageStore2.MaximumActiveBytes),
        ];
        foreach (string text in damaged)
        {
            File.WriteAllText(Path.Combine(Root, "v2", "active2"), text);
            var read = Store().ReadActive();
            Assert.IsFalse(read.IsOK(), text);
            Assert.IsNull(read.Value, text);
        }
        File.WriteAllText(Path.Combine(Root, "v2", "active2"), valid);
        Assert.AreEqual(Describe(MuslR1, Hello), Describe(Store()));
        // Auch ein zu grosses active2 verhindert die Reparatur durch eine neu gestufte Menge nicht.
        File.WriteAllText(Path.Combine(Root, "v2", "active2"), damaged[^1]);
        Install(Store(), MuslR2, Hello);
        Assert.AreEqual(Describe(MuslR2, Hello), Describe(Store()));
    }

    [TestMethod]
    public void ChangedStoredManifestOrSignatureLocksTheWholeSet()
    {
        var store = Store();
        Install(store, MuslR1, Hello);
        string manifest = Path.Combine(Root, "v2", "musl", "1.2.5-r1", "manifest");
        string signature = manifest + ".sig";
        byte[] original = File.ReadAllBytes(manifest), originalSignature = File.ReadAllBytes(signature);
        // Anders, aber gueltig signiert: Hash passt nicht zu active2.
        byte[] other = ManifestBytes(MuslR1 with { Provides = ["so:libother.so"] });
        File.WriteAllBytes(manifest, other);
        File.WriteAllBytes(signature, Sign(other));
        StringAssert.Contains(Store().ReadActive().Status.Message, "veraendert");
        // Fremder Schluessel.
        File.WriteAllBytes(manifest, original);
        File.WriteAllBytes(signature, Sign(original, OtherKey));
        StringAssert.Contains(Store().ReadActive().Status.Message, "Paketsignatur ungueltig");
        File.Delete(signature);
        Assert.IsFalse(Store().ReadActive().IsOK());
        File.WriteAllBytes(signature, originalSignature);
        Assert.AreEqual(Describe(MuslR1, Hello), Describe(Store()));
        // Eine veraenderte Inhaltsdatei aendert den Bestand nicht, sperrt aber den Start (VerifyFiles).
        File.WriteAllBytes(Path.Combine(Root, "v2", "musl", "1.2.5-r1", "files", "usr", "lib", "musl.so"), [9]);
        var active = Store().ReadActive().Value!;
        StringAssert.Contains(PackageStore2.VerifyFiles(active.Single(package => package.Manifest.Id == "musl"), CancellationToken.None).Message, "veraendert");
    }

    [TestMethod]
    public void SignatureIsCheckedBeforeTheManifestIsParsed()
    {
        // Ungueltiges Manifest mit passendem Katalog-Hash: Die Meldung muss von der Signatur kommen, nicht vom Parser.
        byte[] garbage = Encoding.UTF8.GetBytes("zehos-package 2\nid=musl\nkaputt\n");
        string directory = Path.Combine(Repo, "musl", "1.2.5-r1");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "manifest"), garbage);
        File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), new byte[64]);
        var key = new CatalogEntry2Key("musl", MuslR1.Parsed, PackageTrust.Sha256Hex(garbage));
        var staged = Store().Stage(Source(), key, CancellationToken.None);
        Assert.AreEqual("Paketsignatur ungueltig.", staged.Status.Message);
        // Mit gueltiger Signatur meldet erst der Parser.
        File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), Sign(garbage));
        StringAssert.StartsWith(Store().Stage(Source(), key, CancellationToken.None).Status.Message, "Ungueltiges Paketmanifest");

        // Dasselbe beim Lesen des Bestands: unsignierter Muell im Versionsordner, Hash in active2 passend.
        var store = Store();
        Install(store, MuslR1);
        File.WriteAllBytes(Path.Combine(Root, "v2", "musl", "1.2.5-r1", "manifest"), garbage);
        File.WriteAllText(Path.Combine(Root, "v2", "active2"), $"zehos-active 2\npackage=musl 1.2.5-r1 requested {PackageTrust.Sha256Hex(garbage)}\nend\n");
        string message = Store().ReadActive().Status.Message;
        StringAssert.Contains(message, "Paketsignatur ungueltig");
        Assert.IsFalse(message.Contains("Ungueltiges Paketmanifest", StringComparison.Ordinal), message);
    }

    [TestMethod]
    public void StageRejectsBeforeOrWhileWritingAndLeavesNoDirectory()
    {
        var store = Store();
        var key = Publish(MuslR1);
        string target = Path.Combine(Root, "v2", "musl", "1.2.5-r1");
        Assert.AreEqual("Manifest passt nicht zum signierten Katalog.",
            store.Stage(Source(), key with { ManifestSha256 = new string('0', 64) }, CancellationToken.None).Status.Message);
        Assert.IsFalse(store.Stage(Source(), key with { ManifestSha256 = "x" }, CancellationToken.None).IsOK());
        Assert.IsFalse(store.Stage(Source(), key with { Id = "Musl" }, CancellationToken.None).IsOK());
        // Ordner einer anderen Version liefert ein anderes, gueltig signiertes Manifest.
        var r2 = Publish(MuslR2);
        CopyTree(Path.Combine(Repo, "musl", "1.2.5-r2"), Path.Combine(Repo, "musl", "9.9.9-r1"));
        StringAssert.Contains(store.Stage(Source(), r2 with { Version = new PackageVersion2(new UpdateVersion(9, 9, 9), 1) }, CancellationToken.None).Status.Message, "andere");
        // min-kernel ueber dem laufenden Kernel: nichts geladen.
        var future = Publish(MuslR1 with { MinKernel = "2.0.0" });
        StringAssert.Contains(store.Stage(Source(), future, CancellationToken.None).Status.Message, "braucht ZehOS 2.0.0");
        Assert.IsFalse(Directory.Exists(target));
        // Veraenderter Inhalt auf dem Spiegel: Pruefsumme falsch, keine halbe Stufe.
        key = Publish(MuslR1);
        byte[] changed = Pattern(70000, MuslR1.Version);
        changed[69999] ^= 1;
        File.WriteAllBytes(Path.Combine(Repo, "musl", "1.2.5-r1", "files", "usr", "share", "musl", "big.dat"), changed);
        StringAssert.Contains(store.Stage(Source(), key, CancellationToken.None).Status.Message, "Pruefsumme falsch");
        Assert.IsFalse(Directory.Exists(target));
        // Falsche Laenge.
        File.WriteAllBytes(Path.Combine(Repo, "musl", "1.2.5-r1", "files", "usr", "share", "musl", "big.dat"), new byte[10]);
        Assert.IsFalse(store.Stage(Source(), key, CancellationToken.None).IsOK());
        Assert.IsFalse(Directory.Exists(target));
        // Abbruch.
        Publish(MuslR1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.AreEqual("Stufen abgebrochen.", store.Stage(Source(), key, cancelled.Token).Status.Message);
        Assert.IsFalse(Directory.Exists(target));
        Assert.IsTrue(store.Activate([Entry(MuslR1)], CancellationToken.None).Message.Contains("nicht gestuft", StringComparison.Ordinal));
        // Ein frueherer, halber Versuch derselben Version wird verworfen.
        Directory.CreateDirectory(Path.Combine(target, "files", "usr", "lib"));
        File.WriteAllBytes(Path.Combine(target, "files", "usr", "lib", "musl.so"), [7]);
        File.WriteAllBytes(Path.Combine(target, "files", "rest"), [7]);
        Install(store, MuslR1);
        Assert.IsFalse(File.Exists(Path.Combine(target, "files", "rest")));
        Assert.IsTrue(PackageStore2.VerifyFiles(store.ReadActive().Value!.Single(), CancellationToken.None).IsOK());
    }

    [TestMethod]
    public void StagingTheActiveVersionNeverRewritesIt()
    {
        var store = Store();
        Install(store, MuslR1);
        string file = Path.Combine(Root, "v2", "musl", "1.2.5-r1", "files", "usr", "lib", "musl.so");
        var stamp = File.GetLastWriteTimeUtc(file);
        int steps = 0;
        var counting = Store(step: _ => steps++);
        Assert.IsTrue(counting.Stage(Source(), Publish(MuslR1), CancellationToken.None).IsOK());
        Assert.AreEqual(1, steps, "Nur active2 wird abgeschlossen; die aktive Version wird nicht neu geschrieben.");
        Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(file));
        Assert.IsTrue(counting.Activate([Entry(MuslR1)], CancellationToken.None).IsOK());
        // Veraendert: kein Ueberschreiben an Ort und Stelle; der Bestand bleibt, der Start bleibt gesperrt.
        File.WriteAllBytes(file, [9]);
        StringAssert.Contains(Store().Stage(Source(), Publish(MuslR1), CancellationToken.None).Status.Message, "beschaedigt");
        CollectionAssert.AreEqual(new byte[] { 9 }, File.ReadAllBytes(file));
        Assert.AreEqual(Describe(MuslR1), Describe(Store()));
    }

    [TestMethod]
    public void ActivateChecksTheWholeSetBeforeSwitching()
    {
        var store = Store();
        Install(store, MuslR1);
        string before = ActiveText;
        void Rejected(string expected, Pkg[] stage, params Pkg[] set)
        {
            var fresh = Store();
            foreach (var pkg in stage)
            {
                var staged = fresh.Stage(Source(), Publish(pkg), CancellationToken.None);
                Assert.IsTrue(staged.IsOK(), staged.Status.Message);
            }
            var result = fresh.Activate(set.Select(pkg => Entry(pkg)).ToArray(), CancellationToken.None);
            Assert.IsFalse(result.IsOK(), expected);
            StringAssert.Contains(result.Message, expected);
            Assert.AreEqual(before, ActiveText, expected);
        }
        Rejected("nicht gestuft", [], MuslR2);
        Rejected("nicht in der Menge", [Hello], Hello);
        Rejected("braucht musl>=1.2.5-r2", [Hello with { Depends = ["musl>=1.2.5-r2"] }], MuslR1, Hello with { Depends = ["musl>=1.2.5-r2"] });
        var clash = new Pkg("clash", "1.0.0-r1", [("usr/lib/musl.so", [5], false)]);
        Rejected("liegt in", [clash], MuslR1, clash);
        var upper = new Pkg("upper", "1.0.0-r1", [("usr/lib/MUSL.so", [5], false)]);
        Rejected("liegt in", [upper], MuslR1, upper);
        var linkClash = new Pkg("linkclash", "1.0.0-r1", [("usr/bin/x", [5], false)]) { Links = [("usr/lib/musl.so", "../bin/x")] };
        Rejected("liegt in", [linkClash], MuslR1, linkClash);
        var nested = new Pkg("nested", "1.0.0-r1", [("usr/lib/musl.so/inner", [5], false)]);
        Rejected("ist Ordner von", [nested], MuslR1, nested);
        var parent = new Pkg("parent", "1.0.0-r1", [("usr/share", [5], false)]);
        Rejected("ist Ordner von", [parent], MuslR1, parent);
        var provider = new Pkg("provider", "1.0.0-r1", [("usr/lib/other.so", [5], false)]) { Provides = ["so:libc.so"] };
        Rejected("bereitgestellt", [provider], MuslR1, provider);
        // Kreis ueber zwei Pakete: jedes Manifest fuer sich ist gueltig.
        var left = new Pkg("left", "1.0.0-r1", [("usr/l/file", [1], false)]) { Links = [("usr/x", "y")] };
        var right = new Pkg("right", "1.0.0-r1", [("usr/r/file", [1], false)]) { Links = [("usr/y", "x")] };
        Rejected("sich selbst", [left, right], MuslR1, left, right);
        // Link ueber einen Link des anderen Pakets in den eigenen Ordner.
        var loopA = new Pkg("loopa", "1.0.0-r1", [("usr/a/file", [1], false)]) { Links = [("usr/a/up", "../b")] };
        var loopB = new Pkg("loopb", "1.0.0-r1", [("usr/c/file", [1], false)]) { Links = [("usr/b", "a/up/x")] };
        Rejected("sich selbst", [loopA, loopB], MuslR1, loopA, loopB);
        // Kette von 40 Links in hopsa (fuer sich gueltig: ab a01 folgen 39), hopsb verlaengert sie auf 41 Folgeschritte.
        var hopsA = new Pkg("hopsa", "1.0.0-r1", [("usr/ha/file", [1], false)])
        { Links = Enumerable.Range(1, 40).Select(index => ($"usr/c/a{index:D2}", $"a{index + 1:D2}")).ToArray() };
        var hopsB = new Pkg("hopsb", "1.0.0-r1", [("usr/hb/file", [1], false)]) { Links = [("usr/c/a41", "a42"), ("usr/c/a42", "a43")] };
        Rejected("Mehr als 40 Links", [hopsA, hopsB], MuslR1, hopsA, hopsB);
        Assert.IsTrue(Store().Stage(Source(), Publish(hopsA), CancellationToken.None).IsOK(), "40 Links in einem Paket sind erlaubt.");
        // Link durch einen Link des anderen Pakets: Ergebnis laenger als 200 Zeichen.
        var longA = new Pkg("longa", "1.0.0-r1", [("usr/la/file", [1], false)]) { Links = [("usr/t", "s/" + new string('z', 190))] };
        var longB = new Pkg("longb", "1.0.0-r1", [("usr/lb/file", [1], false)]) { Links = [("usr/s", new string('q', 20))] };
        Rejected("ueber 200 Zeichen", [longA, longB], MuslR1, longA, longB);
        // Rueckstufung gegen den aktiven Bestand, auch wenn die aeltere Version sauber gestuft ist.
        var upgraded = Store();
        Install(upgraded, MuslR2);
        before = ActiveText;
        Rejected("Rueckstufung", [MuslR1], MuslR1);
        // Ungueltige Mengen.
        Assert.AreEqual("Keine Paketmenge.", Store().Activate(null!, CancellationToken.None).Message);
        Assert.IsFalse(Store().Activate([Entry(MuslR2), Entry(MuslR2)], CancellationToken.None).IsOK());
        Assert.IsFalse(Store().Activate([("Bad", MuslR2.Parsed, true)], CancellationToken.None).IsOK());
        Assert.IsFalse(Store().Activate(Enumerable.Range(0, PackageStore2.MaximumPackages + 1).Select(index => ("p" + index, MuslR2.Parsed, true)).ToArray(), CancellationToken.None).IsOK());
        Assert.AreEqual(before, ActiveText);
        Assert.AreEqual(Describe(MuslR2), Describe(Store()));
    }

    [TestMethod]
    public void LinksIntoOtherPackagesAreAllowedWithoutCycle()
    {
        var store = Store();
        var tool = new Pkg("tool", "1.0.0-r1", [("usr/bin/tool", [1], true)]) { Links = [("usr/bin/libc", "../lib/musl.so")], Depends = ["musl>=1.2.5-r1"] };
        Install(store, MuslR1, tool);
        Assert.AreEqual(Describe(MuslR1, tool), Describe(store));
    }

    [TestMethod]
    public void StagedManifestChangedBeforeActivateIsRejected()
    {
        var store = Store();
        Assert.IsTrue(store.Stage(Source(), Publish(MuslR1), CancellationToken.None).IsOK());
        byte[] other = ManifestBytes(MuslR1 with { Provides = [] });
        File.WriteAllBytes(Path.Combine(Root, "v2", "musl", "1.2.5-r1", "manifest"), other);
        File.WriteAllBytes(Path.Combine(Root, "v2", "musl", "1.2.5-r1", "manifest.sig"), Sign(other));
        StringAssert.Contains(store.Activate([Entry(MuslR1)], CancellationToken.None).Message, "veraendert");
        Assert.AreEqual("", Describe(store));
    }

    [TestMethod]
    public void KernelDowngradeBlocksActivationOfNewerRequirement()
    {
        var needs = MuslR1 with { MinKernel = "0.9.0" };
        Install(Store(), needs);
        StringAssert.Contains(Store(kernel: new UpdateVersion(0, 5, 0)).Activate([Entry(needs)], CancellationToken.None).Message, "braucht ZehOS 0.9.0");
        Assert.AreEqual(Describe(needs), Describe(Store(kernel: new UpdateVersion(0, 5, 0))), "Lesen bleibt moeglich; den Start sperrt spaeter Acquire.");
    }

    [TestMethod]
    public void ReconcileRemovesOnlyInactiveVersionsAndKeepsV1()
    {
        WriteV1(Root);
        string v1 = TreeV1(Root);
        var store = Store();
        Install(store, MuslR1, Hello);
        Install(store, MuslR2, Hello);
        // Halbe Stufe und Fremdes: unbekannte Ordner bleiben, gueltige Versionsnamen ohne Eintrag gehen.
        Directory.CreateDirectory(Path.Combine(Root, "v2", "hello", "2.0.0-r1", "files"));
        Directory.CreateDirectory(Path.Combine(Root, "v2", "ghost", "1.0.0-r1"));
        Directory.CreateDirectory(Path.Combine(Root, "v2", "hello", "keep-me"));
        Directory.CreateDirectory(Path.Combine(Root, "v2", "Upper", "1.0.0-r1"));
        CollectionAssert.Contains(VersionDirectories(Root), "musl/1.2.5-r1", "Alte Versionen bleiben bis Reconcile.");
        Assert.IsTrue(Store().Reconcile().IsOK());
        CollectionAssert.AreEqual(new[] { "Upper/1.0.0-r1", "hello/1.0.0-r1", "hello/keep-me", "musl/1.2.5-r2" }, VersionDirectories(Root));
        Assert.IsFalse(Directory.Exists(Path.Combine(Root, "v2", "ghost")));
        Assert.AreEqual(Describe(MuslR2, Hello), Describe(Store()));
        Assert.AreEqual(v1, TreeV1(Root));

        // Unlesbares active2: nichts loeschen.
        Directory.CreateDirectory(Path.Combine(Root, "v2", "ghost", "1.0.0-r1"));
        File.WriteAllText(Path.Combine(Root, "v2", "active2"), "zehos-active 2\nkaputt\nend\n");
        Assert.IsFalse(Store().Reconcile().IsOK());
        Assert.IsTrue(Directory.Exists(Path.Combine(Root, "v2", "ghost", "1.0.0-r1")));
        Assert.AreEqual(v1, TreeV1(Root));
    }

    [TestMethod]
    public void HalfReplacedActiveFileIsReadLikeRestore()
    {
        var store = Store();
        Install(store, MuslR1);
        string active = Path.Combine(Root, "v2", "active2");
        string r1 = File.ReadAllText(active);
        Install(store, MuslR2);
        string r2 = File.ReadAllText(active);
        // Doppelfehler: alte Fassung als .old, neue als .new, Ziel fehlt. Die alte gewinnt, ohne dass Lesen etwas veraendert.
        File.Delete(active);
        File.WriteAllText(active + ".old", r1);
        File.WriteAllText(active + ".new", r2);
        Assert.AreEqual(Describe(MuslR1), Describe(Store()));
        Assert.IsTrue(File.Exists(active + ".old") && File.Exists(active + ".new") && !File.Exists(active));
        // Nach dem letzten Umbenennen, vor dem Loeschen der .old: das Ziel gilt, Reconcile entfernt die .old.
        File.WriteAllText(active, r2);
        Assert.AreEqual(Describe(MuslR2), Describe(Store()));
        File.Delete(active + ".new");
        Assert.IsTrue(Store().Reconcile().IsOK());
        Assert.IsFalse(File.Exists(active + ".old"));
        Assert.AreEqual(r2, File.ReadAllText(active));
        File.Delete(active);
        File.WriteAllText(active + ".new", r2);
        // Nur eine vollstaendige .new (erste Aktivierung): sie gilt.
        File.Delete(active + ".old");
        Assert.AreEqual(Describe(MuslR2), Describe(Store()));
        // Gekuerzte .new ohne Zeilenende wuerde Restore verwerfen.
        File.WriteAllText(active + ".new", r2[..^1]);
        Assert.AreEqual("", Describe(Store()));
        File.WriteAllText(active + ".new", r2);
        Assert.IsTrue(Store().Reconcile().IsOK());
        Assert.AreEqual(r2, File.ReadAllText(active));
        Assert.IsFalse(File.Exists(active + ".new"));
    }

    // Abnahme: Abbruch (Stromausfall) nach jedem Dateischritt. Der Zustand im Moment des Abbruchs wird kopiert; danach Neustart
    // mit neuer Instanz, Reconcile, Lesen: alte oder neue Menge, nie eine Mischung, nur aktive Versionen auf der Platte, v1 gleich.
    // Zuletzt laeuft die Operation erneut bis zum Ende.
    private void CrashAfterEveryStep(Action<PackageStore2> prepare, Action<PackageStore2> operation, string oldSet, string newSet, int minimumSteps)
    {
        string baseline = Path.Combine(work, "baseline");
        WriteV1(baseline);
        prepare(Store(baseline));
        string v1 = TreeV1(baseline);
        int crashes = 0;
        for (int crashAt = 1; ; crashAt++)
        {
            string live = Path.Combine(work, "live-" + crashAt), snapshot = Path.Combine(work, "crash-" + crashAt);
            CopyTree(baseline, live);
            int steps = 0;
            var store = Store(live, step: _ =>
            {
                if (++steps != crashAt) return;
                CopyTree(live, snapshot);
                throw new PowerLoss();
            });
            try { operation(store); }
            catch (PowerLoss) { }
            catch (AssertFailedException) when (Directory.Exists(snapshot)) { /* Operation meldete den Abbruch als Fehlerergebnis. */ }
            if (!Directory.Exists(snapshot))
            {
                Assert.IsTrue(crashAt - 1 >= minimumSteps, $"Nur {crashAt - 1} Dateischritte.");
                Assert.AreEqual(newSet, Describe(Store(live)));
                break;
            }
            crashes++;
            var rebooted = Store(snapshot);
            var reconciled = rebooted.Reconcile();
            Assert.IsTrue(reconciled.IsOK(), $"Schritt {crashAt}: {reconciled.Message}");
            string state = Describe(rebooted);
            Assert.IsTrue(state == oldSet || state == newSet, $"Schritt {crashAt}: Mischzustand {state}");
            var active = rebooted.ReadActive().Value!;
            foreach (var package in active) Assert.IsTrue(PackageStore2.VerifyFiles(package, CancellationToken.None).IsOK(), $"Schritt {crashAt}");
            CollectionAssert.AreEqual(active.Select(package => package.Manifest.Id + "/" + package.Manifest.Version).Order(StringComparer.Ordinal).ToArray(),
                VersionDirectories(snapshot), $"Schritt {crashAt}: nur aktive Versionen nach Reconcile");
            Assert.AreEqual(v1, TreeV1(snapshot), $"Schritt {crashAt}: v1 veraendert");
            // Wiederholung nach dem Abbruch.
            var retry = Store(snapshot);
            operation(retry);
            Assert.AreEqual(newSet, Describe(retry), $"Schritt {crashAt}: Wiederholung");
            Assert.AreEqual(v1, TreeV1(snapshot));
            Directory.Delete(snapshot, true);
            Directory.Delete(live, true);
        }
        Assert.IsTrue(crashes >= minimumSteps);
    }

    [TestMethod]
    public void CrashDuringStageAndActivateKeepsOldOrNewSet()
    {
        // Upgrade musl r1 -> r2 und neues Paket hello in einer Menge: Stufen (zwei Versionen) und ein Wechsel.
        CrashAfterEveryStep(
            prepare: store => Install(store, MuslR1),
            operation: store => Install(store, MuslR2, Hello),
            oldSet: Describe(MuslR1), newSet: Describe(MuslR2, Hello), minimumSteps: 15);
    }

    [TestMethod]
    public void CrashDuringFirstActivationKeepsEmptyOrNewSet()
    {
        CrashAfterEveryStep(prepare: _ => { }, operation: store => Install(store, MuslR1), oldSet: "", newSet: Describe(MuslR1), minimumSteps: 8);
    }

    [TestMethod]
    public void CrashDuringReconcileKeepsActiveSet()
    {
        CrashAfterEveryStep(
            prepare: store =>
            {
                Install(store, MuslR1, Hello);
                Install(store, MuslR2, Hello);
                // Unvollstaendige Stufe einer weiteren Version.
                Assert.IsTrue(store.Stage(Source(), Publish(Lib("extra", "1.0.0-r1")), CancellationToken.None).IsOK());
            },
            operation: store => Assert.IsTrue(store.Reconcile().IsOK()),
            oldSet: Describe(MuslR2, Hello), newSet: Describe(MuslR2, Hello), minimumSteps: 6);
    }

    // Volles Laufwerk und andere Schreibfehler: Der Fehler kommt als Ergebnis, die alte Menge gilt, eine Wiederholung gelingt.
    [TestMethod]
    public void WriteErrorAtEveryStepKeepsOldSetAndRetrySucceeds()
    {
        string baseline = Path.Combine(work, "baseline");
        WriteV1(baseline);
        Install(Store(baseline), MuslR1);
        string v1 = TreeV1(baseline);
        for (int failAt = 1; ; failAt++)
        {
            string live = Path.Combine(work, "full-" + failAt);
            CopyTree(baseline, live);
            int steps = 0;
            var store = Store(live, step: what =>
            {
                if (++steps == failAt) throw new IOException("Kein Platz auf dem Datentraeger (" + what + ").");
            });
            bool failed = false;
            foreach (var pkg in new[] { MuslR2, Hello })
                failed |= !store.Stage(Source(), Publish(pkg), CancellationToken.None).IsOK();
            if (!failed) failed = !store.Activate([Entry(MuslR2), Entry(Hello)], CancellationToken.None).IsOK();
            if (!failed)
            {
                Assert.IsTrue(failAt > 15);
                Assert.AreEqual(Describe(MuslR2, Hello), Describe(store));
                break;
            }
            Assert.AreEqual(Describe(MuslR1), Describe(store), $"Schritt {failAt}");
            Install(store, MuslR2, Hello);
            Assert.AreEqual(Describe(MuslR2, Hello), Describe(store), $"Schritt {failAt}: Wiederholung");
            Assert.IsTrue(store.Reconcile().IsOK());
            CollectionAssert.AreEqual(new[] { "hello/1.0.0-r1", "musl/1.2.5-r2" }, VersionDirectories(live), $"Schritt {failAt}");
            Assert.AreEqual(v1, TreeV1(live));
            Directory.Delete(live, true);
        }
    }

    [TestMethod]
    public void StageNeverDeletesAVersionWhileActiveFileIsUnreadable()
    {
        var store = Store();
        Install(store, MuslR1);
        string active = Path.Combine(Root, "v2", "active2");
        string valid = File.ReadAllText(active);
        string directory = Path.Combine(Root, "v2", "musl", "1.2.5-r1");
        // Voruebergehend unlesbar: Die vorhandene, intakte Version wird nur geprueft und als gestuft uebernommen.
        File.WriteAllText(active, valid + "kaputt\n");
        var steps = new List<string>();
        var staged = Store(step: steps.Add).Stage(Source(), Publish(MuslR1), CancellationToken.None);
        Assert.IsTrue(staged.IsOK(), staged.Status.Message);
        CollectionAssert.AreEqual(new[] { "active2 abschliessen" }, steps);
        // Nicht intakt: Fehler, nichts geloescht.
        string file = Path.Combine(directory, "files", "usr", "lib", "musl.so");
        File.WriteAllBytes(file, [9]);
        steps.Clear();
        var refused = Store(step: steps.Add).Stage(Source(), Publish(MuslR1), CancellationToken.None);
        StringAssert.Contains(refused.Status.Message, "nichts geloescht");
        CollectionAssert.AreEqual(new[] { "active2 abschliessen" }, steps);
        CollectionAssert.AreEqual(new byte[] { 9 }, File.ReadAllBytes(file));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "manifest")) && File.Exists(Path.Combine(directory, "manifest.sig")));
        File.WriteAllText(active, valid);
        Assert.AreEqual(Describe(MuslR1), Describe(Store()));
        // Ohne vorhandenen Ordner repariert eine neu gestufte Menge ein unlesbares active2 weiterhin.
        File.WriteAllText(active, valid + "kaputt\n");
        Install(Store(), MuslR2);
        Assert.AreEqual(Describe(MuslR2), Describe(Store()));
    }

    [TestMethod]
    public void StagingActiveVersionWithOtherCatalogManifestIsNotCalledDamaged()
    {
        Install(Store(), MuslR1);
        // Herausgeber hat dieselbe Version neu signiert (anderer Inhalt des Manifests).
        var message = Store().Stage(Source(), Publish(MuslR1 with { Provides = [] }), CancellationToken.None).Status.Message;
        StringAssert.Contains(message, "anderes Manifest");
        Assert.IsFalse(message.Contains("beschaedigt", StringComparison.Ordinal), message);
        Assert.AreEqual(Describe(MuslR1), Describe(Store()));
    }

    // Gegenfall aus dem Review von #688: 40er-Linkkette in einem 92 Ebenen tiefen Ordner, 20 Pakete mit je 4000 Links auf deren
    // Anfang. Alle Einzelgrenzen gelten; ohne Budget brauchte ReadActive 188 s und 64 GB Allokation.
    [TestMethod]
    public void HostileLinkChainsAcrossPackagesFailFastWithoutAllocationStorm()
    {
        string deep = "usr" + string.Concat(Enumerable.Repeat("/a", 91));
        var chain = new Pkg("chain", "1.0.0-r1", [("usr/chain.txt", [1], false)])
        { Links = Enumerable.Range(1, 40).Select(index => ($"{deep}/L{index:D2}", index == 40 ? "end" : $"L{index + 1:D2}")).ToArray() };
        var set = new List<Pkg> { chain };
        for (int package = 0; package < 20; package++)
            set.Add(new Pkg($"p{package:D2}", "1.0.0-r1", [($"usr/p{package:D2}.txt", [1], false)])
            { Links = Enumerable.Range(0, 4000).Select(index => ($"{deep}/p{package:D2}-{index:D4}", "L01")).ToArray() });
        var lines = new StringBuilder("zehos-active 2\n");
        foreach (var pkg in set.OrderBy(pkg => pkg.Id, StringComparer.Ordinal))
        {
            byte[] manifest = ManifestBytes(pkg);
            string directory = Path.Combine(Root, "v2", pkg.Id, pkg.Version);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "manifest"), manifest);
            File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), Sign(manifest));
            lines.Append(CultureInfo.InvariantCulture, $"package={pkg.Id} {pkg.Version} requested {PackageTrust.Sha256Hex(manifest)}\n");
        }
        File.WriteAllText(Path.Combine(Root, "v2", "active2"), lines.Append("end\n").ToString());
        var store = Store();
        // Vergleichswert: dieselben Manifeste erzeugen und parsen.
        long parseStart = GC.GetAllocatedBytesForCurrentThread();
        foreach (var pkg in set) Assert.IsTrue(PackageManifest2.Parse(ManifestBytes(pkg)).IsOK());
        long parseBytes = GC.GetAllocatedBytesForCurrentThread() - parseStart;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var read = store.ReadActive();
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        StringAssert.Contains(read.Status.Message, "zu aufwendig");
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"{watch.ElapsedMilliseconds} ms, {allocated / (1024 * 1024)} MB, parse {parseBytes / (1024 * 1024)} MB");
        // Ueber Erzeugen und Parsen der Manifeste hinaus hoechstens etwa 100 MB (vorher 64 GB).
        Assert.IsTrue(allocated - parseBytes < 100L * 1024 * 1024, $"{allocated / (1024 * 1024)} MB, Parsen allein {parseBytes / (1024 * 1024)} MB");
        StringAssert.Contains(store.Activate(set.Select(pkg => Entry(pkg)).ToArray(), CancellationToken.None).Message, "zu aufwendig");
    }

    [TestMethod]
    public void TooManyManifestBytesInOneSetAreRejected()
    {
        // 17 Pakete mit je knapp 1 MiB Manifest ueberschreiten die 16 MiB der Menge.
        var store = Store();
        var set = new List<Pkg>();
        for (int index = 0; index < 17; index++)
        {
            var files = Enumerable.Range(0, 4096).Select(file => ($"usr/p{index}/{file:D4}-{new string('x', 160)}", new byte[] { (byte)file }, false)).ToArray();
            var pkg = new Pkg("p" + index.ToString("D2", CultureInfo.InvariantCulture), "1.0.0-r1", files);
            Assert.IsTrue(ManifestBytes(pkg).Length is > 1_000_000 and <= PackageManifest2.MaximumBytes);
            set.Add(pkg);
        }
        // Stufen ist teuer (4096 Dateien je Paket); hier genuegt das direkte Ablegen der Versionen samt active2.
        var lines = new StringBuilder("zehos-active 2\n");
        foreach (var pkg in set)
        {
            byte[] manifest = ManifestBytes(pkg);
            string directory = Path.Combine(Root, "v2", pkg.Id, pkg.Version);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "manifest"), manifest);
            File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), Sign(manifest));
            lines.Append(CultureInfo.InvariantCulture, $"package={pkg.Id} {pkg.Version} requested {PackageTrust.Sha256Hex(manifest)}\n");
        }
        File.WriteAllText(Path.Combine(Root, "v2", "active2"), lines.Append("end\n").ToString());
        StringAssert.Contains(store.ReadActive().Status.Message, "16 MiB");
        StringAssert.Contains(store.Activate(set.Select(pkg => Entry(pkg)).ToArray(), CancellationToken.None).Message, "16 MiB");
    }

    [TestMethod]
    public void OperationsWaitForMigrationWriteGate()
    {
        var store = new PackageStore2(Root, Trust, Kernel, () => null);
        Assert.IsFalse(store.Stage(Source(), Publish(MuslR1), CancellationToken.None).IsOK());
        Assert.IsFalse(store.Reconcile().IsOK());
        Assert.IsFalse(Directory.Exists(Path.Combine(Root, "v2", "musl")));
    }
}
