using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class DesktopMetricsTests
{
    [TestMethod]
    public void Sample_CountsActuallyPresentedFramesInsteadOfInvertingDrawTime()
    {
        var metrics = new DesktopMetrics();
        metrics.Sample(0, 0, 0);
        for (int index = 0; index < 60; index++) metrics.RecordFrame(5000);
        metrics.Sample(1000, 0, 0);
        StringAssert.Contains(metrics.Summary, "60 FPS");
        metrics.Sample(2000, 0, 0);
        StringAssert.Contains(metrics.Summary, "0 FPS");
    }
}
