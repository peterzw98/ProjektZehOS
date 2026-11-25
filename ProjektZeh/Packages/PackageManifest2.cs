using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ProjektZeh.Shared;
using ProjektZeh.Update;

namespace ProjektZeh.Packages;

/// <summary>Paketversion aus drei Zahlen und Paketrevision, kanonisch "1.2.3-r4".</summary>
internal readonly record struct PackageVersion2(UpdateVersion Version, int Release) : IComparable<PackageVersion2>
{
    internal const int MaximumRelease = 9999;

    public static bool TryParse(string text, out PackageVersion2 value)
    {
        value = default;
        if (text == null) return false;
        int separator = text.LastIndexOf("-r", StringComparison.Ordinal);
        if (separator < 0 || !UpdateVersion.TryParse(text[..separator], out var version) ||
            !TryParseRelease(text[(separator + 2)..], out int release)) return false;
        value = new PackageVersion2(version, release);
        return true;
    }

    /// <summary>1 bis 9999 ohne fuehrende Null, damit jede Revision genau eine Schreibweise hat.</summary>
    internal static bool TryParseRelease(string text, out int release) =>
        PackageManifest2.TryParseBounded(text, 1, MaximumRelease, out release);

    public int CompareTo(PackageVersion2 other)
    {
        int result = Version.CompareTo(other.Version);
        return result == 0 ? Release.CompareTo(other.Release) : result;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Version}-r{Release}");
}

/// <summary>Abhaengigkeit "id>=version-rN": verlangt mindestens diese Version des Pakets.</summary>
internal sealed record PackageDependency(string Id, PackageVersion2 Minimum)
{
    public static bool TryParse(string text, out PackageDependency? value)
    {
        value = null;
        if (text == null) return false;
        int separator = text.IndexOf(">=", StringComparison.Ordinal);
        if (separator < 0) return false;
        string id = text[..separator];
        if (!PackageManifest.IsValidId(id) || !PackageVersion2.TryParse(text[(separator + 2)..], out var minimum)) return false;
        value = new PackageDependency(id, minimum);
        return true;
    }

    public override string ToString() => Id + ">=" + Minimum;
}

internal sealed record PackageFile2(string Path, long Size, string Sha256, bool Executable);

/// <summary>Symbolischer Link; Target ist relativ zum Ordner von Path und bleibt nach dem Aufloesen unter der Wurzel.</summary>
internal sealed record PackageLink(string Path, string Target);

/// <summary>
/// Signiertes Manifest des Formats 2 (native Pakete, Laufzeit linux-x64-v1). Wie bei v1 ist das Format kanonisch: UTF-8, nur LF,
/// feste Feldreihenfolge, sortierte Listen, genau eine Schreibweise je Wert. Die Signatur deckt die gespeicherten Bytes ab;
/// deshalb wird nie neu serialisiert. Noch nicht an pkg, Kernel oder Server angeschlossen (Plan P1).
/// </summary>
internal sealed class PackageManifest2
{
    internal const string Runtime = "linux-x64-v1";
    internal const int MaximumBytes = 1024 * 1024;
    internal const int MaximumFiles = 4096;
    internal const int MaximumLinks = 4096;
    internal const long MaximumContentBytes = 1024L * 1024 * 1024;
    internal const int MaximumDependencies = 64;
    internal const int MaximumProvides = 64;
    internal const int MaximumMemoryMiB = 4096;
    private const int MaximumPathLength = 200;
    private const int MaximumLicenseLength = 64;
    private const int MaximumNameLength = 32;
    private const int MaximumSonameLength = 200;
    private const string SonamePrefix = "so:";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Rang jedes Schluessels in der festen Reihenfolge. Gleicher Rang nur dort, wo mehrere Zeilen erlaubt sind (exec/file).
    private static readonly string[] Keys =
        ["id", "version", "release", "runtime", "license", "name", "entry", "capability", "memory-mib", "min-kernel",
         "depends", "provides", "exec", "link"];
    private const int RankCapability = 7, RankDepends = 10, RankProvides = 11, RankContent = 12, RankLink = 13;

