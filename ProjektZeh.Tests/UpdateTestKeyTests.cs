using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using ProjektZeh.Update;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class UpdateTestKeyTests
{
    private const string ProductionKey = "eebr5IjF0GjyGoAUrBhwUDUx5STOMTsAhfrq2af5s+Y=";
    private const string Manifest = "zehos-update 1\nversion=0.2.0\nkernel=ProjektZeh-0.2.0.elf\nsize=42\nsha256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n";
    private const string ProductionSignature = "Y5a7UKQ4yG5L7ax8+KMe6yN6psvHsfpkq2jvVdPuOaJ0opGL5KRmL/iJliF3cR0zdY3z2WyXsDmfS0RF63TTAA==";

    [TestMethod]
    public void TestTrustAcceptsOnlyItsOwnSignatures()
    {
        var key = new Ed25519PrivateKeyParameters(new SecureRandom());
        using var resource = new MemoryStream(Encoding.ASCII.GetBytes(
            Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()) + "\n"));
        var trust = ManifestSignature.ReadTestTrust(resource);
        Assert.IsNotNull(trust);
        byte[] manifest = Encoding.UTF8.GetBytes(Manifest);
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        signer.BlockUpdate(manifest, 0, manifest.Length);
        byte[] signature = signer.GenerateSignature();
        Assert.IsTrue(trust.Verify(manifest, signature));
        Assert.IsFalse(trust.Verify(manifest, Convert.FromBase64String(ProductionSignature)));
        Assert.IsFalse(ManifestSignature.Verify(manifest, signature));
        manifest[0] ^= 1;
        Assert.IsFalse(trust.Verify(manifest, signature));
    }

    [TestMethod]
    public void InvalidOrProductionTestKeyNeverFallsBackToProductionTrust()
    {
        Assert.IsNull(ManifestSignature.ReadTestTrust(null));
        foreach (string value in new[] { "", "not base64", Convert.ToBase64String(new byte[31]),
            Convert.ToBase64String(new byte[33]), ProductionKey, new string(' ', 257) })
        {
            using var resource = new MemoryStream(Encoding.ASCII.GetBytes(value));
            Assert.IsNull(ManifestSignature.ReadTestTrust(resource));
        }
    }

    [TestMethod]
    public void FailedResourceReadDisablesTestTrust()
    {
        using var resource = new BrokenResource();
        Assert.IsNull(ManifestSignature.ReadTestTrust(resource));
    }

    private sealed class BrokenResource : MemoryStream
    {
        public BrokenResource() : base(new byte[44]) { }
        public override int Read(Span<byte> buffer) => throw new IOException("Testfehler beim Lesen.");
    }
}
