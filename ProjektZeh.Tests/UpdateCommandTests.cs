using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Installer;
using ProjektZeh.Shared;
using ProjektZeh.Shell;
using ProjektZeh.Update;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class UpdateCommandTests
{
    [TestMethod]
    public void Commands_RejectNonAdministratorBeforeAccessingServices()
    {
        var controller = new UpdateController(new(0, 1, 0), _ => throw new AssertFailedException("Netzwerkzugriff"), new Target(), "/nonexistent");
        var update = new UpdateShellCommands(controller, () => false).Commands[0];
        var setup = new SetupShellCommands(new Installer(), () => false).Commands[0];
        foreach (string[] arguments in new string[][] { ["status"], ["source", "10.0.2.2:8000"], ["check"], ["install"] })
            Assert.IsFalse(update.Execute(arguments, new Output()).IsOK());
        Assert.IsFalse(setup.Execute(["list"], new Output()).IsOK());
        Assert.IsFalse(setup.Execute(["install", "sata0", "--erase"], new Output()).IsOK());
    }

    [TestMethod]
    public void Source_UsesDefaultAndPersistsValidatedEndpoint()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var controller = new UpdateController(new(0, 1, 0), _ => new Source(), new Target(), root);
            Assert.AreEqual("10.0.2.2:8080", controller.GetSource().Value);
            Assert.IsTrue(controller.SetSource("mirror.example:8000").IsOK());
            Assert.IsFalse(controller.SetSource("mirror/path").IsOK());
            Assert.AreEqual("mirror.example:8000", controller.GetSource().Value);
            Assert.AreEqual("mirror.example:8000\n", File.ReadAllText(Path.Combine(root, ".zehos-update.conf")));
            Assert.IsFalse(controller.Install().IsOK());
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Source : IUpdateSource
    {
        public ResultObject<byte[]> Read(string name, int maxBytes) => throw new AssertFailedException("Netzwerkzugriff");
    }
    private sealed class Target : IUpdateTarget
    {
        public ResultErr ReplaceKernel(byte[] kernel) => throw new AssertFailedException("Schreibzugriff");
        public ResultErr WriteVersion(string version) => throw new AssertFailedException("Schreibzugriff");
        public ResultObject<string> ReadVersion() => throw new AssertFailedException("Lesezugriff");
    }
    private sealed class Installer : IInstallService
    {
        public string ProductVersion => "0.1.0";
        public ResultObject<InstallDisk[]> GetDisks() => throw new AssertFailedException("Plattenzugriff");
        public ResultObject<IInstallJob> Begin(string diskName, bool eraseExisting) => throw new AssertFailedException("Plattenzugriff");
        public ResultErr Restart() => throw new AssertFailedException("Neustart");
    }
    private sealed class Output : ITextOutput
    {
        public void WriteLine(string text) { }
        public void Clear() { }
    }
}
