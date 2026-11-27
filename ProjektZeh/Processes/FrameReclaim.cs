using System;

namespace ProjektZeh.Processes;

/// <summary>
/// V47: Nutzer-Frames kommen aus demselben Seitenverwalter wie der Kernel-Heap, doch der GC sammelt nur bei einem Refill-Fehlschlag des Heaps.
/// Garbage von Exec, Seitenfehlern und Dateilesen hält deshalb Seiten fest, bis die V43-Reserve greift und ein Programm ENOMEM bekommt, obwohl
/// eine Sammlung Megabytes zurückgäbe. Diese Klasse sammelt einmal und versucht es neu, statt gleich aufzugeben.
/// </summary>
internal static class FrameReclaim
{
    /// <summary>Anteil der Gesamtseiten (1/<c>FreeShift</c>-Potenz), unter dem am Prozessende gesammelt wird: 1/4.</summary>
    private const int ThresholdShift = 2;

    /// <summary>Holt einen Frame; bei 0 einmal <paramref name="collect"/> und ein zweiter Versuch. 0 bleibt 0 (ENOMEM).</summary>
    public static ulong Allocate(Func<ulong> allocate, Action collect)
    {
        ulong frame = allocate();
        if (frame != 0)
        {
            return frame;
        }

        collect();
        return allocate();
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
}
