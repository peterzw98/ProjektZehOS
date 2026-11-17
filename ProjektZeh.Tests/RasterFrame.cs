using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using ProjektZeh.Graphics.Raster;

namespace ProjektZeh.Tests;

/// <summary>Ein Vertex in der Form, die die App ablegt; Farbe in Speicherreihenfolge R, G, B, A.</summary>
internal readonly record struct RasterVertex(float X, float Y, float Z, uint C, float U, float V);

internal delegate int RasterDraw(Span<byte> memory, uint call);

internal delegate int RasterFill(Span<byte> memory, uint offset, uint count, uint value);

/// <summary>
/// Ein vollstaendiges Bild als Speicherabbild: Puffer, Texturen, Vertices und alle Beschreibungen liegen fest im Speicher,
/// damit Bitgleichheitstests und Benchmark dieselben Aufrufe gegen beide Fassungen ausfuehren koennen.
/// </summary>
internal sealed class RasterFrame
{
    public const uint FarDepth = 0x4CBEBC20; // 1e8f wie ClassiCube beim Loeschen der Tiefe

    public readonly byte[] Memory;
    public readonly List<uint> Calls = new();
    public readonly int Width, Height, ColorStride, DepthStride;
    public readonly uint ColorAt, DepthAt;
    public uint ClearColor = 0xFF7FB2FF;
    private int used = 64;
    private readonly int limit;

    public RasterFrame(int capacity, int width, int height, int colorStride, int depthStride)
    {
        Memory = new byte[capacity];
        limit = capacity;
        Width = width;
        Height = height;
        ColorStride = colorStride;
        DepthStride = depthStride;
        ColorAt = Allocate(colorStride * height * 4);
        DepthAt = Allocate(depthStride * height * 4);
    }

    /// <summary>
    /// Puffer an festen Stellen (auch ueberlappend oder buendig am Speicherende); Vertices, Beschreibungen und Texturen
    /// ohne feste Stelle liegen unterhalb von allocationLimit. Der Speicher ist anfangs mit pattern gefuellt.
    /// </summary>
    public RasterFrame(int capacity, int width, int height, int colorStride, int depthStride, uint colorAt, uint depthAt,
        int allocationLimit, byte pattern)
    {
        Memory = new byte[capacity];
        Memory.AsSpan().Fill(pattern);
        limit = allocationLimit;
        Width = width;
        Height = height;
        ColorStride = colorStride;
        DepthStride = depthStride;
        ColorAt = colorAt;
        DepthAt = depthAt;
    }

    public uint Allocate(int bytes)
    {
        uint at = (uint)used;
        used += (bytes + 3) & ~3;
        if (used > limit) throw new InvalidOperationException("Testspeicher zu klein");
        return at;
    }

    public uint AddTexture(int width, int height, Func<int, int, uint> texel, uint? place = null)
    {
        uint at = place ?? Allocate(width * height * 4);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                BinaryPrimitives.WriteUInt32LittleEndian(Memory.AsSpan((int)at + (y * width + x) * 4), texel(x, y));
        return at;
    }

