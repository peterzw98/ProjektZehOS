using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Update;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class UpdateTests
{
    private const string Manifest = "zehos-update 1\nversion=0.2.0\nkernel=ProjektZeh-0.2.0.elf\nsize=42\nsha256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n";
    private const string Signature = "Y5a7UKQ4yG5L7ax8+KMe6yN6psvHsfpkq2jvVdPuOaJ0opGL5KRmL/iJliF3cR0zdY3z2WyXsDmfS0RF63TTAA==";

    [TestMethod]
    public void ManifestRoundTripsAndSignatureCoversExactBytes()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Manifest);
        var parsed = UpdateManifest.Parse(bytes);
        Assert.IsTrue(parsed.IsOK());
        CollectionAssert.AreEqual(bytes, parsed.Value!.Serialize());
        byte[] signature = Convert.FromBase64String(Signature);
        Assert.IsTrue(ManifestSignature.Verify(bytes, signature));
        bytes[0] ^= 1;
        Assert.IsFalse(ManifestSignature.Verify(bytes, signature));
        Assert.IsFalse(ManifestSignature.Verify(bytes, new byte[63]));
    }

    [TestMethod]
    public void RejectsMalformedManifests()
    {
        string[] invalid = [Manifest.TrimEnd('\n'), Manifest.Replace("0.2.0", "0.02.0"),
            Manifest.Replace("ProjektZeh-0.2.0.elf", "../kernel"), Manifest.Replace("size=42", "size=-1"),
            Manifest.Replace("sha256=", "sha256=x"), Manifest + "extra\n", Manifest.Replace("\n", "\r\n")];
        foreach (string text in invalid) Assert.IsFalse(UpdateManifest.Parse(Encoding.UTF8.GetBytes(text)).IsOK(), text);
        Assert.IsFalse(UpdateManifest.Parse([0xff]).IsOK());
    }

    [TestMethod]
    public void VersionsCompareNumericallyAndRejectInvalidForms()
    {
        Assert.IsTrue(UpdateVersion.TryParse("1.10.0", out var higher));
        Assert.IsTrue(UpdateVersion.TryParse("1.2.99", out var lower));
        Assert.IsTrue(higher.CompareTo(lower) > 0);
        foreach (string value in new[] { "1.2", "1.2.3.4", "01.2.3", "-1.2.3", "1. 2.3", "2147483648.0.0" })
            Assert.IsFalse(UpdateVersion.TryParse(value, out _), value);
    }
}
