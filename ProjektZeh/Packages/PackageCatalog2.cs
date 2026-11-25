using System;
using System.Collections.Generic;
using System.Text;
using ProjektZeh.Shared;

namespace ProjektZeh.Packages;

/// <summary>Katalogeintrag des Formats 2; Dependencies und Provides sind so sortiert, wie sie im Katalog stehen.</summary>
internal sealed record CatalogEntry2(string Id, PackageVersion2 Version, string ManifestSha256, long ContentBytes,
    PackageDependency[] Dependencies, string[] Provides);

/// <summary>
/// Signierter Katalog des Formats 2 (Plan P2, noch nicht an pkg angeschlossen). Wie das Manifest ist er kanonisch: UTF-8, nur LF,
/// Eintraege strikt ordinal nach id, je Eintrag erst depends, dann provides. Die Signatur prueft der Aufrufer ueber genau die Bytes,
/// die er hier uebergibt (PackageTrust, unveraendert wie bei Version 1); Parse prueft nur die Form und nimmt nie etwas an, was
/// ein Katalog der Version 1 nicht auch erst nach der Signaturpruefung duerfte. Fehlermeldungen geben Eingabetext nur ueber
/// PackageManifest2.Quote wieder.
/// </summary>
internal sealed class PackageCatalog2
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const int MaximumEntries = 4096;
    private const string Header = "zehos-catalog 2";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly Dictionary<string, CatalogEntry2> _byId;

    public long Generation { get; }
    public CatalogEntry2[] Entries { get; }

    private PackageCatalog2(long generation, CatalogEntry2[] entries)
    {
        Generation = generation;
        Entries = entries;
        _byId = new Dictionary<string, CatalogEntry2>(entries.Length, StringComparer.Ordinal);
        foreach (var entry in entries) _byId[entry.Id] = entry;
    }

    public CatalogEntry2? Find(string id) => id != null && _byId.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>Prueft den Katalog vollstaendig; jede Abweichung ergibt InvalidArgument, nie eine Ausnahme.</summary>
    public static ResultObject<PackageCatalog2> Parse(byte[] bytes)
    {
        if (bytes == null) return Invalid("Keine Daten.");
        if (bytes.Length > MaximumBytes) return Invalid("Katalog ist groesser als 4 MiB.");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Invalid("Katalog ist kein gueltiges UTF-8."); }
        if (text.Contains('\r', StringComparison.Ordinal)) return Invalid("Katalog enthaelt CR; nur LF ist erlaubt.");

        int position = 0;
        if (!NextLine(text, ref position, out var header) || !header.SequenceEqual(Header)) return Invalid("Unbekanntes Katalogformat.");
        if (!NextLine(text, ref position, out var generationLine) || !generationLine.StartsWith("generation=", StringComparison.Ordinal) ||
            !PackageManifest2.TryParseSize(generationLine[11..], out long generation) || generation < 1) return Invalid("Ungueltige Generation.");

        var entries = new List<CatalogEntry2>();
        Pending? pending = null;
        for (int number = 3; ; number++)
        {
            if (!NextLine(text, ref position, out var line)) return Invalid("Katalogende fehlt.");
            if (line.SequenceEqual("end"))
            {
                if (position != text.Length) return Invalid("Text nach end.");
                if (pending != null) entries.Add(pending.ToEntry());
                break;
            }
            string? error;
            if (line.StartsWith("package=", StringComparison.Ordinal))
            {
                if (pending != null) entries.Add(pending.ToEntry());
                if (entries.Count == MaximumEntries) return Invalid("Mehr als 4096 Eintraege.");
                error = StartEntry(line[8..], entries.Count == 0 ? null : entries[^1].Id, out pending);
            }
            else if (pending == null) error = "Unbekannte Zeile";
            else if (line.StartsWith("depends=", StringComparison.Ordinal)) error = AddDependency(pending, line[8..]);
            else if (line.StartsWith("provides=", StringComparison.Ordinal)) error = AddProvides(pending, line[9..]);
            else error = "Unbekannte Zeile";
            if (error != null) return Invalid(error + " (Zeile " + number + ")");
        }
        return ResultObjectFactory.CreateSuccess(new PackageCatalog2(generation, [.. entries]));
    }

    // Sammelt die Zeilen eines Eintrags, bis der naechste beginnt.
    private sealed class Pending(string id, PackageVersion2 version, string sha, long bytes)
    {
        public readonly string Id = id;
        public readonly List<PackageDependency> Dependencies = [];
        public readonly List<string> Provides = [];
        public CatalogEntry2 ToEntry() => new(Id, version, sha, bytes, [.. Dependencies], [.. Provides]);
    }

    private static string? StartEntry(ReadOnlySpan<char> value, string? previousId, out Pending? pending)
    {
        pending = null;
        // Aufbau "<id> <version>-r<n> <sha256> <bytes>": genau drei Leerzeichen.
        int first = value.IndexOf(' ');
        int second = first < 0 ? -1 : value[(first + 1)..].IndexOf(' ');
        if (second >= 0) second += first + 1;
        int third = second < 0 ? -1 : value[(second + 1)..].IndexOf(' ');
        if (third >= 0) third += second + 1;
        if (third < 0 || value[(third + 1)..].IndexOf(' ') >= 0) return "Ungueltiger Eintrag: " + PackageManifest2.Quote(value);
        string id = value[..first].ToString();
        var hash = value[(second + 1)..third];
        if (!PackageManifest.IsValidId(id) || !PackageVersion2.TryParse(value[(first + 1)..second].ToString(), out var version) ||
            hash.Length != 64 || !PackageManifest2.IsLowerHex(hash) ||
            !PackageManifest2.TryParseSize(value[(third + 1)..], out long bytes) || bytes > PackageManifest2.MaximumContentBytes)
            return "Ungueltiger Eintrag: " + PackageManifest2.Quote(value);
        if (previousId != null)
        {
            int order = string.CompareOrdinal(previousId, id);
            if (order == 0) return "Doppelte Paketkennung: " + PackageManifest2.Quote(id);
            if (order > 0) return "Eintraege sind nicht nach Kennung sortiert: " + PackageManifest2.Quote(id);
        }
        pending = new Pending(id, version, hash.ToString(), bytes);
        return null;
    }

    private static string? AddDependency(Pending pending, ReadOnlySpan<char> value)
    {
        if (pending.Provides.Count > 0) return "depends steht nach provides";
        if (!PackageDependency.TryParse(value.ToString(), out var dependency)) return "Ungueltige Abhaengigkeit: " + PackageManifest2.Quote(value);
        if (dependency!.Id == pending.Id) return "Paket haengt von sich selbst ab";
        if (pending.Dependencies.Count == PackageManifest2.MaximumDependencies) return "Mehr als 64 Abhaengigkeiten";
        if (pending.Dependencies.Count > 0)
        {
            int order = string.CompareOrdinal(pending.Dependencies[^1].Id, dependency.Id);
            if (order == 0) return "Doppelte Abhaengigkeit: " + PackageManifest2.Quote(dependency.Id);
            if (order > 0) return "Abhaengigkeiten sind nicht nach Kennung sortiert";
        }
        pending.Dependencies.Add(dependency);
        return null;
    }

    private static string? AddProvides(Pending pending, ReadOnlySpan<char> value)
    {
        if (!value.StartsWith(PackageManifest2.SonamePrefix, StringComparison.Ordinal) ||
            !PackageManifest2.IsValidSoname(value[PackageManifest2.SonamePrefix.Length..]))
            return "Ungueltige provides-Zeile: " + PackageManifest2.Quote(value);
        if (pending.Provides.Count == PackageManifest2.MaximumProvides) return "Mehr als 64 provides-Zeilen";
        if (pending.Provides.Count > 0)
        {
            int order = pending.Provides[^1].AsSpan().SequenceCompareTo(value);
            if (order == 0) return "Doppelte provides-Zeile: " + PackageManifest2.Quote(value);
            if (order > 0) return "provides-Zeilen sind nicht sortiert";
        }
        pending.Provides.Add(value.ToString());
        return null;
    }

    // Liefert die naechste mit LF abgeschlossene Zeile; eine Zeile ohne LF gilt als abgeschnitten.
    private static bool NextLine(string text, ref int position, out ReadOnlySpan<char> line)
    {
        int end = text.IndexOf('\n', position);
        if (end < 0)
        {
            line = default;
            return false;
        }
        line = text.AsSpan(position, end - position);
        position = end + 1;
        return true;
    }

    private static ResultObject<PackageCatalog2> Invalid(string message) =>
        ResultObjectFactory.CreateFailure<PackageCatalog2>(ResultCode.InvalidArgument, "Ungueltiger Paketkatalog: " + message);
}
