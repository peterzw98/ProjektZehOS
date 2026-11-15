using System;
using System.Collections.Generic;
using System.Text;
using ProjektZeh.Shared;
using ProjektZeh.Update;

namespace ProjektZeh.Packages;

internal sealed record CatalogEntry(string Id, UpdateVersion Version, string ManifestSha256);

/// <summary>
/// Signierter Repositorykatalog: bindet je Paket genau eine Version an den SHA-256 ihres Manifests. Die Generation steigt
/// mit jeder Veroeffentlichung und verhindert, dass ein alter Katalog wieder angenommen wird.
/// </summary>
internal sealed class PackageCatalog
{
    internal const int MaximumBytes = 256 * 1024;
    internal const int MaximumEntries = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public long Generation { get; }
    public CatalogEntry[] Entries { get; }

    private PackageCatalog(long generation, CatalogEntry[] entries) => (Generation, Entries) = (generation, entries);

    public CatalogEntry? Find(string id) => Array.Find(Entries, entry => entry.Id == id);

    public static ResultObject<PackageCatalog> Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) return Invalid("Katalog ist groesser als 256 KiB.");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Invalid("Katalog ist kein gueltiges UTF-8."); }
        string[] lines = text.Split('\n');
        if (lines.Length < 4 || lines[^1].Length != 0 || lines[^2] != "end" || lines[0] != "zehos-catalog 1")
            return Invalid("Unbekanntes Katalogformat.");
        if (!lines[1].StartsWith("generation=", StringComparison.Ordinal) ||
            !PackageManifest.TryParseSize(lines[1][11..], out long generation) || generation < 1) return Invalid("Ungueltige Generation.");
        int count = lines.Length - 4;
        if (count > MaximumEntries) return Invalid("Mehr als 128 Eintraege.");
        var entries = new CatalogEntry[count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            string line = lines[2 + index];
            string[] parts = line.StartsWith("package=", StringComparison.Ordinal) ? line[8..].Split(' ') : [];
            if (parts.Length != 3 || !PackageManifest.IsValidId(parts[0]) || !UpdateVersion.TryParse(parts[1], out var version) ||
                parts[2].Length != 64 || !PackageManifest.IsLowerHex(parts[2])) return Invalid("Ungueltiger Eintrag.");
            if (!seen.Add(parts[0])) return Invalid("Doppelte Paketkennung: " + parts[0]);
            entries[index] = new CatalogEntry(parts[0], version, parts[2]);
        }
        return ResultObjectFactory.CreateSuccess(new PackageCatalog(generation, entries));
    }

    private static ResultObject<PackageCatalog> Invalid(string message) =>
        ResultObjectFactory.CreateFailure<PackageCatalog>(ResultCode.InvalidArgument, "Ungueltiger Paketkatalog: " + message);
}
