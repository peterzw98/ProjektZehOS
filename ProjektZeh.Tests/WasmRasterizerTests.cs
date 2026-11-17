using System;
using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjektZeh.Graphics.Raster;

namespace ProjektZeh.Tests;

[TestClass]
public sealed class WasmRasterizerTests
{
    // Feste Ablage im Testspeicher: Beschreibung, Vertices, Farb-, Tiefen- und Texturpuffer.
    private const int CallAt = 0, VerticesAt = 256, ColorAt = 8192, DepthAt = 16384, TextureAt = 24576, MemorySize = 32768;
    private const int Width = 8, Height = 8;
    private const uint Black = 0xFF000000;

    private sealed class Scene
    {
        public readonly byte[] Memory = new byte[MemorySize];
        public uint Flags = WasmRasterizer.FlagColorWrite;
        public uint Count;
        public uint TexWidth = 1, TexHeight = 1;
        public float[] Mvp = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        public Scene()
        {
            for (int index = 0; index < Width * Height; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(Memory.AsSpan(ColorAt + index * 4), Black);
                BinaryPrimitives.WriteSingleLittleEndian(Memory.AsSpan(DepthAt + index * 4), 1e8f);
            }
            SetTexel(0, 0xFFFFFFFF);
        }

        public void SetTexel(int index, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Memory.AsSpan(TextureAt + index * 4), value);

        public uint Pixel(int x, int y) => BinaryPrimitives.ReadUInt32LittleEndian(Memory.AsSpan(ColorAt + (y * Width + x) * 4));

        /// <summary>Haengt ein Quad an; Farbe in Speicherreihenfolge R, G, B, A wie ClassiCubes PackedCol.</summary>
        public void Quad(uint color, params (float X, float Y, float Z, float U, float V)[] corners)
        {
            int stride = (Flags & WasmRasterizer.FlagTextured) != 0 ? 24 : 16;
            foreach (var corner in corners)
            {
                Span<byte> at = Memory.AsSpan(VerticesAt + (int)Count * stride);
                BinaryPrimitives.WriteSingleLittleEndian(at, corner.X);
                BinaryPrimitives.WriteSingleLittleEndian(at.Slice(4), corner.Y);
                BinaryPrimitives.WriteSingleLittleEndian(at.Slice(8), corner.Z);
                BinaryPrimitives.WriteUInt32LittleEndian(at.Slice(12), color);
                if (stride == 24)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(at.Slice(16), corner.U);
                    BinaryPrimitives.WriteSingleLittleEndian(at.Slice(20), corner.V);
                }
                Count++;
            }
        }

