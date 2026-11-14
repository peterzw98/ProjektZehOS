using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Installer;
using ProjektZeh.Shared;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class InstallJobTests
{
    [TestMethod]
    public void CopiesInChunksAndWritesBootConfigurationLast()
    {
        var target = new Target();
        var job = new InstallJob(target, new byte[3], new byte[140000], "0.1.0");
        int previousPercent = 0;
        while (!job.IsCompleted)
        {
            Assert.IsTrue(job.Step().IsOK());
            Assert.IsTrue(job.Percent >= previousPercent);
            previousPercent = job.Percent;
        }
        Assert.AreEqual(100, job.Percent);
        Assert.AreEqual(3, target.KernelChunks);
        StringAssert.Contains(target.Config!, "/ZehOS (vorherige Version)");
        StringAssert.Contains(target.Config!, "/ZehOS (1024x768)");
        StringAssert.Contains(target.Config!, "resolution: 1920x1080x32");
        StringAssert.Contains(target.Config!, "ProjektZeh.elf.bak");
        Assert.AreEqual("/boot/limine/limine.conf", target.Paths[^1]);
    }

    [TestMethod]
    public void FailureStopsSubsequentWrites()
    {
        var target = new Target { FailFormat = true };
        var job = new InstallJob(target, [1], [2], "0.1.0");
        Assert.IsTrue(job.Step().IsOK());
        Assert.IsFalse(job.Step().IsOK());
        Assert.IsFalse(job.Step().IsOK());
        Assert.AreEqual(0, target.Paths.Count);
    }

    private sealed class Target : IInstallTarget
    {
        public bool FailFormat { get; init; }
        public int KernelChunks { get; private set; }
        public string? Config { get; private set; }
        public List<string> Paths { get; } = [];
        public ResultErr Abort() => ResultErr.CreateSuccess();
        public ResultErr Complete() => ResultErr.CreateSuccess();
        public ResultErr Partition() => ResultErr.CreateSuccess();
        public ResultErr Format() => FailFormat ? ResultErr.CreateFailure(ResultCode.Failure, "Format") : ResultErr.CreateSuccess();
        public ResultErr WriteFileChunk(string path, byte[] data, int offset, int count, bool first, bool last)
        {
            Paths.Add(path);
            if (path == "/boot/ProjektZeh.elf") KernelChunks++;
            if (path == "/boot/limine/limine.conf") Config = Encoding.UTF8.GetString(data, offset, count);
            Assert.IsTrue(count <= 65536);
            return ResultErr.CreateSuccess();
        }
    }
}
