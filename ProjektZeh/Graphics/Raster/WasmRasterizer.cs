using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ProjektZeh.Graphics.Raster;

/// <summary>
/// Software-Rasterizer fuer Wasm-Apps (zehos_v1.raster_draw und raster_fill, Vertrag in docs/PAKETE_ERSTELLEN.md).
/// Zeichnet Quads direkt in Farb- und Tiefenpuffer im linearen Speicher der App; der Kernel haelt keinen Zustand.
/// Die Rechenschritte sind aus ClassiCube Graphics_SoftGPU.c uebertragen (BSD-3-Clause, Copyright (c) 2014 - 2024,
/// UnknownShadow200), damit das Bild dem Wasm-Renderer der App gleicht. Ganzzahlen laufen wie i32 in Wasm ueber,
/// Gleitkomma wird nie zusammengezogen; die Umwandlung nach int bildet die Absenkung von clang fuer wasm32 nach.
/// </summary>
internal static class WasmRasterizer
{
    /// <summary>Erfolg (WASI).</summary>
    public const int Success = 0;
    /// <summary>Unzulaessige Werte in der Beschreibung (WASI EINVAL).</summary>
    public const int InvalidArgument = 28;
    /// <summary>Ein Bereich liegt nicht im linearen Speicher; die Bruecke loest daraus einen Trap aus.</summary>
    public const int OutOfBounds = -1;

    public const int CallSize = 140;
    public const int Version = 1;
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1200;
    public const int MaxTextureSize = 4096;
    public const int MaxVertices = 262144;

    public const uint FlagDepthTest = 1 << 0;
    public const uint FlagDepthWrite = 1 << 1;
    public const uint FlagColorWrite = 1 << 2;
    public const uint FlagAlphaTest = 1 << 3;
    public const uint FlagAlphaBlend = 1 << 4;
    public const uint FlagFaceCulling = 1 << 5;
    public const uint Flag2D = 1 << 6;
    public const uint FlagSprite = 1 << 7;
    public const uint FlagTextured = 1 << 8;
    private const uint KnownFlags = (1 << 9) - 1;

    private const int ColouredStride = 16;
    private const int TexturedStride = 24;
    private const uint PackedWhite = 0xFFFFFFFF;

    private struct Vertex
    {
        public float X, Y, Z, W, U, V;
        public uint C;
    }

    private ref struct Context
    {
        public Span<uint> Color;
        public Span<float> Depth;
        public ReadOnlySpan<uint> Texture;
        public ReadOnlySpan<byte> Vertices;
        public int ColorStride, DepthStride, Stride;
        public int TexWidth, TexHeight, TexWidthMask, TexHeightMask;
        public int MaxX, MaxY;
        public float HalfWidth, HalfHeight, TexOffsetU, TexOffsetV;
        public bool DepthTest, DepthWrite, ColorWrite, AlphaTest, AlphaBlend, FaceCulling, Textured;
        public float M11, M12, M13, M14, M21, M22, M23, M24, M31, M32, M33, M34, M41, M42, M43, M44;
    }

    /// <summary>Fuellt count 32-Bit-Werte ab offset mit value (Farbe oder Tiefe loeschen).</summary>
    public static int Fill(Span<byte> memory, uint offset, uint count, uint value)
    {
        if (count > (uint)(MaxWidth * MaxHeight) * 4u) return InvalidArgument;
        if (!TryRange(memory.Length, offset, (ulong)count * 4, out int start)) return OutOfBounds;
        MemoryMarshal.Cast<byte, uint>(memory.Slice(start, (int)count * 4)).Fill(value);
        return Success;
    }

