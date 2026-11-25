using System;
using System.Collections.Generic;
using System.Text;
using ProjektZeh.Shared;

namespace ProjektZeh.Packages;

/// <summary>Installiertes Paket des Formats 2, wie es der Bestand (Plan P3) meldet. Requested: vom Benutzer angefordert, nicht nur Abhaengigkeit.</summary>
internal sealed record InstalledPackage2(string Id, PackageVersion2 Version, bool Requested, PackageDependency[] Dependencies);

/// <summary>
/// Loest Abhaengigkeiten des Formats 2 gegen einen Katalog auf (Plan P2, rein rechnerisch, noch nicht an pkg angeschlossen). Die
/// Ergebnisse sind Plaene: Signatur, Manifesthash und Dateipruefung bleiben Sache der Installation (PackageTrust); hier wird nichts
/// angenommen, was nicht im bereits gepruefte Katalog steht. Die Aufloesung ist iterativ (kein Stack je Abhaengigkeitstiefe) und durch
/// die Katalog- und Bestandsgrenzen (je 4096 Eintraege) in Zeit und Speicher begrenzt. Eingabetext erscheint in Meldungen nur
/// ueber PackageManifest2.Quote.
/// </summary>
internal static class PackageResolver
{
    internal const int MaximumInstalled = 4096;
    private const int MaximumListed = 8;

    /// <summary>
    /// Neue und zu erneuernde Eintraege fuer die angeforderten Pakete samt noetiger Abhaengigkeiten, Abhaengigkeiten zuerst, sonst
    /// ordinal nach id (angeforderte Pakete ordinal, Abhaengigkeiten je Paket ordinal). Ein angefordertes Paket mit gleicher oder
    /// neuerer installierter Version ist nichts zu tun; eine Abhaengigkeit, die der Bestand schon erfuellt, ebenso. Es wird nie auf
    /// eine kleinere Version gewechselt. Ein Zyklus zaehlt, wenn er von den Angeforderten erreichbar ist (auch ohne sie selbst zu
    /// enthalten); Zyklen im uebrigen Katalog stoeren nicht. Zwei Pakete der Zielmenge mit gleichem provides sind ein Fehler.
    /// </summary>
    public static ResultObject<CatalogEntry2[]> Install(PackageCatalog2 catalog, IReadOnlyList<InstalledPackage2> installed, IReadOnlyList<string> ids)
    {
        if (catalog == null || installed == null || ids == null) return Fail<CatalogEntry2[]>(ResultCode.MissingArgument, "Argument fehlt.");
        if (ids.Count > PackageCatalog2.MaximumEntries) return Fail<CatalogEntry2[]>(ResultCode.InvalidArgument, "Mehr als 4096 Pakete angefordert.");
        var map = MapInstalled(installed, out var mapError);
        if (map == null) return Fail<CatalogEntry2[]>(ResultCode.InvalidArgument, mapError!);
        var roots = new List<string>(ids.Count);
        foreach (string id in ids)
        {
            if (id == null || catalog.Find(id) == null)
                return Fail<CatalogEntry2[]>(ResultCode.NotFound, "Paket nicht im Katalog: " + PackageManifest2.Quote(id ?? ""));
            roots.Add(id);
        }
        roots.Sort(StringComparer.Ordinal);
        return Resolve(catalog, map, roots);
    }

    /// <summary>Erneuert jedes installierte Paket, dessen Katalogversion groesser ist, samt Abhaengigkeiten; nie auf kleinere Versionen. Ein Fehler bei einem Paket bricht den ganzen Plan ab, damit nur ein stimmiger Satz aktiviert wird.</summary>
    public static ResultObject<CatalogEntry2[]> Upgrade(PackageCatalog2 catalog, IReadOnlyList<InstalledPackage2> installed)
    {
        if (catalog == null || installed == null) return Fail<CatalogEntry2[]>(ResultCode.MissingArgument, "Argument fehlt.");
        var map = MapInstalled(installed, out var mapError);
        if (map == null) return Fail<CatalogEntry2[]>(ResultCode.InvalidArgument, mapError!);
        var roots = new List<string>();
        foreach (var package in map.Values)
        {
            var entry = catalog.Find(package.Id);
            if (entry != null && entry.Version.CompareTo(package.Version) > 0) roots.Add(package.Id);
        }
        roots.Sort(StringComparer.Ordinal);
        return Resolve(catalog, map, roots);
    }