    public string Id { get; }
    public PackageVersion2 Version { get; }
    public string License { get; }
    public string? Name { get; }
    public string? Entry { get; }
    public int? MemoryMiB { get; }
    public bool AllowsNetwork { get; }
    public bool AllowsStorage { get; }
    public bool AllowsFiles { get; }
    public UpdateVersion? MinimumKernel { get; }
    public PackageDependency[] Dependencies { get; }
    /// <summary>Bereitgestellte Namen samt Praefix, etwa "so:libc.so".</summary>
    public string[] Provides { get; }
    public PackageFile2[] Files { get; }
    public PackageLink[] Links { get; }
    public byte[] Bytes { get; }

    private PackageManifest2(Fields fields, byte[] bytes)
    {
        Id = fields.Id;
        Version = new PackageVersion2(fields.Version, fields.Release);
        License = fields.License;
        Name = fields.Name;
        Entry = fields.Entry;
        MemoryMiB = fields.MemoryMiB;
        AllowsNetwork = fields.AllowsNetwork;
        AllowsStorage = fields.AllowsStorage;
        AllowsFiles = fields.AllowsFiles;
        MinimumKernel = fields.MinimumKernel;
        Dependencies = [.. fields.Dependencies];
        Provides = [.. fields.Provides];
        Files = [.. fields.Files];
        Links = [.. fields.Links];
        Bytes = bytes;
    }

    // Sammelt die Werte waehrend des Lesens; erst das vollstaendig gepruefte Ergebnis wird zum Manifest.
    private sealed class Fields
    {
        public string Id = "", License = "";
        public UpdateVersion Version;
        public int Release;
        public string? Name, Entry;
        public int? MemoryMiB;
        public bool AllowsNetwork, AllowsStorage, AllowsFiles;
        public UpdateVersion? MinimumKernel;
        public readonly List<PackageDependency> Dependencies = [];
        public readonly List<string> Provides = [];
        public readonly List<PackageFile2> Files = [];
        public readonly List<PackageLink> Links = [];
        public long ContentBytes;
        public int LastCapability = -1;
    }

    /// <summary>Prueft das Manifest vollstaendig; jede Abweichung ergibt InvalidArgument, nie eine Ausnahme.</summary>
    public static ResultObject<PackageManifest2> Parse(byte[] bytes)
    {
        if (bytes == null) return Invalid("Keine Daten.");
        if (bytes.Length > MaximumBytes) return Invalid("Manifest ist groesser als 1 MiB.");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Invalid("Manifest ist kein gueltiges UTF-8."); }
        if (text.Contains('\r', StringComparison.Ordinal)) return Invalid("Manifest enthaelt CR; nur LF ist erlaubt.");
        string[] lines = text.Split('\n');
        if (lines[0] != "zehos-package 2") return Invalid("Unbekanntes Manifestformat.");
        if (lines.Length < 3 || lines[^1].Length != 0 || lines[^2] != "end") return Invalid("Manifestende fehlt.");

