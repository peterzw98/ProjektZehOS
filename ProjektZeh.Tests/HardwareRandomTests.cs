using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class HardwareRandomTests
{
    private const int RdrandBit = 0x40000000;
    private const int RdseedBit = 0x40000;

    private static Func<int, int, (int Eax, int Ebx, int Ecx, int Edx)> Cpu(int maximumLeaf, int leaf1Ecx, int leaf7Ebx, List<int>? read = null) =>
        (leaf, _) =>
        {
            read?.Add(leaf);
            return leaf switch
            {
                0 => (maximumLeaf, 0, 0, 0),
                1 => (0, 0, leaf1Ecx, 0),
                7 => (0, leaf7Ebx, 0, 0),
                _ => throw new AssertFailedException($"Unerwartetes CPUID-Blatt {leaf}."),
            };
        };

    private static HardwareRandom.ReadWord Counter(ulong start, Func<int, bool>? fails = null)
    {
        int calls = 0;
        ulong next = start;
        return (out ulong value) =>
        {
            calls++;
            value = next++;
            return fails != null && fails(calls) ? 0 : 1;
        };
    }

    private static readonly HardwareRandom.ReadWord Broken = (out ulong value) => { value = 0; return 0; };

    [TestMethod]
    public void DetectReadsLeafSevenOnlyWhenOffered()
    {
        var read = new List<int>();
        // Ohne Blatt 7 wuerde eine echte CPU Daten des hoechsten Blatts liefern; hier ist das Bit bewusst gesetzt.
        Assert.AreEqual((false, true), HardwareRandom.Detect(Cpu(1, RdrandBit, RdseedBit, read)));
        CollectionAssert.DoesNotContain(read, 7);
        Assert.AreEqual((true, true), HardwareRandom.Detect(Cpu(7, RdrandBit, RdseedBit)));
        Assert.AreEqual((false, false), HardwareRandom.Detect(Cpu(0, RdrandBit, RdseedBit)));
    }

    [TestMethod]
    public void MissingSeedAndRandomThrowsUnavailable()
    {
        var (seed, random) = HardwareRandom.Detect(Cpu(7, 0, 0));
        Assert.ThrowsException<HardwareRandomUnavailableException>(() =>
            HardwareRandom.Fill(new byte[16], seed, random, Broken, Broken));
    }

    [TestMethod]
    public void FailingSeedFallsBackToRandom()
    {
        var bytes = new byte[16];
        HardwareRandom.Fill(bytes, true, true, Broken, Counter(0x0102030405060708));
        Assert.AreEqual(0x0102030405060708ul, BitConverter.ToUInt64(bytes, 0));
        Assert.AreEqual(0x0102030405060709ul, BitConverter.ToUInt64(bytes, 8));
    }

    [TestMethod]
    public void SeedIsPreferredAndRetriedBeforeFallback()
    {
        var bytes = new byte[8];
        // Zwei Carry-Fehler liegen innerhalb des Wiederholungslimits; RDRAND darf dann nicht gelesen werden.
        HardwareRandom.Fill(bytes, true, true, Counter(0x1111, calls => calls <= 2), (out ulong value) => throw new AssertFailedException("RDRAND gelesen."));
        Assert.AreEqual(0x1113ul, BitConverter.ToUInt64(bytes, 0));
    }

    [TestMethod]
    public void BothSourcesFailingThrowsNotReady()
    {
        var exception = Assert.ThrowsException<CryptographicException>(() => HardwareRandom.Fill(new byte[8], true, true, Broken, Broken));
        Assert.IsNotInstanceOfType<HardwareRandomUnavailableException>(exception);
    }

    [TestMethod]
    public void RejectsKnownBrokenValues()
    {
        // Konstante, Nullwerte und alle Bits gesetzt (bekannte Firmwarefehler) gelten als Fehler.
        foreach (ulong constant in new[] { 0ul, ulong.MaxValue, 0x4242ul })
        {
            HardwareRandom.ReadWord stuck = (out ulong value) => { value = constant; return 1; };
            var bytes = new byte[16];
            Assert.ThrowsException<CryptographicException>(() => HardwareRandom.Fill(bytes, false, true, Broken, stuck), constant.ToString("x", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
