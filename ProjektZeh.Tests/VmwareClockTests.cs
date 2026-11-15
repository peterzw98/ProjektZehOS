using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class VmwareClockTests
{
    [TestMethod]
    public void ConvertsHypervisorKilohertzWithoutOverflow()
    {
        Assert.IsTrue(VmwareClock.TryDecode(0x40000010, 0x61774d56, 0x4d566572, 0x65726177,
            3_070_000, 66_000, out long tsc, out uint lapic));
        Assert.AreEqual(3_070_000_000L, tsc);
        Assert.AreEqual(4125U, lapic);
    }

    [TestMethod]
    public void RejectsMissingLeafWrongVendorAndEmptyFrequencies()
    {
        Assert.IsFalse(VmwareClock.TryDecode(0x4000000f, 0x61774d56, 0x4d566572, 0x65726177, 3_070_000, 66_000, out _, out _));
        Assert.IsFalse(VmwareClock.TryDecode(0x40000010, 0, 0, 0, 3_070_000, 66_000, out _, out _));
        Assert.IsFalse(VmwareClock.TryDecode(0x40000010, 0x61774d56, 0x4d566572, 0x65726177, 0, 66_000, out _, out _));
        Assert.IsFalse(VmwareClock.TryDecode(0x40000010, 0x61774d56, 0x4d566572, 0x65726177, 3_070_000, 0, out _, out _));
        Assert.IsFalse(VmwareClock.TryDecode(0x40000010, 0x61774d56, 0x4d566572, 0x65726177, 3_070_000, 15, out _, out _));
    }
}
