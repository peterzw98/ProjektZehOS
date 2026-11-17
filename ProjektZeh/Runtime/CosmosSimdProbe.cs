#if ZEHOS_SIMD_PROBE
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Cosmos.Kernel.System.Diagnostics;

namespace ProjektZeh.Runtime;

/// <summary>
/// Gastprobe fuer System.Runtime.Intrinsics im Cosmos-/NativeAOT-Kernel (nur mit -p:ZehOSSimdProbe=true).
/// Jede Stufe schreibt vor dem Lauf "begin ..." seriell: ein Absturz (#UD bei fehlender OS-Unterstuetzung) zeigt so die letzte Stufe.
/// Korrektheit entscheidet PASS/FAIL; Zeiten sind INFO (unter TCG ist SIMD oft langsamer, das ist kein Fehler der Probe).
/// </summary>
internal static class CosmosSimdProbe
{
    private const int Length = 16384; // Vielfaches von 8 und 16
    private static int failures;

    public static bool Run()
    {
        try
        {
            ReportInstructionSets();
            var left = new int[Length];
            var right = new int[Length];
            uint seed = 12345;
            for (int i = 0; i < Length; i++)
            {
                seed = seed * 1664525 + 1013904223;
                left[i] = (int)(seed >> 16) & 0xFF;
                seed = seed * 1664525 + 1013904223;
                right[i] = (int)(seed >> 16) & 0xFF;
            }

            IntDot(left, right);
            FloatDot(left, right);
            ByteCount(left);
            ThreadState(left, right);
            Write(failures == 0 ? "PASS" : $"FAIL {failures} Stufen fehlerhaft");
            return failures == 0;
        }
        catch (Exception exception)
        {
            Write($"FAIL {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static void ReportInstructionSets()
    {
        Write($"isa X86Base={X86Base.IsSupported} Sse={Sse.IsSupported} Sse2={Sse2.IsSupported} Sse3={Sse3.IsSupported} Ssse3={Ssse3.IsSupported} " +
              $"Sse41={Sse41.IsSupported} Sse42={Sse42.IsSupported} Popcnt={Popcnt.IsSupported} Avx={Avx.IsSupported} Avx2={Avx2.IsSupported} " +
              $"Fma={Fma.IsSupported} Bmi1={Bmi1.IsSupported} Bmi2={Bmi2.IsSupported} Lzcnt={Lzcnt.IsSupported} Avx512F={Avx512F.IsSupported}");
        Write($"accel Vector128={Vector128.IsHardwareAccelerated} Vector256={Vector256.IsHardwareAccelerated} Vector512={Vector512.IsHardwareAccelerated} " +
              $"Vector<T>={Vector.IsHardwareAccelerated} Vector<int>.Count={Vector<int>.Count}");
    }

    // ---- int-Skalarprodukt (Multiplikation + Addition, exakt vergleichbar durch Ganzzahl-Ueberlauf) ----

    private static void IntDot(int[] a, int[] b)
    {
        int expected = DotScalar(a, b);
        int reps = Calibrate(() => DotScalar(a, b));
        long baseline = Measure(() => DotScalar(a, b), reps);
        Info("int-dot scalar", baseline, baseline, reps);
        if (Vector128.IsHardwareAccelerated) Tier("int-dot vector128", expected, () => DotVector128(a, b), baseline, reps);
        if (Vector256.IsHardwareAccelerated) Tier("int-dot vector256", expected, () => DotVector256(a, b), baseline, reps);
        if (Vector.IsHardwareAccelerated) Tier("int-dot Vector<T>", expected, () => DotNumerics(a, b), baseline, reps);
        if (Sse41.IsSupported) Tier("int-dot sse41", expected, () => DotSse41(a, b), baseline, reps);
        else Skip("int-dot sse41");
        if (Avx2.IsSupported) Tier("int-dot avx2", expected, () => DotAvx2(a, b), baseline, reps);
        else Skip("int-dot avx2");
    }

    private static int DotScalar(int[] a, int[] b)
    {
        int sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static int DotVector128(int[] a, int[] b)
    {
        var sum = Vector128<int>.Zero;
        for (int i = 0; i <= a.Length - 4; i += 4) sum += Vector128.Create(a, i) * Vector128.Create(b, i);
        return Vector128.Sum(sum);
    }

    private static int DotVector256(int[] a, int[] b)
    {
        var sum = Vector256<int>.Zero;
        for (int i = 0; i <= a.Length - 8; i += 8) sum += Vector256.Create(a, i) * Vector256.Create(b, i);
        return Vector256.Sum(sum);
    }

    private static int DotNumerics(int[] a, int[] b)
    {
        var sum = Vector<int>.Zero;
        int step = Vector<int>.Count;
        for (int i = 0; i <= a.Length - step; i += step) sum += new Vector<int>(a, i) * new Vector<int>(b, i);
        return Vector.Sum(sum);
    }

    private static int DotSse41(int[] a, int[] b)
    {
        var sum = Vector128<int>.Zero;
        for (int i = 0; i <= a.Length - 4; i += 4) sum = Sse2.Add(sum, Sse41.MultiplyLow(Vector128.Create(a, i), Vector128.Create(b, i)));
        return Vector128.Sum(sum);
    }

    private static int DotAvx2(int[] a, int[] b)
    {
        var sum = Vector256<int>.Zero;
        for (int i = 0; i <= a.Length - 8; i += 8) sum = Avx2.Add(sum, Avx2.MultiplyLow(Vector256.Create(a, i), Vector256.Create(b, i)));
        return Vector256.Sum(sum);
    }

    // ---- float-Skalarprodukt mit ganzzahligen Werten (exakt, auch mit FMA) ----

    private static void FloatDot(int[] a, int[] b)
    {
        var x = new float[a.Length];
        var y = new float[b.Length];
        for (int i = 0; i < a.Length; i++) { x[i] = a[i] & 7; y[i] = b[i] & 7; }
        float expected = FloatScalar(x, y);
        int reps = Calibrate(() => (int)FloatScalar(x, y));
        long baseline = Measure(() => (int)FloatScalar(x, y), reps);
        Info("float-dot scalar", baseline, baseline, reps);
        if (Vector128.IsHardwareAccelerated) TierFloat("float-dot vector128", expected, () => FloatVector128(x, y), baseline, reps);
        if (Fma.IsSupported && Avx.IsSupported) TierFloat("float-dot fma256", expected, () => FloatFma256(x, y), baseline, reps);
        else Skip("float-dot fma256");
    }

    private static float FloatScalar(float[] x, float[] y)
    {
        float sum = 0;
        for (int i = 0; i < x.Length; i++) sum += x[i] * y[i];
        return sum;
    }

    private static float FloatVector128(float[] x, float[] y)
    {
        var sum = Vector128<float>.Zero;
        for (int i = 0; i <= x.Length - 4; i += 4) sum += Vector128.Create(x, i) * Vector128.Create(y, i);
        return Vector128.Sum(sum);
    }

    private static float FloatFma256(float[] x, float[] y)
    {
        var sum = Vector256<float>.Zero;
        for (int i = 0; i <= x.Length - 8; i += 8) sum = Fma.MultiplyAdd(Vector256.Create(x, i), Vector256.Create(y, i), sum);
        return Vector256.Sum(sum);
    }

    // ---- Bytes zaehlen: Vergleich + MoveMask + PopCount ----

    private static void ByteCount(int[] source)
    {
        var data = new byte[source.Length * 4];
        for (int i = 0; i < source.Length; i++) { data[4 * i] = (byte)source[i]; data[4 * i + 1] = 7; data[4 * i + 2] = 0; data[4 * i + 3] = (byte)(source[i] >> 3); }
        int expected = CountScalar(data, 7);
        int reps = Calibrate(() => CountScalar(data, 7));
        long baseline = Measure(() => CountScalar(data, 7), reps);
        Info("byte-count scalar", baseline, baseline, reps);
        if (Sse2.IsSupported) Tier("byte-count sse2", expected, () => CountSse2(data, 7), baseline, reps);
        else Skip("byte-count sse2");
        if (Avx2.IsSupported) Tier("byte-count avx2", expected, () => CountAvx2(data, 7), baseline, reps);
        else Skip("byte-count avx2");
    }

    private static int CountScalar(byte[] data, byte value)
    {
        int count = 0;
        for (int i = 0; i < data.Length; i++) if (data[i] == value) count++;
        return count;
    }

    private static int CountSse2(byte[] data, byte value)
    {
        var needle = Vector128.Create(value);
        int count = 0;
        for (int i = 0; i <= data.Length - 16; i += 16)
            count += BitOperations.PopCount((uint)Sse2.MoveMask(Sse2.CompareEqual(Vector128.Create(data, i), needle)));
        return count;
    }

    private static int CountAvx2(byte[] data, byte value)
    {
        var needle = Vector256.Create(value);
        int count = 0;
        for (int i = 0; i <= data.Length - 32; i += 32)
            count += BitOperations.PopCount((uint)Avx2.MoveMask(Avx2.CompareEqual(Vector256.Create(data, i), needle)));
        return count;
    }

    // ---- Registerzustand ueber Threadwechsel ----

    /// <summary>Zwei Threads rechnen gleichzeitig mit Vektoren; ein nicht gesicherter XMM/YMM-Zustand beim Umschalten verfaelscht Ergebnisse.</summary>
    private static void ThreadState(int[] a, int[] b)
    {
        if (!Vector128.IsHardwareAccelerated) { Skip("thread-state"); return; }
        Write("begin thread-state");
        int bad = 0;
        long done = 0;
        var first = new int[a.Length];
        var second = new int[a.Length];
        for (int i = 0; i < a.Length; i++) { first[i] = a[i] + 3; second[i] = b[i] + 5; }
        int expectedFirst = DotScalar(first, b);
        int expectedSecond = DotScalar(a, second);
        bool wide = Vector256.IsHardwareAccelerated;
        var one = new Thread(() => Loop(first, b, expectedFirst, wide, ref bad, ref done));
        var two = new Thread(() => Loop(a, second, expectedSecond, wide, ref bad, ref done));
        one.Start();
        two.Start();
        one.Join();
        two.Join();
        bool ok = Volatile.Read(ref bad) == 0 && Volatile.Read(ref done) > 0;
        Write($"thread-state {(ok ? "PASS" : "FAIL")} Laeufe={Volatile.Read(ref done)} Abweichungen={Volatile.Read(ref bad)} breit={wide}");
        if (!ok) failures++;
    }

    private static void Loop(int[] a, int[] b, int expected, bool wide, ref int bad, ref long done)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 400)
        {
            int result = wide ? DotVector256(a, b) : DotVector128(a, b);
            if (result != expected) Interlocked.Increment(ref bad);
            Interlocked.Increment(ref done);
        }
    }

    // ---- Hilfen ----

    private static void Tier(string name, int expected, Func<int> work, long baseline, int reps)
    {
        Write($"begin {name}");
        int actual = work();
        if (actual != expected) { failures++; Write($"{name} FAIL erwartet={expected} erhalten={actual}"); return; }
        Write($"{name} PASS");
        Info(name, Measure(work, reps), baseline, reps);
    }

    private static void TierFloat(string name, float expected, Func<float> work, long baseline, int reps)
    {
        Write($"begin {name}");
        float actual = work();
        if (actual != expected) { failures++; Write($"{name} FAIL erwartet={expected} erhalten={actual}"); return; }
        Write($"{name} PASS");
        Info(name, Measure(() => (int)work(), reps), baseline, reps);
    }

    private static void Skip(string name) => Write($"{name} SKIP (IsSupported=false)");

    /// <summary>Wiederholungen so waehlen, dass eine Stufe etwa 100 ms dauert (TCG und KVM unterscheiden sich um Groessenordnungen).</summary>
    private static int Calibrate(Func<int> work)
    {
        var clock = Stopwatch.StartNew();
        work();
        long ticks = Math.Max(1, clock.ElapsedTicks);
        long target = Stopwatch.Frequency / 10;
        return (int)Math.Clamp(target / ticks, 1, 100000);
    }

    private static long Measure(Func<int> work, int reps)
    {
        int sink = 0;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < reps; i++) sink += work();
        long microseconds = clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
        if (sink == int.MinValue) Write("sink"); // verhindert, dass der Aufruf weggeoptimiert wird
        return microseconds;
    }

    private static void Info(string name, long microseconds, long baselineMicroseconds, int reps)
    {
        // Verhaeltnis in Promille, damit kein Gleitkomma-Formatieren im Kernel noetig ist; > 1000 = schneller als skalar.
        long speedup = microseconds == 0 ? 0 : baselineMicroseconds * 1000 / microseconds;
        Write($"INFO {name} reps={reps} us={microseconds} speedup_permille={speedup} {(speedup > 1000 ? "FASTER" : "NOT_FASTER")}");
    }

    private static void Write(string message) => Log.WriteString($"SIMD_PROBE {message}\n");
}
#endif
