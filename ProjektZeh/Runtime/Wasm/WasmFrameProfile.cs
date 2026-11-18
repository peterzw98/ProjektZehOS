using System;
using System.Text;
using System.Threading;

namespace ProjektZeh.Runtime.Wasm;

#if ZEHOS_APP_PROFILE
// Raster: Zeit in raster_draw/raster_fill je Bild; RasterCalls: Aufrufe je Bild (als "ms" ausgegeben, 1 Aufruf = 1,0).
internal enum WasmFramePhase { App, Sleep, Copy, Scale, Desktop, Display, Raster, RasterCalls }
#else
internal enum WasmFramePhase { App, Sleep, Copy, Scale, Desktop, Display }
#endif

/// <summary>
/// Zerlegt die Bildzeit von Paket-Apps in Anteile (Wasm-Rechnen bis present, sleep_ms der App, Bildkopie, Skalierung,
/// Desktop, Canvas.Display).
/// Zur Laufzeit zuschaltbar; ausgeschaltet kostet eine Messstelle nur die Abfrage von <see cref="Enabled"/>, und
/// pro Messung wird nie allokiert. Die Anteile laufen auf verschiedenen Threads, deshalb eine eigene Sperre.
/// </summary>
internal sealed class WasmFrameProfile
{
    // 100-us-Eimer bis 1 s (Doom erreicht p95 ueber 100 ms); laengere Dauern landen im letzten Eimer, das Maximum bleibt genau.
    private const int BucketMicroseconds = 100;
    private const int Buckets = 10_000;
#if ZEHOS_APP_PROFILE
    private static readonly string[] Names = ["app", "schlaf", "kopie", "skalierung", "desktop", "anzeige", "raster", "rasteraufrufe"];

    private static long rasterTicks;
    private static long rasterCalls;

    /// <summary>Summiert die Zeit eines Rasteraufrufs (Worker-Thread) bis zum naechsten present.</summary>
    public static void AddRaster(long startTicks)
    {
        Interlocked.Add(ref rasterTicks, System.Diagnostics.Stopwatch.GetTimestamp() - startTicks);
        Interlocked.Increment(ref rasterCalls);
    }

    /// <summary>Holt und leert die seit dem letzten present aufgelaufene Rasterzeit in us und die Aufrufzahl.</summary>
    public static (long Microseconds, long Calls) TakeRaster()
    {
        long ticks = Interlocked.Exchange(ref rasterTicks, 0);
        return (ticks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency, Interlocked.Exchange(ref rasterCalls, 0));
    }
#else
    private static readonly string[] Names = ["app", "schlaf", "kopie", "skalierung", "desktop", "anzeige"];
#endif

    // Nach Names: statische Felder werden in Textreihenfolge initialisiert.
    public static WasmFrameProfile Shared { get; } = new();
    private readonly object gate = new();
    private readonly int[][] counts = CreateCounts();
    private readonly int[] totals = new int[Names.Length];
    private readonly long[] maxima = new long[Names.Length];
    private volatile bool enabled;

    public bool Enabled => enabled;

    public static long Microseconds(long startTicks, long endTicks) =>
        (endTicks - startTicks) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Schaltet die Messung um; beide Richtungen beginnen mit leeren Werten.</summary>
    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            enabled = value;
            Clear();
        }
    }

    public void Record(WasmFramePhase phase, long microseconds)
    {
        if (!enabled) return;
        int index = (int)phase;
        long duration = Math.Max(0, microseconds);
        lock (gate)
        {
            // Erneut pruefen: ein Wert nach SetEnabled(false) soll nicht im naechsten Peek auftauchen.
            if (!enabled) return;
            counts[index][Math.Min(duration / BucketMicroseconds, Buckets)]++;
            totals[index]++;
            maxima[index] = Math.Max(maxima[index], duration);
        }
    }

    /// <summary>Median, p95 und Maximum je Anteil in ms; leert danach fuer das naechste Messfenster.</summary>
    public string TakeSummary()
    {
        lock (gate)
        {
            string summary = Describe();
            Clear();
            return summary;
        }
    }

    /// <summary>Aktueller Stand ohne Zuruecksetzen, etwa fuer den Shellbefehl.</summary>
    public string Peek()
    {
        lock (gate) return Describe();
    }

    private string Describe()
    {
        var text = new StringBuilder("Anteile (ms, median/p95/max)");
        for (int index = 0; index < Names.Length; index++)
        {
            text.Append(' ').Append(Names[index]).Append('=');
            if (totals[index] == 0) { text.Append('-'); continue; }
            text.Append(Milliseconds(Percentile(index, 50))).Append('/').Append(Milliseconds(Percentile(index, 95)))
                .Append('/').Append(Milliseconds(maxima[index])).Append(" n=").Append(totals[index]);
        }
        return text.ToString();
    }

    // Obere Grenze des Eimers, in dem der Rang liegt; faellt er in den Ueberlaufeimer, gilt das genaue Maximum.
    private long Percentile(int index, int percent)
    {
        int rank = (int)(((long)totals[index] * percent + 99) / 100);
        int seen = 0;
        for (int bucket = 0; bucket < Buckets; bucket++)
        {
            seen += counts[index][bucket];
            if (seen >= rank) return Math.Min((bucket + 1L) * BucketMicroseconds, maxima[index]);
        }
        return maxima[index];
    }

    private static string Milliseconds(long microseconds) =>
        (microseconds / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + (microseconds % 1000 / 100);

    private void Clear()
    {
        foreach (int[] phase in counts) Array.Clear(phase);
        Array.Clear(totals);
        Array.Clear(maxima);
    }

    private static int[][] CreateCounts()
    {
        var result = new int[Names.Length][];
        for (int index = 0; index < result.Length; index++) result[index] = new int[Buckets + 1];
        return result;
    }
}
