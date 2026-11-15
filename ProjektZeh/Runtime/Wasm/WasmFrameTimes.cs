using System;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>
/// Bildzeiten einer App als festes 1-ms-Histogramm je Messfenster; pro Bild ohne Allokation. Die Sitzung
/// synchronisiert alle Zugriffe und liefert eine Uhr ohne Pausen.
/// </summary>
internal sealed class WasmFrameTimes(long windowMilliseconds = 60_000)
{
    // Laengere Abstaende landen im letzten Eimer; min und max bleiben trotzdem genau.
    private const int Buckets = 1000;
    private readonly int[] counts = new int[Buckets + 1];
    private long last = -1;
    private long windowStart;
    private int total;
    private long min;
    private long max;

    /// <summary>Zaehlt den Abstand zum vorigen Bild; liefert nach Ablauf des Fensters dessen Zusammenfassung.</summary>
    public string? Record(long now)
    {
        if (last < 0)
        {
            Restart(now);
            windowStart = now;
            return null;
        }
        long interval = Math.Max(0, now - last);
        last = now;
        counts[Math.Min(interval, Buckets)]++;
        min = total == 0 ? interval : Math.Min(min, interval);
        max = Math.Max(max, interval);
        total++;
        if (now - windowStart < windowMilliseconds) return null;
        string report = $"Bildzeit n={total} min={min} median={Percentile(50)} p95={Percentile(95)} max={max} ms";
        Array.Clear(counts);
        total = 0;
        max = 0;
        windowStart = now;
        return report;
    }

    /// <summary>Nach einer Pause: der Abstand dorthin ist keine Bildzeit der App.</summary>
    public void Restart(long now) => last = now;

    // Naechster Rang; faellt er in den Ueberlaufeimer, gilt das genaue Maximum.
    private long Percentile(int percent)
    {
        int rank = (int)(((long)total * percent + 99) / 100);
        int seen = 0;
        for (int bucket = 0; bucket < Buckets; bucket++)
        {
            seen += counts[bucket];
            if (seen >= rank) return bucket;
        }
        return max;
    }
}
