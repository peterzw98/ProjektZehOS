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
            !TryParseRelease(text.AsSpan(separator + 2), out int release)) return false;
        value = new PackageVersion2(version, release);
        return true;
    }

    /// <summary>1 bis 9999 ohne fuehrende Null, damit jede Revision genau eine Schreibweise hat.</summary>
    internal static bool TryParseRelease(ReadOnlySpan<char> text, out int release) =>
        PackageManifest2.TryParseBounded(text, 1, MaximumRelease, out release);

    public int CompareTo(PackageVersion2 other)
    {
        int result = Version.CompareTo(other.Version);
        return result == 0 ? Release.CompareTo(other.Release) : result;
    }

    public override string ToString() => Version + "-r" + Release.ToString(CultureInfo.InvariantCulture);
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

/// <summary>Symbolischer Link; Target ist relativ zum Ordner von Path (Regeln bei PackageManifest2.ReadLink).</summary>
internal sealed record PackageLink(string Path, string Target);

/// <summary>
/// Signiertes Manifest des Formats 2 (native Pakete, Laufzeit linux-x64-v1). Wie bei v1 ist das Format kanonisch: UTF-8, nur LF,
/// feste Feldreihenfolge, sortierte Listen, genau eine Schreibweise je Wert. Noch nicht an pkg, Kernel oder Server angeschlossen
/// (Plan P1). Bytes ist eine Kopie der Eingabe, aus der auch geparst wurde: Wer die Signatur ueber Bytes prueft, prueft genau den
/// Text, der den Feldern zugrunde liegt, auch wenn der Aufrufer seinen Puffer danach aendert.
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
    internal const int MaximumPathLength = 200;
    /// <summary>Hoechstens so viele Links folgen beim Aufloesen eines Linkziels aufeinander (wie Linux).</summary>
    internal const int MaximumLinkHops = 40;
    internal const int MaximumQuotedLength = 64;
    private const int MaximumLicenseLength = 64;
    private const int MaximumNameLength = 32;
    private const int MaximumSonameLength = 200;
    internal const string SonamePrefix = "so:";
    private const string Header = "zehos-package 2";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Rang jedes Schluessels in der festen Reihenfolge. Gleicher Rang nur dort, wo mehrere Zeilen erlaubt sind (exec/file).
    private static readonly string[] Keys =
        ["id", "version", "release", "runtime", "license", "name", "entry", "capability", "memory-mib", "min-kernel",
         "depends", "provides", "exec", "link"];
    private const int RankLicense = 4, RankCapability = 7, RankDepends = 10, RankProvides = 11, RankContent = 12, RankLink = 13;

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
        // Aufgeloestes Ziel je Link (gleicher Index wie Links), relativ zur Wurzel.
        public readonly List<string> Resolved = [];
        public long ContentBytes;
        public int LastCapability = -1;
    }

    /// <summary>Prueft das Manifest vollstaendig; jede Abweichung ergibt InvalidArgument, nie eine Ausnahme.</summary>
    public static ResultObject<PackageManifest2> Parse(byte[] bytes)
    {
        if (bytes == null) return Invalid("Keine Daten.");
        if (bytes.Length > MaximumBytes) return Invalid("Manifest ist groesser als 1 MiB.");
        byte[] copy = (byte[])bytes.Clone();
        string text;
        try { text = StrictUtf8.GetString(copy); }
        catch (DecoderFallbackException) { return Invalid("Manifest ist kein gueltiges UTF-8."); }
        if (text.Contains('\r', StringComparison.Ordinal)) return Invalid("Manifest enthaelt CR; nur LF ist erlaubt.");

        // Zeilen werden als Ausschnitte gelesen; Zeichenketten entstehen nur fuer gespeicherte Werte.
        int position = 0;
        if (!NextLine(text, ref position, out var header) || !header.SequenceEqual(Header)) return Invalid("Unbekanntes Manifestformat.");
        var fields = new Fields();
        int rank = -1;
        for (int number = 2; ; number++)
        {
            if (!NextLine(text, ref position, out var line)) return Invalid("Manifestende fehlt.");
            if (line.SequenceEqual("end"))
            {
                if (position != text.Length) return Invalid("Text nach end.");
                break;
            }
            int equals = line.IndexOf('=');
            int lineRank = equals < 0 ? -1 : RankOf(line[..equals]);
            if (lineRank < 0) return Invalid("Unbekannte Zeile " + number + ".");
            // Pflichtfelder lueckenlos am Anfang: das naechste erwartete Feld darf nicht uebersprungen werden.
            if (rank < RankLicense && lineRank != rank + 1) return Invalid("Feld " + Keys[rank + 1] + " fehlt in Zeile " + number + ".");
            bool repeatable = lineRank is RankCapability or RankDepends or RankProvides or RankContent or RankLink;
            if (lineRank < rank || (lineRank == rank && !repeatable))
                return Invalid("Feld " + line[..equals].ToString() + " steht an falscher Stelle oder doppelt (Zeile " + number + ").");
            rank = lineRank;
            string? error = ReadField(fields, lineRank, line[..equals], line[(equals + 1)..]);
            if (error != null) return Invalid(error + " (Zeile " + number + ")");
        }
        if (rank < RankLicense) return Invalid("Feld " + Keys[rank + 1] + " fehlt.");
        string? final = CheckWhole(fields);
        return final != null ? Invalid(final) : ResultObjectFactory.CreateSuccess(new PackageManifest2(fields, copy));
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

    private static int RankOf(ReadOnlySpan<char> key)
    {
        if (key.SequenceEqual("file")) return RankContent;
        for (int index = 0; index < Keys.Length; index++)
            if (key.SequenceEqual(Keys[index])) return index;
        return -1;
    }

    // Liefert null bei Erfolg, sonst die Fehlermeldung; die Reihenfolge der Felder ist schon geprueft.
    private static string? ReadField(Fields fields, int rank, ReadOnlySpan<char> key, ReadOnlySpan<char> value)
    {
        switch (rank)
        {
            case 0:
                fields.Id = value.ToString();
                return PackageManifest.IsValidId(fields.Id) ? null : "Ungueltige Paketkennung.";
            case 1:
                return UpdateVersion.TryParse(value.ToString(), out fields.Version) ? null : "Ungueltige Paketversion.";
            case 2:
                return PackageVersion2.TryParseRelease(value, out fields.Release) ? null : "Ungueltige Paketrevision (1 bis 9999).";
            case 3:
                return value.SequenceEqual(Runtime) ? null : "Nicht unterstuetztes Laufzeitprofil.";
            case RankLicense:
                if (!IsValidLicense(value)) return "Ungueltiger Lizenzausdruck.";
                fields.License = value.ToString();
                return null;
            case 5:
                if (!IsValidName(value)) return "Ungueltiger Anzeigename.";
                fields.Name = value.ToString();
                return null;
            case 6:
                if (!IsValidPath(value)) return "Ungueltiger Einstiegspunkt.";
                fields.Entry = value.ToString();
                return null;
            case RankCapability:
                return ReadCapability(fields, value);
            case 8:
                if (!TryParseBounded(value, 1, MaximumMemoryMiB, out int memory)) return "Ungueltige Speichergrenze (1 bis 4096 MiB).";
                fields.MemoryMiB = memory;
                return null;
            case 9:
                if (!UpdateVersion.TryParse(value.ToString(), out var kernel)) return "Ungueltige Mindest-Kernelversion.";
                fields.MinimumKernel = kernel;
                return null;
            case RankDepends:
                return ReadDependency(fields, value);
            case RankProvides:
                return ReadProvides(fields, value);
            case RankContent:
                return ReadContent(fields, value, key.SequenceEqual("exec"));
            default:
                return ReadLink(fields, value);
        }
    }

    private static string? ReadCapability(Fields fields, ReadOnlySpan<char> value)
    {
        int capability = value is "net" ? 0 : value is "storage" ? 1 : value is "files" ? 2 : -1;
        if (capability < 0) return "Unbekannte Faehigkeit: " + Quote(value);
        // Feste Reihenfolge net, storage, files; eine Wiederholung ist damit ebenfalls ausgeschlossen.
        if (capability <= fields.LastCapability) return "Faehigkeiten doppelt oder nicht in der Reihenfolge net, storage, files.";
        fields.LastCapability = capability;
        if (capability == 0) fields.AllowsNetwork = true;
        else if (capability == 1) fields.AllowsStorage = true;
        else fields.AllowsFiles = true;
        return null;
    }

    private static string? ReadDependency(Fields fields, ReadOnlySpan<char> value)
    {
        if (!PackageDependency.TryParse(value.ToString(), out var dependency)) return "Ungueltige Abhaengigkeit: " + Quote(value);
        if (dependency!.Id == fields.Id) return "Paket haengt von sich selbst ab.";
        if (fields.Dependencies.Count == MaximumDependencies) return "Mehr als 64 Abhaengigkeiten.";
        if (fields.Dependencies.Count > 0)
        {
            int order = string.CompareOrdinal(fields.Dependencies[^1].Id, dependency.Id);
            if (order == 0) return "Doppelte Abhaengigkeit: " + Quote(dependency.Id);
            if (order > 0) return "Abhaengigkeiten sind nicht nach Kennung sortiert.";
        }
        fields.Dependencies.Add(dependency);
        return null;
    }

    private static string? ReadProvides(Fields fields, ReadOnlySpan<char> value)
    {
        if (!value.StartsWith(SonamePrefix, StringComparison.Ordinal) || !IsValidSoname(value[SonamePrefix.Length..]))
            return "Ungueltige provides-Zeile: " + Quote(value);
        if (fields.Provides.Count == MaximumProvides) return "Mehr als 64 provides-Zeilen.";
        if (fields.Provides.Count > 0)
        {
            int order = fields.Provides[^1].AsSpan().SequenceCompareTo(value);
            if (order == 0) return "Doppelte provides-Zeile: " + Quote(value);
            if (order > 0) return "provides-Zeilen sind nicht sortiert.";
        }
        fields.Provides.Add(value.ToString());
        return null;
    }

    private static string? ReadContent(Fields fields, ReadOnlySpan<char> value, bool executable)
    {
        // Aufbau "<sha256> <bytes> <pfad>"; ein weiteres Leerzeichen landet im Pfad und scheitert dort.
        int first = value.IndexOf(' ');
        int second = first < 0 ? -1 : value[(first + 1)..].IndexOf(' ');
        if (second < 0) return "Ungueltige Dateizeile: " + Quote(value);
        second += first + 1;
        var hash = value[..first];
        var path = value[(second + 1)..];
        if (hash.Length != 64 || !IsLowerHex(hash) || !TryParseSize(value[(first + 1)..second], out long size) || !IsValidPath(path))
            return "Ungueltige Dateizeile: " + Quote(value);
        if (fields.Files.Count == MaximumFiles) return "Mehr als 4096 Dateien.";
        if (fields.Files.Count > 0 && fields.Files[^1].Path.AsSpan().SequenceCompareTo(path) >= 0)
            return "Dateien sind nicht nach Pfad sortiert oder doppelt: " + Quote(path);
        // Subtraktion statt Addition, damit die Summe nie ueberlaufen kann.
        if (size > MaximumContentBytes - fields.ContentBytes) return "Paketinhalt groesser als 1 GiB.";
        fields.ContentBytes += size;
        fields.Files.Add(new PackageFile2(path.ToString(), size, hash.ToString(), executable));
        return null;
    }

    /// <summary>
    /// Linkziel ist kanonisch relativ: null oder mehr fuehrende "../", danach mindestens eine normale Komponente. Es wird
    /// textuell gegen den Ordner des Links aufgeloest und muss selbst ein gueltiger Paketpfad sein (unter usr/, lib/ oder etc/,
    /// hoechstens 200 Zeichen), damit ein Link nie auf /dev, /data, /save oder andere Bereiche ausserhalb der Paketbaeume zeigt.
    /// Weil ".." nur vorne steht und kein Link Ordner eines anderen Eintrags sein kann (CheckWhole), ist die textuelle Aufloesung
    /// auch die tatsaechliche. Ketten und Kreise ueber mehrere Links prueft CheckLinkChains.
    /// </summary>
    private static string? ReadLink(Fields fields, ReadOnlySpan<char> value)
    {
        int space = value.IndexOf(' ');
        if (space < 0 || !IsValidPath(value[..space])) return "Ungueltige link-Zeile: " + Quote(value);
        var path = value[..space];
        var target = value[(space + 1)..];
        string? resolved = ResolveTarget(path, target);
        if (resolved == null) return "Linkziel ist nicht kanonisch oder liegt ausserhalb von usr/, lib/, etc/: " + Quote(value);
        if (fields.Links.Count == MaximumLinks) return "Mehr als 4096 Links.";
        if (fields.Links.Count > 0 && fields.Links[^1].Path.AsSpan().SequenceCompareTo(path) >= 0)
            return "Links sind nicht nach Pfad sortiert oder doppelt: " + Quote(path);
        fields.Links.Add(new PackageLink(path.ToString(), target.ToString()));
        fields.Resolved.Add(resolved);
        return null;
    }

    // Liefert das aufgeloeste Ziel relativ zur Wurzel oder null, wenn es nicht kanonisch ist oder kein gueltiger Paketpfad waere.
    private static string? ResolveTarget(ReadOnlySpan<char> linkPath, ReadOnlySpan<char> target)
    {
        if (target.Length is 0 or > MaximumPathLength) return null;
        int up = 0;
        while (target[(up * 3)..].StartsWith("../", StringComparison.Ordinal)) up++;
        var rest = target[(up * 3)..];
        if (!IsValidRelative(rest)) return null;
        // Ende des Ordners, in dem das Ziel beginnt: Ordner des Links ohne die letzten "up" Komponenten; -1 ist die Wurzel.
        int keep = linkPath.LastIndexOf('/');
        for (int step = 0; step < up; step++)
        {
            if (keep < 0) return null;
            keep = linkPath[..keep].LastIndexOf('/');
        }
        string resolved = keep < 0 ? rest.ToString() : string.Concat(linkPath[..keep], "/", rest);
        return IsValidPath(resolved) ? resolved : null;
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
        var table = new PathTable(fields.Files.Count + fields.Links.Count);
        foreach (var file in fields.Files)
            if (!table.Add(file.Path, -1)) return "Doppelter Pfad: " + Quote(file.Path);
        for (int index = 0; index < fields.Links.Count; index++)
            if (!table.Add(fields.Links[index].Path, index)) return "Doppelter Pfad: " + Quote(fields.Links[index].Path);
        for (int entry = 0; entry < table.Count; entry++)
        {
            int parent = table.FindParent(table.Path(entry));
            if (parent >= 0) return "Pfad ist zugleich Ordner eines anderen: " + Quote(table.Path(parent));
        }
        return CheckLinkChains(fields, table);
    }

    /// <summary>
    /// Folgt jedem Linkziel durch die Links dieses Manifests. Abgelehnt wird, wenn ein Schritt auf den Link selbst, einen seiner
    /// Ordner oder einen Pfad unter ihm zeigt (Kreis oder Ordnerschleife), mehr als 40 Links aufeinander folgen (damit auch jeder
    /// Kreis ueber mehrere Links) oder der Pfad laenger als 200 Zeichen wuerde. Arbeitet in zwei festen Puffern ohne Zeichenkette
    /// je Schritt, damit ein feindliches Manifest keine grosse Speicherspitze erzeugt.
    /// </summary>
    private static string? CheckLinkChains(Fields fields, PathTable table)
    {
        var current = new char[MaximumPathLength];
        var next = new char[MaximumPathLength];
        for (int index = 0; index < fields.Links.Count; index++)
        {
            string linkPath = fields.Links[index].Path;
            string start = fields.Resolved[index];
            start.CopyTo(current);
            int length = start.Length;
            for (int hops = 0; ; hops++)
            {
                if (IsSameOrNested(current.AsSpan(0, length), linkPath))
                    return "Link zeigt auf sich selbst, einen eigenen Ordner oder darunter: " + Quote(linkPath);
                int prefix = table.FindLinkPrefix(current.AsSpan(0, length), out int via);
                if (prefix < 0) break;
                if (hops == MaximumLinkHops) return "Mehr als 40 Links in Folge oder Linkkreis: " + Quote(linkPath);
                string through = fields.Resolved[via];
                int total = through.Length + length - prefix;
                if (total > MaximumPathLength) return "Linkkette ergibt einen Pfad ueber 200 Zeichen: " + Quote(linkPath);
                current.AsSpan(prefix, length - prefix).CopyTo(next.AsSpan(through.Length));
                through.CopyTo(next);
                (current, next) = (next, current);
                length = total;
            }
        }
        return null;
    }

    // Gleich, Ordner oder Nachfahre, ohne Gross-/Kleinschreibung (FAT32).
    private static bool IsSameOrNested(ReadOnlySpan<char> path, ReadOnlySpan<char> link)
    {
        int shorter = Math.Min(path.Length, link.Length);
        if (!AsciiEqualsIgnoreCase(path[..shorter], link[..shorter])) return false;
        if (path.Length == link.Length) return true;
        return path.Length < link.Length ? link[shorter] == '/' : path[shorter] == '/';
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (Fold(left[index]) != Fold(right[index])) return false;
        return true;
    }

    private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;

    /// <summary>
    /// Pfadmenge ohne Gross-/Kleinschreibung mit Suche ueber Ausschnitte. Ordnerpruefung und Linkaufloesung fragen jeden Praefix
    /// eines Pfads ab; mit einem Hash, der Zeichen fuer Zeichen mitlaeuft, entsteht dafuer keine Zeichenkette je Ordnerebene.
    /// </summary>
    private sealed class PathTable
    {
        private const ulong Offset = 14695981039346656037, Prime = 1099511628211;
        private readonly string[] paths;
        private readonly ulong[] hashes;
        private readonly int[] linkIndex;
        private readonly int[] buckets;

        public PathTable(int capacity)
        {
            paths = new string[capacity];
            hashes = new ulong[capacity];
            linkIndex = new int[capacity];
            int size = 16;
            while (size < capacity * 2) size *= 2;
            buckets = new int[size];
        }

        public int Count { get; private set; }

        public string Path(int entry) => paths[entry];

        /// <summary>Traegt den Pfad ein (link: Index in Links oder -1); false, wenn er ohne Gross-/Kleinschreibung schon da ist.</summary>
        public bool Add(string path, int link)
        {
            ulong hash = Offset;
            foreach (char c in path) hash = Step(hash, c);
            if (Find(path, hash) >= 0) return false;
            paths[Count] = path;
            hashes[Count] = hash;
            linkIndex[Count] = link;
            int bucket = (int)(hash & (ulong)(buckets.Length - 1));
            while (buckets[bucket] != 0) bucket = (bucket + 1) & (buckets.Length - 1);
            buckets[bucket] = ++Count;
            return true;
        }

        /// <summary>Ein eingetragener Pfad, der Ordner des gegebenen ist (Praefix vor einem '/'), sonst -1.</summary>
        public int FindParent(string path)
        {
            ulong hash = Offset;
            for (int index = 0; index < path.Length; index++)
            {
                if (path[index] == '/')
                {
                    int entry = Find(path.AsSpan(0, index), hash);
                    if (entry >= 0) return entry;
                }
                hash = Step(hash, path[index]);
            }
            return -1;
        }

        /// <summary>Laenge des Praefixes (ganzer Pfad oder vor einem '/'), der ein Link ist, sonst -1; via ist dessen Index.</summary>
        public int FindLinkPrefix(ReadOnlySpan<char> path, out int via)
        {
            ulong hash = Offset;
            for (int index = 0; index <= path.Length; index++)
            {
                if (index == path.Length || path[index] == '/')
                {
                    int entry = Find(path[..index], hash);
                    if (entry >= 0 && linkIndex[entry] >= 0)
                    {
                        via = linkIndex[entry];
                        return index;
                    }
                }
                if (index < path.Length) hash = Step(hash, path[index]);
            }
            via = -1;
            return -1;
        }

        private int Find(ReadOnlySpan<char> path, ulong hash)
        {
            int mask = buckets.Length - 1;
            for (int bucket = (int)(hash & (ulong)mask); buckets[bucket] != 0; bucket = (bucket + 1) & mask)
            {
                int entry = buckets[bucket] - 1;
                if (hashes[entry] == hash && AsciiEqualsIgnoreCase(paths[entry], path)) return entry;
            }
            return -1;
        }

        private static ulong Step(ulong hash, char c) => unchecked((hash ^ Fold(c)) * Prime);
    }

    /// <summary>
    /// Pfad wie v1 (Komponenten aus A–Z a–z 0–9 . _ - und zusaetzlich +, hoechstens 200 Zeichen), der unter usr/, lib/
    /// oder etc/ liegt.
    /// </summary>
    internal static bool IsValidPath(ReadOnlySpan<char> path) =>
        path.Length <= MaximumPathLength &&
        (path.StartsWith("usr/", StringComparison.Ordinal) || path.StartsWith("lib/", StringComparison.Ordinal) ||
         path.StartsWith("etc/", StringComparison.Ordinal)) && IsValidRelative(path);

    // Eine oder mehr Komponenten, getrennt durch einzelne '/'; ohne Zeichenkette je Komponente.
    private static bool IsValidRelative(ReadOnlySpan<char> path)
    {
        if (path.Length == 0) return false;
        int start = 0;
        for (int index = 0; index <= path.Length; index++)
        {
            if (index < path.Length && path[index] != '/') continue;
            if (!IsValidComponent(path[start..index])) return false;
            start = index + 1;
        }
        return true;
    }

    private static bool IsValidComponent(ReadOnlySpan<char> part)
    {
        if (part.Length == 0 || part[^1] == '.') return false;   // schliesst auch "." und ".." aus
        foreach (char c in part)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or '+')) return false;
        return true;
    }

    /// <summary>SPDX-Ausdruck, 1 bis 64 Zeichen aus A–Z a–z 0–9 . + - ( ) und Leerzeichen; die Grammatik prueft erst das Werkzeug.</summary>
    private static bool IsValidLicense(ReadOnlySpan<char> license)
    {
        if (license.Length is < 1 or > MaximumLicenseLength) return false;
        foreach (char c in license)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '+' or '-' or '(' or ')' or ' ')) return false;
        return true;
    }

    internal static bool IsValidSoname(ReadOnlySpan<char> soname)
    {
        if (soname.Length is < 1 or > MaximumSonameLength) return false;
        foreach (char c in soname)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '+' or '-')) return false;
        return true;
    }

    // Wie v1: 1 bis 32 druckbare ASCII-Zeichen ohne Leerzeichen am Rand.
    private static bool IsValidName(ReadOnlySpan<char> name)
    {
        if (name.Length is < 1 or > MaximumNameLength || name[0] == ' ' || name[^1] == ' ') return false;
        foreach (char c in name) if (c is < ' ' or > '~') return false;
        return true;
    }

    internal static bool IsLowerHex(ReadOnlySpan<char> text)
    {
        foreach (char c in text) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    // Wie v1: hoechstens zehn Ziffern ohne fuehrende Null, passt immer in long; die Summengrenze prueft der Aufrufer.
    internal static bool TryParseSize(ReadOnlySpan<char> text, out long value)
    {
        value = 0;
        if (text.Length is 0 or > 10 || (text.Length > 1 && text[0] == '0')) return false;
        foreach (char c in text)
        {
            if (c is < '0' or > '9') return false;
            value = value * 10 + (c - '0');
        }
        return true;
    }

    /// <summary>Dezimalzahl ohne Vorzeichen und fuehrende Null im Bereich [minimum, maximum]; hoechstens neun Ziffern, kein Ueberlauf.</summary>
    internal static bool TryParseBounded(ReadOnlySpan<char> text, int minimum, int maximum, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 9 || (text.Length > 1 && text[0] == '0')) return false;
        foreach (char c in text)
        {
            if (c is < '0' or > '9') return false;
            value = value * 10 + (c - '0');
        }
        return value >= minimum && value <= maximum;
    }

    /// <summary>
    /// Ungepruefter Text fuer Fehlermeldungen: nur druckbares ASCII (Rest als '?'), hoechstens 64 Zeichen. Meldungen landen in
    /// Terminal und Protokoll; ein Spiegel darf darueber weder Steuersequenzen einschleusen noch riesige Zeichenketten erzeugen.
    /// </summary>
    internal static string Quote(ReadOnlySpan<char> text)
    {
        int length = Math.Min(text.Length, MaximumQuotedLength);
        var builder = new StringBuilder(length + 5);
        builder.Append('"');
        for (int index = 0; index < length; index++) builder.Append(text[index] is >= ' ' and <= '~' ? text[index] : '?');
        builder.Append('"');
        if (text.Length > length) builder.Append("...");
        return builder.ToString();
    }

    private static ResultObject<PackageManifest2> Invalid(string message) =>
        ResultObjectFactory.CreateFailure<PackageManifest2>(ResultCode.InvalidArgument, "Ungueltiges Paketmanifest: " + message);
}
