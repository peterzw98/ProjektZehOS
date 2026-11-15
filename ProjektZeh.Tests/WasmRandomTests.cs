using System;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WasmRandomTests
{
    [TestMethod]
    public void FillsWholeTargetAcrossBlocks()
    {
        var target = new byte[10000];
        int calls = 0;
        int result = WasmRandom.Fill(target, bytes => { calls++; bytes.AsSpan().Fill(0x5a); });
        Assert.AreEqual(WasmRandom.Success, result);
        Assert.AreEqual(3, calls);
        Assert.IsTrue(Array.TrueForAll(target, value => value == 0x5a));
    }

    [TestMethod]
    public void EmptyTargetSucceedsWithoutSource()
    {
        Assert.AreEqual(WasmRandom.Success, WasmRandom.Fill(Span<byte>.Empty, _ => throw new InvalidOperationException()));
    }

    [TestMethod]
    public void MissingHardwareRandomReportsIoInsteadOfZeros()
    {
        // Wie CosmosSaltSource ohne RDSEED/RDRAND (#61).
        var target = new byte[16];
        int result = WasmRandom.Fill(target, _ => throw new CryptographicException("RDSEED/RDRAND fehlt"));
        Assert.AreEqual(WasmRandom.Io, result);
    }

    [TestMethod]
    public void FailureInLaterBlockStillReportsIo()
    {
        int calls = 0;
        int result = WasmRandom.Fill(new byte[5000], _ => { if (++calls == 2) throw new CryptographicException("nicht bereit"); });
        Assert.AreEqual(WasmRandom.Io, result);
    }
}