    /// <summary>Zeichnet die Quads der Beschreibung an callOffset. Liefert Success, InvalidArgument oder OutOfBounds.</summary>
    public static int Draw(Span<byte> memory, uint callOffset)
    {
        if (!TryRange(memory.Length, callOffset, CallSize, out int callStart)) return OutOfBounds;
        ReadOnlySpan<byte> call = memory.Slice(callStart, CallSize);

        uint flags = U32(call, 4), count = U32(call, 12), width = U32(call, 24), height = U32(call, 28);
        uint colorStride = U32(call, 20), depthStride = U32(call, 36), texWidth = U32(call, 44), texHeight = U32(call, 48);
        uint maxX = U32(call, 52), maxY = U32(call, 56);
        // Erst alle Werte, dann alle Bereiche: so ist die Fehlerart unabhaengig von der Reihenfolge der Felder.
        if (U32(call, 0) != Version || (flags & ~KnownFlags) != 0) return InvalidArgument;
        if (count % 4 != 0 || count > MaxVertices) return InvalidArgument;
        if (width < 1 || width > MaxWidth || height < 1 || height > MaxHeight) return InvalidArgument;
        if (colorStride < width || colorStride > MaxWidth * 4) return InvalidArgument;
        if (maxX >= width || maxY >= height) return InvalidArgument;
        if (!IsPowerOfTwo(texWidth) || !IsPowerOfTwo(texHeight)) return InvalidArgument;
        bool depthTest = (flags & FlagDepthTest) != 0, depthWrite = (flags & FlagDepthWrite) != 0;
        bool needsDepth = (flags & Flag2D) == 0 && (depthTest || depthWrite);
        if (needsDepth && (depthStride < width || depthStride > MaxWidth * 4)) return InvalidArgument;

        int stride = (flags & FlagTextured) != 0 ? TexturedStride : ColouredStride;
        if (!TryRange(memory.Length, U32(call, 8), (ulong)count * (ulong)stride, out int vertexStart)) return OutOfBounds;
        ulong colorBytes = ((ulong)colorStride * (height - 1) + width) * 4;
        if (!TryRange(memory.Length, U32(call, 16), colorBytes, out int colorStart)) return OutOfBounds;
        if (!TryRange(memory.Length, U32(call, 40), (ulong)texWidth * texHeight * 4, out int textureStart)) return OutOfBounds;
        int depthStart = 0;
        ulong depthBytes = needsDepth ? ((ulong)depthStride * (height - 1) + width) * 4 : 0;
        if (needsDepth && !TryRange(memory.Length, U32(call, 32), depthBytes, out depthStart)) return OutOfBounds;
        if (count == 0) return Success;

        var context = new Context
        {
            Vertices = memory.Slice(vertexStart, (int)count * stride),
            Color = MemoryMarshal.Cast<byte, uint>(memory.Slice(colorStart, (int)colorBytes)),
            Depth = needsDepth ? MemoryMarshal.Cast<byte, float>(memory.Slice(depthStart, (int)depthBytes)) : default,
            Texture = MemoryMarshal.Cast<byte, uint>(memory.Slice(textureStart, (int)(texWidth * texHeight * 4))),
            ColorStride = (int)colorStride,
            DepthStride = (int)depthStride,
            Stride = stride,
            TexWidth = (int)texWidth,
            TexHeight = (int)texHeight,
            TexWidthMask = (int)texWidth - 1,
            TexHeightMask = (int)texHeight - 1,
            MaxX = (int)maxX,
            MaxY = (int)maxY,
            HalfWidth = F32(call, 60),
            HalfHeight = F32(call, 64),
            TexOffsetU = F32(call, 68),
            TexOffsetV = F32(call, 72),
            DepthTest = depthTest,
            DepthWrite = depthWrite,
            ColorWrite = (flags & FlagColorWrite) != 0,
            AlphaTest = (flags & FlagAlphaTest) != 0,
            AlphaBlend = (flags & FlagAlphaBlend) != 0,
            FaceCulling = (flags & FlagFaceCulling) != 0,
            Textured = (flags & FlagTextured) != 0,
            M11 = F32(call, 76), M12 = F32(call, 80), M13 = F32(call, 84), M14 = F32(call, 88),
            M21 = F32(call, 92), M22 = F32(call, 96), M23 = F32(call, 100), M24 = F32(call, 104),
            M31 = F32(call, 108), M32 = F32(call, 112), M33 = F32(call, 116), M34 = F32(call, 120),
            M41 = F32(call, 124), M42 = F32(call, 128), M43 = F32(call, 132), M44 = F32(call, 136),
        };
        DrawQuads(ref context, (int)count, flags);
        return Success;
    }

