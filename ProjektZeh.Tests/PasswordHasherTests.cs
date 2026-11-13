using System;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Security;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class PasswordHasherTests
{
    [TestMethod]
    public void SaltedHashesMatchPlatformPbkdf2AndRejectWrongPassword()
    {
        var hasher = new PasswordHasher(bytes => RandomNumberGenerator.Fill(bytes));
        string first = hasher.Hash("test-password");
        string second = hasher.Hash("test-password");
        Assert.AreNotEqual(first, second);
        Assert.IsTrue(PasswordHasher.Verify("test-password", first));
        Assert.IsFalse(PasswordHasher.Verify("wrong-password", first));
        string[] parts = first.Split('$');
        byte[] expected = Rfc2898DeriveBytes.Pbkdf2("test-password", Convert.FromBase64String(parts[2]), PasswordHasher.Iterations, HashAlgorithmName.SHA256, 32);
        CollectionAssert.AreEqual(expected, Convert.FromBase64String(parts[3]));
        Assert.IsFalse(PasswordHasher.Verify("test-password", "bad"));
        Assert.ThrowsException<CryptographicException>(() => new PasswordHasher(_ => throw new CryptographicException()).Hash("test-password"));
    }
}
