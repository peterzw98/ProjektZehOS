using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class MigrationHashTests
{
    [TestMethod]
    [DataRow("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [DataRow("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void KnownVectorsMatchForBytesAndStreams(string text, string expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        Assert.AreEqual(expected, MigrationHash.Hex(bytes));
        using var stream = new MemoryStream(bytes);
        Assert.AreEqual(expected, MigrationHash.Hex(stream));
    }

    [TestMethod]
    public void MultipleReadBlocksMatchTheHostSha256()
    {
        var bytes = new byte[150_123];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 17);
        string expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.AreEqual(expected, MigrationHash.Hex(bytes));
        using var stream = new MemoryStream(bytes);
        Assert.AreEqual(expected, MigrationHash.Hex(stream));
    }
}