    /// <summary>Erfolg, wenn kein installiertes Paket von id abhaengt; sonst Failure mit den abhaengigen Paketen (ordinal, hoechstens acht genannt).</summary>
    public static ResultErr CheckRemove(IReadOnlyList<InstalledPackage2> installed, string id)
    {
        if (installed == null || id == null) return ResultErr.CreateFailure(ResultCode.MissingArgument, "Argument fehlt.");
        var map = MapInstalled(installed, out var mapError);
        if (map == null) return ResultErr.CreateFailure(ResultCode.InvalidArgument, mapError);
        if (!map.ContainsKey(id)) return ResultErr.CreateFailure(ResultCode.NotFound, "Paket nicht installiert: " + PackageManifest2.Quote(id));
        var dependents = new List<string>();
        foreach (var package in map.Values)
        {
            if (package.Id == id) continue;
            foreach (var dependency in package.Dependencies)
                if (dependency.Id == id)
                {
                    dependents.Add(package.Id);
                    break;
                }
        }
        if (dependents.Count == 0) return ResultErr.CreateSuccess();
        dependents.Sort(StringComparer.Ordinal);
        var message = new StringBuilder("Paket " + PackageManifest2.Quote(id) + " wird gebraucht von:");
        for (int index = 0; index < Math.Min(dependents.Count, MaximumListed); index++)
            message.Append(' ').Append(PackageManifest2.Quote(dependents[index]));
        if (dependents.Count > MaximumListed) message.Append(" und ").Append(dependents.Count - MaximumListed).Append(" weitere");
        return ResultErr.CreateFailure(ResultCode.Failure, message.ToString());
    }

    /// <summary>
    /// Pakete, die nicht angefordert und von keinem angeforderten Paket ueber Abhaengigkeiten erreichbar sind (ordinal). Eine Kette
    /// oder ein Kreis aus lauter nicht angeforderten Paketen ist damit ganz unbenutzt; entfernt werden muss in umgekehrter
    /// Abhaengigkeitsreihenfolge (erst die Abhaengigen). Bei ungueltigem Bestand (Duplikate) ist das Ergebnis leer, nie eine Ausnahme.
    /// </summary>
    public static string[] Unused(IReadOnlyList<InstalledPackage2> installed)
    {
        if (installed == null) return [];
        var map = MapInstalled(installed, out _);
        if (map == null) return [];
        var used = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        foreach (var package in map.Values)
            if (package.Requested && used.Add(package.Id)) stack.Push(package.Id);
        while (stack.Count > 0)
            foreach (var dependency in map[stack.Pop()].Dependencies)
                if (map.ContainsKey(dependency.Id) && used.Add(dependency.Id)) stack.Push(dependency.Id);
        var unused = new List<string>();
        foreach (string id in map.Keys) if (!used.Contains(id)) unused.Add(id);
        unused.Sort(StringComparer.Ordinal);
        return [.. unused];
    }

    // Eine Zeile je Paket; Duplikate oder fehlende Eintraege machen den Bestand unbrauchbar, statt still einen zu waehlen.
    private static Dictionary<string, InstalledPackage2>? MapInstalled(IReadOnlyList<InstalledPackage2> installed, out string? error)
    {
        error = null;
        if (installed.Count > MaximumInstalled)
        {
            error = "Mehr als 4096 installierte Pakete.";
            return null;
        }
        var map = new Dictionary<string, InstalledPackage2>(installed.Count, StringComparer.Ordinal);
        foreach (var package in installed)
        {
            if (package == null || package.Id == null || package.Dependencies == null)
            {
                error = "Ungueltiger Bestandseintrag.";
                return null;
            }
            if (!map.TryAdd(package.Id, package))
            {
                error = "Paket doppelt im Bestand: " + PackageManifest2.Quote(package.Id);
                return null;
            }
        }
        return map;
    }

