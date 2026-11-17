using System;
using System.Globalization;
using System.Text;

namespace ProjektZeh.Storage.Partitioning;

/// <summary>
/// Zeilentext mit abschliessender Zeile "crc32=xxxxxxxx" ueber alle Zeilen davor, damit ein Teilschreiben nie als gueltig gilt.
/// Gemeinsame Regel fuer die Marker (Root-Marker in Etappe 4.4; der ESP-Marker aus 7a soll sie nach seinem Merge ebenfalls
/// verwenden, statt sie zu kopieren).
/// </summary>
internal static class ChecksummedText
{
    private const string CrcKey = "crc32=";

    /// <summary>Haengt die Pruefzeile an; <paramref name="body"/> endet mit einem Zeilenumbruch.</summary>
    public static string Seal(string body)
    {
        if (body.Length == 0 || !body.EndsWith('\n')) throw new ArgumentException("Der Text muss mit einem Zeilenumbruch enden.", nameof(body));
        return body + CrcKey + Crc32.Compute(Encoding.UTF8.GetBytes(body)).ToString("x8", CultureInfo.InvariantCulture) + "\n";
    }

    /// <summary>
    /// Liefert die Zeilen vor der Pruefzeile (ohne leeren Rest), oder <see langword="null"/> bei zu grossem Text, fehlender oder
    /// falscher Pruefzeile. Die Felder selbst prueft der jeweilige Marker.
    /// </summary>
    public static string[]? Open(string text, int maximumBytes, int maximumLines)
    {
        int lines = 0;
        foreach (char value in text) if (value == '\n') lines++;
        if (Encoding.UTF8.GetByteCount(text) > maximumBytes || lines > maximumLines) return null;

        int crcLine = text.LastIndexOf("\n" + CrcKey, StringComparison.Ordinal);
        if (crcLine < 0 || !text.EndsWith('\n')) return null;
        string body = text[..(crcLine + 1)];
        string crcText = text[(crcLine + 1 + CrcKey.Length)..^1];
        if (crcText.Length != 8 || !uint.TryParse(crcText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint stored) ||
            stored != Crc32.Compute(Encoding.UTF8.GetBytes(body))) return null;
        return body[..^1].Split('\n');
    }
}