        var fields = new Fields();
        int rank = -1;
        for (int index = 1; index < lines.Length - 2; index++)
        {
            string line = lines[index];
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            int lineRank = equals < 0 ? -1 : RankOf(line[..equals]);
            if (lineRank < 0) return Invalid("Unbekannte Zeile " + (index + 1) + ".");
            // Pflichtfelder lueckenlos am Anfang: das naechste erwartete Feld darf nicht uebersprungen werden.
            if (rank < 4 && lineRank != rank + 1) return Invalid("Feld " + Keys[rank + 1] + " fehlt in Zeile " + (index + 1) + ".");
            bool repeatable = lineRank is RankCapability or RankDepends or RankProvides or RankContent or RankLink;
            if (lineRank < rank || (lineRank == rank && !repeatable))
                return Invalid("Feld " + line[..equals] + " steht an falscher Stelle oder doppelt (Zeile " + (index + 1) + ").");
            rank = lineRank;
            string? error = ReadField(fields, line[..equals], line[(equals + 1)..]);
            if (error != null) return Invalid(error);
        }
        if (rank < 4) return Invalid("Feld " + Keys[rank + 1] + " fehlt.");
        string? final = CheckWhole(fields);
        return final != null ? Invalid(final) : ResultObjectFactory.CreateSuccess(new PackageManifest2(fields, bytes));
    }

    private static int RankOf(string key) => key == "file" ? RankContent : Array.IndexOf(Keys, key);

    // Liefert null bei Erfolg, sonst die Fehlermeldung; die Reihenfolge der Felder ist schon geprueft.
    private static string? ReadField(Fields fields, string key, string value)
    {
        switch (key)
        {
            case "id":
                if (!PackageManifest.IsValidId(value)) return "Ungueltige Paketkennung.";
                fields.Id = value;
                return null;
            case "version":
                if (!UpdateVersion.TryParse(value, out fields.Version)) return "Ungueltige Paketversion.";
                return null;
            case "release":
                if (!PackageVersion2.TryParseRelease(value, out fields.Release)) return "Ungueltige Paketrevision (1 bis 9999).";
                return null;
            case "runtime":
                return value == Runtime ? null : "Nicht unterstuetztes Laufzeitprofil.";
            case "license":
                if (!IsValidLicense(value)) return "Ungueltiger Lizenzausdruck.";
                fields.License = value;
                return null;
            case "name":
                if (!IsValidName(value)) return "Ungueltiger Anzeigename.";
                fields.Name = value;
                return null;
            case "entry":
                if (!IsValidPath(value)) return "Ungueltiger Einstiegspunkt.";
                fields.Entry = value;
                return null;
            case "capability":
                return ReadCapability(fields, value);
            case "memory-mib":
                if (!TryParseBounded(value, 1, MaximumMemoryMiB, out int memory)) return "Ungueltige Speichergrenze (1 bis 4096 MiB).";
                fields.MemoryMiB = memory;
                return null;
            case "min-kernel":
                if (!UpdateVersion.TryParse(value, out var kernel)) return "Ungueltige Mindest-Kernelversion.";
                fields.MinimumKernel = kernel;
                return null;
            case "depends":
                return ReadDependency(fields, value);
            case "provides":
                return ReadProvides(fields, value);
            case "exec":
            case "file":
                return ReadContent(fields, value, key == "exec");
            default:
                return ReadLink(fields, value);
        }
    }

    private static string? ReadCapability(Fields fields, string value)
    {
        int capability = value switch { "net" => 0, "storage" => 1, "files" => 2, _ => -1 };
        if (capability < 0) return "Unbekannte Faehigkeit: " + value;
        // Feste Reihenfolge net, storage, files; eine Wiederholung ist damit ebenfalls ausgeschlossen.
        if (capability <= fields.LastCapability) return "Faehigkeiten doppelt oder nicht in der Reihenfolge net, storage, files.";
        fields.LastCapability = capability;
        if (capability == 0) fields.AllowsNetwork = true;
        else if (capability == 1) fields.AllowsStorage = true;
        else fields.AllowsFiles = true;
        return null;
    }

    private static string? ReadDependency(Fields fields, string value)
    {
        if (!PackageDependency.TryParse(value, out var dependency)) return "Ungueltige Abhaengigkeit: " + value;
        if (dependency!.Id == fields.Id) return "Paket haengt von sich selbst ab.";
        if (fields.Dependencies.Count == MaximumDependencies) return "Mehr als 64 Abhaengigkeiten.";
        if (fields.Dependencies.Count > 0)
        {
            int order = string.CompareOrdinal(fields.Dependencies[^1].Id, dependency.Id);
            if (order == 0) return "Doppelte Abhaengigkeit: " + dependency.Id;
            if (order > 0) return "Abhaengigkeiten sind nicht nach Kennung sortiert.";
        }
        fields.Dependencies.Add(dependency);
        return null;
    }

    private static string? ReadProvides(Fields fields, string value)
    {
        if (!value.StartsWith(SonamePrefix, StringComparison.Ordinal) || !IsValidSoname(value[SonamePrefix.Length..]))
            return "Ungueltige provides-Zeile: " + value;
        if (fields.Provides.Count == MaximumProvides) return "Mehr als 64 provides-Zeilen.";
        if (fields.Provides.Count > 0)
        {
            int order = string.CompareOrdinal(fields.Provides[^1], value);
            if (order == 0) return "Doppelte provides-Zeile: " + value;
            if (order > 0) return "provides-Zeilen sind nicht sortiert.";
        }
        fields.Provides.Add(value);
        return null;
    }

    private static string? ReadContent(Fields fields, string value, bool executable)
    {
        string[] parts = value.Split(' ');
        if (parts.Length != 3 || parts[0].Length != 64 || !PackageManifest.IsLowerHex(parts[0]) ||
            !PackageManifest.TryParseSize(parts[1], out long size) || !IsValidPath(parts[2]))
            return "Ungueltige Dateizeile: " + value;
        if (fields.Files.Count == MaximumFiles) return "Mehr als 4096 Dateien.";
        if (fields.Files.Count > 0 && string.CompareOrdinal(fields.Files[^1].Path, parts[2]) >= 0)
            return "Dateien sind nicht nach Pfad sortiert oder doppelt: " + parts[2];
        // Subtraktion statt Addition, damit die Summe nie ueberlaufen kann.
        if (size > MaximumContentBytes - fields.ContentBytes) return "Paketinhalt groesser als 1 GiB.";
        fields.ContentBytes += size;
        fields.Files.Add(new PackageFile2(parts[2], size, parts[0], executable));
        return null;
    }

    private static string? ReadLink(Fields fields, string value)
    {
        string[] parts = value.Split(' ');
        if (parts.Length != 2 || !IsValidPath(parts[0])) return "Ungueltige link-Zeile: " + value;
        if (!IsContainedTarget(parts[0], parts[1])) return "Linkziel verlaesst die Wurzel oder ist nicht kanonisch: " + value;
        if (fields.Links.Count == MaximumLinks) return "Mehr als 4096 Links.";
        if (fields.Links.Count > 0 && string.CompareOrdinal(fields.Links[^1].Path, parts[0]) >= 0)
            return "Links sind nicht nach Pfad sortiert oder doppelt: " + parts[0];
        fields.Links.Add(new PackageLink(parts[0], parts[1]));
        return null;
    }

    // Pruefungen, die erst mit dem ganzen Manifest moeglich sind.
    private static string? CheckWhole(Fields fields)
    {
        if (fields.Files.Count == 0) return "Paket enthaelt keine Datei.";
        if (fields.Name != null && fields.Entry == null) return "name ist nur zusammen mit entry erlaubt.";
        if (fields.MemoryMiB != null && fields.Entry == null) return "memory-mib ist nur zusammen mit entry erlaubt.";
        if (fields.Entry != null && !fields.Files.Exists(file => file.Executable && file.Path == fields.Entry))
            return "Einstiegspunkt ist keine gelistete exec-Datei.";
        // FAT32 unterscheidet keine Gross-/Kleinschreibung; zwei solche Namen wuerden denselben Eintrag treffen. Ein Pfad,
        // der zugleich Ordner eines anderen ist, liesse sich nicht anlegen und koennte einen Link als Zwischenordner nutzen.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in fields.Files)
            if (!paths.Add(file.Path)) return "Doppelter Pfad: " + file.Path;
        foreach (var link in fields.Links)
            if (!paths.Add(link.Path)) return "Doppelter Pfad: " + link.Path;
        foreach (string path in paths)
            for (int slash = path.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (paths.Contains(path[..slash])) return "Pfad ist zugleich Ordner eines anderen: " + path[..slash];
        return null;
    }

    /// <summary>
    /// Pfad wie v1 (Komponenten aus A–Z a–z 0–9 . _ - und zusaetzlich +, hoechstens 200 Zeichen), der unter usr/, lib/
    /// oder etc/ liegt.
    /// </summary>
    internal static bool IsValidPath(string path)
    {
        if (path.Length > MaximumPathLength ||
            !(path.StartsWith("usr/", StringComparison.Ordinal) || path.StartsWith("lib/", StringComparison.Ordinal) ||
              path.StartsWith("etc/", StringComparison.Ordinal))) return false;
        foreach (string part in path.Split('/'))
            if (!IsValidComponent(part)) return false;
        return true;
    }

    /// <summary>
    /// Kanonisch relatives Ziel: null oder mehr fuehrende "..", danach mindestens eine normale Komponente. Weil ".." nur am
    /// Anfang steht und kein Linkordner selbst ein Link sein kann (Ordnerpruefung), ist die textuelle Aufloesung auch die
    /// tatsaechliche; ein Ziel, das spaeter durch einen anderen Link fuehrt, steigt danach nur noch ab.
    /// </summary>
    private static bool IsContainedTarget(string linkPath, string target)
    {
        if (target.Length is 0 or > MaximumPathLength) return false;
        string[] parts = target.Split('/');
        int up = 0;
        while (up < parts.Length && parts[up] == "..") up++;
        if (up == parts.Length) return false;
        for (int index = up; index < parts.Length; index++)
            if (!IsValidComponent(parts[index])) return false;
        string[] linkParts = linkPath.Split('/');
        int depth = linkParts.Length - 1;
        if (up > depth) return false;
        // Ein Link auf sich selbst wuerde beim Aufloesen endlos kreisen.
        string resolved = string.Join('/', linkParts[..(depth - up)]);
        resolved = resolved.Length == 0 ? string.Join('/', parts[up..]) : resolved + "/" + string.Join('/', parts[up..]);
        return !string.Equals(resolved, linkPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidComponent(string part)
    {
        if (part.Length == 0 || part == "." || part == ".." || part.EndsWith('.')) return false;
        foreach (char c in part)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or '+')) return false;
        return true;
    }

    /// <summary>SPDX-Ausdruck, 1 bis 64 Zeichen aus A–Z a–z 0–9 . + - ( ) und Leerzeichen; die Grammatik prueft erst das Werkzeug.</summary>
    private static bool IsValidLicense(string license)
    {
        if (license.Length is < 1 or > MaximumLicenseLength) return false;
        foreach (char c in license)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '+' or '-' or '(' or ')' or ' ')) return false;
        return true;
    }

    private static bool IsValidSoname(string soname)
    {
        if (soname.Length is < 1 or > MaximumSonameLength) return false;
        foreach (char c in soname)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '+' or '-')) return false;
        return true;
    }

    // Wie v1: 1 bis 32 druckbare ASCII-Zeichen ohne Leerzeichen am Rand.
    private static bool IsValidName(string name)
    {
        if (name.Length is < 1 or > MaximumNameLength || name[0] == ' ' || name[^1] == ' ') return false;
        foreach (char c in name) if (c is < ' ' or > '~') return false;
        return true;
    }

    /// <summary>Dezimalzahl ohne Vorzeichen und fuehrende Null im Bereich [minimum, maximum]; kein Ueberlauf moeglich.</summary>
    internal static bool TryParseBounded(string text, int minimum, int maximum, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 9 || (text.Length > 1 && text[0] == '0')) return false;
        foreach (char c in text) if (c is < '0' or > '9') return false;
        value = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return value >= minimum && value <= maximum;
    }

    private static ResultObject<PackageManifest2> Invalid(string message) =>
        ResultObjectFactory.CreateFailure<PackageManifest2>(ResultCode.InvalidArgument, "Ungueltiges Paketmanifest: " + message);
}
