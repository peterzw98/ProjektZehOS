using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Shared;
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
    public void ReplaceOrVersionFailure_RequiresRestartBeforeAnotherWrite()
    {
        foreach (bool failReplace in new[] { true, false })
        {
            var target = new Target { FailReplace = failReplace, FailVersion = !failReplace };
            var service = new UpdateService(new Source(), target, new(0, 1, 0));
            Assert.IsTrue(service.Check().IsOK());
            Assert.IsFalse(service.Install().IsOK());
            Assert.IsTrue(service.RequiresRestart);
            Assert.IsFalse(service.Install().IsOK());
            Assert.IsFalse(service.Check().IsOK());
            Assert.AreEqual(1, target.Replacements);
        }
    }

    [TestMethod]
    public void FallbackBoot_RejectsInstalledVersionAndKeepsWorkingBackup()
    {
        var broken = new Target { InstalledVersion = "0.2.0\n" };
        var service = new UpdateService(new Source(), broken, new UpdateVersion(0, 1, 0));
        Assert.IsFalse(service.Check().IsOK(), "Die defekte installierte Version darf nicht erneut eingespielt werden.");
        Assert.IsFalse(service.Install().IsOK());
        Assert.AreEqual(0, broken.Replacements);

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
        public ResultErr WriteVersion(string version) { Version = version; return FailVersion ? ResultErr.CreateFailure(ResultCode.Failure, "Versionsfehler") : ResultErr.CreateSuccess(); }
    }
}
