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
    [TestMethod]
    public void TemporaryPasswordBytesAreClearedOnSuccessAndDerivationFailure()
    {
        byte[] bytes = "password"u8.ToArray();
        byte[] result = PasswordHasher.DeriveAndClear(bytes, "salt"u8.ToArray(), 1);
        Assert.AreEqual("120FB6CFFCF8B32C43E7225256C4F837A86548C92CCC35480805987CB70BE17B", Convert.ToHexString(result));
        CollectionAssert.AreEqual(new byte[8], bytes);
        byte[] invalid = "password"u8.ToArray();
        Assert.ThrowsException<ArgumentException>(() => PasswordHasher.DeriveAndClear(invalid, "salt"u8.ToArray(), 0));
        CollectionAssert.AreEqual(new byte[8], invalid);
    }

}
