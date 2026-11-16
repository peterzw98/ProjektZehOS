using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WasmFrameProfileTests
{
    [TestMethod]
    public void DisabledProfileIgnoresMeasurements()
    {
        var profile = new WasmFrameProfile();
        profile.Record(WasmFramePhase.App, 5000);
        Assert.AreEqual("Anteile (ms, median/p95/max) app=- kopie=- skalierung=- desktop=- anzeige=-", profile.Peek());
    }

    [TestMethod]
    public void SummarySplitsPhasesAndStartsNextWindowEmpty()
    {
        var profile = new WasmFrameProfile();
        profile.SetEnabled(true);
        for (int index = 0; index < 19; index++) profile.Record(WasmFramePhase.App, 2_050);
        profile.Record(WasmFramePhase.App, 9_000);
        profile.Record(WasmFramePhase.Display, 7_400);
        Assert.AreEqual("Anteile (ms, median/p95/max) app=2.1/2.1/9.0 n=20 kopie=- skalierung=- desktop=- anzeige=7.4/7.4/7.4 n=1",
            profile.TakeSummary());
        Assert.AreEqual("Anteile (ms, median/p95/max) app=- kopie=- skalierung=- desktop=- anzeige=-", profile.Peek());
    }

    [TestMethod]
    public void LongDurationsKeepExactMaximumAndNegativeCountsAsZero()
    {
        var profile = new WasmFrameProfile();
        profile.SetEnabled(true);
        profile.Record(WasmFramePhase.Copy, 250_000);
        profile.Record(WasmFramePhase.Scale, -5);
        Assert.AreEqual("Anteile (ms, median/p95/max) app=- kopie=250.0/250.0/250.0 n=1 skalierung=0.0/0.0/0.0 n=1 desktop=- anzeige=-",
            profile.Peek());
    }

    [TestMethod]
    public void SwitchingOffDiscardsCollectedValues()
    {
        var profile = new WasmFrameProfile();
        profile.SetEnabled(true);
        profile.Record(WasmFramePhase.Desktop, 3_000);
        profile.SetEnabled(false);
        profile.SetEnabled(true);
        Assert.IsTrue(profile.Peek().Contains("desktop=-", System.StringComparison.Ordinal));
    }
}
