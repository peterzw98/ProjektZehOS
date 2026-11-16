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

namespace ProjektZeh.Tests;

[TestClass]
public sealed class PackageTests
{
    // Nur fuer Hosttests; nie in einen Kernelbuild uebernehmen.
    private static readonly Ed25519PrivateKeyParameters TestKey = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), 0);
    private static readonly Ed25519PrivateKeyParameters OtherKey = new(Enumerable.Range(50, 32).Select(i => (byte)i).ToArray(), 0);
    private static readonly PackageTrust Trust = new(TestKey.GeneratePublicKey().GetEncoded());
    private string work = "";

    [TestInitialize]
    public void Create() => work = Directory.CreateTempSubdirectory("zehos-packages-").FullName;

    [TestCleanup]
    public void Delete() => Directory.Delete(work, true);

    private static byte[] Sign(byte[] content, Ed25519PrivateKeyParameters? key = null)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, key ?? TestKey);
        signer.BlockUpdate(content, 0, content.Length);
        return signer.GenerateSignature();
    }

    private static string Manifest(string id, string version, params (string Path, byte[] Content)[] files)
    {
        var text = new StringBuilder($"zehos-package 1\nid={id}\nversion={version}\nruntime=zehos-wasm32-v1\nname=Test {id}\nentry=app.wasm\n");
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            text.Append(CultureInfo.InvariantCulture, $"file={PackageTrust.Sha256Hex(file.Content)} {file.Content.Length} {file.Path}\n");
        return text.Append("end\n").ToString();
    }

    // Legt ein Paketverzeichnis wie im Repository an und liefert den Manifesthash.
    private static string Publish(string directory, string id, string version, params (string Path, byte[] Content)[] files)
    {
        files = files.Length == 0 ? [("app.wasm", Encoding.ASCII.GetBytes(id + version)), ("data/level.wad", new byte[70000])] : files;
        Directory.CreateDirectory(Path.Combine(directory, "files"));
        foreach (var file in files)
        {
            string path = Path.Combine(directory, "files", file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Content);
        }
        byte[] manifest = Encoding.UTF8.GetBytes(Manifest(id, version, files));
        File.WriteAllBytes(Path.Combine(directory, "manifest"), manifest);
        File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), Sign(manifest));
        return PackageTrust.Sha256Hex(manifest);
    }

    private static readonly ProjektZeh.Update.UpdateVersion Kernel = new(0, 3, 0);

    private PackageStore Store(ProjektZeh.Update.UpdateVersion? kernel = null) => new(Path.Combine(work, "store"), Trust, kernel ?? Kernel);

    // Ergaenzt ein veroeffentlichtes Manifest um die Mindest-Kernelversion, signiert neu und liefert den neuen Hash.
    private static string RequireKernel(string directory, string version)
    {
        string path = Path.Combine(directory, "manifest");
        byte[] manifest = Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("entry=app.wasm\n", $"entry=app.wasm\nmin-kernel={version}\n"));
        File.WriteAllBytes(path, manifest);
        File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), Sign(manifest));
        return PackageTrust.Sha256Hex(manifest);
    }

    private DirectoryPackageSource Source(string name) => new DirectoryPackageSource(Path.Combine(work, name));

    [TestMethod]
    public void ManifestParsesCanonicalForm()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Manifest("doom", "1.2.3", ("app.wasm", [1]), ("freedoom1.wad", [2, 3])));
        var parsed = PackageManifest.Parse(bytes);
        Assert.IsTrue(parsed.IsOK(), parsed.Status.Message);
        Assert.AreEqual("doom", parsed.Value!.Id);
        Assert.AreEqual("1.2.3", parsed.Value.Version.ToString());
        Assert.AreEqual(2, parsed.Value.Files.Length);
        Assert.AreEqual(2, parsed.Value.Files[1].Size);
        CollectionAssert.AreEqual(bytes, parsed.Value.Bytes);
    }

    [TestMethod]
    public void ManifestNetworkCapabilityIsExplicitAndCanonical()
    {
        string valid = Manifest("browser", "1.0.0", ("app.wasm", [1]));
        var noNetwork = PackageManifest.Parse(Encoding.UTF8.GetBytes(valid));
        Assert.IsTrue(noNetwork.IsOK());
        Assert.IsFalse(noNetwork.Value!.AllowsNetwork);
        string network = valid.Replace("entry=app.wasm\n", "entry=app.wasm\ncapability=net\n");
        var enabled = PackageManifest.Parse(Encoding.UTF8.GetBytes(network));
        Assert.IsTrue(enabled.IsOK(), enabled.Status.Message);
        Assert.IsTrue(enabled.Value!.AllowsNetwork);
        foreach (string invalid in new[]
        {
            network.Replace("capability=net", "capability=other"),
            network.Replace("capability=net\n", "capability=net\ncapability=net\n"),
            valid.Replace("end\n", "capability=net\nend\n"),
            "zehos-package 1\nid=x\nversion=1.0.0\nruntime=zehos-wasm32-v1\nname=X\nentry=app.wasm\ncapability=net\n"
        }) Assert.IsFalse(PackageManifest.Parse(Encoding.UTF8.GetBytes(invalid)).IsOK(), invalid);
    }

    [TestMethod]
    public void ManifestMinimumKernelIsOptionalAndCanonical()
    {
        string valid = Manifest("doom", "1.1.0", ("app.wasm", [1]));
        Assert.IsNull(PackageManifest.Parse(Encoding.UTF8.GetBytes(valid)).Value!.MinimumKernel);
        string required = valid.Replace("entry=app.wasm\n", "entry=app.wasm\nmin-kernel=0.3.0\n");
        var parsed = PackageManifest.Parse(Encoding.UTF8.GetBytes(required));
        Assert.IsTrue(parsed.IsOK(), parsed.Status.Message);
        Assert.AreEqual("0.3.0", parsed.Value!.MinimumKernel.ToString());
        Assert.IsFalse(parsed.Value.AllowsNetwork);
        string both = valid.Replace("entry=app.wasm\n", "entry=app.wasm\ncapability=net\nmin-kernel=0.3.0\n");
        var network = PackageManifest.Parse(Encoding.UTF8.GetBytes(both));
        Assert.IsTrue(network.IsOK(), network.Status.Message);
        Assert.IsTrue(network.Value!.AllowsNetwork);
        Assert.AreEqual("0.3.0", network.Value.MinimumKernel.ToString());
        foreach (string invalid in new[]
        {
            required.Replace("min-kernel=0.3.0", "min-kernel=0.3"),
            required.Replace("min-kernel=0.3.0", "min-kernel="),
            required.Replace("min-kernel=0.3.0", "min-kernel=v0.3.0"),
            required.Replace("min-kernel=0.3.0\n", "min-kernel=0.3.0\nmin-kernel=0.3.0\n"),
            valid.Replace("entry=app.wasm\n", "entry=app.wasm\nmin-kernel=0.3.0\ncapability=net\n"),
            valid.Replace("end\n", "min-kernel=0.3.0\nend\n"),
        }) Assert.IsFalse(PackageManifest.Parse(Encoding.UTF8.GetBytes(invalid)).IsOK(), invalid);
    }

    [TestMethod]
    public void ManifestRejectsAmbiguousOrUnsafeContent()
    {
        string valid = Manifest("doom", "1.0.0", ("app.wasm", [1]));
        string hash = PackageTrust.Sha256Hex([1]);
        string[] invalid =
        [
            valid.Replace("\n", "\r\n"), valid.TrimEnd('\n'), valid.Replace("end\n", ""), valid + "extra\n",
            valid.Replace("id=doom", "id=Doom"), valid.Replace("id=doom", "id=-doom"), valid.Replace("id=doom", "id=" + new string('a', 49)),
            valid.Replace("1.0.0", "1.0"), valid.Replace("1.0.0", "01.0.0"), valid.Replace("zehos-wasm32-v1", "zehos-wasm64-v1"),
            valid.Replace("entry=app.wasm", "entry=other.wasm"), valid.Replace("name=Test doom", "name="),
            valid.Replace($"file={hash} 1 app.wasm\n", $"file={hash} 1 app.wasm\nfile={hash} 1 App.wasm\n"),
            valid.Replace($"file={hash} 1 app.wasm\n", $"file={hash} 1 app.wasm\nfile={hash} 1 app.wasm\n"),
            valid.Replace($"file={hash} 1 app.wasm\n", $"file={hash} 1 app.wasm\nfile={hash} 1 a.dat\n"),
            valid.Replace("1 app.wasm", "01 app.wasm"), valid.Replace("1 app.wasm", "-1 app.wasm"), valid.Replace(hash, hash.ToUpperInvariant()),
        ];
        foreach (string text in invalid) Assert.IsFalse(PackageManifest.Parse(Encoding.UTF8.GetBytes(text)).IsOK(), text);
        foreach (string path in new[] { "", "/abs", "../x", "a/../b", "a//b", "a\\b", "c:x", "x.", "a/./b", "nul\0", "ä.wad", "a b" })
            Assert.IsFalse(PackageManifest.IsValidPath(path), path);
        Assert.IsTrue(PackageManifest.IsValidPath("data/level-1_a.wad"));
        Assert.IsFalse(PackageManifest.Parse([0xff, 0xfe]).IsOK());
        Assert.IsFalse(PackageManifest.Parse(new byte[PackageManifest.MaximumBytes + 1]).IsOK());
    }

    [TestMethod]
    public void ManifestEnforcesFileCountAndContentLimits()
    {
        var many = Enumerable.Range(0, PackageManifest.MaximumFiles).Select(i => ($"f{i:D3}.wasm", new byte[] { 1 })).ToArray();
        string atLimit = Manifest("many", "1.0.0", many).Replace("entry=app.wasm", "entry=f000.wasm");
        Assert.IsTrue(PackageManifest.Parse(Encoding.UTF8.GetBytes(atLimit)).IsOK());
        var tooMany = many.Append(("g.wasm", new byte[] { 1 })).ToArray();
        Assert.IsFalse(PackageManifest.Parse(Encoding.UTF8.GetBytes(Manifest("many", "1.0.0", tooMany).Replace("entry=app.wasm", "entry=f000.wasm"))).IsOK());

        string hash = new('a', 64);
        string Sized(long first, long second) =>
            $"zehos-package 1\nid=big\nversion=1.0.0\nruntime=zehos-wasm32-v1\nname=Big\nentry=a.wasm\nfile={hash} {first} a.wasm\nfile={hash} {second} b.dat\nend\n";
        Assert.IsTrue(PackageManifest.Parse(Encoding.UTF8.GetBytes(Sized(PackageManifest.MaximumContentBytes - 1, 1))).IsOK());
        Assert.IsFalse(PackageManifest.Parse(Encoding.UTF8.GetBytes(Sized(PackageManifest.MaximumContentBytes, 1))).IsOK());
        Assert.IsFalse(PackageManifest.Parse(Encoding.UTF8.GetBytes(Sized(9999999999, 9999999999))).IsOK());
    }

    [TestMethod]
    public void CatalogParsesAndRejectsDuplicates()
    {
        string hash = new('b', 64);
        var parsed = PackageCatalog.Parse(Encoding.UTF8.GetBytes($"zehos-catalog 1\ngeneration=7\npackage=doom 1.0.0 {hash}\nend\n"));
        Assert.IsTrue(parsed.IsOK(), parsed.Status.Message);
        Assert.AreEqual(7, parsed.Value!.Generation);
        Assert.AreEqual(hash, parsed.Value.Find("doom")!.ManifestSha256);
        Assert.IsNull(parsed.Value.Find("other"));
        string[] invalid =
        [
            $"zehos-catalog 1\ngeneration=0\nend\n", $"zehos-catalog 1\ngeneration=x\nend\n",
            $"zehos-catalog 1\ngeneration=1\npackage=doom 1.0.0 {hash}\npackage=doom 1.1.0 {hash}\nend\n",
            $"zehos-catalog 1\ngeneration=1\npackage=doom 1.0.0 {hash[1..]}\nend\n", $"zehos-catalog 2\ngeneration=1\nend\n",
            "zehos-catalog 1\ngeneration=1\n" + string.Concat(Enumerable.Range(0, 129).Select(i => $"package=p{i} 1.0.0 {hash}\n")) + "end\n",
        ];
        foreach (string text in invalid) Assert.IsFalse(PackageCatalog.Parse(Encoding.UTF8.GetBytes(text)).IsOK(), text);
        Assert.IsFalse(PackageCatalog.Parse(new byte[PackageCatalog.MaximumBytes + 1]).IsOK());
    }

    [TestMethod]
    public void SignatureCoversExactBytesAndKey()
    {
        byte[] content = Encoding.ASCII.GetBytes("zehos-catalog 1\n");
        byte[] signature = Sign(content);
        Assert.IsTrue(Trust.Verify(content, signature));
        Assert.IsFalse(Trust.Verify(content, Sign(content, OtherKey)));
        Assert.IsFalse(Trust.Verify(content, signature[..63]));
        content[0] ^= 1;
        Assert.IsFalse(Trust.Verify(content, signature));
    }

    [TestMethod]
    public void InstallsVerifiesAndRemovesPackage()
    {
        string hash = Publish(Path.Combine(work, "repo"), "doom", "1.0.0");
        var store = Store();
        var installed = store.Install(Source("repo"), "", hash, CancellationToken.None);
        Assert.IsTrue(installed.IsOK(), installed.Status.Message);
        var list = store.ReadInstalled();
        Assert.AreEqual("doom", list.Value!.Single().Manifest.Id);
        var package = store.Acquire("doom");
        Assert.IsTrue(package.IsOK());
        Assert.IsTrue(PackageStore.VerifyFiles(package.Value!, CancellationToken.None).IsOK());
        Assert.AreEqual(70000, new FileInfo(Path.Combine(package.Value!.FilesDirectory, "data", "level.wad")).Length);
        Assert.IsFalse(store.Remove("doom").IsOK(), "Laufende Version darf nicht entfernt werden.");
        store.Release("doom");
        Assert.IsTrue(store.Remove("doom").IsOK());
        Assert.AreEqual(0, store.ReadInstalled().Value!.Length);
        Assert.IsFalse(Directory.Exists(Path.Combine(work, "store", "apps", "doom")));
        Assert.AreEqual(ProjektZeh.Shared.ResultCode.NotFound, store.Acquire("doom").Status.Code);
    }

    [TestMethod]
    public void RejectsManipulatedOrMismatchedPackagesWithoutActivating()
    {
        string repo = Path.Combine(work, "repo");
        string hash = Publish(repo, "doom", "1.0.0");
        var store = Store();
        Assert.IsFalse(store.Install(Source("repo"), "", new string('0', 64), CancellationToken.None).IsOK(), "Katalogbindung");
        byte[] wad = File.ReadAllBytes(Path.Combine(repo, "files", "data", "level.wad"));
        wad[100] ^= 1;
        File.WriteAllBytes(Path.Combine(repo, "files", "data", "level.wad"), wad);
        var damaged = store.Install(Source("repo"), "", hash, CancellationToken.None);
        StringAssert.Contains(damaged.Status.Message, "Pruefsumme");
        Assert.AreEqual(0, store.ReadInstalled().Value!.Length);
        Assert.IsFalse(Directory.Exists(Path.Combine(work, "store", "apps", "doom", "1.0.0")));

        Publish(repo, "doom", "1.0.0");
        byte[] signature = File.ReadAllBytes(Path.Combine(repo, "manifest.sig"));
        signature[0] ^= 1;
        File.WriteAllBytes(Path.Combine(repo, "manifest.sig"), signature);
        StringAssert.Contains(store.Install(Source("repo"), "", null, CancellationToken.None).Status.Message, "Paketsignatur");
        File.Delete(Path.Combine(repo, "files", "app.wasm"));
        Assert.IsFalse(store.Install(Source("repo"), "", null, CancellationToken.None).IsOK());
        Assert.IsFalse(new PackageStore(Path.Combine(work, "store"), null, Kernel).ReadInstalled().IsOK(), "Ohne Schluessel keine Pakete.");
    }

    [TestMethod]
    public void RejectsPackageForNewerKernelWithoutTouchingInstalledVersion()
    {
        var store = Store();
        Assert.IsTrue(store.Install(Source("v1"), "", Publish(Path.Combine(work, "v1"), "doom", "1.0.0"), CancellationToken.None).IsOK());
        Publish(Path.Combine(work, "v2"), "doom", "1.1.0");
        string tooNew = RequireKernel(Path.Combine(work, "v2"), "0.3.1");
        var rejected = store.Install(Source("v2"), "", tooNew, CancellationToken.None);
        StringAssert.Contains(rejected.Status.Message, "braucht ZehOS 0.3.1");
        StringAssert.Contains(rejected.Status.Message, "0.3.0");
        Assert.AreEqual("1.0.0", store.ReadInstalled().Value!.Single().Manifest.Version.ToString());
        Assert.IsFalse(Directory.Exists(Path.Combine(work, "store", "apps", "doom", "1.1.0")), "Abgelehnte Version legt nichts an.");

        Publish(Path.Combine(work, "v2"), "doom", "1.1.0");
        string matching = RequireKernel(Path.Combine(work, "v2"), "0.3.0");
        Assert.IsTrue(store.Install(Source("v2"), "", matching, CancellationToken.None).IsOK(), "Gleiche Kernelversion genuegt.");
        Assert.AreEqual("0.3.0", store.ReadInstalled().Value!.Single().Manifest.MinimumKernel.ToString());
    }

    [TestMethod]
    public void InstalledPackageForNewerKernelDoesNotStartAfterKernelDowngrade()
    {
        Publish(Path.Combine(work, "repo"), "doom", "1.1.0");
        string hash = RequireKernel(Path.Combine(work, "repo"), "0.3.0");
        Assert.IsTrue(Store().Install(Source("repo"), "", hash, CancellationToken.None).IsOK());
        var older = Store(new ProjektZeh.Update.UpdateVersion(0, 2, 24));
        StringAssert.Contains(older.Acquire("doom").Status.Message, "braucht ZehOS 0.3.0");
        Assert.AreEqual(1, older.ReadInstalled().Value!.Length, "Das Paket bleibt sichtbar und entfernbar.");
        Assert.IsTrue(older.Remove("doom").IsOK(), "Ein abgelehnter Start haelt keine Sperre.");
    }

    [TestMethod]
    public void UpgradesButRejectsDowngradeReinstallAndChangesWhileRunning()
    {
        var store = Store();
        Assert.IsTrue(store.Install(Source("v1"), "", Publish(Path.Combine(work, "v1"), "doom", "1.0.0"), CancellationToken.None).IsOK());
        string v2 = Publish(Path.Combine(work, "v2"), "doom", "1.1.0");
        Assert.IsTrue(store.Acquire("doom").IsOK());
        StringAssert.Contains(store.Install(Source("v2"), "", v2, CancellationToken.None).Status.Message, "laeuft");
        store.Release("doom");
        Assert.IsTrue(store.Install(Source("v2"), "", v2, CancellationToken.None).IsOK());
        Assert.AreEqual("1.1.0", store.ReadInstalled().Value!.Single().Manifest.Version.ToString());
        Assert.IsFalse(Directory.Exists(Path.Combine(work, "store", "apps", "doom", "1.0.0")), "Alte Version wird entfernt.");
        StringAssert.Contains(store.Install(Source("v2"), "", v2, CancellationToken.None).Status.Message, "bereits installiert");
        StringAssert.Contains(store.Install(Source("v1"), "", null, CancellationToken.None).Status.Message, "Rueckstufung");
    }

    [TestMethod]
    public void DetectsFilesChangedAfterInstallAndDamagedMarkers()
    {
        var store = Store();
        Assert.IsTrue(store.Install(Source("repo"), "", Publish(Path.Combine(work, "repo"), "doom", "1.0.0"), CancellationToken.None).IsOK());
        var package = store.Acquire("doom").Value!;
        store.Release("doom");
        File.WriteAllBytes(Path.Combine(package.FilesDirectory, "app.wasm"), Encoding.ASCII.GetBytes("doom1.0.1"));
        StringAssert.Contains(PackageStore.VerifyFiles(package, CancellationToken.None).Message, "veraendert");

        File.WriteAllText(Path.Combine(work, "store", "apps", "doom", "active"), "9.9.9\n");
        StringAssert.Contains(store.Acquire("doom").Status.Message, "beschaedigt");
        Assert.AreEqual(0, store.ReadInstalled().Value!.Length);
        // Ein beschaedigtes Paket laesst sich durch eine gepruefte Neuinstallation ersetzen.
        Assert.IsTrue(store.Install(Source("repo"), "", null, CancellationToken.None).IsOK());
        Assert.IsTrue(store.Acquire("doom").IsOK());
    }

    [TestMethod]
    public void ReinstallOfSameVersionRepairsChangedFiles()
    {
        var store = Store();
        string hash = Publish(Path.Combine(work, "repo"), "doom", "1.0.0");
        Assert.IsTrue(store.Install(Source("repo"), "", hash, CancellationToken.None).IsOK());
        var package = store.Acquire("doom").Value!;
        store.Release("doom");
        File.WriteAllBytes(Path.Combine(package.FilesDirectory, "app.wasm"), Encoding.ASCII.GetBytes("kaputt"));
        var repaired = store.Install(Source("repo"), "", hash, CancellationToken.None);
        Assert.IsTrue(repaired.IsOK(), repaired.Status.Message);
        Assert.IsTrue(PackageStore.VerifyFiles(store.Acquire("doom").Value!, CancellationToken.None).IsOK());
        store.Release("doom");
        StringAssert.Contains(store.Install(Source("repo"), "", hash, CancellationToken.None).Status.Message, "bereits installiert");
    }

    [TestMethod]
    public void VerificationRunsOncePerVersionUntilChanged()
    {
        var store = Store();
        string hash = Publish(Path.Combine(work, "repo"), "doom", "1.0.0");
        Assert.IsTrue(store.Install(Source("repo"), "", hash, CancellationToken.None).IsOK());
        var package = store.Acquire("doom").Value!;
        store.Release("doom");
        // Nach der Installation gilt die Version als geprueft; spaetere Aenderungen sind nur ausserhalb des laufenden
        // Kernels moeglich und werden nach dem naechsten Neustart (neuer Store) wieder erkannt.
        File.WriteAllBytes(Path.Combine(package.FilesDirectory, "app.wasm"), Encoding.ASCII.GetBytes("kaputt"));
        Assert.IsTrue(store.VerifyOnce(package, CancellationToken.None).IsOK());
        var rebooted = Store();
        StringAssert.Contains(rebooted.VerifyOnce(package, CancellationToken.None).Message, "veraendert");
        StringAssert.Contains(rebooted.VerifyOnce(package, CancellationToken.None).Message, "veraendert");
    }

    [TestMethod]
    public void DowngradeStaysBlockedWhenInstalledPackageIsDamaged()
    {
        var store = Store();
        Assert.IsTrue(store.Install(Source("v2"), "", Publish(Path.Combine(work, "v2"), "doom", "1.1.0"), CancellationToken.None).IsOK());
        File.WriteAllBytes(Path.Combine(work, "store", "apps", "doom", "1.1.0", "manifest.sig"), new byte[64]);
        Publish(Path.Combine(work, "v1"), "doom", "1.0.0");
        StringAssert.Contains(store.Install(Source("v1"), "", null, CancellationToken.None).Status.Message, "Rueckstufung");
    }

    [TestMethod]
    public void ReconcileNeverDeletesVersionsBehindAnUnreadableMarker()
    {
        var store = Store();
        Assert.IsTrue(store.Install(Source("repo"), "", Publish(Path.Combine(work, "repo"), "doom", "1.0.0"), CancellationToken.None).IsOK());
        string apps = Path.Combine(work, "store", "apps", "doom");
        foreach (string marker in new[] { "1.0.0\r\n", "x", "9.9.9\n" })
        {
            File.WriteAllText(Path.Combine(apps, "active"), marker);
            Assert.IsTrue(store.Reconcile().IsOK());
            Assert.IsTrue(Directory.Exists(Path.Combine(apps, "1.0.0")), marker);
        }
    }

    [TestMethod]
    public void ReconcileFinishesReplacementAndRemovesUnpublishedVersions()
    {
        var store = Store();
        Assert.IsTrue(store.Install(Source("repo"), "", Publish(Path.Combine(work, "repo"), "doom", "1.0.0"), CancellationToken.None).IsOK());
        string apps = Path.Combine(work, "store", "apps");
        Directory.CreateDirectory(Path.Combine(apps, "doom", "2.0.0", "files"));
        Directory.CreateDirectory(Path.Combine(apps, "half", "1.0.0"));
        // Abbruch zwischen Loeschen des alten und Umbenennen des neuen Markers.
        File.Move(Path.Combine(apps, "doom", "active"), Path.Combine(apps, "doom", "active.new"));
        Assert.IsTrue(store.Reconcile().IsOK());
        Assert.IsTrue(store.Acquire("doom").IsOK());
        Assert.IsFalse(Directory.Exists(Path.Combine(apps, "doom", "2.0.0")));
        Assert.IsFalse(Directory.Exists(Path.Combine(apps, "half")));
    }

    [TestMethod]
    public void CatalogRequiresSignatureAndNonDecreasingGeneration()
    {
        var store = Store();
        byte[] Catalog(int generation) => Encoding.UTF8.GetBytes($"zehos-catalog 1\ngeneration={generation}\npackage=doom 1.0.0 {new string('c', 64)}\nend\n");
        Assert.IsNull(store.ReadCatalog().Value);
        Assert.IsFalse(store.AcceptCatalog(Catalog(5), Sign(Catalog(5), OtherKey)).IsOK());
        Assert.IsTrue(store.AcceptCatalog(Catalog(5), Sign(Catalog(5))).IsOK());
        StringAssert.Contains(store.AcceptCatalog(Catalog(4), Sign(Catalog(4))).Status.Message, "aelter");
        Assert.IsTrue(store.AcceptCatalog(Catalog(6), Sign(Catalog(6))).IsOK());
        Assert.AreEqual(6, store.ReadCatalog().Value!.Generation);
    }

    private sealed class Output : Shell.ITextOutput
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string text) { lock (Lines) Lines.Add(text); }
        public void Clear() { }
    }

    private static string Status(Shell.PackageShellCommands pkg)
    {
        string? result = null;
        Assert.IsTrue(SpinWait.SpinUntil(() => (result = pkg.TakeResult()) != null, 10000));
        return result!;
    }

    [TestMethod]
    public void CommandsInstallFromSignedCatalogAndUpgrade()
    {
        string repo = Path.Combine(work, "repo");
        string v1 = Publish(Path.Combine(repo, "doom", "1.0.0"), "doom", "1.0.0");
        void WriteCatalog(int generation, string version, string hash)
        {
            byte[] catalog = Encoding.UTF8.GetBytes($"zehos-catalog 1\ngeneration={generation}\npackage=doom {version} {hash}\nend\n");
            File.WriteAllBytes(Path.Combine(repo, "catalog"), catalog);
            File.WriteAllBytes(Path.Combine(repo, "catalog.sig"), Sign(catalog));
        }
        WriteCatalog(1, "1.0.0", v1);
        var store = Store();
        string? requested = null;
        var tasks = new Scheduling.BackgroundTaskService();
        var session = new Shell.PackageShellCommands(store, url => { requested = url; return new DirectoryPackageSource(repo); },
            path => ProjektZeh.Shared.ResultObjectFactory.CreateSuccess(path), tasks, () => true);
        var pkg = session.Commands.Single();
        var output = new Output();
        Assert.IsTrue(pkg.Execute(["install", "doom"], output).IsOK());
        Assert.AreEqual("doom 1.0.0 installiert.", Status(session));
        Assert.AreEqual(PackageStore.DefaultSource, requested);
        Assert.IsTrue(pkg.Execute(["remove", "doom"], output).IsOK());
        Assert.IsTrue(pkg.Execute(["source", "https://repo.test/zehos/"], output).IsOK());
        Assert.IsTrue(pkg.Execute(["install", "doom"], output).IsOK());
        Assert.AreEqual("doom 1.0.0 installiert.", Status(session));
        Assert.AreEqual("https://repo.test/zehos/", requested);

        WriteCatalog(2, "1.1.0", Publish(Path.Combine(repo, "doom", "1.1.0"), "doom", "1.1.0"));
        Assert.IsTrue(pkg.Execute(["upgrade"], output).IsOK());
        Assert.AreEqual("doom 1.1.0 aktualisiert.", Status(session));
        WriteCatalog(1, "1.0.0", v1);
        Assert.IsTrue(pkg.Execute(["update"], output).IsOK());
        StringAssert.Contains(Status(session), "aelter");
        File.Delete(Path.Combine(repo, "catalog"));
        Assert.IsTrue(pkg.Execute(["install", "doom"], output).IsOK());
        StringAssert.StartsWith(Status(session), "Katalog nicht geladen:");
        Assert.AreEqual(ProjektZeh.Shared.ResultCode.InvalidArgument, pkg.Execute(["status"], output).Code);
        Assert.IsTrue(pkg.Execute(["remove", "doom"], output).IsOK());
        Assert.AreEqual(0, store.ReadInstalled().Value!.Length);
        Assert.IsTrue(tasks.StopAll(2000));
    }

    [TestMethod]
    public void ChangingCommandsRequireAdministratorAndKey()
    {
        var output = new Output();
        var tasks = new Scheduling.BackgroundTaskService();
        var user = new Shell.PackageShellCommands(Store(), _ => throw new InvalidOperationException(),
            path => ProjektZeh.Shared.ResultObjectFactory.CreateSuccess(path), tasks, () => false).Commands.Single();
        foreach (string[] arguments in new[] { new[] { "install", "doom" }, ["install-local", "x"], ["remove", "doom"], ["update"], ["upgrade"], ["source", "https://a/"] })
            StringAssert.Contains(user.Execute(arguments, output).Message, "Administrator", string.Join(' ', arguments));
        Assert.IsTrue(user.Execute(["list"], output).IsOK());
        Assert.IsFalse(user.Execute(["bogus"], output).IsOK());
        var keyless = new Shell.PackageShellCommands(new PackageStore(Path.Combine(work, "store"), null, Kernel), _ => throw new InvalidOperationException(),
            path => ProjektZeh.Shared.ResultObjectFactory.CreateSuccess(path), tasks, () => true).Commands.Single();
        StringAssert.Contains(keyless.Execute(["install", "doom"], output).Message, "Paketschluessel");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ShellReportsTransferProgressBeforePublishingAndDeliversResultOnce(bool identityChanges)
    {
        string repo = Path.Combine(work, "live-progress");
        string hash = Publish(Path.Combine(repo, "doom", "1.0.0"), "doom", "1.0.0");
        byte[] catalog = Encoding.UTF8.GetBytes($"zehos-catalog 1\ngeneration=1\npackage=doom 1.0.0 {hash}\nend\n");
        File.WriteAllBytes(Path.Combine(repo, "catalog"), catalog);
        File.WriteAllBytes(Path.Combine(repo, "catalog.sig"), Sign(catalog));
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var tasks = new Scheduling.BackgroundTaskService();
        var store = Store();
        var source = new PausingPackageSource(repo, started, release);
        int generation = 0;
        var session = new Shell.PackageShellCommands(store, _ => source,
            path => ProjektZeh.Shared.ResultObjectFactory.CreateSuccess(path), tasks, () => true, getSessionGeneration: () => generation);
        try
        {
            Assert.IsTrue(session.Commands.Single().Execute(["install", "doom"], new Output()).IsOK());
            Assert.IsTrue(started.Wait(2000));
            if (identityChanges)
            {
                generation++;
                Assert.IsNull(session.TakeProgress(), "Fortschritt der alten Identitaet darf nicht erscheinen.");
                Assert.IsNull(session.TakeResult());
                release.Set();
                Assert.IsTrue(tasks.StopAll(2000));
                Assert.IsNull(session.TakeResult());
                Assert.IsNull(session.TakeProgress());
                return;
            }
            string? progress = session.TakeProgress();
            Assert.IsNotNull(progress);
            StringAssert.Contains(progress, "50%");
            StringAssert.Contains(progress, "35009/70009 B");
            StringAssert.Contains(progress, "level.wad");
            Assert.IsNull(session.TakeProgress());
            Assert.IsNull(session.TakeResult());
            Assert.IsNull(store.ReadActive("doom").Value);
            release.Set();
            string? result = null;
            Assert.IsTrue(SpinWait.SpinUntil(() => (result = session.TakeResult()) != null, 2000));
            Assert.AreEqual("doom 1.0.0 installiert.", result);
            Assert.IsNull(session.TakeResult());
            session.Close();
            Assert.IsNull(session.TakeProgress());
        }
        finally
        {
            release.Set();
            tasks.StopAll(2000);
        }
    }

    private sealed class PausingPackageSource(string directory, ManualResetEventSlim started, ManualResetEventSlim release) : IPackageSource
    {
        private readonly DirectoryPackageSource inner = new(directory);
        public ProjektZeh.Shared.ResultObject<byte[]> Read(string path, int maximumBytes, CancellationToken cancellation) =>
            inner.Read(path, maximumBytes, cancellation);
        public ProjektZeh.Shared.ResultErr Copy(string path, long size, Stream target, CancellationToken cancellation)
        {
            if (!path.EndsWith("level.wad", StringComparison.Ordinal)) return inner.Copy(path, size, target, cancellation);
            byte[] data = File.ReadAllBytes(Path.Combine(directory, path));
            int half = data.Length / 2;
            target.Write(data, 0, half);
            started.Set();
            release.Wait(cancellation);
            target.Write(data, half, data.Length - half);
            return ProjektZeh.Shared.ResultErr.CreateSuccess();
        }
    }
    [TestMethod]
    public void InstallationReportsBytesAndOnlyFinishesAfterVerification()
    {
        string repo = Path.Combine(work, "progress");
        Publish(repo, "doom", "1.0.0");
        var progress = new List<PackageProgress>();
        var result = Store().Install(new DirectoryPackageSource(repo), "", null, CancellationToken.None, progress.Add);
        Assert.IsTrue(result.IsOK(), result.Status.Message);
        var downloads = progress.Where(p => p.Phase == "Download").ToArray();
        Assert.IsTrue(downloads.Any(p => p.CompletedBytes > 0 && p.CompletedBytes < p.TotalBytes));
        Assert.IsTrue(downloads.All(p => p.CompletedBytes >= 0 && p.CompletedBytes <= p.TotalBytes));
        Assert.AreEqual("Fertig", progress[^1].Phase);
        Assert.AreEqual(70009L, progress[^1].TotalBytes);
        Assert.AreEqual(progress[^1].TotalBytes, progress[^1].CompletedBytes);
    }

    [TestMethod]
    public void CancelledDownloadReleasesFilesAndNeverReportsFinished()
    {
        string repo = Path.Combine(work, "cancel-progress");
        Publish(repo, "doom", "1.0.0");
        var store = Store();
        using var cancel = new CancellationTokenSource();
        var progress = new List<PackageProgress>();
        var result = store.Install(new DirectoryPackageSource(repo), "", null, cancel.Token, value =>
        {
            progress.Add(value);
            if (value.Phase == "Download" && value.CompletedBytes > 0) cancel.Cancel();
        });
        Assert.IsFalse(result.IsOK());
        Assert.IsNull(store.ReadActive("doom").Value);
        Assert.IsFalse(progress.Any(value => value.Phase == "Fertig"));
        using (File.Open(Path.Combine(repo, "files", "app.wasm"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.IsTrue(store.Install(new DirectoryPackageSource(repo), "", null, CancellationToken.None).IsOK());
    }
    [TestMethod]
    public void UnexpectedDownloadFailureClosesSourceFile()
    {
        string repo = Path.Combine(work, "throw-progress");
        Publish(repo, "doom", "1.0.0");
        Assert.IsFalse(Store().Install(new DirectoryPackageSource(repo), "", null, CancellationToken.None, value =>
        {
            if (value.Phase == "Download" && value.CompletedBytes > 0) throw new InvalidOperationException("Testfehler beim Schreiben");
        }).IsOK());
        using var file = File.Open(Path.Combine(repo, "files", "app.wasm"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    [TestMethod]
    public void InvalidPackageNeverReportsFinished()
    {
        string repo = Path.Combine(work, "bad-progress");
        Publish(repo, "doom", "1.0.0");
        File.WriteAllBytes(Path.Combine(repo, "files", "app.wasm"), new byte[9]);
        var progress = new List<PackageProgress>();
        Assert.IsFalse(Store().Install(new DirectoryPackageSource(repo), "", null, CancellationToken.None, progress.Add).IsOK());
        Assert.IsFalse(progress.Any(p => p.Phase == "Fertig"));
    }
    [TestMethod]
    public void MissingSourceUsesPublicRepositoryWithoutWritingConfiguration()
    {
        var store = Store();
        Assert.AreEqual("https://packages.pjoter.de/", store.ReadSource().Value);
        Assert.IsFalse(Directory.Exists(Path.Combine(work, "store")));
        Assert.IsTrue(store.WriteSource("https://custom.test/").IsOK());
        Assert.AreEqual("https://custom.test/", store.ReadSource().Value);
    }

    [TestMethod]
    public void SourceMustBeHttpsDirectory()
    {
        var store = Store();
        foreach (string url in new[] { "http://repo.example/", "https://repo.example", "https://user:pw@repo.example/", "https://repo.example/?a=1", "ftp://x/" })
            Assert.IsFalse(store.WriteSource(url).IsOK(), url);
        Assert.IsTrue(store.WriteSource("https://10.0.2.2:8443/zehos/").IsOK());
        Assert.AreEqual("https://10.0.2.2:8443/zehos/", store.ReadSource().Value);
    }
}
