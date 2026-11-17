using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics.Raster;

namespace ProjektZeh.Tests;

/// <summary>
/// Der beschleunigte Rasterizer muss bitgleich zur eingefrorenen Fassung (WasmRasterizerReference) zeichnen:
/// Das Bild der App darf sich durch Optimierungen nicht veraendern.
/// </summary>
[TestClass]
public sealed class WasmRasterizerEquivalenceTests
{
    private static void AssertSame(RasterFrame frame, string scene)
    {
        byte[] expected = (byte[])frame.Memory.Clone(), actual = (byte[])frame.Memory.Clone();
        var expectedResults = new List<int>();
        var actualResults = new List<int>();
        frame.Render(expected, WasmRasterizerReference.Draw, WasmRasterizerReference.Fill, expectedResults);
        frame.Render(actual, WasmRasterizer.Draw, WasmRasterizer.Fill, actualResults);
        CollectionAssert.AreEqual(expectedResults, actualResults, $"{scene}: Rueckgabewerte");
        // Einzige Ausnahme: NaN im Tiefenpuffer gilt unabhaengig von Vorzeichen und Nutzlast als gleich. Treffen zwei NaN
        // verschiedenen Vorzeichens aufeinander (Math.Abs in ClipLine erzeugt +NaN), haengt das Ergebnis davon ab, in welcher
        // Reihenfolge der Compiler die Operanden einer kommutativen Operation an SSE gibt; das unterscheidet sich schon
        // zwischen Uebersetzungsstufen derselben Fassung (und Wasm legt NaN-Bits ebenfalls nicht fest). Alle Vergleiche mit
        // NaN sind vorzeichenunabhaengig, Bild und weiteres Zeichnen bleiben deshalb gleich.
        Span<float> expectedDepth = MemoryMarshal.Cast<byte, float>(expected.AsSpan((int)frame.DepthAt, frame.DepthStride * frame.Height * 4));
        Span<float> actualDepth = MemoryMarshal.Cast<byte, float>(actual.AsSpan((int)frame.DepthAt, frame.DepthStride * frame.Height * 4));
        for (int index = 0; index < expectedDepth.Length; index++)
        {
            if (float.IsNaN(expectedDepth[index]) && float.IsNaN(actualDepth[index])) actualDepth[index] = expectedDepth[index];
        }
        int difference = expected.AsSpan().CommonPrefixLength(actual);
        Assert.AreEqual(expected.Length, difference, $"{scene}: erste Abweichung bei Byte {difference}");
    }

    [TestMethod]
    public void GameScenesMatchReference()
    {
        AssertSame(RasterScenes.Game(1, 0.5f, 11, -6, 0, 0), "Blick geradeaus");
        AssertSame(RasterScenes.Game(2, 3.2f, 6.5f, 20.3f, 0.8f, 0.45f), "Blick nach unten, Kamera dicht am Gelaende");
        AssertSame(RasterScenes.Game(3, -10, 3.7f, 40, 2.5f, -0.3f), "Blick nach oben, Kamera zwischen Bloecken");
        AssertSame(RasterScenes.Game(4, 0.5f, 11, -6, 0.3f, 0.1f, 333, 200), "kleines Fenster");
    }

    [TestMethod]
    public void RandomScenesMatchReference()
    {
        for (int seed = 0; seed < 1500; seed++) AssertSame(RasterScenes.Random(seed), $"Zufallsszene {seed}");
    }

    /// <summary>
    /// Host-Messung beider Fassungen auf der Spielszene. Laeuft nur mit ZEHOS_RASTER_BENCH=1, damit die normalen Tests
    /// schnell bleiben: dotnet test ... --filter Name=BenchmarkGameScene --logger "console;verbosity=detailed".
    /// </summary>
    [TestMethod]
    public void BenchmarkGameScene()
    {
        if (Environment.GetEnvironmentVariable("ZEHOS_RASTER_BENCH") != "1") return;
        RasterFrame frame = RasterScenes.Game(1, 0.5f, 11, -6, 0, 0);
        byte[] memory = (byte[])frame.Memory.Clone();
        double Frame(RasterDraw draw, RasterFill fill)
        {
            long start = Stopwatch.GetTimestamp();
            frame.Render(memory, draw, fill);
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        // Aufwaermen ueber Zeit, damit die Stufenuebersetzung beider Fassungen abgeschlossen ist. Danach Bild um Bild
        // abwechselnd messen und das schnellste Bild nehmen: Auf Hybrid-CPUs streut die Zeit je nach Kern stark.
        long warmup = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(warmup).TotalSeconds < 2)
        {
            Frame(WasmRasterizerReference.Draw, WasmRasterizerReference.Fill);
            Frame(WasmRasterizer.Draw, WasmRasterizer.Fill);
        }
        double reference = double.MaxValue, current = double.MaxValue;
        for (int round = 0; round < 300; round++)
        {
            reference = Math.Min(reference, Frame(WasmRasterizerReference.Draw, WasmRasterizerReference.Fill));
            current = Math.Min(current, Frame(WasmRasterizer.Draw, WasmRasterizer.Fill));
        }
        Console.WriteLine($"Rasterizer-Benchmark ({frame.Calls.Count} Aufrufe, {frame.Width}x{frame.Height}), schnellstes Bild: " +
            $"Referenz {reference:F2} ms, aktuell {current:F2} ms, Faktor {reference / current:F2}");
    }
}