    /// <summary>Legt Vertices und eine Beschreibung ab. Der Aufruf wird erst in Render ausgefuehrt.</summary>
    public void AddCall(uint flags, IReadOnlyList<RasterVertex> vertices, uint textureAt, int texWidth, int texHeight, float[] mvp,
        float texOffsetU = 0, float texOffsetV = 0, int maxX = -1, int maxY = -1)
    {
        int stride = (flags & WasmRasterizer.FlagTextured) != 0 ? 24 : 16;
        uint verticesAt = Allocate(vertices.Count * stride);
        for (int index = 0; index < vertices.Count; index++)
        {
            Span<byte> at = Memory.AsSpan((int)verticesAt + index * stride);
            RasterVertex vertex = vertices[index];
            BinaryPrimitives.WriteSingleLittleEndian(at, vertex.X);
            BinaryPrimitives.WriteSingleLittleEndian(at.Slice(4), vertex.Y);
            BinaryPrimitives.WriteSingleLittleEndian(at.Slice(8), vertex.Z);
            BinaryPrimitives.WriteUInt32LittleEndian(at.Slice(12), vertex.C);
            if (stride == 24)
            {
                BinaryPrimitives.WriteSingleLittleEndian(at.Slice(16), vertex.U);
                BinaryPrimitives.WriteSingleLittleEndian(at.Slice(20), vertex.V);
            }
        }
        uint call = Allocate(WasmRasterizer.CallSize);
        byte[] memory = Memory;
        void U(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan((int)call + at), value);
        void F(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(memory.AsSpan((int)call + at), value);
        U(0, WasmRasterizer.Version); U(4, flags); U(8, verticesAt); U(12, (uint)vertices.Count);
        U(16, ColorAt); U(20, (uint)ColorStride); U(24, (uint)Width); U(28, (uint)Height);
        U(32, DepthAt); U(36, (uint)DepthStride); U(40, textureAt); U(44, (uint)texWidth); U(48, (uint)texHeight);
        U(52, (uint)(maxX < 0 ? Width - 1 : maxX)); U(56, (uint)(maxY < 0 ? Height - 1 : maxY));
        F(60, Width / 2.0f); F(64, Height / 2.0f); F(68, texOffsetU); F(72, texOffsetV);
        for (int index = 0; index < 16; index++) F(76 + index * 4, mvp[index]);
        Calls.Add(call);
    }

    /// <summary>Loescht Farbe und Tiefe und fuehrt alle Aufrufe aus; results erhaelt die Rueckgabewerte.</summary>
    public void Render(byte[] memory, RasterDraw draw, RasterFill fill, List<int>? results = null)
    {
        fill(memory, ColorAt, (uint)(ColorStride * Height), ClearColor);
        fill(memory, DepthAt, (uint)(DepthStride * Height), FarDepth);
        foreach (uint call in Calls)
        {
            int result = draw(memory, call);
            results?.Add(result);
        }
    }

    // Zeilenvektor mal Matrix wie ClassiCube: Ergebnis = a * b.
    public static float[] Multiply(float[] a, float[] b)
    {
        var result = new float[16];
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                float sum = 0;
                for (int k = 0; k < 4; k++) sum += a[row * 4 + k] * b[k * 4 + column];
                result[row * 4 + column] = sum;
            }
        return result;
    }

    public static float[] Perspective(float fovY, float aspect, float near, float far)
    {
        float f = 1.0f / MathF.Tan(fovY / 2);
        float a = far / (far - near);
        return new[] { f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, a, 1, 0, 0, -near * a, 0 };
    }

    public static float[] View(float x, float y, float z, float yaw, float pitch)
    {
        float[] translate = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -x, -y, -z, 1 };
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw), cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        float[] rotateY = { cy, 0, sy, 0, 0, 1, 0, 0, -sy, 0, cy, 0, 0, 0, 0, 1 };
        float[] rotateX = { 1, 0, 0, 0, 0, cp, sp, 0, 0, -sp, cp, 0, 0, 0, 0, 1 };
        return Multiply(Multiply(translate, rotateY), rotateX);
    }
}

/// <summary>
/// Baut eine ClassiCube-aehnliche Szene: Gelaende aus Bloecken mit Seitenflaechen, Baeume mit Alphatest, Wasser mit
/// Alphablending, Himmelsflaeche mit Near-Clipping und eine 2D-Oberflaeche mit Sprites. Fester Seed, damit Messung und
/// Vergleich reproduzierbar sind.
/// </summary>
internal static class RasterScenes
{
    private const int AtlasWidth = 16, AtlasHeight = 1024, Tiles = AtlasHeight / 16;
    private const int TileGrass = 0, TileDirt = 1, TileStone = 2, TileLeaves = 3, TileWater = 4, TileWood = 5;

