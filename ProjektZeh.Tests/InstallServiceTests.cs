using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Installer;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class InstallServiceTests
{
    [TestMethod]
    public void ExcludesInstallMediumAndRequiresExplicitErase()
    {
        var platform = new Platform();
        var service = new InstallService(platform, [1], [2], "0.1.0");
        var disks = service.GetDisks();
        Assert.IsTrue(disks.IsOK());
        Assert.AreEqual(2, disks.Value!.Length);
        Assert.IsFalse(service.Begin("cd0", true).IsOK());
        Assert.IsFalse(service.Begin("sata0", false).IsOK());
        Assert.IsFalse(service.Begin("sata1", true).IsOK());
        Assert.AreEqual(0, platform.Opens);
        Assert.IsTrue(service.Begin("sata0", true).IsOK());
        Assert.AreEqual(1, platform.Opens);
    }

    private sealed class Platform : IInstallPlatform
    {
        public int Opens { get; private set; }
        public ResultObject<InstallCandidate[]> GetCandidates() => ResultObjectFactory.CreateSuccess<InstallCandidate[]>(
        [
            new(new InstallDisk("cd0", 256UL * 1024 * 1024, 1), true),
            new(new InstallDisk("sata0", 256UL * 1024 * 1024, 1), false),
            new(new InstallDisk("sata1", 64UL * 1024 * 1024, 0), false)
        ]);
        public ResultObject<IInstallTarget> OpenTarget(string diskName)
        {
            Opens++;
            return ResultObjectFactory.CreateSuccess<IInstallTarget>(new Target());
        }
        public ResultErr Restart() => ResultErr.CreateSuccess();
    }

    private sealed class Target : IInstallTarget
    {
        public ResultErr Abort() => ResultErr.CreateSuccess();
        public ResultErr Complete() => ResultErr.CreateSuccess();
        public ResultErr Partition() => ResultErr.CreateSuccess();
        public ResultErr Format() => ResultErr.CreateSuccess();
        public ResultErr WriteFileChunk(string path, byte[] data, int offset, int count, bool first, bool last) => ResultErr.CreateSuccess();
    }
}
