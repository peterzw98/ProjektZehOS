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
using ProjektZeh.Shared;
using ProjektZeh.Update;

namespace ProjektZeh.Tests;

/// <summary>
/// Paketdienst v2 (N3-A) gegen ein lokales, mit Testschluessel signiertes Repository: Kette app -> libfoo -> musl, Katalog,
/// Installation, Upgrade, Entfernen und Fehlerfaelle. Nichts davon beruehrt ein echtes Repository oder den Branch packages.
/// </summary>
[TestClass]
public sealed class PackageService2Tests
{
    // Nur fuer Hosttests; nie in einen Kernelbuild uebernehmen.
    private static readonly Ed25519PrivateKeyParameters TestKey = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), 0);
    private static readonly Ed25519PrivateKeyParameters OtherKey = new(Enumerable.Range(50, 32).Select(i => (byte)i).ToArray(), 0);
    private static readonly PackageTrust Trust = new(TestKey.GeneratePublicKey().GetEncoded());
    private static readonly UpdateVersion Kernel = new(1, 0, 0);
    private string work = "";

    private sealed record Pkg(string Id, string Version, string[] Depends, string[] Provides)
    {
        public PackageVersion2 Parsed => PackageVersion2.TryParse(Version, out var value) ? value : throw new AssertFailedException(Version);
        public byte[] Content => Encoding.ASCII.GetBytes(Id + " " + Version + new string('x', 5000));
        public string FilePath => Provides.Length > 0 ? $"usr/lib/{Id}.so" : $"usr/bin/{Id}";
    }

    private static readonly Pkg MuslR1 = new("musl", "1.2.5-r1", [], ["so:libc.so"]);
    private static readonly Pkg MuslR2 = new("musl", "1.2.5-r2", [], ["so:libc.so"]);
    private static readonly Pkg FooR1 = new("libfoo", "1.0.0-r1", ["musl>=1.2.5-r1"], ["so:libfoo.so.1"]);
    private static readonly Pkg FooR2 = new("libfoo", "1.1.0-r1", ["musl>=1.2.5-r2"], ["so:libfoo.so.1"]);
    private static readonly Pkg AppR1 = new("app", "1.0.0-r1", ["libfoo>=1.0.0-r1"], []);
    private static readonly Pkg AppR2 = new("app", "2.0.0-r1", ["libfoo>=1.1.0-r1"], []);

    [TestInitialize]
    public void Create() => work = Directory.CreateTempSubdirectory("zehos-service2-").FullName;

    [TestCleanup]
    public void Delete() => Directory.Delete(work, true);

    private string Root => Path.Combine(work, "store");
    private string Repo => Path.Combine(work, "repo");
    private DirectoryPackageSource Source() => new(Repo);

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
        foreach (string dependency in pkg.Depends) text.Append("depends=").Append(dependency).Append('\n');
        foreach (string provides in pkg.Provides) text.Append("provides=").Append(provides).Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"{(pkg.Provides.Length > 0 ? "file" : "exec")}={PackageTrust.Sha256Hex(pkg.Content)} {pkg.Content.Length} {pkg.FilePath}\n");
        return Encoding.UTF8.GetBytes(text.Append("end\n").ToString());
    }

    // Legt die Version wie package2.py ab; key ersetzt den Signaturschluessel des Manifests (Signaturfehler).
    private void Publish(Pkg pkg, Ed25519PrivateKeyParameters? key = null)
    {
        string directory = Path.Combine(Repo, pkg.Id, pkg.Version);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        string path = Path.Combine(directory, "files", pkg.FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, pkg.Content);
        byte[] manifest = ManifestBytes(pkg);
        File.WriteAllBytes(Path.Combine(directory, "manifest"), manifest);
        File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), Sign(manifest, key));
    }

    private static byte[] CatalogBytes(long generation, params Pkg[] entries)
    {
        var text = new StringBuilder($"zehos-catalog 2\ngeneration={generation}\n");
        foreach (var pkg in entries.OrderBy(pkg => pkg.Id, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"package={pkg.Id} {pkg.Version} {PackageTrust.Sha256Hex(ManifestBytes(pkg))} {pkg.Content.Length}\n");
            foreach (string dependency in pkg.Depends) text.Append("depends=").Append(dependency).Append('\n');
            foreach (string provides in pkg.Provides) text.Append("provides=").Append(provides).Append('\n');
        }
        return Encoding.UTF8.GetBytes(text.Append("end\n").ToString());
    }

    // Katalog ins Testrepo; die Pakete selbst muessen nicht alle veroeffentlicht sein (fehlende Datei = Quelle scheitert).
    private void PublishCatalog(long generation, Pkg[] entries, Ed25519PrivateKeyParameters? key = null)
    {
        Directory.CreateDirectory(Repo);
        byte[] catalog = CatalogBytes(generation, entries);
        File.WriteAllBytes(Path.Combine(Repo, "catalog2"), catalog);
        File.WriteAllBytes(Path.Combine(Repo, "catalog2.sig"), Sign(catalog, key));
    }

    private PackageStore2 Store(Action<string>? step = null) => new(Root, Trust, Kernel) { FileStep = step };

    private PackageService2 Service(PackageStore2? store = null, Func<ResultObject<long>>? free = null, Action<string>? step = null) =>
        new(Root, store ?? Store(), Trust, free) { ServiceStep = step };

    private static string Describe(PackageStore2 store)
    {
        var active = store.ReadActive();
        Assert.IsTrue(active.IsOK(), active.Status.Message);
        return string.Join(",", active.Value!.Select(package => $"{package.Manifest.Id} {package.Manifest.Version} {(package.Requested ? "requested" : "auto")}"));
    }

    private static string[] VersionDirectories(string root) =>
        !Directory.Exists(Path.Combine(root, "v2")) ? [] :
        Directory.GetDirectories(Path.Combine(root, "v2")).SelectMany(Directory.GetDirectories)
            .Select(path => Path.GetRelativePath(Path.Combine(root, "v2"), path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    // Gemeinsamer Ausgangsstand: Kette r1 installiert, app angefordert.
    private PackageService2 InstallChain(PackageStore2 store)
    {
        foreach (var pkg in new[] { MuslR1, FooR1, AppR1 }) Publish(pkg);
        PublishCatalog(1, [MuslR1, FooR1, AppR1]);
        var service = Service(store);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        var installed = service.Install(Source(), ["app"], CancellationToken.None);
        Assert.IsTrue(installed.IsOK(), installed.Status.Message);
        return service;
    }

    private const string ChainR1 = "app 1.0.0-r1 requested,libfoo 1.0.0-r1 auto,musl 1.2.5-r1 auto";

    [TestMethod]
    public void InstallBringsWholeChainInDependencyOrderWithOneActivate()
    {
        var store = Store();
        var service = InstallChain(store);
        Assert.AreEqual(ChainR1, Describe(store));
        var again = service.Install(Source(), ["app"], CancellationToken.None);
        Assert.IsTrue(again.IsOK() && !again.Value!.Changed, "Zweite Installation ist nichts zu tun.");
        // Reihenfolge des Plans: Abhaengigkeiten zuerst.
        var fresh = new PackageService2(Path.Combine(work, "other"), new PackageStore2(Path.Combine(work, "other"), Trust, Kernel), Trust);
        Assert.IsTrue(fresh.Update(Source(), CancellationToken.None).IsOK());
        var plan = fresh.Install(Source(), ["app"], CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "musl", "libfoo", "app" }, plan.Value!.Staged.Select(entry => entry.Id).ToArray());
    }

    [TestMethod]
    public void InstallWithoutCatalogAsksForUpdate()
    {
        var result = Service().Install(Source(), ["app"], CancellationToken.None);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Status.Message, "pkg update");
    }

    [TestMethod]
    public void UpdateChecksSignatureBeforeParsingAndStoresNothingOnFailure()
    {
        PublishCatalog(1, [MuslR1], OtherKey);
        var service = Service();
        var result = service.Update(Source(), CancellationToken.None);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Status.Message, "Katalogsignatur");
        Assert.IsNull(service.ReadCatalog().Value);
        // Unsinn mit gueltiger Signatur kommt erst danach zum Parser und scheitert dort.
        Directory.CreateDirectory(Repo);
        byte[] junk = Encoding.ASCII.GetBytes("kein katalog\n");
        File.WriteAllBytes(Path.Combine(Repo, "catalog2"), junk);
        File.WriteAllBytes(Path.Combine(Repo, "catalog2.sig"), Sign(junk));
        Assert.AreEqual(ResultCode.InvalidArgument, service.Update(Source(), CancellationToken.None).Status.Code);
        Assert.IsNull(service.ReadCatalog().Value);
    }

    [TestMethod]
    public void UpdateRejectsGenerationRollbackAndChangedSameGeneration()
    {
        var service = Service();
        PublishCatalog(5, [MuslR1]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK(), "Gleiche Bytes derselben Generation sind nichts zu tun.");
        PublishCatalog(4, [MuslR2]);
        var older = service.Update(Source(), CancellationToken.None);
        Assert.IsFalse(older.IsOK());
        StringAssert.Contains(older.Status.Message, "4 < 5");
        PublishCatalog(5, [MuslR2]);
        Assert.IsFalse(service.Update(Source(), CancellationToken.None).IsOK(), "Anderer Inhalt bei gleicher Generation.");
        Assert.AreEqual("1.2.5-r1", service.ReadCatalog().Value!.Find("musl")!.Version.ToString());
        PublishCatalog(6, [MuslR2]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        Assert.AreEqual(6, service.ReadCatalog().Value!.Generation);
    }

    [TestMethod]
    public void RollbackStaysBlockedWhenOnlyOldCatalogSurvivedPowerLoss()
    {
        var service = Service();
        PublishCatalog(5, [MuslR1]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        string path = Path.Combine(Root, "v2", "catalog2.signed");
        File.Move(path, path + ".old");
        Assert.AreEqual(5, service.ReadCatalog().Value!.Generation, "Lesen nimmt die gesicherte Fassung.");
        Assert.IsTrue(File.Exists(path + ".old"), "Lesen veraendert nichts.");
        PublishCatalog(3, [MuslR1]);
        Assert.IsFalse(service.Update(Source(), CancellationToken.None).IsOK());
    }

    [TestMethod]
    public void TamperedStoredCatalogIsReportedAndCanBeReplaced()
    {
        var service = Service();
        PublishCatalog(2, [MuslR1]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        string path = Path.Combine(Root, "v2", "catalog2.signed");
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^3] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.IsFalse(service.ReadCatalog().IsOK());
        PublishCatalog(1, [MuslR1]);
        Assert.IsFalse(service.Update(Source(), CancellationToken.None).IsOK(), "Der Boden sperrt aeltere Kataloge auch bei beschaedigtem Katalog.");
        PublishCatalog(2, [MuslR2]);
        Assert.IsFalse(service.Update(Source(), CancellationToken.None).IsOK(), "Gleiche Generation mit anderem Inhalt bleibt gesperrt.");
        PublishCatalog(2, [MuslR1]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK(), "Derselbe Stand repariert den beschaedigten Katalog.");
        Assert.IsTrue(service.ReadCatalog().IsOK());
        PublishCatalog(3, [MuslR2]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
    }

    private string FloorFile => Path.Combine(Root, "v2", "catalog2.floor");
    private string CatalogFile => Path.Combine(Root, "v2", "catalog2.signed");

    [TestMethod]
    public void DamagedFloorFallsBackToCatalogAndBothDamagedFailClosed()
    {
        var service = Service();
        PublishCatalog(4, [MuslR1]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        Assert.IsTrue(PackageService2.TryParseFloor(File.ReadAllBytes(FloorFile), out long generation, out _));
        Assert.AreEqual(4, generation);
        byte[] floor = File.ReadAllBytes(FloorFile);
        floor[30] ^= 1;
        File.WriteAllBytes(FloorFile, floor);
        PublishCatalog(3, [MuslR1]);
        Assert.IsFalse(service.Update(Source(), CancellationToken.None).IsOK(), "Katalog traegt den Schutz, wenn der Boden kaputt ist.");
        PublishCatalog(4, [MuslR1]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK(), "Derselbe Stand schreibt den Boden neu.");
        Assert.IsTrue(PackageService2.TryParseFloor(File.ReadAllBytes(FloorFile), out _, out _));

        // Beide kaputt: kein bekannter Stand, aber etwas lag vor; nichts annehmen, auch nicht die neueste Fassung.
        File.WriteAllBytes(FloorFile, [1, 2, 3]);
        byte[] catalog = File.ReadAllBytes(CatalogFile);
        catalog[^3] ^= 1;
        File.WriteAllBytes(CatalogFile, catalog);
        PublishCatalog(1, [MuslR1]);
        var refused = service.Update(Source(), CancellationToken.None);
        Assert.IsFalse(refused.IsOK());
        StringAssert.Contains(refused.Status.Message, "Rueckstufungsschutz");
        PublishCatalog(9, [MuslR1]);
        Assert.IsFalse(service.Update(Source(), CancellationToken.None).IsOK(), "Fail closed gilt fuer jede Generation.");
        Assert.AreEqual(3, File.ReadAllBytes(FloorFile).Length, "Nichts geschrieben.");
    }

    [TestMethod]
    public void CatalogBelowFloorIsNotUsedAndCannotBeReaccepted()
    {
        var service = Service();
        PublishCatalog(2, [MuslR1]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        byte[] older = File.ReadAllBytes(CatalogFile);
        PublishCatalog(3, [MuslR2]);
        // Abbruch zwischen Boden und Katalog: Boden 3 liegt, der Katalog bleibt bei 2.
        var torn = Service(step: what => { if (what == "Katalog speichern") throw new IOException("Strom weg"); });
        Assert.IsFalse(torn.Update(Source(), CancellationToken.None).IsOK());
        CollectionAssert.AreEqual(older, File.ReadAllBytes(CatalogFile));
        var read = service.ReadCatalog();
        Assert.IsFalse(read.IsOK(), "Katalog unter dem Boden gilt nicht.");
        StringAssert.Contains(read.Status.Message, "Generationsboden");
        Assert.IsFalse(service.Install(Source(), ["musl"], CancellationToken.None).IsOK());
        PublishCatalog(2, [MuslR1]);
        Assert.IsFalse(service.Update(Source(), CancellationToken.None).IsOK(), "Alter Stand bleibt gesperrt.");
        PublishCatalog(3, [MuslR2]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK(), "Der Stand des Bodens geht durch.");
        Assert.AreEqual(3, service.ReadCatalog().Value!.Generation);
    }

    [TestMethod]
    public void FloorParserRejectsTruncatedAndForeignBytes()
    {
        var service = Service();
        PublishCatalog(7, [MuslR1]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        byte[] floor = File.ReadAllBytes(FloorFile);
        Assert.IsTrue(PackageService2.TryParseFloor(floor, out _, out _));
        Assert.IsFalse(PackageService2.TryParseFloor(floor[..^1], out _, out _));
        Assert.IsFalse(PackageService2.TryParseFloor([], out _, out _));
        string text = Encoding.ASCII.GetString(floor).Replace("generation=7", "generation=9", StringComparison.Ordinal);
        Assert.IsFalse(PackageService2.TryParseFloor(Encoding.ASCII.GetBytes(text), out _, out _), "Pruefsumme passt nicht mehr.");
    }

    [TestMethod]
    public void SecondJobWhileBusyIsRejectedAndLeavesNothing()
    {
        var store = Store();
        InstallChain(store);
        foreach (var pkg in new[] { MuslR2, FooR2, AppR2 }) Publish(pkg);
        PublishCatalog(2, [MuslR2, FooR2, AppR2]);
        PackageService2? service = null;
        var inner = new List<string>();
        service = Service(store, step: what =>
        {
            if (what != "stufen libfoo" && what != "Katalog speichern") return;
            inner.Add(service!.Update(Source(), CancellationToken.None).Status.Message);
            inner.Add(service.Remove("app", CancellationToken.None).Status.Message);
            inner.Add(service.Upgrade(Source(), CancellationToken.None).Status.Message);
        });
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        var upgraded = service.Upgrade(Source(), CancellationToken.None);
        Assert.IsTrue(upgraded.IsOK(), upgraded.Status.Message);
        Assert.AreEqual(6, inner.Count);
        foreach (string message in inner) StringAssert.Contains(message, "gerade einen Auftrag");
        Assert.AreEqual("app 2.0.0-r1 requested,libfoo 1.1.0-r1 auto,musl 1.2.5-r2 auto", Describe(store), "Nur der erste Auftrag wirkt.");
        Assert.IsTrue(service.Remove("app", CancellationToken.None).IsOK(), "Nach dem Ende ist der Dienst wieder frei.");
    }

    private sealed class CancelAfterFirstRead(IPackageSource inner, CancellationTokenSource cancel) : IPackageSource
    {
        public ResultObject<byte[]> Read(string path, int maximumBytes, CancellationToken cancellation)
        {
            var result = inner.Read(path, maximumBytes, cancellation);
            cancel.Cancel();
            return result;
        }

        public ResultErr Copy(string path, long size, Stream target, CancellationToken cancellation) => inner.Copy(path, size, target, cancellation);
    }

    [TestMethod]
    public void CancelDuringUpdateStoresNothingAndKeepsKnownCatalog()
    {
        var service = Service();
        PublishCatalog(1, [MuslR1]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        byte[] floor = File.ReadAllBytes(FloorFile), catalog = File.ReadAllBytes(CatalogFile);
        PublishCatalog(2, [MuslR2]);
        using var cancel = new CancellationTokenSource();
        var result = service.Update(new CancelAfterFirstRead(Source(), cancel), cancel.Token);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Status.Message, "abgebrochen");
        CollectionAssert.AreEqual(floor, File.ReadAllBytes(FloorFile));
        CollectionAssert.AreEqual(catalog, File.ReadAllBytes(CatalogFile));
        Assert.AreEqual(1, service.ReadCatalog().Value!.Generation);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK(), "Danach geht derselbe Auftrag durch.");
    }

    [TestMethod]
    public void RemoveOfMissingPackageChangesNothing()
    {
        var store = Store();
        var service = InstallChain(store);
        string active = File.ReadAllText(Path.Combine(Root, "v2", "active2"));
        var result = service.Remove("fehlt", CancellationToken.None);
        Assert.AreEqual(ResultCode.NotFound, result.Status.Code);
        StringAssert.Contains(result.Status.Message, "nicht installiert");
        Assert.AreEqual(active, File.ReadAllText(Path.Combine(Root, "v2", "active2")));
        Assert.AreEqual(ChainR1, Describe(store));
    }

    [TestMethod]
    public void MissingDependencyFailsBeforeAnythingIsStaged()
    {
        Publish(AppR1);
        PublishCatalog(1, [AppR1]);
        var service = Service();
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        var result = service.Install(Source(), ["app"], CancellationToken.None);
        Assert.AreEqual(ResultCode.NotFound, result.Status.Code);
        StringAssert.Contains(result.Status.Message, "libfoo");
        Assert.AreEqual(0, VersionDirectories(Root).Length);
    }

    [TestMethod]
    public void TooOldDependencyFailsBeforeAnythingIsStaged()
    {
        var oldFoo = FooR1 with { Version = "0.9.0-r1" };
        PublishCatalog(1, [MuslR1, oldFoo, AppR1]);
        var service = Service();
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        var result = service.Install(Source(), ["app"], CancellationToken.None);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Status.Message, "zu alt");
        Assert.AreEqual(0, VersionDirectories(Root).Length);
    }

    [TestMethod]
    public void ManifestSignatureErrorInChainKeepsOldSetAndActivatesNothing()
    {
        var store = Store();
        var service = InstallChain(store);
        Publish(MuslR2);
        Publish(FooR2, OtherKey);
        Publish(AppR2);
        PublishCatalog(2, [MuslR2, FooR2, AppR2]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        var result = service.Upgrade(Source(), CancellationToken.None);
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Status.Message, "Paketsignatur ungueltig");
        Assert.AreEqual(ChainR1, Describe(store), "musl r2 ist gestuft, aber nicht aktiv.");
        Assert.IsTrue(store.Reconcile().IsOK());
        Assert.AreEqual(ChainR1, Describe(new PackageStore2(Root, Trust, Kernel)));
    }

    [TestMethod]
    public void UpgradeSwitchesWholeChainAtOnceAndKeepsFlags()
    {
        var store = Store();
        var service = InstallChain(store);
        foreach (var pkg in new[] { MuslR2, FooR2, AppR2 }) Publish(pkg);
        PublishCatalog(2, [MuslR2, FooR2, AppR2]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
        var steps = new List<string>();
        var observed = new PackageService2(Root, store, Trust) { ServiceStep = steps.Add };
        var result = observed.Upgrade(Source(), CancellationToken.None);
        Assert.IsTrue(result.IsOK(), result.Status.Message);
        CollectionAssert.AreEqual(new[] { "stufen musl", "stufen libfoo", "stufen app", "aktivieren", "index" }, steps);
        Assert.AreEqual("app 2.0.0-r1 requested,libfoo 1.1.0-r1 auto,musl 1.2.5-r2 auto", Describe(store));
        Assert.IsFalse(observed.Upgrade(Source(), CancellationToken.None).Value!.Changed);
        Assert.IsTrue(new PackageStore2(Root, Trust, Kernel).Reconcile().IsOK());
        CollectionAssert.AreEqual(new[] { "app/2.0.0-r1", "libfoo/1.1.0-r1", "musl/1.2.5-r2" }, VersionDirectories(Root));
    }

    [TestMethod]
    public void UpgradeInterruptedAtActive2KeepsOldOrNewSetNeverMixed()
    {
        foreach (string failAt in new[] { "active2 ersetzen", "verschieben active2.wip -> active2.new", "verschieben active2 -> active2.old", "verschieben active2.new -> active2" })
        {
            Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            bool armed = false;
            var store = Store(what => { if (armed && what == failAt) throw new IOException("Strom weg bei " + what); });
            var service = InstallChain(store);
            foreach (var pkg in new[] { MuslR2, FooR2, AppR2 }) Publish(pkg);
            PublishCatalog(2, [MuslR2, FooR2, AppR2]);
            Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
            armed = true;
            Assert.IsFalse(service.Upgrade(Source(), CancellationToken.None).IsOK(), failAt);
            // Neustart: neue Instanz, Reconcile beim Einbinden, dann gilt genau eine vollstaendige Menge.
            var reopened = new PackageStore2(Root, Trust, Kernel);
            Assert.IsTrue(reopened.Reconcile().IsOK(), failAt);
            Assert.AreEqual(ChainR1, Describe(reopened), failAt);
            CollectionAssert.AreEqual(new[] { "app/1.0.0-r1", "libfoo/1.0.0-r1", "musl/1.2.5-r1" }, VersionDirectories(Root), failAt);
        }
    }

    [TestMethod]
    public void FullDiskIsDetectedBeforeStagingAndDuringStaging()
    {
        var store = Store();
        var service = InstallChain(store);
        foreach (var pkg in new[] { MuslR2, FooR2, AppR2 }) Publish(pkg);
        PublishCatalog(2, [MuslR2, FooR2, AppR2]);
        Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());

        var tight = Service(store, () => ResultObjectFactory.CreateSuccess(3 * PackageService2.OverheadBytesPerPackage));
        var early = tight.Upgrade(Source(), CancellationToken.None);
        Assert.IsFalse(early.IsOK());
        StringAssert.Contains(early.Status.Message, "Zu wenig Platz");
        CollectionAssert.AreEqual(new[] { "app/1.0.0-r1", "libfoo/1.0.0-r1", "musl/1.2.5-r1" }, VersionDirectories(Root), "Nichts geladen.");
        var unknown = Service(store, () => ResultObjectFactory.CreateFailure<long>(ResultCode.Failure, "statfs"));
        Assert.IsFalse(unknown.Upgrade(Source(), CancellationToken.None).IsOK());

        // Platz reicht laut Schaetzung, der Datentraeger laeuft aber beim zweiten Paket voll.
        var filling = Store(what => { if (what.StartsWith("Datei schreiben usr/lib/libfoo", StringComparison.Ordinal)) throw new IOException("Kein Platz auf dem Datentraeger."); });
        var full = Service(filling, () => ResultObjectFactory.CreateSuccess(long.MaxValue));
        var late = full.Upgrade(Source(), CancellationToken.None);
        Assert.IsFalse(late.IsOK());
        StringAssert.Contains(late.Status.Message, "libfoo");
        Assert.AreEqual(ChainR1, Describe(filling));
        Assert.IsFalse(VersionDirectories(Root).Contains("libfoo/1.1.0-r1"), "Halbe Stufe entfernt.");
    }

    [TestMethod]
    public void CancelBetweenStagesOrBeforeActivateKeepsOldSet()
    {
        foreach (string cancelAt in new[] { "stufen libfoo", "aktivieren" })
        {
            Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var store = Store();
            InstallChain(store);
            foreach (var pkg in new[] { MuslR2, FooR2, AppR2 }) Publish(pkg);
            PublishCatalog(2, [MuslR2, FooR2, AppR2]);
            using var cancel = new CancellationTokenSource();
            var service = Service(store, step: what => { if (what == cancelAt) cancel.Cancel(); });
            Assert.IsTrue(service.Update(Source(), CancellationToken.None).IsOK());
            var result = service.Upgrade(Source(), cancel.Token);
            Assert.IsFalse(result.IsOK(), cancelAt);
            StringAssert.Contains(result.Status.Message, "abgebrochen", cancelAt);
            Assert.AreEqual(ChainR1, Describe(store), cancelAt);
            Assert.IsTrue(service.Upgrade(Source(), CancellationToken.None).IsOK(), "Danach geht derselbe Auftrag durch: " + cancelAt);
        }
    }

    [TestMethod]
    public void RemoveProtectsDependenciesAndUnusedCleansAutomaticPackages()
    {
        var store = Store();
        var service = InstallChain(store);
        var blocked = service.Remove("libfoo", CancellationToken.None);
        Assert.IsFalse(blocked.IsOK());
        StringAssert.Contains(blocked.Status.Message, "app");
        Assert.AreEqual(ChainR1, Describe(store));
        Assert.AreEqual(ResultCode.NotFound, service.Remove("fehlt", CancellationToken.None).Status.Code);

        Assert.IsFalse(service.RemoveUnused(CancellationToken.None).Value!.Changed, "Alles wird gebraucht.");
        var removed = service.Remove("app", CancellationToken.None);
        Assert.IsTrue(removed.IsOK(), removed.Status.Message);
        Assert.AreEqual("libfoo 1.0.0-r1 auto,musl 1.2.5-r1 auto", Describe(store));
        var unused = service.RemoveUnused(CancellationToken.None);
        Assert.IsTrue(unused.IsOK(), unused.Status.Message);
        CollectionAssert.AreEqual(new[] { "libfoo", "musl" }, unused.Value!.Removed);
        Assert.AreEqual("", Describe(store));
        // Dateien bleiben bis zum Einbinden (N3-B zaehlt erst Nutzer); Reconcile entfernt sie dann.
        Assert.AreEqual(3, VersionDirectories(Root).Length);
        Assert.IsTrue(store.Reconcile().IsOK());
        Assert.AreEqual(0, VersionDirectories(Root).Length);
    }

    [TestMethod]
    public void ExplicitInstallOfAutomaticPackageMarksItRequested()
    {
        var store = Store();
        var service = InstallChain(store);
        var marked = service.Install(Source(), ["libfoo"], CancellationToken.None);
        Assert.IsTrue(marked.IsOK() && marked.Value!.Changed && marked.Value.Staged.Length == 0, marked.Status.Message);
        Assert.IsTrue(service.Remove("app", CancellationToken.None).IsOK());
        var unused = service.RemoveUnused(CancellationToken.None);
        CollectionAssert.AreEqual(Array.Empty<string>(), unused.Value!.Removed, "libfoo ist angefordert und braucht musl.");
        Assert.AreEqual("libfoo 1.0.0-r1 requested,musl 1.2.5-r1 auto", Describe(store));
    }

    [TestMethod]
    public void NotInCatalogAndWithoutTrustFailCleanly()
    {
        var store = Store();
        var service = InstallChain(store);
        Assert.AreEqual(ResultCode.NotFound, service.Install(Source(), ["fehlt"], CancellationToken.None).Status.Code);
        Assert.IsFalse(service.Install(Source(), [], CancellationToken.None).IsOK());
        var locked = new PackageService2(Root, new PackageStore2(Root, null, Kernel), null);
        Assert.IsFalse(locked.ReadCatalog().IsOK());
        Assert.IsFalse(locked.Update(Source(), CancellationToken.None).IsOK());
        Assert.IsFalse(locked.Install(Source(), ["app"], CancellationToken.None).IsOK());
        Assert.IsFalse(locked.RemoveUnused(CancellationToken.None).IsOK());
        Assert.AreEqual(ChainR1, Describe(store));
    }

    [TestMethod]
    public void V1StoreBesideV2StaysUsable()
    {
        // v1-Katalog und v2-Katalog liegen getrennt; der v1-Dienst sieht weder catalog2 noch den Bestand v2.
        var v1 = new PackageStore(Root, Trust, Kernel);
        byte[] catalog = Encoding.ASCII.GetBytes("zehos-catalog 1\ngeneration=3\n" +
            $"package=doom 1.0.0 {new string('a', 64)}\nend\n");
        Assert.IsTrue(v1.AcceptCatalog(catalog, Sign(catalog)).IsOK());
        var store = Store();
        var service = InstallChain(store);
        Assert.IsTrue(service.RemoveUnused(CancellationToken.None).IsOK());
        Assert.IsTrue(store.Reconcile().IsOK());
        var read = v1.ReadCatalog();
        Assert.IsTrue(read.IsOK(), read.Status.Message);
        Assert.AreEqual(3, read.Value!.Generation);
        Assert.IsTrue(v1.ReadInstalled().IsOK());
        Assert.IsTrue(v1.Reconcile().IsOK());
        Assert.AreEqual(ChainR1, Describe(store), "Reconcile v1 laesst v2 in Ruhe.");
    }
}
