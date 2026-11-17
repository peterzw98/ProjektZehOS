using System;
using System.Runtime.CompilerServices;

namespace ProjektZeh.Shared;

/// <summary>
/// Grosse Puffer mit Null-Abwehr. Cosmos lieferte bei erschoepftem Heap fuer eine grosse Allokation null statt einer
/// OutOfMemoryException (E4.0-Lauf 10, Page Fault CR2=0x10 beim ersten Zugriff). Der Aufrufer prueft deshalb vor jeder
/// Zustandsaenderung und meldet einen Fehler statt Erfolg.
/// </summary>
internal static class BufferAllocation
{
    /// <summary>Neuer Puffer oder null, wenn der Heap keinen liefert; eine echte OutOfMemoryException zaehlt ebenso.</summary>
    public static byte[]? TryAllocate(int length) => TryAllocate(length, collectFirst: false);

    /// <summary>
    /// Wie <see cref="TryAllocate(int)"/>; mit <paramref name="collectFirst"/> wird vorher gesammelt. Cosmos 3.0.97 sammelt bei
    /// verwalteten Objekten selbst erst, wenn der Seitenspeicher voll ist, und verdichtet nicht: Ein frueher Collect gibt
    /// Segmente frei, solange noch Platz ist, und erhoeht so die Chance auf einen zusammenhaengenden Seitenlauf. Gegen bereits
    /// fragmentierten Speicher hilft er nicht; die Null-Abwehr bleibt deshalb der eigentliche Schutz.
    /// </summary>
    public static byte[]? TryAllocate(int length, bool collectFirst)
    {
        if (collectFirst) GC.Collect();
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        try
        {
            byte[] buffer = new byte[length];
            return IsMissing(buffer) ? null : buffer;
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
    }

    // Nicht inlinen: JIT/ILC sehen newarr als nie null an und wuerden einen direkten Vergleich entfernen.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsMissing(object? value) => value is null;
}