    public static RasterFrame Game(int seed, float cameraX, float cameraY, float cameraZ, float yaw, float pitch,
        int width = 798, int height = 519)
    {
        var random = new Random(seed);
        var frame = new RasterFrame(24 << 20, width, height, width, width);
        uint atlas = frame.AddTexture(AtlasWidth, AtlasHeight, (x, y) =>
        {
            int tile = y / 16;
            uint rgb = (uint)(random.Next() & 0x00FFFFFF);
            if (tile == TileLeaves) return ((x + y) % 3 == 0 ? 0u : 0xFF000000u) | (rgb & 0x0030A030);
            if (tile == TileWater) return 0xA0000000u | (rgb & 0x002040FF);
            return 0xFF000000u | rgb;
        });
        uint gui = frame.AddTexture(256, 256, (x, y) => (uint)((x ^ y) & 4) != 0 ? 0xFFFFFFFF : (y % 7 == 0 ? 0x80404040u : 0u));

        float[] mvp = RasterFrame.Multiply(RasterFrame.View(cameraX, cameraY, cameraZ, yaw, pitch),
            RasterFrame.Perspective(70 * MathF.PI / 180, width / (float)height, 0.1f, 160));
        var camera = (X: cameraX, Y: cameraY, Z: cameraZ);

        // Himmel als grosse untexturierte Flaeche ueber der Kamera; sie schneidet die Near-Ebene.
        frame.AddCall(WasmRasterizer.FlagColorWrite | WasmRasterizer.FlagDepthTest, new[]
        {
            new RasterVertex(-300, 70, -300, 0xFFFFD0A0, 0, 0), new RasterVertex(300, 70, -300, 0xFFFFD0A0, 0, 0),
            new RasterVertex(300, 70, 300, 0xFFFFD0A0, 0, 0), new RasterVertex(-300, 70, 300, 0xFFFFD0A0, 0, 0),
        }, atlas, AtlasWidth, AtlasHeight, mvp);

        const int MinX = -48, MaxX = 48, MinZ = -10, MaxZ = 96;
        var heights = new int[MaxX - MinX + 1, MaxZ - MinZ + 1];
        int Height(int x, int z) => x < MinX || x > MaxX || z < MinZ || z > MaxZ ? 0 : heights[x - MinX, z - MinZ];
        for (int x = MinX; x <= MaxX; x++)
            for (int z = MinZ; z <= MaxZ; z++)
                heights[x - MinX, z - MinZ] = Math.Clamp((int)(3 + 2 * MathF.Sin(x * 0.21f) + 2.5f * MathF.Cos(z * 0.17f) + random.Next(0, 2)), 0, 9);

        var opaque = new List<RasterVertex>();
        var leaves = new List<RasterVertex>();
        var water = new List<RasterVertex>();
        void Flush(List<RasterVertex> quads, uint flags, bool force)
        {
            if (quads.Count == 0 || (!force && quads.Count < 4096)) return;
            frame.AddCall(flags, quads.ToArray(), atlas, AtlasWidth, AtlasHeight, mvp);
            quads.Clear();
        }
        const uint Opaque = WasmRasterizer.FlagColorWrite | WasmRasterizer.FlagDepthTest | WasmRasterizer.FlagDepthWrite
            | WasmRasterizer.FlagFaceCulling | WasmRasterizer.FlagTextured;

        // Flaeche aus vier Ecken im Uhrzeigersinn um die Normale; Windung so, dass nur zugewandte Flaechen sichtbar sind.
        void Face(List<RasterVertex> target, (float X, float Y, float Z)[] corners, (float X, float Y, float Z) normal, int tile, uint shade)
        {
            float toCamera = normal.X * (camera.X - corners[0].X) + normal.Y * (camera.Y - corners[0].Y) + normal.Z * (camera.Z - corners[0].Z);
            float v0 = tile / (float)Tiles, v1 = (tile + 1) / (float)Tiles;
            var quad = new[]
            {
                new RasterVertex(corners[0].X, corners[0].Y, corners[0].Z, shade, 0, v0),
                new RasterVertex(corners[1].X, corners[1].Y, corners[1].Z, shade, 1, v0),
                new RasterVertex(corners[2].X, corners[2].Y, corners[2].Z, shade, 1, v1),
                new RasterVertex(corners[3].X, corners[3].Y, corners[3].Z, shade, 0, v1),
            };
            double area = ScreenArea(quad[0], quad[2], quad[1], mvp, width, height);
            if (!double.IsNaN(area) && (area >= 0) != (toCamera > 0)) (quad[1], quad[3]) = (quad[3], quad[1]);
            target.AddRange(quad);
        }

        void Block(List<RasterVertex> target, int x, int y, int z, int tile, bool all)
        {
            float x0 = x, x1 = x + 1, y0 = y, y1 = y + 1, z0 = z, z1 = z + 1;
            if (all || Height(x, z) == y + 1)
                Face(target, new[] { (x0, y1, z0), (x1, y1, z0), (x1, y1, z1), (x0, y1, z1) }, (0, 1, 0), tile, 0xFFFFFFFF);
            if (all) Face(target, new[] { (x0, y0, z0), (x1, y0, z0), (x1, y0, z1), (x0, y0, z1) }, (0, -1, 0), tile, 0xFF7F7F7F);
            if (all || Height(x - 1, z) <= y) Face(target, new[] { (x0, y0, z0), (x0, y1, z0), (x0, y1, z1), (x0, y0, z1) }, (-1, 0, 0), tile, 0xFF999999);
            if (all || Height(x + 1, z) <= y) Face(target, new[] { (x1, y0, z0), (x1, y1, z0), (x1, y1, z1), (x1, y0, z1) }, (1, 0, 0), tile, 0xFF999999);
            if (all || Height(x, z - 1) <= y) Face(target, new[] { (x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0) }, (0, 0, -1), tile, 0xFFCCCCCC);
            if (all || Height(x, z + 1) <= y) Face(target, new[] { (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1) }, (0, 0, 1), tile, 0xFFCCCCCC);
        }

        for (int z = MinZ; z <= MaxZ; z++)
        {
            for (int x = MinX; x <= MaxX; x++)
            {
                int top = Height(x, z);
                for (int y = 0; y < top; y++)
                {
                    int tile = y == top - 1 ? TileGrass : y > top - 3 ? TileDirt : TileStone;
                    Block(opaque, x, y, z, tile, false);
                }
                if (top < 2) water.AddRange(new[]
                {
                    new RasterVertex(x, 1.8f, z, 0xFFFFFFFF, 0, TileWater / (float)Tiles),
                    new RasterVertex(x + 1, 1.8f, z, 0xFFFFFFFF, 1, TileWater / (float)Tiles),
                    new RasterVertex(x + 1, 1.8f, z + 1, 0xFFFFFFFF, 1, (TileWater + 1) / (float)Tiles),
                    new RasterVertex(x, 1.8f, z + 1, 0xFFFFFFFF, 0, (TileWater + 1) / (float)Tiles),
                });
                if (top >= 2 && random.Next(0, 60) == 0)
                {
                    for (int y = top; y < top + 3; y++) Block(opaque, x, y, z, TileWood, true);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dy = 3; dy <= 4; dy++) Block(leaves, x + dx, top + dy, z + dz, TileLeaves, true);
                }
                Flush(opaque, Opaque, false);
            }
        }
        Flush(opaque, Opaque, true);
        Flush(leaves, Opaque | WasmRasterizer.FlagAlphaTest, true);
        Flush(water, WasmRasterizer.FlagColorWrite | WasmRasterizer.FlagDepthTest | WasmRasterizer.FlagAlphaBlend | WasmRasterizer.FlagTextured, true);

