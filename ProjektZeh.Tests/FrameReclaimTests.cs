using System;
using ProjektZeh.Processes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FrameReclaimTests
{
    [TestInitialize]
    public void Init() => FrameReclaim.ResetForTests();

    [TestMethod]
    public void Retry_SammeltNichtWennErsterVersuchGelingt()
    {
        int collects = 0;
        Assert.IsTrue(FrameReclaim.Retry(() => true, () => collects++, () => 1000));
        Assert.AreEqual(0, collects);
    }

    [TestMethod]
    public void Retry_SammeltEinmalUndWiederholt()
    {
        bool collected = false;
        int calls = 0;
        bool ok = FrameReclaim.Retry(() => { calls++; return collected; }, () => collected = true, () => 1000);
        Assert.IsTrue(ok);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void Retry_SturmbremseUebersprungeneSammlungInnerhalbDesIntervalls()
    {
        int collects = 0;
        Assert.IsFalse(FrameReclaim.Retry(() => false, () => collects++, () => 1000));
        Assert.IsFalse(FrameReclaim.Retry(() => false, () => collects++, () => 1005));
        Assert.AreEqual(1, collects);
        Assert.IsFalse(FrameReclaim.Retry(() => false, () => collects++, () => 1100));
        Assert.AreEqual(2, collects);
    }

    [TestMethod]
    public void Retry_WerfenderCollectBleibtEnomemPfad()
    {
        int calls = 0;
        bool ok = FrameReclaim.Retry(() => { calls++; return calls > 1; }, () => throw new InvalidOperationException(), () => 1000);
        Assert.IsTrue(ok);
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
