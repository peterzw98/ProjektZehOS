using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Network;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class NetworkTargetTests
{
    [TestMethod]
    public void HostnamesAndIpv4RequireUnambiguousAsciiSyntax()
    {
        foreach (string valid in new[] { "example.com", "HOST-1.example", "10.0.2.2" }) NetworkTarget.ValidateHost(valid);
        foreach (string invalid in new[] { "10.2.2", "999.0.0.1", "127.1", "-host.test", "a..test", "täst.test", "::1", "host:80", "host\r\n" })
            Assert.ThrowsException<ArgumentException>(() => NetworkTarget.ValidateHost(invalid));
    }
}
