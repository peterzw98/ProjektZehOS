using System;
using System.IO;
using System.Text;

namespace ProjektZeh.Network;

internal static class TlsRoots
{
    private static byte[]? bundle;

    private static DateTime? minimumTime;

    // Einmal je Start lesen: jeder HTTPS-Abruf (ein Paket sind 4 + Dateienzahl) las sonst 189 KB neu.
    internal static byte[] Load() => bundle ??= Read("ZehOS.TlsRoots");

    /// <summary>
    /// Etappe 12.2: Stand des eingebetteten Mozilla-Bundles als Untergrenze jeder TLS-Uhrzeit. Eine Uhr davor ist sicher
    /// falsch, weil das Bundle nicht aus der Zukunft stammen kann.
    /// </summary>
    internal static DateTime MinimumTime => minimumTime ??= ParseBundleDate(Load());

    private const string DateMarker = "Certificate data from Mozilla as of:";
    private const string Months = "JanFebMarAprMayJunJulAugSepOctNovDec";

    /// <summary>
    /// Liest "## Certificate data from Mozilla as of: Fri Sep 25 03:12:01 2026 GMT" (Perl-gmtime, Tag auch mit
    /// Leerzeichen aufgefuellt). Von Hand statt mit ParseExact, damit keine Kulturdaten in den Kernel muessen.
    /// </summary>
    internal static DateTime ParseBundleDate(byte[] pem)
    {
        string head = Encoding.ASCII.GetString(pem, 0, Math.Min(pem.Length, 4096));
        int start = head.IndexOf(DateMarker, StringComparison.Ordinal);
        int end = start < 0 ? -1 : head.IndexOf('\n', start);
        if (end < 0) throw new InvalidOperationException("Stand des TLS-Zertifikatbundles fehlt.");
        string[] parts = head[(start + DateMarker.Length)..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int month = parts.Length == 6 ? Months.IndexOf(parts[1], StringComparison.Ordinal) : -1;
        string[] time = parts.Length == 6 ? parts[3].Split(':') : [];
        if (month < 0 || month % 3 != 0 || parts[1].Length != 3 || time.Length != 3 || parts[5].TrimEnd('\r') != "GMT" ||
            !int.TryParse(parts[2], out int day) || !int.TryParse(parts[4], out int year) || !int.TryParse(time[0], out int hour) ||
            !int.TryParse(time[1], out int minute) || !int.TryParse(time[2], out int second))
            throw new InvalidOperationException("Stand des TLS-Zertifikatbundles unlesbar.");
        return new DateTime(year, month / 3 + 1, day, hour, minute, second, DateTimeKind.Utc);
    }

    internal static byte[] Read(string resourceName)
    {
        Stream? stream = typeof(TlsRoots).Assembly.GetManifestResourceStream(resourceName);
        if (stream == null) throw new InvalidOperationException("TLS-Zertifikatbundle fehlt: " + resourceName);
        Exception? failure = null;
        byte[]? result = null;
        try
        {
            if (stream.Length is <= 0 or > 262144) throw new InvalidOperationException("Ungueltige Groesse des TLS-Zertifikatbundles.");
            result = new byte[(int)stream.Length];
            int offset = 0;
            while (offset < result.Length)
            {
                int count = stream.Read(result, offset, result.Length - offset);
                if (count <= 0) throw new EndOfStreamException("TLS-Zertifikatbundle unvollstaendig.");
                offset += count;
            }
        }
        catch (Exception exception) { failure = exception; }
        stream.Dispose();
        if (failure != null) throw failure;
        return result!;
    }
}
