using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ProjektZeh.Shared;

namespace ProjektZeh.Packages;

internal enum PackageIndexKind
{
    File,
    Directory,
}

/// <summary>
/// Ergebnis einer Pfadaufloesung im Paketindex v2. Bei einer Datei nennt der Eintrag Paket, Version, die Manifestzeile und den
/// Ablageort unter <c>v2/&lt;id&gt;/&lt;version&gt;/files/</c>; bei einem Ordner (Elternordner von Paketpfaden) nur den Pfad.
/// <see cref="Path"/> ist der aufgeloeste Pfad in der Schreibweise des Manifests, nach allen Links.
/// </summary>
internal sealed record PackageIndexEntry(PackageIndexKind Kind, string Path, string? PackageId, PackageVersion2 Version, PackageFile2? File,
    string? Location)
{
    public bool Executable => File?.Executable == true;
}

/// <summary>
/// Unveraenderlicher Pfadindex einer aktiven Menge v2 (Plan N3-B, Schritt B1): <c>Pfad → Paketdatei</c> statt eines
/// Vereinigungsdateisystems. Gebaut wird er nur aus <see cref="PackageStore2.ReadActive"/>, also aus Manifesten mit gepruefter
/// Signatur und gepruefter Mengenregel (keine Pfadkonflikte ohne Gross-/Kleinschreibung, kein Pfad unter einem fremden Pfad,
/// Linkketten ohne Kreis). Links sind nur Eintraege dieses Index, nie Dateien auf FAT32.
/// </summary>
/// <remarks>
/// Aufloesung ohne Gross-/Kleinschreibung (ASCII), wie die Mengenpruefung: FAT32 kann zwei Schreibweisen ohnehin nicht
/// trennen. Angefragt werden nur kanonische Pfade (optional ein fuehrendes "/", keine leeren, "." oder ".."-Komponenten):
/// Ein Pfad mit ".." koennte ueber einen Link aus dem Paketbaum herausfuehren, deshalb normalisiert der Aufrufer (Prozess
/// mit eigener Wurzel, N3-B2) vorher, und hier wird er abgewiesen statt textuell gekuerzt. Der Inhalt einer Datei ist mit der
/// Aufloesung noch nicht geprueft; <see cref="Verify"/> hasht sie gegen das Manifest, bevor ein Start ihr vertraut.
/// Ein Index ist nach dem Bau nur lesbar und darf ohne Sperre von mehreren Threads benutzt werden.
/// </remarks>
internal sealed class PackageIndex2
{
    private sealed record Node(PackageIndexKind Kind, string Path, ActivePackage2? Package, PackageFile2? File, string? Target);

    private readonly Dictionary<string, Node> nodes;

    /// <summary>Die Menge, aus der der Index entstand (Reihenfolge wie active2).</summary>
    public ActivePackage2[] Packages { get; }

    /// <summary>Laufende Nummer des Stands in diesem Kernel; ein neuer Stand nach Activate hat eine groessere.</summary>
    public long Generation { get; }

    private PackageIndex2(ActivePackage2[] packages, Dictionary<string, Node> nodes, long generation)
    {
        Packages = packages;
        this.nodes = nodes;
        Generation = generation;
    }

