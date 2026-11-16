using Cosmos.Network.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class CertificateVerifierTests
{
    [TestMethod]
    [DataRow("example.com", "example.com")]
    [DataRow("Example.COM.", "example.com")]
    [DataRow("*.example.com", "www.example.com")]
    [DataRow("*.EXAMPLE.com", "WWW.example.COM")]
    [DataRow("*.co.uk", "example.co.uk")]
    [DataRow("*.example.com.", "www.example.com")]
    [DataRow("*.example.com", "xn--bcher-kva.example.com")]
    public void MatchesExactNamesAndLeftmostWildcards(string pattern, string host) =>
        Assert.IsTrue(CertificateVerifier.MatchesDnsName(pattern, host), pattern + " / " + host);

    [TestMethod]
    [DataRow("*.com", "example.com")]
    [DataRow("*.example.com", "example.com")]
    [DataRow("*.example.com", "a.b.example.com")]
    [DataRow("*", "localhost")]
    [DataRow("*.", "example.com")]
    [DataRow("w*.example.com", "www.example.com")]
    [DataRow("*w.example.com", "www.example.com")]
    [DataRow("www.*.com", "www.example.com")]
    [DataRow("*.*.com", "www.example.com")]
    [DataRow("*.0.0.1", "127.0.0.1")]
    [DataRow("*.example.com", ".example.com")]
    [DataRow("example.com", "www.example.com")]
    [DataRow("*..com", "x..com")]
    public void RejectsBroadPartialAndMisplacedWildcards(string pattern, string host) =>
        Assert.IsFalse(CertificateVerifier.MatchesDnsName(pattern, host), pattern + " / " + host);
}
