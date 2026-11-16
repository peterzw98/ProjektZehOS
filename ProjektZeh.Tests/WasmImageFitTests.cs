using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Input;
using ProjektZeh.Runtime.Wasm;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WasmImageFitTests
{
    [TestMethod]
    public void FitKeepsAspectRatioUpAndDown()
    {
        // NetSurf im Paketfenster beim Oeffnen: genau 1:1.
        Assert.AreEqual((798, 519), WasmImageFit.Fit(798, 519, 798, 519));
        // Doom 320x200 fuellt die Breite statt nur 2x (640x400).
        Assert.AreEqual((798, 498), WasmImageFit.Fit(798, 519, 320, 200));
        // NetSurf im Paketminimum 640x440 (Inhalt 638x399) wird verkleinert statt abgeschnitten.
        Assert.AreEqual((613, 399), WasmImageFit.Fit(638, 399, 798, 519));
        Assert.AreEqual((1280, 960), WasmImageFit.Fit(1280, 1000, 640, 480));
    }

    [TestMethod]
    public void FitWithoutImageOrAreaIsEmpty()
    {
        Assert.AreEqual((0, 0), WasmImageFit.Fit(0, 519, 320, 200));
        Assert.AreEqual((0, 0), WasmImageFit.Fit(798, 519, 0, 0));
        // Extrem flache Bilder behalten mindestens eine Zeile.
        Assert.AreEqual((10, 1), WasmImageFit.Fit(10, 10, 1920, 1));
    }

    [TestMethod]
    public void SourceTablesCoverWholeImageWithoutGaps()
    {
        var columns = new int[798];
        WasmImageFit.FillSourceIndices(columns, 320);
        Assert.AreEqual(0, columns[0]);
        Assert.AreEqual(319, columns[^1]);
        for (int index = 1; index < columns.Length; index++)
        {
            int step = columns[index] - columns[index - 1];
            Assert.IsTrue(step is 0 or 1, $"Spalte {index} springt um {step}");
        }
        var identity = new int[519];
        WasmImageFit.FillSourceIndices(identity, 519);
        for (int index = 0; index < identity.Length; index++) Assert.AreEqual(index, identity[index]);
    }

    [TestMethod]
    public void ClickHitsThePixelDrawnThere()
    {
        // Doom im Standardfenster: 798x498 mittig, oben 10 px Rand.
        var input = new WasmInputBuffer();
        input.SetViewport(798, 519, 320, 200);
        Assert.IsTrue(input.TryRead(out WasmInputEvent resize) && resize.Type == WasmInputEventType.Resize);
        var columns = new int[798];
        var rows = new int[498];
        WasmImageFit.FillSourceIndices(columns, 320);
        WasmImageFit.FillSourceIndices(rows, 200);
        foreach (var (x, y) in new[] { (0, 10), (400, 260), (797, 507) })
        {
            input.Handle(new(InputEventType.PointerDown, X: x, Y: y, Button: PointerButtons.Left));
            Assert.IsTrue(input.TryRead(out WasmInputEvent value));
            Assert.AreEqual((columns[x], rows[y - 10]), (value.X, value.Y));
        }
        input.Handle(new(InputEventType.PointerDown, X: 400, Y: 9, Button: PointerButtons.Left));
        Assert.IsFalse(input.TryRead(out _), "Klick in den Rand darf die App nicht erreichen");
    }
}
