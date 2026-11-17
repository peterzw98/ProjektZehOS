using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private static byte[] AssertSame(RasterFrame frame, string scene)
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
        // NaN sind vorzeichenunabhaengig, Bild und weiteres Zeichnen bleiben deshalb gleich. Tiefenwerte, die in den
        // Farbpuffer hineinragen (ueberlappende Puffer), werden ohne Ausnahme verglichen: Dort waeren es Farben.
        int colorEnd = (int)frame.ColorAt + frame.ColorStride * frame.Height * 4;
        for (int at = (int)frame.DepthAt; at + 4 <= (int)frame.DepthAt + frame.DepthStride * frame.Height * 4; at += 4)
        {
            if (at + 4 > (int)frame.ColorAt && at < colorEnd) continue;
            if (float.IsNaN(BitConverter.ToSingle(expected, at)) && float.IsNaN(BitConverter.ToSingle(actual, at)))
                Array.Copy(expected, at, actual, at, 4);
        }
        int difference = expected.AsSpan().CommonPrefixLength(actual);
        Assert.AreEqual(expected.Length, difference, $"{scene}: erste Abweichung bei Byte {difference}");
        return actual;
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

    private const byte Pattern = 0xA5;

    [TestMethod]
    public void BuffersFlushWithMemoryEndMatchReference()
    {
        // Breite durch 4 teilbar und Scissor bis zur letzten Spalte: Der Vektorpfad laeuft bis an das Pufferende. Jeder
        // Puffer liegt einmal buendig am Speicherende; Pruefmuster in den Luecken davor muessen unveraendert bleiben.
        const int Width = 64, Height = 40, TexSize = 32, Gap = 64, Low = 128 << 10;
        int bufferBytes = Width * Height * 4, textureBytes = TexSize * TexSize * 4;
        foreach (string lastBuffer in new[] { "Farbe", "Tiefe", "Textur" })
        {
            var order = new List<string> { "Farbe", "Tiefe", "Textur" };
            order.Remove(lastBuffer);
            order.Add(lastBuffer);
            var places = new Dictionary<string, int>();
            var gaps = new List<int>();
            int at = Low;
            foreach (string buffer in order)
            {
                gaps.Add(at);
                at += Gap;
                places[buffer] = at;
                at += buffer == "Textur" ? textureBytes : bufferBytes;
            }
            var frame = new RasterFrame(at, Width, Height, Width, Width, (uint)places["Farbe"], (uint)places["Tiefe"], Low, Pattern);
            var random = new Random(4242);
            uint texture = frame.AddTexture(TexSize, TexSize, (_, _) => (uint)random.Next() | (random.Next(0, 3) == 0 ? 0xFF000000u : 0u),
                (uint)places["Textur"]);
            float[] identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            const uint Flags = WasmRasterizer.FlagColorWrite | WasmRasterizer.FlagDepthTest | WasmRasterizer.FlagDepthWrite
                | WasmRasterizer.FlagTextured;
            foreach (float z in new[] { 0.5f, 0.8f, 0.2f })
            {
                frame.AddCall(Flags, new[]
                {
                    new RasterVertex(-1.5f, 1.5f, z, 0xFFFFFFFF, 0, 0), new RasterVertex(1.5f, 1.5f, z, 0xFFFFFFFF, 2, 0),
                    new RasterVertex(1.5f, -1.5f, z, 0xFFFFFFFF, 2, 2), new RasterVertex(-1.5f, -1.5f, z, 0xFFFFFFFF, 0, 2),
                }, texture, TexSize, TexSize, identity);
            }
            RasterScenes.RandomCalls(frame, random, new List<(uint, int, int)> { (texture, TexSize, TexSize) }, 12,
                WasmRasterizer.FlagDepthTest | WasmRasterizer.FlagColorWrite, WasmRasterizer.Flag2D, fullScissor: true);
            byte[] memory = AssertSame(frame, $"{lastBuffer} am Speicherende");
            foreach (int gap in gaps)
            {
                Assert.IsTrue(Array.TrueForAll(memory[gap..(gap + Gap)], value => value == Pattern), $"{lastBuffer} am Ende: Luecke bei {gap} veraendert");
            }
        }
    }

    [TestMethod]
    public void OverlappingDepthAndColorMatchReference()
    {
        // Tiefenpuffer innerhalb oder ueberlappend mit dem Farbpuffer: Der Vektorpfad ist dann abgeschaltet (separate == false).
        // Nur endliche Eingaben: Das Vorzeichen einer NaN-Tiefe ist nicht festgelegt (siehe AssertSame) und landete hier im
        // Farbpuffer; verglichen wird deshalb ohne NaN-Quellen, dafuer Byte fuer Byte.
        const int Low = 256 << 10, Buffers = 512 << 10;
        for (int seed = 0; seed < 300; seed++)
        {
            var random = new Random(seed);
            int width = random.Next(4, 64), height = random.Next(1, 40), stride = width + random.Next(0, 4);
            int colorBytes = stride * height * 4;
            int shift = random.Next(0, 4) == 0 ? 0 : random.Next(-colorBytes / 2, colorBytes / 2) & ~(random.Next(0, 3) == 0 ? 0 : 3);
            uint colorAt = (uint)(Buffers + colorBytes), depthAt = (uint)(colorAt + shift);
            var frame = new RasterFrame(Buffers + 4 * colorBytes, width, height, stride, stride, colorAt, depthAt, Low, Pattern);
            var textures = new List<(uint At, int W, int H)>();
            for (int index = 0; index < 2; index++)
            {
                int w = 1 << random.Next(0, 6), h = 1 << random.Next(0, 6);
                textures.Add((frame.AddTexture(w, h, (_, _) => (uint)random.Next() | (random.Next(0, 3) == 0 ? 0xFF000000u : 0u)), w, h));
            }
            RasterScenes.RandomCalls(frame, random, textures, random.Next(1, 10),
                seed % 2 == 0 ? WasmRasterizer.FlagDepthTest | WasmRasterizer.FlagColorWrite : 0, WasmRasterizer.Flag2D, fullScissor: seed % 3 == 0,
                finite: true);
            AssertSame(frame, $"Ueberlappung {seed} (Versatz {shift})");
        }
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