        // Oberflaeche: Schnellleiste als 2D-Dreiecke, Text als Sprites, Fadenkreuz untexturiert.
        float[] identity = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var bar = new List<RasterVertex>();
        for (int slot = 0; slot < 9; slot++)
        {
            float left = width / 2 - 180 + slot * 40, topY = height - 44;
            bar.Add(new RasterVertex(left, topY, 0, 0xFFFFFFFF, 0, 0));
            bar.Add(new RasterVertex(left + 40, topY, 0, 0xFFFFFFFF, 0.25f, 0));
            bar.Add(new RasterVertex(left + 40, topY + 40, 0, 0xFFFFFFFF, 0.25f, 0.25f));
            bar.Add(new RasterVertex(left, topY + 40, 0, 0xFFFFFFFF, 0, 0.25f));
        }
        frame.AddCall(WasmRasterizer.FlagColorWrite | WasmRasterizer.Flag2D | WasmRasterizer.FlagAlphaBlend | WasmRasterizer.FlagTextured,
            bar, gui, 256, 256, identity);
        var text = new List<RasterVertex>();
        for (int glyph = 0; glyph < 240; glyph++)
        {
            float left = 4 + glyph % 60 * 9, topY = 4 + glyph / 60 * 10;
            float u0 = (glyph % 32) * 8 / 256f, v0 = (glyph / 32) * 8 / 256f, u1 = u0 + 8 / 256f, v1 = v0 + 8 / 256f;
            uint tint = glyph % 5 == 0 ? 0xFF40FFFF : 0xFFFFFFFF;
            text.Add(new RasterVertex(left, topY, 0, tint, u0, v0));
            text.Add(new RasterVertex(left + 8, topY, 0, tint, u1, v0));
            text.Add(new RasterVertex(left + 8, topY + 8, 0, tint, u1, v1));
            text.Add(new RasterVertex(left, topY + 8, 0, tint, u0, v1));
        }
        frame.AddCall(WasmRasterizer.FlagColorWrite | WasmRasterizer.Flag2D | WasmRasterizer.FlagSprite | WasmRasterizer.FlagAlphaBlend
            | WasmRasterizer.FlagTextured, text, gui, 256, 256, identity);
        float cx = width / 2f, cy = height / 2f;
        frame.AddCall(WasmRasterizer.FlagColorWrite | WasmRasterizer.Flag2D, new[]
        {
            new RasterVertex(cx - 8, cy - 1, 0, 0xFFFFFFFF, 0, 0), new RasterVertex(cx + 8, cy - 1, 0, 0xFFFFFFFF, 0, 0),
            new RasterVertex(cx + 8, cy + 1, 0, 0xFFFFFFFF, 0, 0), new RasterVertex(cx - 8, cy + 1, 0, 0xFFFFFFFF, 0, 0),
        }, gui, 256, 256, identity);
        return frame;
    }

    // Vorzeichen der Flaeche wie DrawTriangle3D (Kanten auf ganzzahligen Bildschirmecken); NaN hinter der Kamera.
    private static double ScreenArea(RasterVertex a, RasterVertex b, RasterVertex c, float[] mvp, int width, int height)
    {
        (double X, double Y, double W) Project(RasterVertex v) => (
            v.X * mvp[0] + v.Y * mvp[4] + v.Z * mvp[8] + mvp[12],
            v.X * mvp[1] + v.Y * mvp[5] + v.Z * mvp[9] + mvp[13],
            v.X * mvp[3] + v.Y * mvp[7] + v.Z * mvp[11] + mvp[15]);
        var pa = Project(a); var pb = Project(b); var pc = Project(c);
        if (pa.W <= 0 || pb.W <= 0 || pc.W <= 0) return double.NaN;
        double ax = Math.Floor(width / 2.0 * (1 + pa.X / pa.W)), ay = Math.Floor(height / 2.0 * (1 - pa.Y / pa.W));
        double bx = Math.Floor(width / 2.0 * (1 + pb.X / pb.W)), by = Math.Floor(height / 2.0 * (1 - pb.Y / pb.W));
        double cx = Math.Floor(width / 2.0 * (1 + pc.X / pc.W)), cy = Math.Floor(height / 2.0 * (1 - pc.Y / pc.W));
        return (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    }

    /// <summary>Zufaellige, auch entartete Aufrufe auf kleinem Puffer: alle Flags, Clipping, NaN, riesige Werte, Zeilenabstaende.</summary>
    public static RasterFrame Random(int seed)
    {
        var random = new Random(seed);
        int width = random.Next(1, 72), height = random.Next(1, 56);
        var frame = new RasterFrame(1 << 20, width, height, width + random.Next(0, 5), width + random.Next(0, 5));
        frame.ClearColor = (uint)random.Next();
        var textures = new List<(uint At, int W, int H)>();
        for (int index = 0; index < 3; index++)
        {
            int w = 1 << random.Next(0, 6), h = 1 << random.Next(0, 6);
            textures.Add((frame.AddTexture(w, h, (_, _) => (uint)random.Next() | (random.Next(0, 3) == 0 ? 0xFF000000u : 0u)), w, h));
        }
        RandomCalls(frame, random, textures, random.Next(1, 12), 0, 0);
        return frame;
    }

    /// <summary>Haengt zufaellige Aufrufe an; requiredFlags werden immer gesetzt, forbiddenFlags nie, fullScissor nutzt den ganzen Puffer, finite vermeidet NaN, Unendlich und riesige Werte.</summary>
    public static void RandomCalls(RasterFrame frame, Random random, List<(uint At, int W, int H)> textures, int calls,
        uint requiredFlags, uint forbiddenFlags, bool fullScissor = false, bool finite = false)
    {
        int width = frame.Width, height = frame.Height;
        float Coordinate(float range)
        {
            int kind = random.Next(0, 40);
            if (kind < 3 && finite) kind = 3;
            if (kind == 0) return float.NaN;
            if (kind == 1) return random.Next(0, 2) == 0 ? float.PositiveInfinity : float.NegativeInfinity;
            if (kind == 2) return (random.NextSingle() - 0.5f) * 1e9f;
            if (kind < 6) return MathF.Round((random.NextSingle() - 0.5f) * range * 2);
            return (random.NextSingle() - 0.5f) * range * 2;
        }
        for (int call = 0; call < calls; call++)
        {
            uint flags = ((uint)random.Next(0, 512) | requiredFlags) & ~forbiddenFlags;
            bool twoD = (flags & WasmRasterizer.Flag2D) != 0;
            float[] mvp;
            int matrixKind = random.Next(0, 3);
            if (matrixKind == 0)
                mvp = RasterFrame.Multiply(RasterFrame.View(Coordinate(2), Coordinate(2), Coordinate(2) - 3, random.NextSingle() * 6, random.NextSingle() - 0.5f),
                    RasterFrame.Perspective(1.2f, width / (float)height, 0.1f, 50));
            else
            {
                mvp = new float[16];
                for (int index = 0; index < 16; index++) mvp[index] = matrixKind == 1 ? (random.NextSingle() - 0.5f) * 4 : Coordinate(2);
            }
            var vertices = new List<RasterVertex>();
            int quads = random.Next(0, 24);
            for (int quad = 0; quad < quads; quad++)
            {
                float range = twoD ? Math.Max(width, height) : 4;
                var first = new RasterVertex(twoD ? Coordinate(range) + width / 2f : Coordinate(range), twoD ? Coordinate(range) + height / 2f : Coordinate(range),
                    Coordinate(range), (uint)random.Next() | (random.Next(0, 2) == 0 ? 0xFF000000u : 0u), Coordinate(2), Coordinate(2));
                vertices.Add(first);
                for (int corner = 1; corner < 4; corner++)
                {
                    // Teilweise wiederholte oder achsparallele Ecken, damit entartete Dreiecke und Sprite-Rechtecke vorkommen.
                    int shape = random.Next(0, 4);
                    if (shape == 0) vertices.Add(first);
                    else vertices.Add(new RasterVertex(shape == 1 ? first.X : first.X + Coordinate(range / 2), shape == 2 ? first.Y : first.Y + Coordinate(range / 2),
                        first.Z + Coordinate(1), first.C, first.U + Coordinate(1), first.V + Coordinate(1)));
                }
            }
            var texture = textures[random.Next(0, textures.Count)];
            frame.AddCall(flags, vertices, texture.At, texture.W, texture.H, mvp, random.Next(0, 4) == 0 ? Coordinate(1) : 0,
                random.Next(0, 4) == 0 ? Coordinate(1) : 0, fullScissor ? width - 1 : random.Next(0, width), fullScissor ? height - 1 : random.Next(0, height));
        }
    }
}
