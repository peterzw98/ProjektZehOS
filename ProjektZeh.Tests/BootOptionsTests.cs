using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class BootOptionsTests
{
    [TestMethod]
    public void Parse_Null_IsEmpty() => Assert.AreEqual(new BootOptions(null, false), BootOptions.Parse(null));

    [TestMethod]
    public void Parse_Cpus() => Assert.AreEqual(1, BootOptions.Parse("zehos.cpus=1").Cpus);

    [TestMethod]
    public void Parse_CpusAndSmt() => Assert.AreEqual(new BootOptions(4, true), BootOptions.Parse("quiet zehos.cpus=4 zehos.smt=off"));

    [TestMethod]
    [DataRow("zehos.cpus=0")]
    [DataRow("zehos.cpus=abc")]
    [DataRow("zehos.cpus=")]
    [DataRow("zehos.cpus=-1")]
    [DataRow("xzehos.cpus=2")]
    public void Parse_InvalidCpus_IsNull(string line) => Assert.IsNull(BootOptions.Parse(line).Cpus);

    [TestMethod]
    public void Parse_SmtOn_IsNotOff() => Assert.IsFalse(BootOptions.Parse("zehos.smt=on").SmtOff);

    [TestMethod]
    public void Parse_SmtWholeWordOnly() => Assert.IsFalse(BootOptions.Parse("xzehos.smt=off").SmtOff);
}