    private static uint U32(ReadOnlySpan<byte> call, int at) => BinaryPrimitives.ReadUInt32LittleEndian(call.Slice(at, 4));
    private static float F32(ReadOnlySpan<byte> call, int at) => BinaryPrimitives.ReadSingleLittleEndian(call.Slice(at, 4));

    private static bool IsPowerOfTwo(uint value) => value >= 1 && value <= MaxTextureSize && (value & (value - 1)) == 0;

    /// <summary>Liegt [offset, offset + length) im Speicher? In 64 Bit gerechnet, damit nichts ueberlaeuft.</summary>
    private static bool TryRange(int memoryLength, uint offset, ulong length, out int start)
    {
        start = (int)Math.Min(offset, int.MaxValue);
        return (ulong)offset + length <= (ulong)memoryLength;
    }

    // clang senkt (int)float fuer wasm32 ohne nontrapping-fptoint so ab: ausserhalb von int (und NaN) ergibt INT_MIN.
    private static int ToInt(float value) =>
        value > -2147483904.0f && value < 2147483648.0f ? (int)value : int.MinValue;

    private static int R(uint packed) => (int)(packed & 0xFF);
    private static int G(uint packed) => (int)((packed >> 8) & 0xFF);
    private static int B(uint packed) => (int)((packed >> 16) & 0xFF);
    private static int A(uint packed) => (int)(packed >> 24);
    private static int BitmapR(uint color) => (int)((color >> 16) & 0xFF);
    private static int BitmapG(uint color) => (int)((color >> 8) & 0xFF);
    private static int BitmapB(uint color) => (int)(color & 0xFF);
    private static int BitmapA(uint color) => (int)(color >> 24);
    // Wie BitmapCol_Make: Werte werden ohne Begrenzung in ihre Bytes geschoben.
    private static uint Make(int r, int g, int b, int a) =>
        unchecked(((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b);

    private static void ReadVertex(ref Context context, int index, ref Vertex vertex, bool transform)
    {
        ReadOnlySpan<byte> data = context.Vertices.Slice(index * context.Stride, context.Stride);
        float x = BinaryPrimitives.ReadSingleLittleEndian(data);
        float y = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(4));
        float z = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(8));
        vertex.C = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12));
        if (context.Textured)
        {
            vertex.U = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(16));
            vertex.V = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(20));
        }
        else
        {
            vertex.U = 0.0f;
            vertex.V = 0.0f;
        }
        if (!transform)
        {
            vertex.X = x;
            vertex.Y = y;
            return;
        }
        vertex.X = x * context.M11 + y * context.M21 + z * context.M31 + context.M41;
        vertex.Y = x * context.M12 + y * context.M22 + z * context.M32 + context.M42;
        vertex.Z = x * context.M13 + y * context.M23 + z * context.M33 + context.M43;
        vertex.W = x * context.M14 + y * context.M24 + z * context.M34 + context.M44;
        if (context.Textured)
        {
            vertex.U += context.TexOffsetU;
            vertex.V += context.TexOffsetV;
        }
    }

    private static void Viewport(ref Context context, ref Vertex vertex)
    {
        float invW = 1.0f / vertex.W;
        vertex.X = context.HalfWidth * (1 + vertex.X * invW);
        vertex.Y = context.HalfHeight * (1 - vertex.Y * invW);
        vertex.Z = vertex.Z * invW;
        vertex.W = invW;
        vertex.U *= invW;
        vertex.V *= invW;
    }

    private static void DrawQuads(ref Context context, int count, uint flags)
    {
        Vertex v0 = default, v1 = default, v2 = default, v3 = default;
        bool twoD = (flags & Flag2D) != 0;
        bool sprite = twoD && (flags & FlagSprite) != 0;
        for (int index = 0; index < count; index += 4)
        {
            if (sprite)
            {
                ReadVertex(ref context, index, ref v0, false);
                ReadVertex(ref context, index + 1, ref v1, false);
                ReadVertex(ref context, index + 2, ref v2, false);
                DrawSprite2D(ref context, ref v0, ref v1, ref v2);
            }
            else if (twoD)
            {
                ReadVertex(ref context, index, ref v0, false);
                ReadVertex(ref context, index + 1, ref v1, false);
                ReadVertex(ref context, index + 2, ref v2, false);
                ReadVertex(ref context, index + 3, ref v3, false);
                DrawTriangle2D(ref context, ref v0, ref v2, ref v1);
                DrawTriangle2D(ref context, ref v2, ref v0, ref v3);
            }
            else
            {
                ReadVertex(ref context, index, ref v0, true);
                ReadVertex(ref context, index + 1, ref v1, true);
                ReadVertex(ref context, index + 2, ref v2, true);
                ReadVertex(ref context, index + 3, ref v3, true);
                int mask = (v0.Z >= 0.0f ? 1 : 0) | (v1.Z >= 0.0f ? 2 : 0) | (v2.Z >= 0.0f ? 4 : 0) | (v3.Z >= 0.0f ? 8 : 0);
                if (mask == 0) continue;
                if (mask == 0x0F)
                {
                    Viewport(ref context, ref v0);
                    Viewport(ref context, ref v1);
                    Viewport(ref context, ref v2);
                    Viewport(ref context, ref v3);
                    DrawTriangle3D(ref context, ref v0, ref v2, ref v1);
                    DrawTriangle3D(ref context, ref v2, ref v0, ref v3);
                }
                else
                {
                    DrawClipped(ref context, mask, ref v0, ref v1, ref v2, ref v3);
                }
            }
        }
    }

    private static void DrawSprite2D(ref Context context, ref Vertex v0, ref Vertex v1, ref Vertex v2)
    {
        uint vertexColor = v0.C;
        int minX = ToInt(v0.X), minY = ToInt(v0.Y), maxX = ToInt(v1.X), maxY = ToInt(v2.Y);
        if (maxX < 0 || minX > context.MaxX) return;
        if (maxY < 0 || minY > context.MaxY) return;

        int begTX = ToInt(v0.U * context.TexWidth);
        int begTY = ToInt(v0.V * context.TexHeight);
        int delTX = unchecked(ToInt(v1.U * context.TexWidth) - begTX);
        int delTY = unchecked(ToInt(v2.V * context.TexHeight) - begTY);
        int width = unchecked(maxX - minX), height = unchecked(maxY - minY);
        if (width == 0) width = 1;
        if (height == 0) height = 1;
        bool fast = delTX == width && delTY == height
            && unchecked(begTX + delTX) < context.TexWidth && unchecked(begTY + delTY) < context.TexHeight;

        minX = Math.Max(minX, 0); maxX = Math.Min(maxX, context.MaxX);
        minY = Math.Max(minY, 0); maxY = Math.Min(maxY, context.MaxY);
        for (int y = minY; y <= maxY; y++)
        {
            int texY = fast ? unchecked(begTY + (y - minY)) : unchecked(begTY + Divide(delTY * (y - minY), height)) & context.TexHeightMask;
            for (int x = minX; x <= maxX; x++)
            {
                int texX = fast ? unchecked(begTX + (x - minX)) : unchecked(begTX + Divide(delTX * (x - minX), width)) & context.TexWidthMask;
                // SoftGPU vertraut hier auf die Werte der App; der Kernel begrenzt den Index auf die Textur.
                uint color = TexelAt(ref context, unchecked(texY * context.TexWidth + texX));
                int a = BitmapA(color);
                if (context.AlphaBlend && a == 0) continue;
                int index = y * context.ColorStride + x;
                if (context.AlphaBlend && a != 255)
                {
                    uint destination = context.Color[index];
                    int dr = BitmapR(destination), dg = BitmapG(destination), db = BitmapB(destination);
                    int r = dr + ((a * (BitmapR(color) - dr)) >> 8);
                    int g = dg + ((a * (BitmapG(color) - dg)) >> 8);
                    int b = db + ((a * (BitmapB(color) - db)) >> 8);
                    color = Make(r, g, b, 0xFF);
                }
                if (vertexColor != PackedWhite)
                {
                    color = Make((R(vertexColor) * BitmapR(color)) >> 8, (G(vertexColor) * BitmapG(color)) >> 8,
                        (B(vertexColor) * BitmapB(color)) >> 8, 0xFF);
                }
                context.Color[index] = color;
            }
        }
    }

    // Wasm i32.div_s trapt bei Division durch null; height/width sind hier nie null, INT_MIN / -1 kann nicht entstehen.
    private static int Divide(int value, int divisor) => divisor == -1 ? unchecked(-value) : value / divisor;

    // Ausserhalb der Textur (nur bei widerspruechlichen Sprite-Koordinaten moeglich) liest SoftGPU fremden Speicher;
    // der Kernel nimmt stattdessen das Texel an der begrenzten Stelle.
    private static uint TexelAt(ref Context context, int index) =>
        (uint)index < (uint)context.Texture.Length ? context.Texture[index] : context.Texture[index < 0 ? 0 : context.Texture.Length - 1];

    private static int Edge(int ax, int ay, int bx, int by, int cx, int cy) =>
        unchecked((bx - ax) * (cy - ay) - (by - ay) * (cx - ax));

    private static float Edge(int ax, int ay, int bx, int by, float cx, float cy) =>
        unchecked(bx - ax) * (cy - ay) - unchecked(by - ay) * (cx - ax);

    private static void DrawTriangle2D(ref Context context, ref Vertex v0, ref Vertex v1, ref Vertex v2)
    {
        int x0 = ToInt(v0.X), y0 = ToInt(v0.Y), x1 = ToInt(v1.X), y1 = ToInt(v1.Y), x2 = ToInt(v2.X), y2 = ToInt(v2.Y);
        int minX = Math.Min(x0, Math.Min(x1, x2)), minY = Math.Min(y0, Math.Min(y1, y2));
        int maxX = Math.Max(x0, Math.Max(x1, x2)), maxY = Math.Max(y0, Math.Max(y1, y2));
        if (maxX < 0 || minX > context.MaxX) return;
        if (maxY < 0 || minY > context.MaxY) return;
        minX = Math.Max(minX, 0); maxX = Math.Min(maxX, context.MaxX);
        minY = Math.Max(minY, 0); maxY = Math.Min(maxY, context.MaxY);

        float u0 = v0.U * context.TexWidth, u1 = v1.U * context.TexWidth, u2 = v2.U * context.TexWidth;
        float t0 = v0.V * context.TexHeight, t1 = v1.V * context.TexHeight, t2 = v2.V * context.TexHeight;
        uint color = v0.C;
        int area = Edge(x0, y0, x1, y1, x2, y2);
        float factor = 1.0f / area;
        int dx01 = unchecked(y0 - y1), dy01 = unchecked(x1 - x0);
        int dx12 = unchecked(y1 - y2), dy12 = unchecked(x2 - x1);
        int dx20 = unchecked(y2 - y0), dy20 = unchecked(x0 - x2);
        float bc0Start = Edge(x1, y1, x2, y2, minX + 0.5f, minY + 0.5f);
        float bc1Start = Edge(x2, y2, x0, y0, minX + 0.5f, minY + 0.5f);
        float bc2Start = Edge(x0, y0, x1, y1, minX + 0.5f, minY + 0.5f);

        for (int y = minY; y <= maxY; y++, bc0Start += dy12, bc1Start += dy20, bc2Start += dy01)
        {
            float bc0 = bc0Start, bc1 = bc1Start, bc2 = bc2Start;
            for (int x = minX; x <= maxX; x++, bc0 += dx12, bc1 += dx20, bc2 += dx01)
            {
                float ic0 = bc0 * factor, ic1 = bc1 * factor, ic2 = bc2 * factor;
                if (ic0 < 0 || ic1 < 0 || ic2 < 0) continue;
                int index = y * context.ColorStride + x;
                int r, g, b, a;
                if (context.Textured)
                {
                    float u = ic0 * u0 + ic1 * u1 + ic2 * u2;
                    float v = ic0 * t0 + ic1 * t1 + ic2 * t2;
                    int texX = ToInt(u) & context.TexWidthMask;
                    int texY = ToInt(v) & context.TexHeightMask;
                    uint texel = context.Texture[texY * context.TexWidth + texX];
                    a = (A(color) * BitmapA(texel)) >> 8;
                    r = (R(color) * BitmapR(texel)) >> 8;
                    g = (G(color) * BitmapG(texel)) >> 8;
                    b = (B(color) * BitmapB(texel)) >> 8;
                }
                else
                {
                    r = R(color); g = G(color); b = B(color); a = A(color);
                }
                if (context.AlphaTest && a < 0x80) continue;
                if (context.AlphaBlend && a == 0) continue;
                if (context.AlphaBlend && a != 255)
                {
                    uint destination = context.Color[index];
                    int dr = BitmapR(destination), dg = BitmapG(destination), db = BitmapB(destination);
                    r = dr + ((a * (r - dr)) >> 8);
                    g = dg + ((a * (g - dg)) >> 8);
                    b = db + ((a * (b - db)) >> 8);
                }
                context.Color[index] = Make(r, g, b, 0xFF);
            }
        }
    }

    private static void DrawTriangle3D(ref Context context, ref Vertex v0, ref Vertex v1, ref Vertex v2)
    {
        int x0 = ToInt(v0.X), y0 = ToInt(v0.Y), x1 = ToInt(v1.X), y1 = ToInt(v1.Y), x2 = ToInt(v2.X), y2 = ToInt(v2.Y);
        int minX = Math.Min(x0, Math.Min(x1, x2)), minY = Math.Min(y0, Math.Min(y1, y2));
        int maxX = Math.Max(x0, Math.Max(x1, x2)), maxY = Math.Max(y0, Math.Max(y1, y2));
        int area = Edge(x0, y0, x1, y1, x2, y2);
        if (context.FaceCulling && area < 0) return;
        if (maxX < 0 || minX > context.MaxX) return;
        if (maxY < 0 || minY > context.MaxY) return;
        minX = Math.Max(minX, 0); maxX = Math.Min(maxX, context.MaxX);
        minY = Math.Max(minY, 0); maxY = Math.Min(maxY, context.MaxY);

        float factor = 1.0f / area;
        float w0 = v0.W, w1 = v1.W, w2 = v2.W;
        if (w0 <= 0 || w1 <= 0 || w2 <= 0) return;
        float z0 = v0.Z, z1 = v1.Z, z2 = v2.Z;
        uint color = v0.C;
        float u0 = v0.U * context.TexWidth, u1 = v1.U * context.TexWidth, u2 = v2.U * context.TexWidth;
        float t0 = v0.V * context.TexHeight, t1 = v1.V * context.TexHeight, t2 = v2.V * context.TexHeight;
        int dx01 = unchecked(y0 - y1), dy01 = unchecked(x1 - x0);
        int dx12 = unchecked(y1 - y2), dy12 = unchecked(x2 - x1);
        int dx20 = unchecked(y2 - y0), dy20 = unchecked(x0 - x2);
        float bc0Start = Edge(x1, y1, x2, y2, minX + 0.5f, minY + 0.5f);
        float bc1Start = Edge(x2, y2, x0, y0, minX + 0.5f, minY + 0.5f);
        float bc2Start = Edge(x0, y0, x1, y1, minX + 0.5f, minY + 0.5f);

        int r = 0, g = 0, b = 0, a = 0;
        bool texturing = context.Textured;
        if (!texturing)
        {
            r = R(color); g = G(color); b = B(color); a = A(color);
        }
        else if (context.TexWidth == 1)
        {
            // Wie SoftGPU: Eine 1 Pixel breite Textur liefert eine feste Farbe je Dreieck.
            float rawY = Math.Min(t0 / w0, t1 / w1);
            uint texel = context.Texture[(ToInt(rawY + 0.01f) & context.TexHeightMask) * context.TexWidth];
            a = (A(color) * BitmapA(texel)) >> 8;
            r = (R(color) * BitmapR(texel)) >> 8;
            g = (G(color) * BitmapG(texel)) >> 8;
            b = (B(color) * BitmapB(texel)) >> 8;
            texturing = false;
        }

        for (int y = minY; y <= maxY; y++, bc0Start += dy12, bc1Start += dy20, bc2Start += dy01)
        {
            float bc0 = bc0Start, bc1 = bc1Start, bc2 = bc2Start;
            for (int x = minX; x <= maxX; x++, bc0 += dx12, bc1 += dx20, bc2 += dx01)
            {
                float ic0 = bc0 * factor, ic1 = bc1 * factor, ic2 = bc2 * factor;
                if (ic0 < 0 || ic1 < 0 || ic2 < 0) continue;
                int depthIndex = y * context.DepthStride + x;
                float w = 1 / (ic0 * w0 + ic1 * w1 + ic2 * w2);
                float z = (ic0 * z0 + ic1 * z1 + ic2 * z2) * w;
                if (context.DepthTest && (z < 0 || z > context.Depth[depthIndex])) continue;
                if (!context.ColorWrite)
                {
                    if (context.DepthWrite) context.Depth[depthIndex] = z;
                    continue;
                }
                if (texturing)
                {
                    float u = (ic0 * u0 + ic1 * u1 + ic2 * u2) * w;
                    float v = (ic0 * t0 + ic1 * t1 + ic2 * t2) * w;
                    int texX = ToInt(u) & context.TexWidthMask;
                    int texY = ToInt(v) & context.TexHeightMask;
                    uint texel = context.Texture[texY * context.TexWidth + texX];
                    a = (A(color) * BitmapA(texel)) >> 8;
                    r = (R(color) * BitmapR(texel)) >> 8;
                    g = (G(color) * BitmapG(texel)) >> 8;
                    b = (B(color) * BitmapB(texel)) >> 8;
                }
                if (context.AlphaTest && a < 0x80) continue;
                if (context.DepthWrite) context.Depth[depthIndex] = z;
                int index = y * context.ColorStride + x;
                if (!context.AlphaBlend)
                {
                    context.Color[index] = Make(r, g, b, 0xFF);
                    continue;
                }
                uint destination = context.Color[index];
                int dr = BitmapR(destination), dg = BitmapG(destination), db = BitmapB(destination);
                context.Color[index] = Make(dr + ((a * (r - dr)) >> 8), dg + ((a * (g - dg)) >> 8), db + ((a * (b - db)) >> 8), 0xFF);
            }
        }
    }

    private static void ClipLine(ref Vertex first, ref Vertex second, ref Vertex result)
    {
        float t = Math.Abs(first.Z / (second.Z - first.Z));
        float invT = 1.0f - t;
        result.X = invT * first.X + t * second.X;
        result.Y = invT * first.Y + t * second.Y;
        result.Z = 0.0f;
        result.W = invT * first.W + t * second.W;
        result.U = invT * first.U + t * second.U;
        result.V = invT * first.V + t * second.V;
        result.C = first.C;
    }

    // Near-Clipping eines Quads; Faelle und Dreiecksreihenfolge wie DrawClipped in SoftGPU.
    private static void DrawClipped(ref Context context, int mask, ref Vertex v0, ref Vertex v1, ref Vertex v2, ref Vertex v3)
    {
        Vertex a = default, b = default;
        switch (mask)
        {
            case 1:
                ClipLine(ref v3, ref v0, ref a); ClipLine(ref v0, ref v1, ref b);
                Viewport(ref context, ref v0); Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref v0, ref a, ref b);
                break;
            case 2:
                ClipLine(ref v0, ref v1, ref a); ClipLine(ref v1, ref v2, ref b);
                Viewport(ref context, ref v1); Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref a, ref b, ref v1);
                break;
            case 4:
                ClipLine(ref v1, ref v2, ref a); ClipLine(ref v2, ref v3, ref b);
                Viewport(ref context, ref v2); Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref a, ref b, ref v2);
                break;
            case 8:
                ClipLine(ref v2, ref v3, ref a); ClipLine(ref v3, ref v0, ref b);
                Viewport(ref context, ref v3); Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref b, ref v3, ref a);
                break;
            case 1 | 2:
                ClipLine(ref v1, ref v2, ref a); ClipLine(ref v3, ref v0, ref b);
                Viewport(ref context, ref v0); Viewport(ref context, ref v1); Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref v1, ref v0, ref a);
                DrawTriangle3D(ref context, ref a, ref v0, ref b);
                break;
            case 1 | 8:
                ClipLine(ref v0, ref v1, ref a); ClipLine(ref v2, ref v3, ref b);
                Viewport(ref context, ref v0); Viewport(ref context, ref v3); Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref a, ref v0, ref b);
                DrawTriangle3D(ref context, ref b, ref v0, ref v3);
                break;
            case 2 | 4:
                ClipLine(ref v2, ref v3, ref a); ClipLine(ref v0, ref v1, ref b);
                Viewport(ref context, ref v1); Viewport(ref context, ref v2); Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref v1, ref b, ref v2);
                DrawTriangle3D(ref context, ref v2, ref b, ref a);
                break;
            case 4 | 8:
                ClipLine(ref v3, ref v0, ref a); ClipLine(ref v1, ref v2, ref b);
                Viewport(ref context, ref v2); Viewport(ref context, ref v3); Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref b, ref a, ref v2);
                DrawTriangle3D(ref context, ref v2, ref a, ref v3);
                break;
            case 1 | 2 | 4:
                ClipLine(ref v2, ref v3, ref a); ClipLine(ref v3, ref v0, ref b);
                Viewport(ref context, ref v0); Viewport(ref context, ref v1); Viewport(ref context, ref v2);
                Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref v1, ref v0, ref v2);
                DrawTriangle3D(ref context, ref v2, ref v0, ref a);
                DrawTriangle3D(ref context, ref v0, ref b, ref a);
                break;
            case 1 | 2 | 8:
                ClipLine(ref v1, ref v2, ref a); ClipLine(ref v2, ref v3, ref b);
                Viewport(ref context, ref v0); Viewport(ref context, ref v1); Viewport(ref context, ref v3);
                Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref v0, ref v3, ref v1);
                DrawTriangle3D(ref context, ref v1, ref v3, ref a);
                DrawTriangle3D(ref context, ref v3, ref b, ref a);
                break;
            case 1 | 4 | 8:
                ClipLine(ref v0, ref v1, ref a); ClipLine(ref v1, ref v2, ref b);
                Viewport(ref context, ref v0); Viewport(ref context, ref v2); Viewport(ref context, ref v3);
                Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref v3, ref v2, ref v0);
                DrawTriangle3D(ref context, ref v0, ref v2, ref a);
                DrawTriangle3D(ref context, ref v2, ref b, ref a);
                break;
            case 2 | 4 | 8:
                ClipLine(ref v3, ref v0, ref a); ClipLine(ref v0, ref v1, ref b);
                Viewport(ref context, ref v1); Viewport(ref context, ref v2); Viewport(ref context, ref v3);
                Viewport(ref context, ref a); Viewport(ref context, ref b);
                DrawTriangle3D(ref context, ref v2, ref v1, ref v3);
                DrawTriangle3D(ref context, ref v3, ref v1, ref a);
                DrawTriangle3D(ref context, ref v1, ref b, ref a);
                break;
        }
    }
}
