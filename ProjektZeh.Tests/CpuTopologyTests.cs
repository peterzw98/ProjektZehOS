using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Scheduling;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class CpuTopologyTests
{
    private const int AmdEbx = 0x68747541, AmdEdx = 0x69746E65, AmdEcx = 0x444D4163;

    private static Func<int, int, (int, int, int, int)> Cpuid(Dictionary<(int, int), (int, int, int, int)> table) =>
        (leaf, subleaf) => table.TryGetValue((leaf, subleaf), out var value) ? value : (0, 0, 0, 0);

    [TestMethod]
    public void Flat_FourCpus_EachItsOwnCore()
    {
        var topology = CpuTopology.Flat(4);
        Assert.AreEqual(4, topology.CoreCount);
        Assert.AreEqual(0, topology.SiblingsOf(2).Length);
        Assert.AreEqual(0, topology.PackageOf(3));
    }

    [TestMethod]
    public void FromApicIds_TwoCoresTwoThreads_PairsSiblings()
    {
        var topology = CpuTopology.FromApicIds([0, 1, 2, 3], 1, 2);
        CollectionAssert.AreEqual(new[] { 0, 0, 1, 1 }, new[] { topology.CoreOf(0), topology.CoreOf(1), topology.CoreOf(2), topology.CoreOf(3) });
        CollectionAssert.AreEqual(new[] { 1 }, topology.SiblingsOf(0).ToArray());
        CollectionAssert.AreEqual(new[] { 2 }, topology.SiblingsOf(3).ToArray());
        Assert.AreEqual(2, topology.CoreCount);
    }

    [TestMethod]
    public void FromApicIds_SparseIds_NumbersCoresDensely()
    {
        var topology = CpuTopology.FromApicIds([0, 1, 4, 5], 1, 4);
        CollectionAssert.AreEqual(new[] { 0, 0, 1, 1 }, new[] { topology.CoreOf(0), topology.CoreOf(1), topology.CoreOf(2), topology.CoreOf(3) });
        for (int cpu = 0; cpu < 4; cpu++) Assert.AreEqual(0, topology.PackageOf(cpu));
    }

    [TestMethod]
    public void FromApicIds_TwoPackages_SeparatesThem()
    {
        var topology = CpuTopology.FromApicIds([0, 1, 16, 17], 1, 4);
        CollectionAssert.AreEqual(new[] { 0, 0, 1, 1 }, new[] { topology.PackageOf(0), topology.PackageOf(1), topology.PackageOf(2), topology.PackageOf(3) });
        CollectionAssert.AreEqual(new[] { 0, 0, 1, 1 }, new[] { topology.CoreOf(0), topology.CoreOf(1), topology.CoreOf(2), topology.CoreOf(3) });
    }

    [TestMethod]
    public void FromApicIds_InvalidShift_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => CpuTopology.FromApicIds([0, 1], 3, 2));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => CpuTopology.FromApicIds([0, 1], -1, 2));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => CpuTopology.Flat(0));
    }

    [TestMethod]
    public void ReadShifts_LeafB_FourCoresEightThreads()
    {
        var cpuid = Cpuid(new()
        {
            [(0, 0)] = (0x16, 0, 0, 0),
            [(0x0B, 0)] = (1, 2, 0x100, 0),
            [(0x0B, 1)] = (4, 8, 0x201, 0),
        });
        Assert.AreEqual((1, 4), CpuTopologyDecoder.ReadShifts(cpuid));
    }

    [TestMethod]
    public void ReadShifts_LeafB_QemuTwoCoresTwoThreads()
    {
        var cpuid = Cpuid(new()
        {
            [(0, 0)] = (0x0D, 0, 0, 0),
            [(0x0B, 0)] = (1, 2, 0x100, 0),
            [(0x0B, 1)] = (2, 4, 0x201, 0),
        });
        Assert.AreEqual((1, 2), CpuTopologyDecoder.ReadShifts(cpuid));
    }

    [TestMethod]
    public void ReadShifts_LeafB_NoHyperthreading()
    {
        var cpuid = Cpuid(new()
        {
            [(0, 0)] = (0x0D, 0, 0, 0),
            [(0x0B, 0)] = (0, 1, 0x100, 0),
            [(0x0B, 1)] = (2, 4, 0x201, 0),
        });
        Assert.AreEqual((0, 2), CpuTopologyDecoder.ReadShifts(cpuid));
    }

    [TestMethod]
    public void ReadShifts_Leaf1F_PreferredOverLeafB()
    {
        var cpuid = Cpuid(new()
        {
            [(0, 0)] = (0x20, 0, 0, 0),
            [(0x1F, 0)] = (1, 2, 0x100, 0),
            [(0x1F, 1)] = (6, 24, 0x201, 0),
            [(0x0B, 0)] = (1, 2, 0x100, 0),
            [(0x0B, 1)] = (5, 16, 0x201, 0),
        });
        Assert.AreEqual((1, 6), CpuTopologyDecoder.ReadShifts(cpuid));
    }

    [TestMethod]
    public void ReadShifts_LeafBWithoutData_FallsBackToLegacy()
    {
        var cpuid = Cpuid(new()
        {
            [(0, 0)] = (0x0D, 0, 0, 0),
            [(1, 0)] = (0, 0x00040000, 0, 0x10000000),
            [(4, 0)] = (0x04000000, 0, 0, 0),
        });
        Assert.AreEqual((1, 2), CpuTopologyDecoder.ReadShifts(cpuid));
    }

    [TestMethod]
    public void ReadShifts_Amd_UsesExtendedLeaves()
    {
        var cpuid = Cpuid(new()
        {
            [(0, 0)] = (0x0A, AmdEbx, AmdEcx, AmdEdx),
            [(unchecked((int)0x80000000), 0)] = (unchecked((int)0x8000001E), 0, 0, 0),
            [(unchecked((int)0x8000001E), 0)] = (0, 0x100, 0, 0),
            [(unchecked((int)0x80000008), 0)] = (0, 0, 0x4000, 0),
        });
        Assert.AreEqual((1, 4), CpuTopologyDecoder.ReadShifts(cpuid));
    }

    [TestMethod]
    public void ReadShifts_NothingUsable_SinglePackageFlat()
    {
        var cpuid = Cpuid(new() { [(0, 0)] = (1, 0, 0, 0), [(1, 0)] = (0, 0, 0, 0) });
        Assert.AreEqual((0, CpuTopologyDecoder.SinglePackage), CpuTopologyDecoder.ReadShifts(cpuid));
    }
}
