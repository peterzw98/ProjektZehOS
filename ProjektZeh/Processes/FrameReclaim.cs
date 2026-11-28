using System;
using System.Threading;

namespace ProjektZeh.Processes;

/// <summary>
/// V47: Nutzer-Frames kommen aus demselben Seitenverwalter wie der Kernel-Heap, doch der GC sammelt nur bei einem Refill-Fehlschlag des Heaps.
/// Garbage von Exec, Seitenfehlern und Dateilesen hält deshalb Seiten fest, bis die V43-Reserve greift und ein Programm ENOMEM bekommt, obwohl
/// eine Sammlung Megabytes zurückgäbe. Diese Klasse sammelt einmal und versucht es neu, statt gleich aufzugeben.
/// </summary>
internal static class FrameReclaim
{
    /// <summary>Anteil der Gesamtseiten (1 geteilt durch 2 hoch ThresholdShift), unter dem am Prozessende gesammelt wird: 1/4.</summary>
    private const int ThresholdShift = 2;

    private static readonly object CollectGate = new();
    private static long generation;
    private static long lastCollectMs = long.MinValue / 2;

    /// <summary>Mindestabstand zweier Sammlungen (ms): Bei einem Sturm von Fehlschlägen nahe der Reserve sammelt nicht jeder Thread voll.</summary>
    private const long MinIntervalMs = 20;

    /// <summary>
    /// Führt <paramref name="attempt"/> aus; scheitert er, wird einmal gesammelt und er läuft ein zweites Mal. Der Aufrufer hält dabei
    /// <b>keine</b> Sperre (_memGate, _gate, StorageLock): Ein Voll-GC unter einer Sperre hielte jeden anderen Thread, der sie braucht, so lange fest.
    /// Hat ein anderer Thread inzwischen gesammelt (Generationszähler) oder lag die letzte Sammlung weniger als <see cref="MinIntervalMs"/> zurück,
    /// entfällt die eigene Sammlung (Sturmbremse). Wirft die Sammlung (etwa OOM beim Markieren), zählt sie als erfolglos und der zweite Versuch läuft trotzdem.
    /// </summary>
    public static bool Retry(Func<bool> attempt, Action collect, Func<long> nowMs)
    {
        long seen = Volatile.Read(ref generation);
        if (attempt())
        {
            return true;
        }

        lock (CollectGate)
        {
            long now = nowMs();
            if (Volatile.Read(ref generation) == seen && now - lastCollectMs >= MinIntervalMs)
            {
                try
                {
                    collect();
                }
                catch (Exception)
                {
                    // Der ENOMEM-Pfad bleibt erhalten; der zweite Versuch entscheidet.
                }

                lastCollectMs = nowMs();
                Interlocked.Increment(ref generation);
            }
        }

        return attempt();
    }

    /// <summary>Nur für Tests: Zustand der Sturmbremse zurücksetzen.</summary>
    internal static void ResetForTests()
    {
        lock (CollectGate)
        {
            lastCollectMs = long.MinValue / 2;
            generation = 0;
        }
    }

    /// <summary>Soll am Prozessende gesammelt werden? Ja, wenn frei unter einem Viertel der Seiten oder nahe der Reserve liegt.</summary>
    public static bool ShouldCollectAtExit(ulong freePages, ulong totalPages, ulong reservedPages)
    {
        if (totalPages == 0)
        {
            return false;
        }

        // Die Reserve ist die Grenze, ab der Nutzer-Frames scheitern; doppelt so viel Abstand lässt Zeit für die Sammlung.
        return freePages < (totalPages >> ThresholdShift) || freePages < reservedPages * 2;
    }

    /// <summary>
    /// V51: Liegt der freie Speicher nahe der Kernel-Reserve (V43)? Dann ist ein false der Cosmos-VFS (Lookup/Öffnen, das Allokationsfehler nur
    /// als false meldet) eher fehlender Speicher als ein fehlender Eintrag: ENOMEM statt ENOENT. Ohne Reserve (0, kein Linux-Lauf) nie.
    /// </summary>
    public static bool IsNearReserve(ulong freePages, ulong reservedPages) =>
        reservedPages != 0 && freePages < reservedPages + (reservedPages >> 2);
}
