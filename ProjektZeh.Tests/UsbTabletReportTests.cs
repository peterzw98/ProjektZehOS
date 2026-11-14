using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class UsbTabletReportTests
{
    [TestMethod]
    public void RejectsShortReports()
    {
        for (int length = 0; length < 6; length++)
            Assert.IsFalse(UsbTabletReport.TryDecode(new byte[length], 1024, 768, out _, out _, out _));
    }

    [TestMethod]
    public void DecodesLittleEndianCoordinatesAndButtons()
    {
        byte[] report = [0xFD, 0x34, 0x12, 0x78, 0x56, 0xFF];
        Assert.IsTrue(UsbTabletReport.TryDecode(report, 32768, 32768, out int x, out int y, out byte buttons));
        Assert.AreEqual(0x1234, x);
        Assert.AreEqual(0x5678, y);
        Assert.AreEqual((byte)5, buttons);
    }

    [TestMethod]
    public void ScalesCornersToScreen()
    {
        Assert.IsTrue(UsbTabletReport.TryDecode([0, 0, 0, 0, 0, 0], 1024, 768, out int x, out int y, out byte buttons));
        Assert.AreEqual(0, x);
        Assert.AreEqual(0, y);
        Assert.AreEqual((byte)0, buttons);
        Assert.IsTrue(UsbTabletReport.TryDecode([7, 0xFF, 0x7F, 0xFF, 0x7F, 0], 1024, 768, out x, out y, out buttons));
        Assert.AreEqual(1023, x);
        Assert.AreEqual(767, y);
        Assert.AreEqual((byte)7, buttons);
    }
}
