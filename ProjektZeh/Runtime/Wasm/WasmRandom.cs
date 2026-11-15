using System;

namespace ProjektZeh.Runtime.Wasm;

/// <summary>Plattformunabhaengiger Teil von WASI random_get; die native Hostfunktion in WasmRuntime ruft ihn auf.</summary>
internal static class WasmRandom
{
    internal const int Success = 0;
    internal const int Io = 29; // WASI EIO

    /// <summary>Fuellt target aus source und liefert einen WASI-Fehlercode; wirft nie, weil der Aufrufer an der nativen Grenze steht.</summary>
    internal static int Fill(Span<byte> target, Action<byte[]> source)
    {
        try
        {
            // In kleinen Bloecken: eine Anfrage ueber die ganze lineare Memory darf keine riesige Allokation ausloesen.
            var bytes = new byte[Math.Min(target.Length, 4096)];
            for (int offset = 0; offset < target.Length; offset += bytes.Length)
            {
                source(bytes);
                int count = Math.Min(bytes.Length, target.Length - offset);
                bytes.AsSpan(0, count).CopyTo(target.Slice(offset, count));
            }
            return Success;
        }
        catch (Exception)
        {
            // Ohne Hardwarezufall gibt es keinen Ersatz: Nullen oder ein schwacher Rueckfall waeren fuer Schluessel
            // vorhersagbar. Der Gast erhaelt den Fehler; bereits kopierte Bytes sind trotzdem nicht als gueltig zu werten.
            return Io;
        }
    }
}