    private sealed class Frame(CatalogEntry2 entry)
    {
        public readonly CatalogEntry2 Entry = entry;
        public int Next;
    }

    // Tiefensuche mit eigenem Stapel; Nachordnung ergibt Abhaengigkeiten vor den Abhaengigen.
    private static ResultObject<CatalogEntry2[]> Resolve(PackageCatalog2 catalog, Dictionary<string, InstalledPackage2> installed, List<string> roots)
    {
        var order = new List<CatalogEntry2>();
        var gray = new HashSet<string>(StringComparer.Ordinal);
        var done = new Dictionary<string, CatalogEntry2>(StringComparer.Ordinal);
        var stack = new List<Frame>();
        foreach (string root in roots)
        {
            if (gray.Contains(root) || done.ContainsKey(root)) continue;
            var rootEntry = catalog.Find(root)!;
            if (installed.TryGetValue(root, out var present) && present.Version.CompareTo(rootEntry.Version) >= 0) continue;
            gray.Add(root);
            stack.Add(new Frame(rootEntry));
            while (stack.Count > 0)
            {
                var frame = stack[^1];
                if (frame.Next == frame.Entry.Dependencies.Length)
                {
                    stack.RemoveAt(stack.Count - 1);
                    gray.Remove(frame.Entry.Id);
                    done[frame.Entry.Id] = frame.Entry;
                    order.Add(frame.Entry);
                    continue;
                }
                var dependency = frame.Entry.Dependencies[frame.Next++];
                if (installed.TryGetValue(dependency.Id, out var have) && have.Version.CompareTo(dependency.Minimum) >= 0) continue;
                if (gray.Contains(dependency.Id)) return Fail<CatalogEntry2[]>(ResultCode.Failure, CycleMessage(stack, dependency.Id));
                if (!done.TryGetValue(dependency.Id, out var target))
                {
                    target = catalog.Find(dependency.Id);
                    if (target == null)
                        return Fail<CatalogEntry2[]>(ResultCode.NotFound, "Abhaengigkeit fehlt im Katalog: " + dependency + ", gebraucht von " + frame.Entry.Id);
                }
                if (target.Version.CompareTo(dependency.Minimum) < 0)
                    return Fail<CatalogEntry2[]>(ResultCode.Failure, "Abhaengigkeit zu alt: " + dependency + ", Katalog hat " + target.Version + ", gebraucht von " + frame.Entry.Id);
                if (done.ContainsKey(dependency.Id)) continue;
                gray.Add(dependency.Id);
                stack.Add(new Frame(target));
            }
        }
        var provided = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in order)
            foreach (string name in entry.Provides)
                if (!provided.TryAdd(name, entry.Id))
                    return Fail<CatalogEntry2[]>(ResultCode.Failure, PackageManifest2.Quote(name) + " wird von " + provided[name] + " und " + entry.Id + " geliefert");
        return ResultObjectFactory.CreateSuccess(order.ToArray());
    }

    private static string CycleMessage(List<Frame> stack, string repeated)
    {
        int start = stack.FindIndex(frame => frame.Entry.Id == repeated);
        var message = new StringBuilder("Abhaengigkeitszyklus: ");
        int count = stack.Count - start;
        for (int index = 0; index < Math.Min(count, MaximumListed); index++) message.Append(stack[start + index].Entry.Id).Append(" -> ");
        if (count > MaximumListed) message.Append("... -> ");
        return message.Append(repeated).ToString();
    }

    private static ResultObject<T> Fail<T>(ResultCode code, string message) => ResultObjectFactory.CreateFailure<T>(code, message);
}
