using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ProjektZeh.Graphics.Raster;

/// <summary>
/// Software-Rasterizer fuer Wasm-Apps (zehos_v1.raster_draw und raster_fill, Vertrag in docs/PAKETE_ERSTELLEN.md).
/// Zeichnet Quads direkt in Farb- und Tiefenpuffer im linearen Speicher der App; der Kernel haelt keinen Zustand.
/// Die Rechenschritte sind aus ClassiCube Graphics_SoftGPU.c uebertragen (BSD-3-Clause, Copyright (c) 2014 - 2024,
/// UnknownShadow200; Lizenztext in LICENSE-ClassiCube.txt), damit das Bild dem Wasm-Renderer der App gleicht. Ganzzahlen laufen wie i32 in Wasm ueber,
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
        public int MaxX, MaxY, Width;
        public float HalfWidth, HalfHeight, TexOffsetU, TexOffsetV;
        public bool DepthTest, DepthWrite, ColorWrite, AlphaTest, AlphaBlend, FaceCulling, Textured;
        // Vier Pixel je Schritt (nur SSE2): Die Tiefen eines Schritts werden vor dessen Schreibzugriffen gelesen, das ist
        // nur bitgleich, solange Farb- und Tiefenpuffer im Gastspeicher getrennt liegen.
        public bool Vector;
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
        bool separate = !needsDepth || (ulong)depthStart + depthBytes <= (ulong)colorStart || (ulong)colorStart + colorBytes <= (ulong)depthStart;

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
            Width = (int)width,
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
            Vector = Sse2.IsSupported && separate,
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
    // Wie BitmapCol_Make: jeder Kanal wird auf sein Byte gekuerzt ((cc_uint8)value << shift).
    private static uint Make(int r, int g, int b, int a) =>
        unchecked(((uint)(a & 0xFF) << 24) | ((uint)(r & 0xFF) << 16) | ((uint)(g & 0xFF) << 8) | (uint)(b & 0xFF));

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

    // width/height sind hier nie null. Ein Teiler -1 entsteht im Zeichenpfad nicht; der Sonderfall verhindert nur,
    // dass INT_MIN / -1 in C# eine Ausnahme statt des Wasm-Ergebnisses (Ueberlauf) liefert.
    private static int Divide(int value, int divisor) => divisor == -1 ? unchecked(-value) : value / divisor;

    // Ausserhalb der Textur (nur bei widerspruechlichen Sprite-Koordinaten moeglich) liest SoftGPU fremden Speicher;
    // der Kernel nimmt stattdessen das Texel an der begrenzten Stelle.
    private static uint TexelAt(ref Context context, int index) =>
        (uint)index < (uint)context.Texture.Length ? context.Texture[index] : context.Texture[index < 0 ? 0 : context.Texture.Length - 1];

    private static int Edge(int ax, int ay, int bx, int by, int cx, int cy) =>
        unchecked((bx - ax) * (cy - ay) - (by - ay) * (cx - ax));

    private static float Edge(int ax, int ay, int bx, int by, float cx, float cy) =>
        unchecked(bx - ax) * (cy - ay) - unchecked(by - ay) * (cx - ax);

    // Gleichwertig zu "ic0 < 0 || ic1 < 0 || ic2 < 0" mit ic = bc * factor, aber ohne Multiplikation: factor = 1 / area ist
    // nie NaN, bei area = 0 +Unendlich, sonst endlich und vom Betrag mindestens 2^-31 (area ist int). bc ist nie NaN oder
    // unendlich und stets ein Vielfaches von 0,5 (Pixelmitte, ganzzahlige Ecken; Runden auf float erhaelt das), ausser 0
    // also vom Betrag mindestens 0,5. Das Produkt wird deshalb weder 0 noch NaN, sein Vorzeichen ist das von bc mal dem
    // von factor (bc = 0 ergibt +-0 oder NaN, beides nicht < 0).
    // Entlang einer Zeile waechst oder faellt jedes bc monoton (Runden ist monoton), daher liegen die abgedeckten Pixel
    // einer Zeile lueckenlos beieinander: Nach dem ersten Pixel dahinter kann die Zeile enden.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Outside(bool positive, float bc0, float bc1, float bc2) =>
        positive ? (bc0 < 0) | (bc1 < 0) | (bc2 < 0) : (bc0 > 0) | (bc1 > 0) | (bc2 > 0);

    private const double ExactLimit = 8388608; // 2^23: Darunter ist jedes Vielfache von 0,5 als float exakt darstellbar.

    // Bleibt eine Kantenfunktion an allen vier Ecken des Rechtecks unter 2^23, gilt das (linear) fuer jedes Pixel darin.
    // Dann rundet keine der fortlaufenden Additionen, und bc = start + n * dx + m * dy laesst sich direkt berechnen.
    private static bool Exact(float start, int dx, int dy, int columns, int rows)
    {
        double right = (double)columns * dx, down = (double)rows * dy;
        return Math.Abs(start) < ExactLimit && Math.Abs(start + right) < ExactLimit
            && Math.Abs(start + down) < ExactLimit && Math.Abs(start + right + down) < ExactLimit;
    }

    // Grenze der abgedeckten Pixel einer Kante, Zeile fuer Zeile (nur fuer exakte Kanten, siehe Exact). Dort ist
    // e = 2 * bc = v + n * s + m * t ganzzahlig (n Spalte, m Zeile ab der Ecke des Rechtecks); nicht verworfen wird
    // e >= 0 (Vorzeichen wie Outside bereits eingerechnet). Fuer s > 0 ist das n >= bound = ceil(-e0 / s), fuer s < 0
    // n <= bound = floor(e0 / -s). Je Zeile aendert sich bound um t / |s|; Quotient und Rest werden einmal je Dreieck
    // berechnet, danach genuegen ganzzahlige Additionen wie bei Bresenham. |e| < 2^24, |s|, |t| <= 2^33: alles passt in long.
    private struct EdgeWalk
    {
        private readonly long divisor, quotient, remainder, rowStep;
        private readonly int kind;
        private long bound, rest, value;

        public EdgeWalk(bool positive, float start, int dx, int dy)
        {
            long v = (long)((double)start * 2), s = 2L * dx, t = 2L * dy;
            if (!positive)
            {
                v = -v;
                s = -s;
                t = -t;
            }
            kind = Math.Sign(s);
            divisor = Math.Abs(s);
            rowStep = t;
            value = v;
            quotient = remainder = bound = rest = 0;
            if (kind == 0) return;
            (quotient, remainder) = FloorDivRem(t, divisor);
            // Mit v = q * |s| + r, 0 <= r < |s|: s > 0 ergibt bound = ceil(-v / s) = -q, s < 0 bound = floor(v / -s) = q;
            // in beiden Faellen ist r der Abstand zur naechsten Grenze.
            (long q, long r) = FloorDivRem(v, divisor);
            bound = kind > 0 ? -q : q;
            rest = r;
        }

        private static (long Quotient, long Remainder) FloorDivRem(long value, long divisor)
        {
            long quotient = value / divisor, remainder = value - quotient * divisor;
            return remainder < 0 ? (quotient - 1, remainder + divisor) : (quotient, remainder);
        }

        /// <summary>Engt [first, last] auf die Spalten dieser Zeile ein und geht zur naechsten Zeile.</summary>
        public void Apply(int minX, int maxX, ref int first, ref int last)
        {
            if (kind > 0)
            {
                if (minX + bound > first) first = (int)Math.Min(minX + bound, maxX + 1L);
                bound -= quotient;
                rest += remainder;
                if (rest >= divisor)
                {
                    bound--;
                    rest -= divisor;
                }
            }
            else if (kind < 0)
            {
                if (minX + bound < last) last = (int)Math.Max(minX + bound, minX - 1L);
                bound += quotient;
                rest += remainder;
                if (rest >= divisor)
                {
                    bound++;
                    rest -= divisor;
                }
            }
            else
            {
                if (value < 0) last = minX - 1;
                value += rowStep;
            }
        }
    }

    // Kantenwert an Spalte first, aus dem Zeilenanfang; bei exakten Kanten ohne Rundung, bei first = minX unveraendert.
    private static float Advance(float start, int steps, int dx) => (float)(start + (double)steps * dx);

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

        bool textured = context.Textured, alphaTest = context.AlphaTest, alphaBlend = context.AlphaBlend;
        int colorStride = context.ColorStride, texWidth = context.TexWidth;
        int texWidthMask = context.TexWidthMask, texHeightMask = context.TexHeightMask;
        int colorR = R(color), colorG = G(color), colorB = B(color), colorA = A(color);
        // Indizes wie in DrawTriangle3D begrenzt: x, y innerhalb des Scissors, Texturindex durch die Masken.
        ref uint colors = ref MemoryMarshal.GetReference(context.Color);
        ref uint texels = ref MemoryMarshal.GetReference(context.Texture);
        bool positive = factor > 0;
        int columns = maxX - minX, rows = maxY - minY;
        bool exact = Exact(bc0Start, dx12, dy12, columns, rows) && Exact(bc1Start, dx20, dy20, columns, rows)
            && Exact(bc2Start, dx01, dy01, columns, rows);
        EdgeWalk walk0 = exact ? new(positive, bc0Start, dx12, dy12) : default;
        EdgeWalk walk1 = exact ? new(positive, bc1Start, dx20, dy20) : default;
        EdgeWalk walk2 = exact ? new(positive, bc2Start, dx01, dy01) : default;

        for (int y = minY; y <= maxY; y++, bc0Start += dy12, bc1Start += dy20, bc2Start += dy01)
        {
            int first = minX, last = maxX;
            if (exact)
            {
                walk0.Apply(minX, maxX, ref first, ref last);
                walk1.Apply(minX, maxX, ref first, ref last);
                walk2.Apply(minX, maxX, ref first, ref last);
                if (first > last) continue;
            }
            float bc0 = Advance(bc0Start, first - minX, dx12), bc1 = Advance(bc1Start, first - minX, dx20), bc2 = Advance(bc2Start, first - minX, dx01);
            int colorRow = y * colorStride;
            bool entered = false;
            for (int x = first; x <= last; x++, bc0 += dx12, bc1 += dx20, bc2 += dx01)
            {
                if (Outside(positive, bc0, bc1, bc2))
                {
                    if (entered) break;
                    continue;
                }
                entered = true;
                float ic0 = bc0 * factor, ic1 = bc1 * factor, ic2 = bc2 * factor;
                int r, g, b, a;
                if (textured)
                {
                    float u = ic0 * u0 + ic1 * u1 + ic2 * u2;
                    float v = ic0 * t0 + ic1 * t1 + ic2 * t2;
                    int texX = ToInt(u) & texWidthMask;
                    int texY = ToInt(v) & texHeightMask;
                    uint texel = Unsafe.Add(ref texels, texY * texWidth + texX);
                    a = (colorA * BitmapA(texel)) >> 8;
                    r = (colorR * BitmapR(texel)) >> 8;
                    g = (colorG * BitmapG(texel)) >> 8;
                    b = (colorB * BitmapB(texel)) >> 8;
                }
                else
                {
                    r = colorR; g = colorG; b = colorB; a = colorA;
                }
                if (alphaTest && a < 0x80) continue;
                if (alphaBlend && a == 0) continue;
                ref uint target = ref Unsafe.Add(ref colors, colorRow + x);
                if (alphaBlend && a != 255)
                {
                    uint destination = target;
                    int dr = BitmapR(destination), dg = BitmapG(destination), db = BitmapB(destination);
                    r = dr + ((a * (r - dr)) >> 8);
                    g = dg + ((a * (g - dg)) >> 8);
                    b = db + ((a * (b - db)) >> 8);
                }
                target = Make(r, g, b, 0xFF);
            }
        }
    }

    // Feste Werte eines Dreiecks fuer Plot; Farbe ist die Vertexfarbe (texturiert) oder die feste Farbe des Dreiecks.
    private struct Shading
    {
        public bool Texturing, ColorWrite, DepthWrite, AlphaTest, AlphaBlend;
        public int R, G, B, A;
    }

    // Ein Pixel nach bestandenem Tiefentest, Reihenfolge wie die Schleife in SoftGPU. Indizes wie in DrawTriangle3D begrenzt.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Plot(in Shading shading, ref uint colors, ref float depths, ref uint texels, int colorIndex, int depthIndex,
        float z, int texIndex)
    {
        if (!shading.ColorWrite)
        {
            if (shading.DepthWrite) Unsafe.Add(ref depths, depthIndex) = z;
            return;
        }
        int r = shading.R, g = shading.G, b = shading.B, a = shading.A;
        if (shading.Texturing)
        {
            uint texel = Unsafe.Add(ref texels, texIndex);
            a = (a * BitmapA(texel)) >> 8;
            r = (r * BitmapR(texel)) >> 8;
            g = (g * BitmapG(texel)) >> 8;
            b = (b * BitmapB(texel)) >> 8;
        }
        if (shading.AlphaTest && a < 0x80) return;
        if (shading.DepthWrite) Unsafe.Add(ref depths, depthIndex) = z;
        ref uint target = ref Unsafe.Add(ref colors, colorIndex);
        if (!shading.AlphaBlend)
        {
            target = Make(r, g, b, 0xFF);
            return;
        }
        uint destination = target;
        int dr = BitmapR(destination), dg = BitmapG(destination), db = BitmapB(destination);
        target = Make(dr + ((a * (r - dr)) >> 8), dg + ((a * (g - dg)) >> 8), db + ((a * (b - db)) >> 8), 0xFF);
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
            // Wie das C-Makro min(a, b) = a < b ? a : b; Math.Min verhielte sich bei NaN anders.
            float rawY0 = t0 / w0, rawY1 = t1 / w1;
            float rawY = rawY0 < rawY1 ? rawY0 : rawY1;
            uint texel = context.Texture[(ToInt(rawY + 0.01f) & context.TexHeightMask) * context.TexWidth];
            a = (A(color) * BitmapA(texel)) >> 8;
            r = (R(color) * BitmapR(texel)) >> 8;
            g = (G(color) * BitmapG(texel)) >> 8;
            b = (B(color) * BitmapB(texel)) >> 8;
            texturing = false;
        }

        var shading = new Shading
        {
            Texturing = texturing, ColorWrite = context.ColorWrite, DepthWrite = context.DepthWrite,
            AlphaTest = context.AlphaTest, AlphaBlend = context.AlphaBlend,
            R = texturing ? R(color) : r, G = texturing ? G(color) : g, B = texturing ? B(color) : b, A = texturing ? A(color) : a,
        };
        bool depthTest = context.DepthTest;
        int colorStride = context.ColorStride, depthStride = context.DepthStride;
        int texWidth = context.TexWidth, texWidthMask = context.TexWidthMask, texHeightMask = context.TexHeightMask;
        // Ohne Bereichspruefung je Pixel: x liegt in [0, MaxX] mit MaxX < width, y in [0, MaxY] mit MaxY < height, und Draw
        // hat Farb- und (bei Tiefentest oder -schreiben) Tiefenpuffer mit stride * (height - 1) + width Werten geprueft.
        // Der Texturindex ist durch die Masken kleiner als TexWidth * TexHeight, die Laenge der geprueften Textur.
        ref uint colors = ref MemoryMarshal.GetReference(context.Color);
        ref float depths = ref MemoryMarshal.GetReference(context.Depth);
        ref uint texels = ref MemoryMarshal.GetReference(context.Texture);
        bool positive = factor > 0;
        int columns = maxX - minX, rows = maxY - minY;
        bool exact = Exact(bc0Start, dx12, dy12, columns, rows) && Exact(bc1Start, dx20, dy20, columns, rows)
            && Exact(bc2Start, dx01, dy01, columns, rows);
        EdgeWalk walk0 = exact ? new(positive, bc0Start, dx12, dy12) : default;
        EdgeWalk walk1 = exact ? new(positive, bc1Start, dx20, dy20) : default;
        EdgeWalk walk2 = exact ? new(positive, bc2Start, dx01, dy01) : default;
        // Vier Pixel je Schritt nur bei exakten Kanten: Dann ist bc an Spalte x + k genau bc(x) + k * dx, auch als float.
        bool vector = exact && context.Vector;
        int lastVector = context.Width - 4;
        TriangleLanes lanes = vector ? new TriangleLanes(factor, w0, w1, w2, z0, z1, z2, u0, u1, u2, t0, t1, t2, dx12, dx20, dx01,
            texWidthMask, texHeightMask, BitOperations.Log2((uint)texWidth)) : default;

        for (int y = minY; y <= maxY; y++, bc0Start += dy12, bc1Start += dy20, bc2Start += dy01)
        {
            int first = minX, last = maxX;
            if (exact)
            {
                walk0.Apply(minX, maxX, ref first, ref last);
                walk1.Apply(minX, maxX, ref first, ref last);
                walk2.Apply(minX, maxX, ref first, ref last);
                if (first > last) continue;
            }
            int colorRow = y * colorStride, depthRow = y * depthStride;
            int x = first;
            if (vector && x <= lastVector)
            {
                x = lanes.Row(in shading, ref colors, ref depths, ref texels, depthTest, colorRow, depthRow, x, last, lastVector,
                    Advance(bc0Start, x - minX, dx12), Advance(bc1Start, x - minX, dx20), Advance(bc2Start, x - minX, dx01));
            }
            float bc0 = Advance(bc0Start, x - minX, dx12), bc1 = Advance(bc1Start, x - minX, dx20), bc2 = Advance(bc2Start, x - minX, dx01);
            bool entered = false;
            for (; x <= last; x++, bc0 += dx12, bc1 += dx20, bc2 += dx01)
            {
                if (Outside(positive, bc0, bc1, bc2))
                {
                    if (entered) break;
                    continue;
                }
                entered = true;
                float ic0 = bc0 * factor, ic1 = bc1 * factor, ic2 = bc2 * factor;
                int depthIndex = depthRow + x;
                float w = 1 / (ic0 * w0 + ic1 * w1 + ic2 * w2);
                float z = (ic0 * z0 + ic1 * z1 + ic2 * z2) * w;
                if (depthTest && (z < 0 || z > Unsafe.Add(ref depths, depthIndex))) continue;
                int texIndex = 0;
                if (texturing && shading.ColorWrite)
                {
                    float u = (ic0 * u0 + ic1 * u1 + ic2 * u2) * w;
                    float v = (ic0 * t0 + ic1 * t1 + ic2 * t2) * w;
                    texIndex = (ToInt(v) & texHeightMask) * texWidth + (ToInt(u) & texWidthMask);
                }
                Plot(in shading, ref colors, ref depths, ref texels, colorRow + x, depthIndex, z, texIndex);
            }
        }
    }

    /// <summary>
    /// Rechnet vier Pixel einer Zeile gleichzeitig (SSE2): dieselben float-Operationen in derselben Reihenfolge wie die
    /// skalare Schleife, daher bitgleich. Einzige Ausnahme: Treffen zwei NaN verschiedenen Vorzeichens aufeinander, bestimmt
    /// die vom Compiler gewaehlte Operandenreihenfolge das Vorzeichen einer NaN-Tiefe (wie schon zwischen Uebersetzungsstufen
    /// der skalaren Fassung); jeder Vergleich mit NaN ist davon unabhaengig, das Bild bleibt gleich.
    /// cvttps2dq liefert ausserhalb von int und bei NaN INT_MIN, genau wie ToInt.
    /// Die Farbe entsteht danach je Pixel in Plot; ganzzahlige Vektormultiplikation (erst ab SSE4.1) wird nicht gebraucht.
    /// </summary>
    private readonly struct TriangleLanes
    {
        private readonly Vector128<float> factor, w0, w1, w2, z0, z1, z2, u0, u1, u2, t0, t1, t2;
        private readonly Vector128<float> offset0, offset1, offset2, step0, step1, step2;
        private readonly Vector128<int> texWidthMask, texHeightMask;
        private readonly int texShift;

        public TriangleLanes(float factor, float w0, float w1, float w2, float z0, float z1, float z2, float u0, float u1, float u2,
            float t0, float t1, float t2, int dx12, int dx20, int dx01, int texWidthMask, int texHeightMask, int texShift)
        {
            this.factor = Vector128.Create(factor);
            this.w0 = Vector128.Create(w0); this.w1 = Vector128.Create(w1); this.w2 = Vector128.Create(w2);
            this.z0 = Vector128.Create(z0); this.z1 = Vector128.Create(z1); this.z2 = Vector128.Create(z2);
            this.u0 = Vector128.Create(u0); this.u1 = Vector128.Create(u1); this.u2 = Vector128.Create(u2);
            this.t0 = Vector128.Create(t0); this.t1 = Vector128.Create(t1); this.t2 = Vector128.Create(t2);
            // k * dx ist fuer jede gueltige Spalte exakt (Exact); Spalten hinter last werden nur maskiert, nie verwendet.
            offset0 = Vector128.Create(0f, dx12, 2f * dx12, 3f * dx12);
            offset1 = Vector128.Create(0f, dx20, 2f * dx20, 3f * dx20);
            offset2 = Vector128.Create(0f, dx01, 2f * dx01, 3f * dx01);
            step0 = Vector128.Create(4f * dx12); step1 = Vector128.Create(4f * dx20); step2 = Vector128.Create(4f * dx01);
            this.texWidthMask = Vector128.Create(texWidthMask);
            this.texHeightMask = Vector128.Create(texHeightMask);
            this.texShift = texShift;
        }

        /// <summary>Zeichnet [x, last] in Schritten zu vier Pixeln, solange x &lt;= lastVector (Schritt in der Pufferzeile); liefert das naechste x.</summary>
        public int Row(in Shading shading, ref uint colors, ref float depths, ref uint texels, bool depthTest, int colorRow, int depthRow,
            int x, int last, int lastVector, float bc0, float bc1, float bc2)
        {
            Vector128<float> b0 = Vector128.Create(bc0) + offset0, b1 = Vector128.Create(bc1) + offset1, b2 = Vector128.Create(bc2) + offset2;
            bool texture = shading.Texturing && shading.ColorWrite;
            for (; x <= last && x <= lastVector; x += 4, b0 += step0, b1 += step1, b2 += step2)
            {
                // Spalten hinter last liegen ausserhalb des Dreiecks oder des Scissors und werden nur maskiert; x + 3 < width.
                int lanes = last - x >= 3 ? 0xF : (1 << (last - x + 1)) - 1;
                Vector128<float> ic0 = b0 * factor, ic1 = b1 * factor, ic2 = b2 * factor;
                Vector128<float> w = Vector128<float>.One / (ic0 * w0 + ic1 * w1 + ic2 * w2);
                Vector128<float> z = (ic0 * z0 + ic1 * z1 + ic2 * z2) * w;
                if (depthTest)
                {
                    Vector128<float> depth = Vector128.LoadUnsafe(ref depths, (nuint)(depthRow + x));
                    Vector128<float> fail = Vector128.LessThan(z, Vector128<float>.Zero) | Vector128.GreaterThan(z, depth);
                    lanes &= ~(int)fail.ExtractMostSignificantBits();
                    if (lanes == 0) continue;
                }
                Vector128<int> texIndex = Vector128<int>.Zero;
                if (texture)
                {
                    Vector128<float> u = (ic0 * u0 + ic1 * u1 + ic2 * u2) * w;
                    Vector128<float> v = (ic0 * t0 + ic1 * t1 + ic2 * t2) * w;
                    Vector128<int> texX = Sse2.ConvertToVector128Int32WithTruncation(u) & texWidthMask;
                    Vector128<int> texY = Sse2.ConvertToVector128Int32WithTruncation(v) & texHeightMask;
                    // Zweierpotenz: Verschieben statt Multiplizieren (pmulld gibt es erst mit SSE4.1).
                    texIndex = Vector128.ShiftLeft(texY, texShift) + texX;
                }
                do
                {
                    int lane = BitOperations.TrailingZeroCount(lanes);
                    lanes &= lanes - 1;
                    Plot(in shading, ref colors, ref depths, ref texels, colorRow + x + lane, depthRow + x + lane, z.GetElement(lane),
                        texIndex.GetElement(lane));
                }
                while (lanes != 0);
            }
            return x;
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
