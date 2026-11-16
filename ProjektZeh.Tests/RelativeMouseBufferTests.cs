using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class RelativeMouseBufferTests
{
    [TestMethod]
    public void PacketsStayOrderedAndMotionSurvivesIntermediateDrain()
    {
        var buffer = new RelativeMouseBuffer();
        buffer.Add(new(7, -2, PointerButtons.Left, 1));
        Assert.IsTrue(buffer.TryRead(out var first));
        Assert.AreEqual(new RelativeMousePacket(7, -2, PointerButtons.Left, 1), first);
        buffer.Add(new(-3, 5, PointerButtons.None, 0));
        Assert.IsTrue(buffer.TryRead(out var second));
        Assert.AreEqual(new RelativeMousePacket(-3, 5, PointerButtons.None, 0), second);
        buffer.TakeMotion(out int x, out int y, out long lost);
        Assert.AreEqual(4, x);
        Assert.AreEqual(3, y);
        Assert.AreEqual(0L, lost);
        buffer.TakeMotion(out x, out y, out lost);
        Assert.AreEqual(0, x);
        Assert.AreEqual(0, y);
    }

    [TestMethod]
    public void OverflowSignalsLossAndKeepsLatestState()
    {
        var buffer = new RelativeMouseBuffer();
        for (int i = 0; i < 128; i++) buffer.Add(new(1, 0, PointerButtons.Left, 0));
        buffer.Add(new(2, 0, PointerButtons.None, 0));
        buffer.TakeMotion(out int x, out _, out long lost);
        Assert.AreEqual(128L, lost);
        Assert.AreEqual(2, x);
        Assert.IsTrue(buffer.TryRead(out var packet));
        Assert.AreEqual(PointerButtons.None, packet.Buttons);
        Assert.IsFalse(buffer.TryRead(out _));
    }
}