    /// <summary>
    /// Baut den Index. Erwartet eine von <see cref="PackageStore2.ReadActive"/> gepruefte Menge; doppelte Pfade (ohne
    /// Gross-/Kleinschreibung) oder ein Pfad, der zugleich Ordner ist, sind trotzdem ein Fehler statt eines stillen Vorrangs.
    /// </summary>
    public static ResultObject<PackageIndex2> Build(ActivePackage2[] packages, long generation)
    {
        if (packages == null) return ResultObjectFactory.CreateFailure<PackageIndex2>(ResultCode.MissingArgument, "Keine Paketmenge.");
        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            foreach (var file in package.Manifest.Files)
            {
                if (!nodes.TryAdd(file.Path, new Node(PackageIndexKind.File, file.Path, package, file, null)))
                    return Conflict(file.Path);
            }
            foreach (var link in package.Manifest.Links)
                if (!nodes.TryAdd(link.Path, new Node(PackageIndexKind.File, link.Path, package, null, ResolveLink(link))))
                    return Conflict(link.Path);
        }
        // Elternordner aller Eintraege; ein Ordner darf kein Datei- oder Linkpfad sein (sonst waere die Aufloesung mehrdeutig).
        var directories = new List<string>();
        foreach (var node in nodes.Values)
            for (int slash = node.Path.IndexOf('/'); slash > 0; slash = node.Path.IndexOf('/', slash + 1))
                directories.Add(node.Path[..slash]);
        foreach (string directory in directories)
        {
            if (nodes.TryGetValue(directory, out var existing))
            {
                if (existing.Kind != PackageIndexKind.Directory)
                    return ResultObjectFactory.CreateFailure<PackageIndex2>(ResultCode.Failure, $"Pfad {existing.Path} ist zugleich Ordner anderer Paketpfade.");
                continue;
            }
            nodes.Add(directory, new Node(PackageIndexKind.Directory, directory, null, null, null));
        }
        return ResultObjectFactory.CreateSuccess(new PackageIndex2(packages, nodes, generation));
    }

    /// <summary>
    /// Loest einen Pfad auf: Datei- und Ordnerlinks werden verfolgt (hoechstens <see cref="PackageManifest2.MaximumLinkHops"/>),
    /// auch mitten im Pfad. Nicht vorhanden ist <see cref="ResultCode.NotFound"/>, ein nicht kanonischer Pfad
    /// <see cref="ResultCode.InvalidArgument"/>, eine Datei als Ordner, eine zu lange Kette oder ein Kreis <see cref="ResultCode.Failure"/>.
    /// </summary>
    public ResultObject<PackageIndexEntry> Resolve(string path)
    {
        if (!TryCanonical(path, out string current))
            return ResultObjectFactory.CreateFailure<PackageIndexEntry>(ResultCode.InvalidArgument, "Kein kanonischer Pfad: " + Shown(path));
        for (int hops = 0; ; )
        {
            if (nodes.TryGetValue(current, out var node))
            {
                if (node.Target == null) return ResultObjectFactory.CreateSuccess(Entry(node));
                if (++hops > PackageManifest2.MaximumLinkHops) return TooManyLinks(path);
                current = node.Target;
                continue;
            }
            // Kein ganzer Treffer: ein Link oder eine Datei als Ordner mitten im Pfad? Hoechstens ein Praefix kann Link oder Datei
            // sein, weil kein Paketpfad unter einem anderen liegt; die Reihenfolge der Suche ist deshalb gleichgueltig.
            Node? through = null;
            int cut = -1;
            for (int slash = current.IndexOf('/'); slash > 0; slash = current.IndexOf('/', slash + 1))
                if (nodes.TryGetValue(current[..slash], out var prefix) && prefix.Kind == PackageIndexKind.File)
                {
                    through = prefix;
                    cut = slash;
                    break;
                }
            if (through == null) return ResultObjectFactory.CreateFailure<PackageIndexEntry>(ResultCode.NotFound, "Nicht im Paketbestand: " + Shown(path));
            if (through.Target == null)
                return ResultObjectFactory.CreateFailure<PackageIndexEntry>(ResultCode.Failure, $"{through.Path} ist eine Datei, kein Ordner: {Shown(path)}");
            if (++hops > PackageManifest2.MaximumLinkHops) return TooManyLinks(path);
            current = string.Concat(through.Target, current.AsSpan(cut));
            if (current.Length > PackageManifest2.MaximumPathLength)
                return ResultObjectFactory.CreateFailure<PackageIndexEntry>(ResultCode.Failure, "Linkkette ergibt einen Pfad ueber 200 Zeichen: " + Shown(path));
        }
    }

    /// <summary>Prueft eine aufgeloeste Datei gegen Groesse und SHA-256 ihres signierten Manifests (Sperren wie <see cref="PackageStore2.VerifyFiles"/>).</summary>
    public static ResultErr Verify(PackageIndexEntry entry, CancellationToken cancellation)
    {
        if (entry?.File == null || entry.Location == null) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Keine Paketdatei.");
        return PackageStore2.VerifyFile(entry.Location, entry.File, cancellation);
    }

    private static PackageIndexEntry Entry(Node node) => node.Package == null
        ? new PackageIndexEntry(PackageIndexKind.Directory, node.Path, null, default, null, null)
        : new PackageIndexEntry(PackageIndexKind.File, node.Path, node.Package.Manifest.Id, node.Package.Manifest.Version, node.File,
            System.IO.Path.Combine(node.Package.FilesDirectory, node.File!.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    // Kanonisch: optional ein fuehrendes "/", dann Komponenten ohne leere, "." und "..", hoechstens 200 Zeichen. Die erlaubten
    // Zeichen prueft der Index nicht: Was es nicht gibt, findet er nicht.
    private static bool TryCanonical(string? path, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrEmpty(path)) return false;
        string relative = path[0] == '/' ? path[1..] : path;
        if (relative.Length is 0 or > PackageManifest2.MaximumPathLength) return false;
        int start = 0;
        for (int index = 0; index <= relative.Length; index++)
        {
            if (index < relative.Length && relative[index] != '/')
            {
                if (relative[index] is '\\' or '\0') return false;
                continue;
            }
            var component = relative.AsSpan(start, index - start);
            if (component.Length == 0 || component is "." or "..") return false;
            start = index + 1;
        }
        canonical = relative;
        return true;
    }

    // Ziel relativ zur Wurzel; der Parser hat Form und Ergebnis geprueft (fuehrende "../", danach normale Komponenten unter usr/, lib/, etc/).
    private static string ResolveLink(PackageLink link)
    {
        string target = link.Target;
        int keep = link.Path.LastIndexOf('/');
        while (target.StartsWith("../", StringComparison.Ordinal))
        {
            target = target[3..];
            keep = link.Path.LastIndexOf('/', keep - 1);
        }
        return link.Path[..(keep + 1)] + target;
    }

    private static ResultObject<PackageIndex2> Conflict(string path) =>
        ResultObjectFactory.CreateFailure<PackageIndex2>(ResultCode.Failure, $"Pfad {path} steht doppelt in der Menge.");

    private static ResultObject<PackageIndexEntry> TooManyLinks(string path) =>
        ResultObjectFactory.CreateFailure<PackageIndexEntry>(ResultCode.Failure, "Mehr als 40 Links in Folge: " + Shown(path));

    // Anfragen kommen spaeter aus Prozessen: nur druckbares ASCII und gekuerzt in Meldungen uebernehmen.
    private static string Shown(string? path)
    {
        if (path == null) return "\"\"";
        var text = new System.Text.StringBuilder(Math.Min(path.Length, 64) + 2).Append('"');
        foreach (char c in path.Length > 64 ? path[..64] : path) text.Append(c is >= ' ' and <= '~' ? c : '?');
        return text.Append(path.Length > 64 ? "...\"" : "\"").ToString();
    }
}
