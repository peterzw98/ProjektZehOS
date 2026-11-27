using ProjektZeh.Processes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FrameReclaimTests
{
    [TestMethod]
    public void Allocate_SammeltNichtWennFrameDa()
    {
        int collects = 0;
        ulong f = FrameReclaim.Allocate(() => 0x1000, () => collects++);
        Assert.AreEqual(0x1000UL, f);
        Assert.AreEqual(0, collects);
    }

    [TestMethod]
    public void Allocate_SammeltEinmalUndWiederholt()
    {
        bool collected = false;
        int calls = 0;
        ulong f = FrameReclaim.Allocate(() => { calls++; return collected ? 0x2000UL : 0; }, () => collected = true);
        Assert.AreEqual(0x2000UL, f);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void Allocate_BleibtNullNachEinerSammlung()
    {
        int collects = 0, calls = 0;
        ulong f = FrameReclaim.Allocate(() => { calls++; return 0; }, () => collects++);
        Assert.AreEqual(0UL, f);
        Assert.AreEqual(1, collects);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    [DataRow(1000UL, 1000UL, 100UL, false)]
    [DataRow(300UL, 1000UL, 10UL, false)]
    [DataRow(200UL, 1000UL, 10UL, true)]
    [DataRow(900UL, 1000UL, 500UL, true)]
    [DataRow(0UL, 0UL, 0UL, false)]
    public void ShouldCollectAtExit_Schwelle(ulong free, ulong total, ulong reserved, bool expected) =>
        Assert.AreEqual(expected, FrameReclaim.ShouldCollectAtExit(free, total, reserved));
}
