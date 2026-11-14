using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Network;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class Ipv4ConfigurationTests
{
    [TestMethod]
    public void StaticConfigurationRejectsMalformedInputsBeforeUse()
    {
        Ipv4Configuration.Validate(["10.0.2.15", "255.255.255.0", "10.0.2.2", "10.0.2.3"]);
        foreach (string[] input in new string[][]
        {
            ["10.2.15", "255.255.255.0", "10.0.2.2", "10.0.2.3"],
            ["0x0a.0.2.15", "255.255.255.0", "10.0.2.2", "10.0.2.3"],
            ["10.0.2.256", "255.255.255.0", "10.0.2.2", "10.0.2.3"],
            ["10.0.2.15", "255.0.255.0", "10.0.2.2", "10.0.2.3"],
            ["10.0.2.15", "0.0.0.0", "10.0.2.2", "10.0.2.3"],
            ["10.0.2.15", "255.255.255.0", "224.0.0.1", "10.0.2.3"],
            ["10.0.2.15", "255.255.255.0", "10.0.2.2", "0.0.0.0"]
        }) Assert.ThrowsException<ArgumentException>(() => Ipv4Configuration.Validate(input));
    }
}
