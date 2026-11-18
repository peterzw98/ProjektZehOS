using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Update;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class UpdateServiceTests
{
    private const string Manifest = "zehos-update 1\nversion=0.2.0\nkernel=ProjektZeh-0.2.0.elf\nsize=42\nsha256=2b2573d5ea0b352e24bebd015f3fe83693a5b81a6252cf811b65dcf6a5037def\n";
    private const string Signature = "dGQpBJA1RBv/Yjx/Bnw3ZkSnImOIy2daZA7lpy8A8wjHKyVLlnFv6V9ZGjJNdukmjHREcyNAdgkwIwp/NSeTCw==";

    [TestMethod]
    public void ValidUpdateWritesOnlyAfterVerification()
    {
        var source = new Source();
        var target = new Target();
        var service = new UpdateService(source, target, new UpdateVersion(0, 1, 0));
        Assert.IsTrue(service.Check().IsOK());
        Assert.IsFalse(service.Install(new byte[41]).IsOK());
        Assert.IsFalse(service.Install(new byte[42]).IsOK());
        Assert.AreEqual(0, target.Replacements);
        Assert.IsTrue(service.Install().IsOK());
        Assert.AreEqual(1, target.Replacements);
        Assert.AreEqual("0.2.0", target.Version);
        Assert.IsFalse(service.Install().IsOK());
    }

    [TestMethod]
    public void RejectsBadSignatureAndNonNewerVersion()
    {
        var source = new Source();
        var target = new Target();
        Assert.IsFalse(new UpdateService(source, target, new UpdateVersion(0, 2, 0)).Check().IsOK());
        Assert.IsFalse(new UpdateService(source, target, new UpdateVersion(0, 3, 0)).Check().IsOK());
        source.Files["update-manifest.sig"][0] ^= 1;
        var service = new UpdateService(source, target, new UpdateVersion(0, 1, 0));
        Assert.IsFalse(service.Check().IsOK());
        Assert.IsFalse(service.Install().IsOK());
        Assert.AreEqual(0, target.Replacements);
    }

    [TestMethod]
    public void OfflineInstall_VerifiesLocalPackageWithoutNetwork()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "update-manifest.txt"), Manifest);
            File.WriteAllBytes(Path.Combine(root, "update-manifest.sig"), Convert.FromBase64String(Signature));
            File.WriteAllText(Path.Combine(root, "kernel.elf"), new string('x', 42));
            var target = new Target();
            var controller = new UpdateController(new(0, 1, 0), _ => throw new AssertFailedException("Netzwerkzugriff"), target, root);
            Assert.IsFalse(controller.InstallOffline("../kernel.elf").IsOK());
            Assert.AreEqual(0, target.Replacements);
            Assert.IsTrue(controller.InstallOffline("kernel.elf").IsOK());
            Assert.AreEqual(1, target.Replacements);
            Assert.AreEqual("0.2.0", target.Version);
            Assert.IsFalse(controller.InstallOffline("kernel.elf").IsOK());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReplaceOrVersionFailure_KeepsMarkerConsistentWithKernel()
    {
        foreach (bool failReplace in new[] { true, false })
        {
            var target = new Target { FailReplace = failReplace, FailVersion = !failReplace };
            var service = new UpdateService(new Source(), target, new(0, 1, 0));
            Assert.IsTrue(service.Check().IsOK());
            Assert.IsFalse(service.Install().IsOK());
            Assert.IsFalse(service.Install().IsOK());
            // Der Marker wird vor dem Kernel geschrieben: ohne Marker kein Austausch, nach Austauschfehler zurueckgesetzt.
            Assert.AreEqual(failReplace ? 1 : 0, target.Replacements);
            Assert.AreEqual(failReplace, service.RequiresRestart);
            if (failReplace) Assert.AreEqual("0.1.0", target.Version);
        }
    }

    [TestMethod]
    public void PersistFailure_AfterReplace_KeepsNewMarkerAndWritesNothingMore()
    {
        var target = new Target { FailPersist = true };
        var service = new UpdateService(new Source(), target, new(0, 1, 0));
        Assert.IsTrue(service.Check().IsOK());
        var result = service.Install();
        Assert.IsFalse(result.IsOK());
        StringAssert.Contains(result.Message, "nicht dauerhaft", StringComparison.Ordinal);
        // Spec 7b §1: Marker bleibt neu, kein Ruecksetzen, Neustart verlangt.
        Assert.AreEqual("0.2.0", target.Version);
        Assert.AreEqual(1, target.Writes);
        Assert.AreEqual(1, target.Replacements);
        Assert.IsTrue(service.RequiresRestart);
    }

    [TestMethod]
    public void ReplaceFailure_DoesNotPersist()
    {
        var target = new Target { FailReplace = true };
        var service = new UpdateService(new Source(), target, new(0, 1, 0));
        Assert.IsTrue(service.Check().IsOK());
        Assert.IsFalse(service.Install().IsOK());
        Assert.AreEqual(0, target.Persists);
        Assert.AreEqual("0.1.0", target.Version);
    }

    [TestMethod]
    public void OfflineInstall_RejectsExcludedSystemAreasAndUsesConfigPath()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string boot = Path.Combine(root, "boot", "efi");
        Directory.CreateDirectory(boot);
        try
        {
            foreach (string directory in new[] { root, boot })
            {
                File.WriteAllText(Path.Combine(directory, "update-manifest.txt"), Manifest);
                File.WriteAllBytes(Path.Combine(directory, "update-manifest.sig"), Convert.FromBase64String(Signature));
                File.WriteAllText(Path.Combine(directory, "kernel.elf"), new string('x', 42));
            }
            string config = Path.Combine(root, "update.conf");
            var target = new Target();
            var controller = new UpdateController(new(0, 1, 0), _ => throw new AssertFailedException("Netzwerkzugriff"), target, root,
                configPath: config, offlineExcluded: [boot]);
            var refused = controller.InstallOffline("boot/efi/kernel.elf");
            Assert.IsFalse(refused.IsOK());
            StringAssert.Contains(refused.Message, "nicht unter", StringComparison.Ordinal);
            Assert.AreEqual(0, target.Replacements);
            Assert.IsTrue(controller.SetSource("mirror.example:8000").IsOK());
            Assert.AreEqual("mirror.example:8000\n", File.ReadAllText(config));
            Assert.IsFalse(File.Exists(Path.Combine(root, ".zehos-update.conf")));
            Assert.IsTrue(controller.InstallOffline("kernel.elf").IsOK());
            Assert.AreEqual(1, target.Persists);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FallbackBoot_RejectsOlderVersionAndKeepsWorkingBackup()
    {
        var newer = new Target { InstalledVersion = "0.3.0\n" };
        var service = new UpdateService(new Source(), newer, new UpdateVersion(0, 1, 0));
        Assert.IsFalse(service.Check().IsOK(), "Aelter als die installierte Version wird nicht eingespielt.");
        Assert.IsFalse(service.Install().IsOK());
        Assert.AreEqual(0, newer.Replacements);

        var same = new Target { InstalledVersion = "0.2.0\n" };
        var repeat = new UpdateService(new Source(), same, new UpdateVersion(0, 1, 0));
        Assert.IsTrue(repeat.Check().IsOK(), "Dieselbe Version darf einen unvollstaendigen Austausch wiederholen.");
        Assert.IsTrue(repeat.Install().IsOK());
        Assert.IsFalse(same.RotatedBackup!.Value);

        var older = new Target { InstalledVersion = "0.1.5\n" };
        var fixedRelease = new UpdateService(new Source(), older, new UpdateVersion(0, 1, 0));
        Assert.IsTrue(fixedRelease.Check().IsOK());
        Assert.IsTrue(fixedRelease.Install().IsOK());
        Assert.IsFalse(older.RotatedBackup!.Value, "Der laufende Rueckfallkernel in .bak bleibt erhalten.");

        var normal = new Target { InstalledVersion = "0.1.0\n" };
        var regular = new UpdateService(new Source(), normal, new UpdateVersion(0, 1, 0));
        Assert.IsTrue(regular.Check().IsOK());
        Assert.IsTrue(regular.Install().IsOK());
        Assert.IsTrue(normal.RotatedBackup!.Value);
    }

    [TestMethod]
    public void KernelDownload_IsLimitedToSignedSize()
    {
        var source = new Source();
        var service = new UpdateService(source, new Target(), new UpdateVersion(0, 1, 0));
        Assert.IsTrue(service.Check().IsOK());
        source.Files["ProjektZeh-0.2.0.elf"] = new byte[43];
        Assert.IsFalse(service.Install().IsOK());
        Assert.AreEqual(42, source.Limits["ProjektZeh-0.2.0.elf"]);
        Assert.AreEqual(64, source.Limits["update-manifest.sig"]);
    }

    [TestMethod]
    public void UpdateCommand_Status_PrintsRunningVersionAndSource() => RunUpdateCommand((shell, _, _, output) =>
    {
        Assert.IsTrue(shell.ExecuteLine("update status", output).IsOK());

        CollectionAssert.AreEqual(new[] { "Laufender Kernel: 0.1.0", "Updatequelle: 10.0.2.2:8080" }, output.Lines);
    });

    [TestMethod]
    public void UpdateCommand_Source_StoresEndpointAndRejectsPath() => RunUpdateCommand((shell, _, _, output) =>
    {
        Assert.IsTrue(shell.ExecuteLine("update source mirror.example:8000", output).IsOK());
        Assert.IsFalse(shell.ExecuteLine("update source mirror/path", output).IsOK());

        Assert.IsTrue(shell.ExecuteLine("update status", output).IsOK());
        Assert.AreEqual("Updatequelle: mirror.example:8000", output.Lines[^1]);
    });

    [TestMethod]
    public void UpdateCommand_CheckThenInstall_ReportsUpdateAndReplacesKernelOnce() => RunUpdateCommand((shell, _, target, output) =>
    {
        Assert.IsTrue(shell.ExecuteLine("update check", output).IsOK());
        Assert.AreEqual(0, target.Replacements, "Pruefen allein darf nichts schreiben.");
        Assert.IsTrue(shell.ExecuteLine("update install", output).IsOK());

        CollectionAssert.AreEqual(new[]
        {
            "Geprueftes Update: 0.2.0 (42 Bytes).",
            "Kernel installiert. Bitte neu starten; die vorherige Version bleibt im Bootmenue."
        }, output.Lines);
        Assert.AreEqual(1, target.Replacements);
        Assert.AreEqual("0.2.0", target.Version);
    });

    [DataTestMethod]
    [DataRow("update check")]
    [DataRow("update install")]
    public void UpdateCommand_SourceWithoutSignature_FailsWithoutOutputOrWrite(string line) => RunUpdateCommand((shell, source, target, output) =>
    {
        source.Files.Remove("update-manifest.sig");

        Assert.IsFalse(shell.ExecuteLine(line, output).IsOK());

        Assert.AreEqual(0, output.Lines.Count);
        Assert.AreEqual(0, target.Replacements);
    });

    [TestMethod]
    public void UpdateCommand_InstallMissingOfflineFile_FailsWithoutOutputOrWrite() => RunUpdateCommand((shell, _, target, output) =>
    {
        Assert.IsFalse(shell.ExecuteLine("update install fehlt.zip", output).IsOK());

        Assert.AreEqual(0, output.Lines.Count);
        Assert.AreEqual(0, target.Replacements);
    });

    [DataTestMethod]
    [DataRow("update")]
    [DataRow("update status now")]
    [DataRow("update source")]
    [DataRow("update install a b")]
    [DataRow("update bogus")]
    public void UpdateCommand_InvalidArguments_ReturnUsage(string line) => RunUpdateCommand((shell, _, target, output) =>
    {
        var result = shell.ExecuteLine(line, output);

        Assert.AreEqual(ResultCode.InvalidArgument, result.Code);
        StringAssert.StartsWith(result.Message, "Verwendung: update", StringComparison.Ordinal);
        Assert.AreEqual(0, target.Replacements);
    });

    private static void RunUpdateCommand(Action<ShellService, Source, Target, Output> test)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = new Source();
            var target = new Target();
            var controller = new UpdateController(new(0, 1, 0), _ => source, target, root);
            test(new ShellService(new UpdateShellCommands(controller, () => true).Commands), source, target, new Output());
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Output : ITextOutput
    {
        public List<string> Lines { get; } = [];
        public void WriteLine(string text) => Lines.Add(text);
        public void Clear() => Lines.Clear();
    }

    private sealed class Source : IUpdateSource
    {
        public Dictionary<string, byte[]> Files { get; } = new()
        {
            ["update-manifest.txt"] = Encoding.UTF8.GetBytes(Manifest),
            ["update-manifest.sig"] = Convert.FromBase64String(Signature),
            ["ProjektZeh-0.2.0.elf"] = Encoding.ASCII.GetBytes(new string('x', 42))
        };
        public Dictionary<string, int> Limits { get; } = new();
        public ResultObject<byte[]> Read(string name, int maxBytes)
        {
            Limits[name] = maxBytes;
            if (!Files.TryGetValue(name, out byte[]? value)) return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.NotFound, name);
            return value.Length <= maxBytes ? ResultObjectFactory.CreateSuccess(value)
                : ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "zu gross");
        }
    }

    private sealed class Target : IUpdateTarget
    {
        public bool FailReplace { get; init; }
        public bool FailVersion { get; init; }
        public int Replacements { get; private set; }
        public string? Version { get; private set; }
        public string? InstalledVersion { get; init; }
        public ResultObject<string> ReadVersion() => ResultObjectFactory.CreateSuccess(InstalledVersion);
        public bool? RotatedBackup { get; private set; }
        public ResultErr ReplaceKernel(byte[] kernel, bool rotateBackup) { Replacements++; RotatedBackup = rotateBackup; return FailReplace ? ResultErr.CreateFailure(ResultCode.Failure, "Austauschfehler") : ResultErr.CreateSuccess(); }
        public ResultErr WriteVersion(string version) { Version = version; Writes++; return FailVersion ? ResultErr.CreateFailure(ResultCode.Failure, "Versionsfehler") : ResultErr.CreateSuccess(); }
        public bool FailPersist { get; init; }
        public int Persists { get; private set; }
        public int Writes { get; private set; }
        public ResultErr Persist() { Persists++; return FailPersist ? ResultErr.CreateFailure(ResultCode.Failure, "Syncfehler") : ResultErr.CreateSuccess(); }
    }
}
