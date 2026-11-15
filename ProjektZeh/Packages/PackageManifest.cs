using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ProjektZeh.Shared;
using ProjektZeh.Update;

namespace ProjektZeh.Packages;

internal sealed record PackageFile(string Path, long Size, string Sha256);

/// <summary>
/// Signiertes Paketmanifest im kanonischen Textformat (UTF-8, LF, feste Feldreihenfolge, Dateien nach Pfad sortiert).
/// Die Signatur deckt genau die gespeicherten Bytes ab; deshalb wird nie neu serialisiert, sondern das Original behalten.
/// </summary>
internal sealed class PackageManifest
{
    internal const string Runtime = "zehos-wasm32-v1";
    internal const int MaximumBytes = 16 * 1024;
    internal const int MaximumFiles = 128;
    internal const long MaximumContentBytes = 128L * 1024 * 1024;
    private const int MaximumNameLength = 32;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string Id { get; }
    public UpdateVersion Version { get; }
    public string Name { get; }
    public string Entry { get; }
    public PackageFile[] Files { get; }
    public byte[] Bytes { get; }

    private PackageManifest(string id, UpdateVersion version, string name, string entry, PackageFile[] files, byte[] bytes) =>
        (Id, Version, Name, Entry, Files, Bytes) = (id, version, name, entry, files, bytes);

    public static ResultObject<PackageManifest> Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) return Invalid("Manifest ist groesser als 16 KiB.");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Invalid("Manifest ist kein gueltiges UTF-8."); }
        string[] lines = text.Split('\n');
        if (lines.Length < 8 || lines[^1].Length != 0 || lines[0] != "zehos-package 1") return Invalid("Unbekanntes Manifestformat.");
        if (!Field(lines[1], "id", out string id) || !IsValidId(id)) return Invalid("Ungueltige Paketkennung.");
        if (!Field(lines[2], "version", out string versionText) || !UpdateVersion.TryParse(versionText, out var version))
            return Invalid("Ungueltige Paketversion.");
        if (!Field(lines[3], "runtime", out string runtime) || runtime != Runtime) return Invalid("Nicht unterstuetztes Laufzeitprofil.");
        if (!Field(lines[4], "name", out string name) || !IsValidName(name)) return Invalid("Ungueltiger Anzeigename.");
        if (!Field(lines[5], "entry", out string entry)) return Invalid("Einstiegspunkt fehlt.");
        int count = lines.Length - 8;
        if (count > MaximumFiles) return Invalid("Mehr als 128 Dateien.");
        var files = new PackageFile[count];
        // FAT32 unterscheidet keine Gross-/Kleinschreibung; zwei solche Namen wuerden dieselbe Datei treffen.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        for (int index = 0; index < count; index++)
        {
            if (!Field(lines[6 + index], "file", out string value)) return Invalid("Ungueltige Dateizeile.");
            string[] parts = value.Split(' ');
            if (parts.Length != 3 || parts[0].Length != 64 || !IsLowerHex(parts[0]) || !TryParseSize(parts[1], out long size) || !IsValidPath(parts[2]))
                return Invalid("Ungueltige Dateizeile.");
            if (!seen.Add(parts[2])) return Invalid("Doppelter Dateipfad: " + parts[2]);
            if (index > 0 && string.CompareOrdinal(files[index - 1].Path, parts[2]) >= 0) return Invalid("Dateien sind nicht sortiert.");
            if (size > MaximumContentBytes - total) return Invalid("Paketinhalt groesser als 128 MiB.");
            total += size;
            files[index] = new PackageFile(parts[2], size, parts[0]);
        }
        if (lines[^2] != "end") return Invalid("Manifestende fehlt.");
        if (!entry.EndsWith(".wasm", StringComparison.Ordinal) || Array.FindIndex(files, file => file.Path == entry) < 0)
            return Invalid("Einstiegspunkt ist keine gelistete Wasm-Datei.");
        return ResultObjectFactory.CreateSuccess(new PackageManifest(id, version, name, entry, files, bytes));
    }

    public static bool IsValidId(string id)
    {
        if (id.Length is < 1 or > 48 || !IsLowerAlphanumeric(id[0])) return false;
        foreach (char c in id) if (!IsLowerAlphanumeric(c) && c != '-') return false;
        return true;
    }

    /// <summary>Relativer Pfad aus ASCII-Komponenten; lehnt alles ab, was auf FAT32 mehrdeutig oder ausserhalb liegt.</summary>
    public static bool IsValidPath(string path)
    {
        if (path.Length is 0 or > 200) return false;
        foreach (string part in path.Split('/'))
        {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith('.')) return false;
            foreach (char c in part)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')) return false;
        }
        return true;
    }

    private static bool IsValidName(string name)
    {
        if (name.Length is < 1 or > MaximumNameLength || name[0] == ' ' || name[^1] == ' ') return false;
        foreach (char c in name) if (c is < ' ' or > '~') return false;
        return true;
    }

    private static bool Field(string line, string key, out string value)
    {
        value = "";
        if (line.Length <= key.Length || !line.StartsWith(key, StringComparison.Ordinal) || line[key.Length] != '=') return false;
        value = line[(key.Length + 1)..];
        return true;
    }

    internal static bool TryParseSize(string text, out long value)
    {
        value = 0;
        if (text.Length is 0 or > 10 || (text.Length > 1 && text[0] == '0')) return false;
        foreach (char c in text) if (c is < '0' or > '9') return false;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    internal static bool IsLowerHex(string text)
    {
        foreach (char c in text) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static bool IsLowerAlphanumeric(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static ResultObject<PackageManifest> Invalid(string message) =>
        ResultObjectFactory.CreateFailure<PackageManifest>(ResultCode.InvalidArgument, "Ungueltiges Paketmanifest: " + message);
}
