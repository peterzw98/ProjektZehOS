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
/// Paketindex v2 (N3-B1): Aufloesung von Dateien, Ordnern und Links, Grossschreibung (FAT), Kreis, Wurzelausbruch, Wechsel nach
/// Activate, Neubau nach Neustart und die Shellbefehle <c>pkg native</c> gegen ein lokales Testrepo mit Testschluessel.
/// </summary>
[TestClass]
public sealed partial class PackageIndex2Tests
{
    // Nur fuer Hosttests; nie in einen Kernelbuild uebernehmen.
    private static readonly Ed25519PrivateKeyParameters TestKey = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), 0);
    private static readonly PackageTrust Trust = new(TestKey.GeneratePublicKey().GetEncoded());
    private static readonly UpdateVersion Kernel = new(1, 0, 0);
    private string work = "";

    [TestInitialize]
    public void Create() => work = Directory.CreateTempSubdirectory("zehos-index2-").FullName;

    [TestCleanup]
    public void Delete() => Directory.Delete(work, true);

    private string Root => Path.Combine(work, "store");
    private string Repo => Path.Combine(work, "repo");

    /// <summary>Testpaket: Dateien (Pfad, Inhalt, ausfuehrbar), Links (Pfad, Ziel), Abhaengigkeiten.</summary>
    private sealed record Pkg(string Id, string Version, (string Path, string Content, bool Exec)[] Files, (string Path, string Target)[] Links,
        string[] Depends)
    {
        public PackageVersion2 Parsed => PackageVersion2.TryParse(Version, out var value) ? value : throw new AssertFailedException(Version);
        public long Bytes => Files.Sum(file => (long)Encoding.ASCII.GetByteCount(file.Content));
    }

    private static readonly Pkg MuslR1 = new("musl", "1.2.5-r1",
        [("lib/ld-musl-x86_64.so.1", "musl r1", true)], [("usr/lib/libc.so", "../../lib/ld-musl-x86_64.so.1")], []);
    private static readonly Pkg MuslR2 = new("musl", "1.2.5-r2",
        [("lib/ld-musl-x86_64.so.1", "musl r2 neu", true)], [("usr/lib/libc.so", "../../lib/ld-musl-x86_64.so.1")], []);
    private static readonly Pkg FooR1 = new("libfoo", "1.0.0-r1",
        [("usr/lib/libfoo.so.1.0", "foo r1", false)], [("usr/lib/libfoo.so.1", "libfoo.so.1.0")], ["musl>=1.2.5-r1"]);
    // Ordnerlink: usr/lib64 zeigt auf usr/lib (Datei- und Ordnerlinks ueber Paketgrenzen).
    private static readonly Pkg AppR1 = new("app", "1.0.0-r1",
        [("usr/bin/app", "app r1", true), ("usr/share/app/readme", "lies", false)],
        [("usr/bin/app-link", "app"), ("usr/lib64", "lib")], ["libfoo>=1.0.0-r1"]);

    private static byte[] Sign(byte[] content)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, TestKey);
        signer.BlockUpdate(content, 0, content.Length);
        return signer.GenerateSignature();
    }

    private static byte[] ManifestBytes(Pkg pkg)
    {
        string[] parts = pkg.Version.Split("-r");
        var text = new StringBuilder($"zehos-package 2\nid={pkg.Id}\nversion={parts[0]}\nrelease={parts[1]}\nruntime=linux-x64-v1\nlicense=MIT\n");
        foreach (string dependency in pkg.Depends) text.Append("depends=").Append(dependency).Append('\n');
        foreach (var file in pkg.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            byte[] content = Encoding.ASCII.GetBytes(file.Content);
            text.Append(CultureInfo.InvariantCulture, $"{(file.Exec ? "exec" : "file")}={PackageTrust.Sha256Hex(content)} {content.Length} {file.Path}\n");
        }
        foreach (var link in pkg.Links.OrderBy(link => link.Path, StringComparer.Ordinal)) text.Append(CultureInfo.InvariantCulture, $"link={link.Path} {link.Target}\n");
        return Encoding.UTF8.GetBytes(text.Append("end\n").ToString());
    }

    private void Publish(Pkg pkg)
    {
        string directory = Path.Combine(Repo, pkg.Id, pkg.Version);
        foreach (var file in pkg.Files)
        {
            string path = Path.Combine(directory, "files", file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content, Encoding.ASCII);
        }
        byte[] manifest = ManifestBytes(pkg);
        File.WriteAllBytes(Path.Combine(directory, "manifest"), manifest);
        File.WriteAllBytes(Path.Combine(directory, "manifest.sig"), Sign(manifest));
    }

    private void PublishCatalog(long generation, params Pkg[] entries)
    {
        Directory.CreateDirectory(Repo);
        var text = new StringBuilder($"zehos-catalog 2\ngeneration={generation}\n");
        foreach (var pkg in entries.OrderBy(pkg => pkg.Id, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"package={pkg.Id} {pkg.Version} {PackageTrust.Sha256Hex(ManifestBytes(pkg))} {pkg.Bytes}\n");
            foreach (string dependency in pkg.Depends) text.Append("depends=").Append(dependency).Append('\n');
        }
        byte[] catalog = Encoding.UTF8.GetBytes(text.Append("end\n").ToString());
        File.WriteAllBytes(Path.Combine(Repo, "catalog2"), catalog);
        File.WriteAllBytes(Path.Combine(Repo, "catalog2.sig"), Sign(catalog));
    }

    private static ActivePackage2 Active(Pkg pkg, string directory = "/x")
    {
        var parsed = PackageManifest2.Parse(ManifestBytes(pkg));
        Assert.IsTrue(parsed.IsOK(), parsed.Status.Message);
        return new ActivePackage2(parsed.Value!, true, Path.Combine(directory, pkg.Id, pkg.Version));
    }

    private static PackageIndex2 Index(params Pkg[] set)
    {
        var built = PackageIndex2.Build([.. set.Select(pkg => Active(pkg))], 1);
        Assert.IsTrue(built.IsOK(), built.Status.Message);
        return built.Value!;
    }

    private (PackageStore2 Store, PackageService2 Service) Installed()
    {
        foreach (var pkg in new[] { MuslR1, FooR1, AppR1 }) Publish(pkg);
        PublishCatalog(1, MuslR1, FooR1, AppR1);
        var store = new PackageStore2(Root, Trust, Kernel);
        var service = new PackageService2(Root, store, Trust);
        Assert.IsTrue(service.Update(new DirectoryPackageSource(Repo), CancellationToken.None).IsOK());
        var installed = service.Install(new DirectoryPackageSource(Repo), ["app"], CancellationToken.None);
        Assert.IsTrue(installed.IsOK(), installed.Status.Message);
        return (store, service);
    }

    private static string Describe(ResultObject<PackageIndexEntry> entry) => !entry.IsOK() ? entry.Status.Code + ": " + entry.Status.Message
        : entry.Value!.Kind == PackageIndexKind.Directory ? "dir " + entry.Value.Path
        : $"{entry.Value.PackageId} {entry.Value.Version} {entry.Value.Path}{(entry.Value.Executable ? " exec" : "")}";

    [TestMethod]
    public void ResolvesFilesLinksAndDirectoryLinksAcrossPackages()
    {
        var index = Index(MuslR1, FooR1, AppR1);
        Assert.AreEqual("app 1.0.0-r1 usr/bin/app exec", Describe(index.Resolve("/usr/bin/app")));
        Assert.AreEqual("app 1.0.0-r1 usr/bin/app exec", Describe(index.Resolve("usr/bin/app-link")));
        Assert.AreEqual("musl 1.2.5-r1 lib/ld-musl-x86_64.so.1 exec", Describe(index.Resolve("/usr/lib/libc.so")));
        Assert.AreEqual("libfoo 1.0.0-r1 usr/lib/libfoo.so.1.0", Describe(index.Resolve("/usr/lib/libfoo.so.1")));
        // Ordnerlink des einen Pakets, Datei- und Dateilink eines anderen dahinter.
        Assert.AreEqual("musl 1.2.5-r1 lib/ld-musl-x86_64.so.1 exec", Describe(index.Resolve("/usr/lib64/libc.so")));
        Assert.AreEqual("dir usr/lib", Describe(index.Resolve("/usr/lib64")));
        Assert.AreEqual("dir usr", Describe(index.Resolve("/usr")));
        var entry = index.Resolve("/usr/lib/libfoo.so.1").Value!;
        Assert.AreEqual(Path.Combine("/x", "libfoo", "1.0.0-r1", "files", "usr", "lib", "libfoo.so.1.0"), entry.Location);
    }

    [TestMethod]
    public void LookupIgnoresAsciiCaseLikeFatAndKeepsManifestSpelling()
    {
        var index = Index(MuslR1, FooR1, AppR1);
        Assert.AreEqual("app 1.0.0-r1 usr/bin/app exec", Describe(index.Resolve("/USR/Bin/APP")));
        Assert.AreEqual("musl 1.2.5-r1 lib/ld-musl-x86_64.so.1 exec", Describe(index.Resolve("/Usr/LIB64/LIBC.SO")));
    }

    [TestMethod]
    public void MissingNonCanonicalAndFileAsDirectoryAreDistinctErrors()
    {
        var index = Index(MuslR1, FooR1, AppR1);
        Assert.AreEqual(ResultCode.NotFound, index.Resolve("/usr/bin/fehlt").Status.Code);
        Assert.AreEqual(ResultCode.NotFound, index.Resolve("/data/geheim").Status.Code);
        // Wurzelausbruch und nicht kanonische Formen werden abgewiesen, nicht textuell gekuerzt.
        foreach (string path in new[] { "", "/", "../etc/passwd", "/usr/lib64/../../data/x", "/usr/./bin/app", "usr//bin/app", "/usr/bin/app/",
                     "usr\\bin\\app", "usr/bin/app\0", "/" + new string('a', 201) })
            Assert.AreEqual(ResultCode.InvalidArgument, index.Resolve(path).Status.Code, path);
        var file = index.Resolve("/usr/bin/app/mehr");
        Assert.AreEqual(ResultCode.Failure, file.Status.Code);
        StringAssert.Contains(file.Status.Message, "kein Ordner");
    }

    [TestMethod]
    public void BuildRejectsDuplicateCaseAndPathBelowFile()
    {
        var upper = new Pkg("zwei", "1.0.0-r1", [("usr/BIN/App", "x", true)], [], []);
        var built = PackageIndex2.Build([Active(AppR1), Active(upper)], 1);
        Assert.IsFalse(built.IsOK());
        StringAssert.Contains(built.Status.Message, "doppelt");
        var below = new Pkg("drei", "1.0.0-r1", [("usr/bin/app/x", "x", false)], [], []);
        built = PackageIndex2.Build([Active(AppR1), Active(below)], 1);
        Assert.IsFalse(built.IsOK());
        StringAssert.Contains(built.Status.Message, "Ordner");
    }

    [TestMethod]
    public void LinkLoopEndsWithErrorInsteadOfHanging()
    {
        // Die Mengenpruefung von PackageStore2 verhindert Kreise; der Index bricht trotzdem nach 40 Schritten ab.
        // Der Parser lehnt einen Kreis in einem Paket ab, deshalb zwei Pakete mit je einem Link.
        var first = new Pkg("kreis-a", "1.0.0-r1", [("usr/share/kreis-a/x", "x", false)], [("usr/bin/a", "b")], []);
        var second = new Pkg("kreis-b", "1.0.0-r1", [("usr/share/kreis-b/x", "x", false)], [("usr/bin/b", "a")], []);
        var built = PackageIndex2.Build([Active(first), Active(second)], 1);
        Assert.IsTrue(built.IsOK(), built.Status.Message);
        var result = built.Value!.Resolve("/usr/bin/a");
        Assert.AreEqual(ResultCode.Failure, result.Status.Code);
        StringAssert.Contains(result.Status.Message, "40 Links");
        Assert.AreEqual(ResultCode.NotFound, Index(MuslR1).Resolve("/usr/lib64/libc.so").Status.Code, "Ohne app gibt es den Ordnerlink nicht.");
    }

    [TestMethod]
    public void FortyLinksInARowResolveAndFortyOneFail()
    {
        // Ein Link je Paket: Der Parser begrenzt Ketten nur innerhalb eines Manifests, ueber Paketgrenzen zaehlt der Index.
        // Dateilinks usr/bin/l0 -> l1 -> ... -> l40 -> ziel: ab l1 sind es 40 Links, ab l0 41.
        var set = new List<ActivePackage2> { Active(new Pkg("ziel", "1.0.0-r1", [("usr/bin/ziel", "z", true)], [], [])) };
        for (int link = 0; link <= 40; link++)
            set.Add(Active(new Pkg($"kette{link}", "1.0.0-r1", [($"usr/share/kette{link}/x", "x", false)],
                [($"usr/bin/l{link}", link == 40 ? "ziel" : $"l{link + 1}")], [])));
        // Ordnerlinks mitten im Pfad: usr/d0 -> d1 -> ... -> d40 -> ../lib/ziel, das letzte mit "../".
        set.Add(Active(new Pkg("zielordner", "1.0.0-r1", [("lib/ziel/datei", "d", false)], [], [])));
        for (int link = 0; link <= 40; link++)
            set.Add(Active(new Pkg($"ordner{link}", "1.0.0-r1", [($"usr/share/ordner{link}/x", "x", false)],
                [($"usr/d{link}", link == 40 ? "../lib/ziel" : $"d{link + 1}")], [])));
        var built = PackageIndex2.Build([.. set], 1);
        Assert.IsTrue(built.IsOK(), built.Status.Message);
        var index = built.Value!;

        Assert.AreEqual("ziel 1.0.0-r1 usr/bin/ziel exec", Describe(index.Resolve("/usr/bin/l1")));
        var tooMany = index.Resolve("/usr/bin/l0");
        Assert.AreEqual(ResultCode.Failure, tooMany.Status.Code);
        StringAssert.Contains(tooMany.Status.Message, "40 Links");

        Assert.AreEqual("zielordner 1.0.0-r1 lib/ziel/datei", Describe(index.Resolve("/usr/d40/datei")));
        Assert.AreEqual("zielordner 1.0.0-r1 lib/ziel/datei", Describe(index.Resolve("/usr/d1/datei")));
        Assert.AreEqual("dir lib/ziel", Describe(index.Resolve("/usr/d1")));
        tooMany = index.Resolve("/usr/d0/datei");
        Assert.AreEqual(ResultCode.Failure, tooMany.Status.Code);
        StringAssert.Contains(tooMany.Status.Message, "40 Links");
        // "../" des Ordnerlinks fuehrt aus usr/ heraus, aber nur bis zur Paketwurzel; dahinter gibt es nur Paketpfade.
        Assert.AreEqual(ResultCode.NotFound, index.Resolve("/usr/d40/fehlt").Status.Code);
    }

    [TestMethod]
    public void LinkAboveRootFailsBuildInsteadOfThrowing()
    {
        // Der Parser liesse diesen Link nie durch; der Index verlaesst sich nicht darauf (zweites "../" ohne Elternordner).
        foreach (string target in new[] { "../../../lib/x", "../../../../x" })
        {
            var package = Active(MuslR1);
            package.Manifest.Links[0] = new PackageLink("usr/lib/libc.so", target);
            var built = PackageIndex2.Build([package], 1);
            Assert.IsFalse(built.IsOK(), target);
            StringAssert.Contains(built.Status.Message, "Paketwurzel");
        }
        var root = Active(MuslR1);
        root.Manifest.Links[0] = new PackageLink("wurzel", "../x");
        Assert.IsFalse(PackageIndex2.Build([root], 1).IsOK());
    }

    [TestMethod]
    public void FailedIndexAfterActivateIsReportedAsFailure()
    {
        var (store, _) = Installed();
        // Zwischen Activate und Neubau wird ein verbleibendes Manifest beschaedigt: active2 ist neu, ein Index entsteht nicht.
        var service = new PackageService2(Root, store, Trust)
        {
            ServiceStep = step =>
            {
                if (step == "index") File.AppendAllText(Path.Combine(Root, "v2", "libfoo", "1.0.0-r1", "manifest"), "x");
            },
        };
        var removed = service.Remove("app", CancellationToken.None);
        Assert.IsFalse(removed.IsOK());
        StringAssert.Contains(removed.Status.Message, "Bestand gewechselt, aber kein Index");
        Assert.IsFalse(service.CurrentIndex().IsOK());
    }

    [TestMethod]
    public void ServiceSwitchesIndexOnlyAfterSuccessfulActivateAndOldStandStaysUsable()
    {
        var (store, service) = Installed();
        var first = service.CurrentIndex();
        Assert.IsTrue(first.IsOK(), first.Status.Message);
        var old = first.Value!;
        Assert.AreEqual("musl 1.2.5-r1 lib/ld-musl-x86_64.so.1 exec", Describe(old.Resolve("/usr/lib/libc.so")));
        Assert.IsTrue(PackageIndex2.Verify(old.Resolve("/usr/lib/libc.so").Value!, CancellationToken.None).IsOK());

        // Gescheitertes Upgrade (Version fehlt in der Quelle): Index bleibt derselbe Stand.
        PublishCatalog(2, MuslR2, FooR1, AppR1);
        var source = new DirectoryPackageSource(Repo);
        Assert.IsTrue(service.Update(source, CancellationToken.None).IsOK());
        Assert.IsFalse(service.Upgrade(source, CancellationToken.None).IsOK());
        Assert.AreSame(old, service.CurrentIndex().Value);

        Publish(MuslR2);
        var upgraded = service.Upgrade(source, CancellationToken.None);
        Assert.IsTrue(upgraded.IsOK(), upgraded.Status.Message);
        var current = service.CurrentIndex().Value!;
        Assert.IsTrue(current.Generation > old.Generation);
        Assert.AreEqual("musl 1.2.5-r2 lib/ld-musl-x86_64.so.1 exec", Describe(current.Resolve("/usr/lib64/libc.so")));
        // Wer den alten Stand hielt, sieht ihn weiter; seine Dateien liegen bis Reconcile (N3-B3: erst nach der letzten Nutzung).
        Assert.AreEqual("musl 1.2.5-r1 lib/ld-musl-x86_64.so.1 exec", Describe(old.Resolve("/usr/lib/libc.so")));
        Assert.IsTrue(PackageIndex2.Verify(old.Resolve("/usr/lib/libc.so").Value!, CancellationToken.None).IsOK());

        // Entfernen wechselt ebenfalls: app weg, Ordnerlink weg.
        Assert.IsTrue(service.Remove("app", CancellationToken.None).IsOK());
        Assert.AreEqual(ResultCode.NotFound, service.CurrentIndex().Value!.Resolve("/usr/bin/app").Status.Code);
        Assert.IsTrue(service.RemoveUnused(CancellationToken.None).IsOK());
        Assert.AreEqual(0, service.CurrentIndex().Value!.Packages.Length);
        Assert.IsTrue(store.Reconcile().IsOK());
    }

    [TestMethod]
    public void RestartRebuildsIndexFromActive2AndDamageGivesNoIndex()
    {
        Installed();
        // Neustart: neue Instanzen ohne Gedaechtnis lesen active2.
        var service = new PackageService2(Root, new PackageStore2(Root, Trust, Kernel), Trust);
        Assert.AreEqual("app 1.0.0-r1 usr/bin/app exec", Describe(service.CurrentIndex().Value!.Resolve("/usr/bin/app-link")));
        // Veraenderte Paketdatei: Aufloesung geht (Manifest gilt), die Pruefung vor dem Start nicht.
        string location = service.CurrentIndex().Value!.Resolve("/usr/bin/app").Value!.Location!;
        File.WriteAllText(location, "app r1", Encoding.ASCII);
        Assert.IsTrue(PackageIndex2.Verify(service.CurrentIndex().Value!.Resolve("/usr/bin/app").Value!, CancellationToken.None).IsOK());
        File.WriteAllText(location, "app r9", Encoding.ASCII);
        StringAssert.Contains(PackageIndex2.Verify(service.CurrentIndex().Value!.Resolve("/usr/bin/app").Value!, CancellationToken.None).Message, "veraendert");
        // Beschaedigtes Manifest: kein Index statt eines leeren Bestands.
        File.AppendAllText(Path.Combine(Root, "v2", "app", "1.0.0-r1", "manifest"), "x");
        Assert.IsFalse(service.RefreshIndex().IsOK());
        var none = service.CurrentIndex();
        Assert.IsFalse(none.IsOK());
        StringAssert.Contains(none.Status.Message, "Kein Paketindex v2");
    }

    [TestMethod]
    public void IndexWithoutTrustIsRefused()
    {
        var service = new PackageService2(Root, new PackageStore2(Root, null, Kernel), null);
        StringAssert.Contains(service.CurrentIndex().Status.Message, "Paketschluessel");
    }

    private sealed class Output : Shell.ITextOutput
    {
        public readonly List<string> Lines = [];
        public void Write(string text) => Lines.Add(text);
        public void WriteLine(string text = "") => Lines.Add(text);
        public void Clear() { }
    }

    private static string Result(Shell.PackageShellCommands pkg)
    {
        string? result = null;
        Assert.IsTrue(SpinWait.SpinUntil(() => (result = pkg.TakeResult()) != null, 10000));
        return result!;
    }

    [TestMethod]
    public void ShellNativeCommandsUpdateInstallResolveUpgradeAndRemove()
    {
        foreach (var package in new[] { MuslR1, FooR1, AppR1 }) Publish(package);
        PublishCatalog(1, MuslR1, FooR1, AppR1);
        var store = new PackageStore2(Root, Trust, Kernel);
        var service = new PackageService2(Root, store, Trust);
        var v1 = new PackageStore(Root, Trust, Kernel);
        var tasks = new Scheduling.BackgroundTaskService();
        var logged = new List<string>();
        var session = new Shell.PackageShellCommands(v1, _ => throw new InvalidOperationException("keine HTTPS-Quelle im Test"),
            path => ResultObjectFactory.CreateSuccess(path), tasks, () => true, logged.Add, null, service);
        var pkg = session.Commands.Single();
        var output = new Output();
        try
        {
            Assert.IsTrue(pkg.Execute(["native", "list"], output).IsOK());
            CollectionAssert.Contains(logged, "Noch kein Katalog v2 geladen; pkg native update.");
            Assert.IsTrue(pkg.Execute(["native", "update", "--from", Repo], output).IsOK());
            Assert.AreEqual("Katalog v2 geladen (Generation 1, 3 Pakete).", Result(session));
            Assert.IsTrue(pkg.Execute(["native", "install", "--from", Repo, "app"], output).IsOK());
            Assert.AreEqual("v2 installiert: musl 1.2.5-r1, libfoo 1.0.0-r1, app 1.0.0-r1.", Result(session));
            Assert.IsTrue(pkg.Execute(["native", "resolve", "/usr/lib64/libc.so"], output).IsOK());
            StringAssert.StartsWith(logged[^1], "v2 resolve lib/ld-musl-x86_64.so.1: musl 1.2.5-r1, 7 B, ausfuehrbar, Index ");
            Assert.AreEqual(ResultCode.NotFound, pkg.Execute(["native", "resolve", "/usr/bin/fehlt"], output).Code);
            Assert.AreEqual(ResultCode.InvalidArgument, pkg.Execute(["native", "resolve", "/usr/../data"], output).Code);

            Publish(MuslR2);
            PublishCatalog(2, MuslR2, FooR1, AppR1);
            Assert.IsTrue(pkg.Execute(["native", "update", "--from", Repo], output).IsOK());
            Assert.AreEqual("Katalog v2 geladen (Generation 2, 3 Pakete).", Result(session));
            logged.Clear();
            Assert.IsTrue(pkg.Execute(["native", "list"], output).IsOK());
            CollectionAssert.AreEqual(new[] { "v2 app 1.0.0-r1 angefordert", "v2 libfoo 1.0.0-r1 automatisch", "v2 musl 1.2.5-r1 automatisch, Update 1.2.5-r2" }, logged.Order(StringComparer.Ordinal).ToArray());
            Assert.IsTrue(pkg.Execute(["native", "upgrade", "--from", Repo], output).IsOK());
            Assert.AreEqual("v2 aktualisiert: musl 1.2.5-r2.", Result(session));
            Assert.IsTrue(pkg.Execute(["native", "resolve", "/usr/lib/libc.so"], output).IsOK());
            StringAssert.Contains(logged[^1], "musl 1.2.5-r2, 11 B");

            Assert.IsTrue(pkg.Execute(["native", "remove", "musl"], output).IsOK());
            StringAssert.Contains(Result(session), "libfoo");
            Assert.IsTrue(pkg.Execute(["native", "remove", "app"], output).IsOK());
            Assert.AreEqual("v2 entfernt: app.", Result(session));
            Assert.IsTrue(pkg.Execute(["native", "remove", "--unused"], output).IsOK());
            Assert.AreEqual("v2 entfernt: libfoo, musl.", Result(session));
            Assert.IsTrue(pkg.Execute(["native", "remove", "--unused"], output).IsOK());
            Assert.AreEqual("v2: nichts zu tun.", Result(session));
            // v1 bleibt unberuehrt.
            Assert.AreEqual(0, v1.ReadInstalled().Value!.Length);
        }
        finally { Assert.IsTrue(tasks.StopAll(2000)); }
    }

    [TestMethod]
    public void ShellNativeChecksArgumentsAndAdministrator()
    {
        var tasks = new Scheduling.BackgroundTaskService();
        var service = new PackageService2(Root, new PackageStore2(Root, Trust, Kernel), Trust);
        var user = new Shell.PackageShellCommands(new PackageStore(Root, Trust, Kernel), _ => throw new InvalidOperationException(),
            path => ResultObjectFactory.CreateSuccess(path), tasks, () => false, null, null, service).Commands.Single();
        var output = new Output();
        foreach (string[] arguments in new[] { new[] { "native", "update" }, ["native", "install", "app"], ["native", "upgrade"], ["native", "remove", "app"],
                     ["native", "remove", "--unused"] })
            StringAssert.Contains(user.Execute(arguments, output).Message, "Administrator", string.Join(' ', arguments));
        Assert.IsTrue(user.Execute(["native", "list"], output).IsOK());
        Assert.AreEqual(ResultCode.NotFound, user.Execute(["native", "resolve", "/usr/bin/app"], output).Code);
        foreach (string[] arguments in new[] { new[] { "native" }, ["native", "bogus"], ["native", "install"], ["native", "remove"],
                     ["native", "remove", "--from", "x", "app"], ["native", "update", "--from"], ["native", "resolve"] })
            Assert.AreEqual(ResultCode.InvalidArgument, user.Execute(arguments, output).Code, string.Join(' ', arguments));
        var admin = new Shell.PackageShellCommands(new PackageStore(Root, Trust, Kernel), _ => throw new InvalidOperationException(),
            path => ResultObjectFactory.CreateSuccess(path), tasks, () => true, null, null, service).Commands.Single();
        Assert.AreEqual(ResultCode.InvalidArgument, admin.Execute(["native", "install", "Ungueltig!"], output).Code);
        // Ohne Dienst v2 kennt pkg den Unterbefehl nicht.
        var plain = new Shell.PackageShellCommands(new PackageStore(Root, Trust, Kernel), _ => throw new InvalidOperationException(),
            path => ResultObjectFactory.CreateSuccess(path), tasks, () => true).Commands.Single();
        Assert.AreEqual(ResultCode.InvalidArgument, plain.Execute(["native", "list"], output).Code);
        Assert.IsTrue(tasks.StopAll(2000));
    }
}
