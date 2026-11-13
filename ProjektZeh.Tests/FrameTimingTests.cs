using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class FrameTimingTests
{
    [TestMethod]
    public void TenMillisecondWakeups_KeepSixtyFramesWithoutAccumulatingDelay()
    {
        long next = 0;
        int frames = 0;
        for (long now = 0; now < 1000; now += 10)
        {
            if (now < next) continue;
            frames++;
            next = FrameTiming.NextDeadline(now);
        }
        Assert.AreEqual(60, frames);
    }

    [DataTestMethod]
    [DataRow(0L, 17L)]
    [DataRow(20L, 34L)]
    [DataRow(40L, 50L)]
    [DataRow(50L, 67L)]
    [DataRow(1009L, 1017L)]
    public void Deadline_IsNextSixtiethOfSecondAndSkipsMissedFrames(long now, long expected)
    {
        Assert.AreEqual(expected, FrameTiming.NextDeadline(now));
    }
}
