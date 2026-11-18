using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Packages;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WasmEngineSelectionTests
{
    private static readonly byte[] Aot = [0x00, 0x61, 0x6f, 0x74];

    [TestMethod]
    public void AotRunsOnlyWithLoaderBytesAndWithoutBudget()
    {
        Assert.AreEqual(WasmEngine.Aot, WasmEngineSelection.Select(aotBuiltIn: true, Aot, gas: 0));
    }

    [TestMethod]
    public void WithoutAotBytesEverythingStaysWithWasm3()
    {
        Assert.AreEqual(WasmEngine.Wasm3, WasmEngineSelection.Select(aotBuiltIn: true, null, gas: 0));
        Assert.AreEqual(WasmEngine.Wasm3, WasmEngineSelection.Select(aotBuiltIn: true, [], gas: 0));
    }

    [TestMethod]
    public void KernelWithoutWamrIgnoresAotBytes()
    {
        Assert.AreEqual(WasmEngine.Wasm3, WasmEngineSelection.Select(aotBuiltIn: false, Aot, gas: 0));
    }

    [TestMethod]
    public void GasBudgetKeepsTheRunOnWasm3()
    {
        // AOT-Code misst kein Gas; ein Lauf mit Budget (Probe, Netzwerkprobe) darf es nicht verlieren.
        Assert.AreEqual(WasmEngine.Wasm3, WasmEngineSelection.Select(aotBuiltIn: true, Aot, gas: 1));
        Assert.AreEqual(WasmEngine.Wasm3, WasmEngineSelection.Select(aotBuiltIn: true, Aot, gas: ulong.MaxValue));
    }

    private const string OwnAot = "app." + WasmEngineSelection.AotAbi + ".aot";
    private static readonly byte[] Wasm = [0x00, 0x61, 0x73, 0x6d];

    private static PackageManifest Manifest(params (string Path, byte[] Content)[] files)
    {
        var text = new StringBuilder("zehos-package 1\nid=app\nversion=1.0.0\nruntime=zehos-wasm32-v1\nname=App\nentry=app.wasm\n");
        foreach (var file in files.Append((Path: "app.wasm", Content: Wasm)).OrderBy(file => file.Path, StringComparer.Ordinal))
            text.Append(CultureInfo.InvariantCulture, $"file={PackageTrust.Sha256Hex(file.Content)} {file.Content.Length} {file.Path}\n");
        var parsed = PackageManifest.Parse(Encoding.UTF8.GetBytes(text.Append("end\n").ToString()));
        Assert.IsTrue(parsed.IsOK(), parsed.Status.Message);
        return parsed.Value!;
    }

    [TestMethod]
    public void AotFileNameFollowsEntryAndAbi()
    {
        Assert.AreEqual("app.wamr245-aot5-x64-bc-nrz.aot", WasmEngineSelection.AotPath("app.wasm"));
        Assert.AreEqual("bin/doom.wamr245-aot5-x64-bc-nrz.aot", WasmEngineSelection.AotPath("bin/doom.wasm"));
    }

    [TestMethod]
    public void MatchingAbiIsFoundOnlyWhenBuiltInAndEnabled()
    {
        var manifest = Manifest((OwnAot, Aot));
        Assert.AreEqual(OwnAot, WasmEngineSelection.FindAot(manifest, aotBuiltIn: true, enabled: true)?.Path);
        Assert.IsNull(WasmEngineSelection.FindAot(manifest, aotBuiltIn: true, enabled: false));
        Assert.IsNull(WasmEngineSelection.FindAot(manifest, aotBuiltIn: false, enabled: true));
    }

    [TestMethod]
    public void ForeignAbiOrMissingFileStaysWithWasm3()
    {
        // Ein .aot anderer Kennung (anderes WAMR oder andere Pruefschalter) ist fuer diesen Kernel nur Daten.
        Assert.IsNull(WasmEngineSelection.FindAot(Manifest(("app.wamr999-aot6-x64-bc.aot", Aot)), aotBuiltIn: true, enabled: true));
        Assert.IsNull(WasmEngineSelection.FindAot(Manifest(("other." + WasmEngineSelection.AotAbi + ".aot", Aot)), aotBuiltIn: true, enabled: true));
        Assert.IsNull(WasmEngineSelection.FindAot(Manifest(), aotBuiltIn: true, enabled: true));
    }

    [TestMethod]
    public void ReadAotReturnsExactlyTheVerifiedBytes()
    {
        string directory = Directory.CreateTempSubdirectory("zehos-aot-").FullName;
        try
        {
            var file = Manifest((OwnAot, Aot)).Files.Single(entry => entry.Path == OwnAot);
            File.WriteAllBytes(Path.Combine(directory, OwnAot), Aot);
            var read = WasmEngineSelection.ReadAot(directory, file);
            Assert.IsTrue(read.IsOK(), read.Status.Message);
            CollectionAssert.AreEqual(Aot, read.Value);

            // Gleiche Laenge, anderer Inhalt: nur der Hash der gelesenen Bytes faellt auf.
            File.WriteAllBytes(Path.Combine(directory, OwnAot), [0x00, 0x61, 0x6f, 0x75]);
            var modified = WasmEngineSelection.ReadAot(directory, file);
            Assert.IsFalse(modified.IsOK());
            StringAssert.Contains(modified.Status.Message, "veraendert");

            File.WriteAllBytes(Path.Combine(directory, OwnAot), [.. Aot, 0]);
            Assert.IsFalse(WasmEngineSelection.ReadAot(directory, file).IsOK());

            File.Delete(Path.Combine(directory, OwnAot));
            Assert.IsFalse(WasmEngineSelection.ReadAot(directory, file).IsOK());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void EmptyOrOversizedAotFallsBackToWasm3()
    {
        // Die Groesse steht im signierten Manifest; ausserhalb der Grenze wird das .aot gar nicht erst gelesen.
        string hash = new('a', 64);
        string Manifest(long size) =>
            $"zehos-package 1\nid=app\nversion=1.0.0\nruntime=zehos-wasm32-v1\nname=App\nentry=app.wasm\nfile={hash} {size} {OwnAot}\nfile={hash} 4 app.wasm\nend\n";
        foreach (long size in new[] { 0L, WasmEngineSelection.MaximumAotBytes + 1L })
        {
            var parsed = PackageManifest.Parse(Encoding.UTF8.GetBytes(Manifest(size)));
            Assert.IsTrue(parsed.IsOK(), parsed.Status.Message);
            Assert.IsNull(WasmEngineSelection.FindAot(parsed.Value!, aotBuiltIn: true, enabled: true), size.ToString(CultureInfo.InvariantCulture));
        }
        var limit = PackageManifest.Parse(Encoding.UTF8.GetBytes(Manifest(WasmEngineSelection.MaximumAotBytes)));
        Assert.IsNotNull(WasmEngineSelection.FindAot(limit.Value!, aotBuiltIn: true, enabled: true));
    }

    [TestMethod]
    public void ReadAotRejectsInvalidSizesWithoutReading()
    {
        // Der Ordner existiert nicht: ein Lesezugriff ergaebe "nicht lesbar" statt der Groessenmeldung.
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        foreach (long size in new[] { 0L, WasmEngineSelection.MaximumAotBytes + 1L })
        {
            var read = WasmEngineSelection.ReadAot(missing, new PackageFile(OwnAot, size, new string('a', 64)));
            Assert.IsFalse(read.IsOK());
            StringAssert.Contains(read.Status.Message, "ungueltige Groesse");
        }
    }

    [TestMethod]
    public void OnlyFailuresBeforeInstantiationRunAgainUnderWasm3()
    {
        Assert.IsTrue(WasmEngineSelection.FallsBackToWasm3(hasWasm: true, aotFailed: true, aotInstantiated: false));
        // Nach dem Instanziieren kann Gastcode gelaufen sein (start-Funktion, Konstruktoren): kein zweiter Lauf.
        Assert.IsFalse(WasmEngineSelection.FallsBackToWasm3(hasWasm: true, aotFailed: true, aotInstantiated: true));
        Assert.IsFalse(WasmEngineSelection.FallsBackToWasm3(hasWasm: true, aotFailed: false, aotInstantiated: true));
        // Ohne .wasm (AOT-Probe) bleibt der AOT-Status stehen.
        Assert.IsFalse(WasmEngineSelection.FallsBackToWasm3(hasWasm: false, aotFailed: true, aotInstantiated: false));
    }
}