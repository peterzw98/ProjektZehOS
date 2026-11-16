using System;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>
/// Seitentreue Einpassung eines App-Bildes in den Fensterinhalt, vergroessernd wie verkleinernd. Zeichnen und
/// Eingabeabbildung verwenden dieselben Formeln, damit ein Klick das Bildpixel trifft, das dort gezeichnet ist.
/// </summary>
internal static class WasmImageFit
{
    /// <summary>Groesste Zielgroesse mit dem Seitenverhaeltnis des Bildes; (0, 0) ohne Bild oder Flaeche.</summary>
    public static (int Width, int Height) Fit(int viewportWidth, int viewportHeight, int imageWidth, int imageHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || imageWidth <= 0 || imageHeight <= 0) return (0, 0);
        // Ganzzahlig verglichen: die Breite begrenzt, wenn viewportWidth/imageWidth <= viewportHeight/imageHeight.
        if ((long)viewportWidth * imageHeight <= (long)viewportHeight * imageWidth)
            return (viewportWidth, (int)Math.Max(1, (long)imageHeight * viewportWidth / imageWidth));
        return ((int)Math.Max(1, (long)imageWidth * viewportHeight / imageHeight), viewportHeight);
    }

    /// <summary>Quellindex fuer eine Zielposition (naechster Nachbar); gleiche Formel wie die Zeichentabellen.</summary>
    public static int SourceIndex(long target, int targetSize, int sourceSize) => (int)(target * sourceSize / targetSize);

    /// <summary>Fuellt eine Spalten- oder Zeilentabelle, damit beim Zeichnen keine Division pro Pixel anfaellt.</summary>
    public static void FillSourceIndices(int[] table, int sourceSize)
    {
        for (int target = 0; target < table.Length; target++) table[target] = SourceIndex(target, table.Length, sourceSize);
    }
}