        public void WriteCall(uint version = WasmRasterizer.Version)
        {
            void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Memory.AsSpan(CallAt + at), value);
            void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(Memory.AsSpan(CallAt + at), value);
            U(0, version); U(4, Flags); U(8, VerticesAt); U(12, Count);
            U(16, ColorAt); U(20, Width); U(24, Width); U(28, Height);
            U(32, DepthAt); U(36, Width); U(40, TextureAt); U(44, TexWidth); U(48, TexHeight);
            U(52, Width - 1); U(56, Height - 1);
            F(60, Width / 2.0f); F(64, Height / 2.0f); F(68, 0); F(72, 0);
            for (int index = 0; index < 16; index++) F(76 + index * 4, Mvp[index]);
        }

        public int Draw()
        {
            WriteCall();
            return WasmRasterizer.Draw(Memory, CallAt);
        }

        public void SetCallField(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Memory.AsSpan(CallAt + at), value);
    }

    private static readonly uint Red = 0xFF0000FF;     // PackedCol R=255, A=255
    private const uint RedPixel = 0xFFFF0000;          // BitmapCol 0xAARRGGBB

    private static int Count(Scene scene, uint value)
    {
        int count = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (scene.Pixel(x, y) == value) count++;
        return count;
    }

    [TestMethod]
    public void FillWritesValuesAndChecksRange()
    {
        var memory = new byte[64];
        Assert.AreEqual(WasmRasterizer.Success, WasmRasterizer.Fill(memory, 8, 4, 0x11223344));
        Assert.AreEqual(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(20)));
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(24)));
        Assert.AreEqual(WasmRasterizer.OutOfBounds, WasmRasterizer.Fill(memory, 60, 2, 0));
        Assert.AreEqual(WasmRasterizer.OutOfBounds, WasmRasterizer.Fill(memory, uint.MaxValue - 2, 1, 0));
        Assert.AreEqual(WasmRasterizer.InvalidArgument, WasmRasterizer.Fill(memory, 0, uint.MaxValue, 0));
    }

    [TestMethod]
    public void Flat2DQuadCoversPixelCentersInside()
    {
        var scene = new Scene { Flags = WasmRasterizer.FlagColorWrite | WasmRasterizer.Flag2D };
        scene.Quad(Red, (1, 1, 0, 0, 0), (5, 1, 0, 0, 0), (5, 5, 0, 0, 0), (1, 5, 0, 0, 0));
        Assert.AreEqual(WasmRasterizer.Success, scene.Draw());
        Assert.AreEqual(16, Count(scene, RedPixel));
        Assert.AreEqual(RedPixel, scene.Pixel(1, 1));
        Assert.AreEqual(RedPixel, scene.Pixel(4, 4));
        Assert.AreEqual(Black, scene.Pixel(5, 5));
        Assert.AreEqual(Black, scene.Pixel(0, 0));
    }

    [TestMethod]
    public void AlphaBlendMixesWithDestination()
    {
        var scene = new Scene { Flags = WasmRasterizer.FlagColorWrite | WasmRasterizer.Flag2D | WasmRasterizer.FlagAlphaBlend };
        scene.Quad(0x80FFFFFF, (0, 0, 0, 0, 0), (8, 0, 0, 0, 0), (8, 8, 0, 0, 0), (0, 8, 0, 0, 0));
        Assert.AreEqual(WasmRasterizer.Success, scene.Draw());
        // dst + (A * (src - dst) >> 8) = 0 + (128 * 255 >> 8) = 127
        Assert.AreEqual(0xFF7F7F7Fu, scene.Pixel(5, 2));
        // Pixelmitten genau auf der gemeinsamen Diagonale zaehlen wie in SoftGPU zu beiden Dreiecken: zweimal gemischt.
        Assert.AreEqual(0xFFBFBFBFu, scene.Pixel(3, 3));
    }

    [TestMethod]
    public void AlphaTestDiscardsTransparentTexels()
    {
        var scene = new Scene
        {
            Flags = WasmRasterizer.FlagColorWrite | WasmRasterizer.Flag2D | WasmRasterizer.FlagAlphaTest | WasmRasterizer.FlagTextured,
            TexWidth = 2,
        };
        scene.SetTexel(0, 0x40FFFFFF);
        scene.SetTexel(1, 0xFF00FF00);
        // Linke Haelfte Texel 0 (verworfen), rechte Haelfte Texel 1 (gruen).
        scene.Quad(0xFFFFFFFF, (0, 0, 0, 0, 0), (8, 0, 0, 1, 0), (8, 8, 0, 1, 1), (0, 8, 0, 0, 1));
        Assert.AreEqual(WasmRasterizer.Success, scene.Draw());
        Assert.AreEqual(Black, scene.Pixel(1, 4));
        Assert.AreEqual(0xFF00FE00u, scene.Pixel(6, 4)); // (255 * 255) >> 8 = 254
    }

    [TestMethod]
    public void TextureCoordinatesWrapAround()
    {
        var scene = new Scene
        {
            Flags = WasmRasterizer.FlagColorWrite | WasmRasterizer.Flag2D | WasmRasterizer.FlagTextured,
            TexWidth = 2,
        };
        scene.SetTexel(0, 0xFFFF0000);
        scene.SetTexel(1, 0xFF0000FF);
        // u von 0 bis 2: Die Textur erscheint zweimal nebeneinander.
        scene.Quad(0xFFFFFFFF, (0, 0, 0, 0, 0), (8, 0, 0, 2, 0), (8, 8, 0, 2, 1), (0, 8, 0, 0, 1));
        Assert.AreEqual(WasmRasterizer.Success, scene.Draw());
        Assert.AreEqual(scene.Pixel(0, 4), scene.Pixel(4, 4));
        Assert.AreNotEqual(scene.Pixel(0, 4), scene.Pixel(2, 4));
    }

    private static Scene Scene3D(uint extraFlags = 0) => new()
    {
        Flags = WasmRasterizer.FlagColorWrite | WasmRasterizer.FlagDepthTest | WasmRasterizer.FlagDepthWrite | extraFlags,
    };

    [TestMethod]
    public void DepthTestKeepsNearerQuadRegardlessOfOrder()
    {
        foreach (bool nearFirst in new[] { true, false })
        {
            var scene = Scene3D();
            void Near() => scene.Quad(0xFF00FF00, (-1, 1, 0.2f, 0, 0), (1, 1, 0.2f, 0, 0), (1, -1, 0.2f, 0, 0), (-1, -1, 0.2f, 0, 0));
            void Far() => scene.Quad(Red, (-1, 1, 0.8f, 0, 0), (1, 1, 0.8f, 0, 0), (1, -1, 0.8f, 0, 0), (-1, -1, 0.8f, 0, 0));
            if (nearFirst) { Near(); Far(); } else { Far(); Near(); }
            Assert.AreEqual(WasmRasterizer.Success, scene.Draw());
            Assert.AreEqual(0xFF00FF00u, scene.Pixel(4, 4), nearFirst ? "nah zuerst" : "fern zuerst");
            Assert.AreEqual(0, Count(scene, RedPixel));
        }
    }

    [TestMethod]
    public void FaceCullingDropsBackFaces()
    {
        var front = Scene3D(WasmRasterizer.FlagFaceCulling);
        front.Quad(Red, (-1, 1, 0.5f, 0, 0), (1, 1, 0.5f, 0, 0), (1, -1, 0.5f, 0, 0), (-1, -1, 0.5f, 0, 0));
        Assert.AreEqual(WasmRasterizer.Success, front.Draw());
        var back = Scene3D(WasmRasterizer.FlagFaceCulling);
        back.Quad(Red, (-1, -1, 0.5f, 0, 0), (1, -1, 0.5f, 0, 0), (1, 1, 0.5f, 0, 0), (-1, 1, 0.5f, 0, 0));
        Assert.AreEqual(WasmRasterizer.Success, back.Draw());
        // Genau eine der beiden Windungen ist sichtbar.
        Assert.AreNotEqual(Count(front, RedPixel) > 0, Count(back, RedPixel) > 0);
    }

    [TestMethod]
    public void NearClippingDrawsVisiblePartOnly()
    {
        // Perspektive mit w = -z: Ecken mit z < 0 liegen hinter der Kamera.
        float[] perspective = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0 };
        var partial = Scene3D();
        partial.Mvp = perspective;
        partial.Quad(Red, (-1, 1, 1, 0, 0), (1, 1, 1, 0, 0), (1, -1, -1, 0, 0), (-1, -1, -1, 0, 0));
        Assert.AreEqual(WasmRasterizer.Success, partial.Draw());
        int drawn = Count(partial, RedPixel);
        Assert.IsTrue(drawn > 0 && drawn < Width * Height, $"teilweise sichtbar, {drawn} Pixel");

        var hidden = Scene3D();
        hidden.Mvp = perspective;
        hidden.Quad(Red, (-1, 1, -1, 0, 0), (1, 1, -1, 0, 0), (1, -1, -1, 0, 0), (-1, -1, -1, 0, 0));
        Assert.AreEqual(WasmRasterizer.Success, hidden.Draw());
        Assert.AreEqual(0, Count(hidden, RedPixel));
    }

    [TestMethod]
    public void EveryNearClippingMaskIsHandled()
    {
        // Ecken mit z = -1 liegen vor der Near-Ebene (z = 0) und werden abgeschnitten; w = z + 2 bleibt dabei positiv
        // wie bei einer echten Projektion. Masken 5 und 10 (gegenueberliegende Ecken) sind wie in SoftGPU entartet und
        // zeichnen nichts; alle anderen Masken mit sichtbaren Ecken zeichnen.
        float[] perspective = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 2 };
        (float X, float Y)[] corners = { (-1, 1), (1, 1), (1, -1), (-1, -1) };
        for (int mask = 0; mask < 16; mask++)
        {
            var scene = Scene3D();
            scene.Flags &= ~WasmRasterizer.FlagDepthTest;
            scene.Mvp = perspective;
            var quad = new (float, float, float, float, float)[4];
            for (int corner = 0; corner < 4; corner++)
                quad[corner] = (corners[corner].X, corners[corner].Y, (mask & (1 << corner)) != 0 ? 1 : -1, 0, 0);
            scene.Quad(Red, quad);
            Assert.AreEqual(WasmRasterizer.Success, scene.Draw(), $"Maske {mask}");
            int drawn = Count(scene, RedPixel);
            bool expectDrawn = mask != 0 && mask != 5 && mask != 10;
            Assert.AreEqual(expectDrawn, drawn > 0, $"Maske {mask}: {drawn} Pixel");
        }
    }

    private static Scene SpriteScene()
    {
        var scene = new Scene
        {
            Flags = WasmRasterizer.FlagColorWrite | WasmRasterizer.Flag2D | WasmRasterizer.FlagSprite | WasmRasterizer.FlagTextured,
            TexWidth = 4,
            TexHeight = 4,
        };
        for (int index = 0; index < 16; index++) scene.SetTexel(index, 0xFF000000u | (uint)(index * 16) << 16 | (uint)index);
        return scene;
    }

    private static uint Texel(int x, int y) => 0xFF000000u | (uint)((y * 4 + x) * 16) << 16 | (uint)(y * 4 + x);

    [TestMethod]
    public void SpriteFastPathCopiesTexelsOneToOne()
    {
        // Breite 3 und u-Spanne 3 Texel: schneller Pfad, Pixel (x, y) zeigt Texel (x, y).
        var scene = SpriteScene();
        scene.Quad(0xFFFFFFFF, (0, 0, 0, 0, 0), (3, 0, 0, 0.75f, 0), (3, 3, 0, 0.75f, 0.75f), (0, 3, 0, 0, 0.75f));
        Assert.AreEqual(WasmRasterizer.Success, scene.Draw());
        for (int y = 0; y <= 3; y++)
            for (int x = 0; x <= 3; x++)
                Assert.AreEqual(Texel(x, y), scene.Pixel(x, y), $"({x},{y})");
    }

    [TestMethod]
    public void SpriteSlowPathScalesAndTintsWithVertexColor()
    {
        // Breite 8 bei 4 Texeln: langsamer Pfad mit Skalierung; Vertexfarbe halbiert Rot (R=128 in PackedCol).
        var scene = SpriteScene();
        scene.Quad(0xFFFFFF80, (0, 0, 0, 0, 0), (8, 0, 0, 1, 0), (8, 8, 0, 1, 1), (0, 8, 0, 0, 1));
        Assert.AreEqual(WasmRasterizer.Success, scene.Draw());
        uint texel = Texel(1, 1);  // Pixel (2, 2) -> Texel (4*2/8, 4*2/8)
        uint expected = 0xFF000000u | (uint)((128 * (int)((texel >> 16) & 0xFF)) >> 8) << 16 | (uint)((255 * (int)(texel & 0xFF)) >> 8);
        Assert.AreEqual(expected, scene.Pixel(2, 2));
    }

    [TestMethod]
    public void SinglePixelWideTextureUsesOneColorPerTriangle()
    {
        var scene = Scene3D(WasmRasterizer.FlagTextured);
        scene.TexWidth = 1;
        scene.TexHeight = 2;
        scene.SetTexel(0, 0xFFFF0000);
        scene.SetTexel(1, 0xFF00FF00);
        scene.Quad(0xFFFFFFFF, (-1, 1, 0.5f, 0, 0), (1, 1, 0.5f, 0, 0), (1, -1, 0.5f, 0, 0), (-1, -1, 0.5f, 0, 0));
        Assert.AreEqual(WasmRasterizer.Success, scene.Draw());
        Assert.AreEqual(0xFFFE0000u, scene.Pixel(4, 4)); // (255 * 255) >> 8 = 254, Zeile 0 der Textur
    }

    [TestMethod]
    public void InvalidDescriptionsAreRejected()
    {
        var scene = new Scene { Flags = WasmRasterizer.FlagColorWrite | WasmRasterizer.Flag2D };
        scene.Quad(Red, (0, 0, 0, 0, 0), (1, 0, 0, 0, 0), (1, 1, 0, 0, 0), (0, 1, 0, 0, 0));
        void Expect(int expected, int at, uint value, string reason)
        {
            scene.WriteCall();
            scene.SetCallField(at, value);
            Assert.AreEqual(expected, WasmRasterizer.Draw(scene.Memory, CallAt), reason);
        }
        Expect(WasmRasterizer.InvalidArgument, 0, 2, "Version");
        Expect(WasmRasterizer.InvalidArgument, 4, 1u << 12, "unbekanntes Flag");
        Expect(WasmRasterizer.InvalidArgument, 12, 3, "count nicht durch 4 teilbar");
        Expect(WasmRasterizer.InvalidArgument, 12, WasmRasterizer.MaxVertices + 4, "zu viele Vertices");
        Expect(WasmRasterizer.InvalidArgument, 24, 0, "Breite 0");
        Expect(WasmRasterizer.InvalidArgument, 28, WasmRasterizer.MaxHeight + 1, "Hoehe zu gross");
        Expect(WasmRasterizer.InvalidArgument, 20, Width - 1, "Zeilenabstand kleiner als Breite");
        Expect(WasmRasterizer.InvalidArgument, 44, 3, "Texturbreite keine Zweierpotenz");
        Expect(WasmRasterizer.InvalidArgument, 48, 8192, "Texturhoehe zu gross");
        Expect(WasmRasterizer.InvalidArgument, 52, Width, "Scissor ausserhalb");
        Expect(WasmRasterizer.OutOfBounds, 8, MemorySize - 8, "Vertices ausserhalb");
        Expect(WasmRasterizer.OutOfBounds, 16, MemorySize - 64, "Farbpuffer ausserhalb");
        Expect(WasmRasterizer.OutOfBounds, 40, uint.MaxValue - 1, "Textur ueberlaeuft");
        Assert.AreEqual(WasmRasterizer.OutOfBounds, WasmRasterizer.Draw(scene.Memory, MemorySize - 100), "Beschreibung ausserhalb");
    }

    [TestMethod]
    public void DepthBufferIsCheckedOnlyWhenUsed()
    {
        var scene = Scene3D();
        scene.Quad(Red, (-1, 1, 0.5f, 0, 0), (1, 1, 0.5f, 0, 0), (1, -1, 0.5f, 0, 0), (-1, -1, 0.5f, 0, 0));
        scene.WriteCall();
        scene.SetCallField(32, MemorySize - 16);
        Assert.AreEqual(WasmRasterizer.OutOfBounds, WasmRasterizer.Draw(scene.Memory, CallAt));
        scene.Flags = WasmRasterizer.FlagColorWrite;
        scene.WriteCall();
        scene.SetCallField(32, MemorySize - 16);
        Assert.AreEqual(WasmRasterizer.Success, WasmRasterizer.Draw(scene.Memory, CallAt));
    }

    [TestMethod]
    public void NonFiniteCoordinatesNeverLeaveTheBuffers()
    {
        foreach (uint mode in new[] { WasmRasterizer.Flag2D, WasmRasterizer.Flag2D | WasmRasterizer.FlagSprite, 0u })
        {
            var scene = new Scene { Flags = WasmRasterizer.FlagColorWrite | WasmRasterizer.FlagTextured | WasmRasterizer.FlagDepthTest | mode };
            scene.Quad(Red, (float.NaN, 0, 0, 0, 0), (float.PositiveInfinity, 1, 0, 1e30f, 0), (1e20f, -1e20f, 0.5f, float.NaN, 1), (-3e9f, 3e9f, 0, -1e10f, 1));
            Assert.AreEqual(WasmRasterizer.Success, scene.Draw());
        }
    }

    [TestMethod]
    public void RandomDescriptionsNeverThrow()
    {
        // Jede Ausnahme wuerde im Kernel die native Grenze erreichen; Fehler muessen als Rueckgabewert kommen.
        var random = new Random(4711);
        var memory = new byte[MemorySize];
        for (int round = 0; round < 3000; round++)
        {
            random.NextBytes(memory);
            Span<byte> call = memory.AsSpan(CallAt);
            // Teilweise gueltige Kopfwerte, damit die Zeichenpfade tatsaechlich erreicht werden.
            BinaryPrimitives.WriteUInt32LittleEndian(call, WasmRasterizer.Version);
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(4), (uint)random.Next(0, 512));
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(8), VerticesAt);
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(12), (uint)random.Next(0, 16) * 4);
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(16), ColorAt);
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(20), Width);
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(24), Width);
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(28), Height);
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(32), DepthAt);
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(36), Width);
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(40), TextureAt);
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(44), 1u << random.Next(0, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(48), 1u << random.Next(0, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(52), (uint)random.Next(0, Width));
            BinaryPrimitives.WriteUInt32LittleEndian(call.Slice(56), (uint)random.Next(0, Height));
            int result = WasmRasterizer.Draw(memory, CallAt);
            Assert.IsTrue(result is WasmRasterizer.Success or WasmRasterizer.InvalidArgument, $"Runde {round}: {result}");
        }
    }
}
