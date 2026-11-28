using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ProjektZeh.Shared;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Migration;

namespace ProjektZeh.Packages;

/// <summary>Ergebnis eines Paketauftrags v2: neu gestufte und aktivierte Versionen sowie entfernte Pakete, je ordinal oder in Planreihenfolge.</summary>
internal sealed record PackageChange2(CatalogEntry2[] Staged, string[] Removed, bool Changed);

/// <summary>
/// Paketdienst des Formats 2 (Plan N3-A): verbindet den signierten <c>catalog2</c> ueber <see cref="IPackageSource"/> und
/// <see cref="PackageTrust"/> mit <see cref="PackageResolver"/> und <see cref="PackageStore2"/>. Jeder aendernde Auftrag berechnet
/// zuerst die vollstaendige Zielmenge, prueft den Platz, stuft alle neuen Versionen und wechselt dann genau einmal mit
/// <see cref="PackageStore2.Activate"/>. Scheitert oder endet ein Auftrag vorher, gilt der bisherige Bestand unveraendert; gestufte
/// Reste entfernt <see cref="PackageStore2.Reconcile"/> beim naechsten Einbinden. Noch nicht an <c>pkg</c> und den Startpfad
/// angeschlossen (Gastanschluss mit N3-B); der Bestand v1 bleibt davon unberuehrt.
/// </summary>
/// <remarks>
/// Entfernte Versionen bleiben als Ordner liegen, bis <see cref="PackageStore2.Reconcile"/> laeuft: Erst N3-B zaehlt laufende Nutzer,
/// vorher darf nur das Einbinden vor dem ersten Paketstart aufraeumen. Die Platzpruefung ist eine Vorabschaetzung (Inhalt plus
/// Verwaltungsaufschlag je Paket); den tatsaechlich vollen Datentraeger erkennt <see cref="PackageStore2.Stage"/> an der
/// gespeicherten Laenge.
/// </remarks>
internal sealed class PackageService2(string root, PackageStore2 store, PackageTrust? trust, Func<ResultObject<long>>? freeBytes = null,
    Func<IDisposable?>? tryBeginWrite = null)
{
    /// <summary>Aufschlag je gestufter Version fuer Manifest, Signatur und Verzeichniseintraege (FAT-Cluster), grob und grosszuegig.</summary>
    internal const long OverheadBytesPerPackage = 256 * 1024;
    private const string CatalogName = "catalog2";
    private readonly object gate = new();
    private bool busy;

    private const int FloorMaximumBytes = 512;

    private string CatalogPath => Path.Combine(root, "v2", "catalog2.signed");

    /// <summary>Generationsboden: hoechste je angenommene Katalog-Generation, getrennt vom Katalog abgelegt (Rueckstufungsschutz).</summary>
    private string FloorPath => Path.Combine(root, "v2", "catalog2.floor");

    /// <summary>Testnaht: wird vor jedem Paketschritt aufgerufen (Abbruch zwischen Stufen und vor Activate).</summary>
    internal Action<string>? ServiceStep { get; init; }

    /// <summary>
    /// Liest den gespeicherten catalog2; Signatur vor dem Parsen. Kein Katalog ist Erfolg mit null; ein beschaedigter ist ein Fehler.
    /// </summary>
    public ResultObject<PackageCatalog2?> ReadCatalog()
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<PackageCatalog2?>(NoTrust());
        try
        {
            var stored = ReadStored();
            if (stored == null) return ResultObjectFactory.CreateSuccess<PackageCatalog2?>(null);
            var parsed = PackageCatalog2.Parse(stored.Value.Catalog);
            if (!parsed.IsOK()) return DamagedCatalog();
            // Liegt der Katalog unter dem Boden (Abbruch zwischen Boden und Katalog, alte Fassung zurueckgelegt), gilt er nicht.
            long floor = HighestValidFloor();
            if (parsed.Value!.Generation < floor)
                return ResultObjectFactory.CreateFailure<PackageCatalog2?>(ResultCode.Failure,
                    $"Gespeicherter Katalog v2 ist aelter als der Generationsboden ({parsed.Value.Generation} < {floor}); pkg update ausfuehren.");
            return ResultObjectFactory.CreateSuccess<PackageCatalog2?>(parsed.Value);
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFailure<PackageCatalog2?>(ResultCode.Failure, "Gespeicherter Katalog v2 nicht lesbar: " + exception.Message);
        }
        catch (InvalidDataException) { return DamagedCatalog(); }
    }

    /// <summary>
    /// <c>pkg update</c>: laedt <c>catalog2</c> und <c>catalog2.sig</c>, prueft die Signatur vor dem Parsen und nimmt den Katalog nur an,
    /// wenn seine Generation groesser als die gespeicherte ist. Dieselbe Generation gilt nur mit denselben Bytes (dann nichts zu tun):
    /// Ein Herausgeber berichtigt einen Stand mit hoeherer Generation (Plan N3-G), zwei verschiedene Kataloge einer Generation sind
    /// ein Fehler. Der Bestand aendert sich dabei nicht.
    /// </summary>
    public ResultObject<PackageCatalog2> Update(IPackageSource source, CancellationToken cancellation)
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<PackageCatalog2>(NoTrust());
        if (source == null) return ResultObjectFactory.CreateFailure<PackageCatalog2>(ResultCode.MissingArgument, "Keine Paketquelle.");
        var begun = Begin();
        if (!begun.IsOK()) return ResultObjectFactory.CreateFailure<PackageCatalog2>(begun);
        try
        {
            var catalog = source.Read(CatalogName, PackageCatalog2.MaximumBytes, cancellation);
            if (!catalog.IsOK()) return ResultObjectFactory.CreateFailure<PackageCatalog2>(catalog.Status);
            var signature = source.Read(CatalogName + ".sig", PackageTrust.SignatureBytes, cancellation);
            if (!signature.IsOK()) return ResultObjectFactory.CreateFailure<PackageCatalog2>(signature.Status);
            // Bricht der Server zwischen den beiden Abrufen um, passt das Paar nicht: abweisen, ein neuer Abruf laedt beide neu.
            if (!trust.Verify(catalog.Value!, signature.Value!))
                return ResultObjectFactory.CreateFailure<PackageCatalog2>(ResultCode.InvalidArgument, "Katalogsignatur v2 ungueltig; pkg update wiederholen.");
            var parsed = PackageCatalog2.Parse(catalog.Value!);
            if (!parsed.IsOK()) return parsed;
            cancellation.ThrowIfCancellationRequested();
            var accepted = Accept(parsed.Value!, catalog.Value!, signature.Value!);
            return accepted.IsOK() ? parsed : ResultObjectFactory.CreateFailure<PackageCatalog2>(accepted);
        }
        catch (OperationCanceledException) { return ResultObjectFactory.CreateFailure<PackageCatalog2>(ResultCode.Failure, "Katalog laden abgebrochen."); }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFailure<PackageCatalog2>(ResultCode.Failure, "Katalog v2 nicht gespeichert: " + exception.Message);
        }
        finally { End(); }
    }

    // Rueckstufungsschutz: Der hoechste angenommene Stand steht zusaetzlich als Generationsboden in einer eigenen Datei. Ist der
    // gespeicherte Katalog beschaedigt, gilt der Boden weiter; nur wenn keine der beiden Angaben lesbar ist, obwohl eine vorliegt,
    // wird abgelehnt (fail closed): Sonst nimmt ein beschaedigter Katalog jede aeltere, gueltig signierte Fassung an, und
    // Neuinstallationen fielen auf alte Versionen zurueck. Reihenfolge beim Schreiben: erst der Boden, dann der Katalog.
    private ResultErr Accept(PackageCatalog2 parsed, byte[] catalog, byte[] signature)
    {
        string hash = PackageTrust.Sha256Hex(catalog);
        var floor = KnownGeneration(out byte[]? storedCatalog);
        if (floor.Damaged && floor.Hashes.Count == 0)
            return ResultErr.CreateFailure(ResultCode.Failure,
                "Katalog v2 und Generationsboden sind beschaedigt; Rueckstufungsschutz unbekannt, pkg update abgelehnt.");
        if (floor.Hashes.Count > 0)
        {
            if (parsed.Generation < floor.Generation)
                return ResultErr.CreateFailure(ResultCode.Failure, $"Katalog v2 ist aelter als der bekannte Stand (Generation {parsed.Generation} < {floor.Generation}).");
            if (parsed.Generation == floor.Generation)
            {
                if (!floor.Hashes.Contains(hash))
                    return ResultErr.CreateFailure(ResultCode.Failure, $"Katalog v2 nennt die bekannte Generation {floor.Generation} mit anderem Inhalt; abgelehnt.");
                // Derselbe Stand: nichts zu tun, wenn Katalog und Boden schon vorliegen; sonst beide wiederherstellen.
                if (storedCatalog != null && storedCatalog.AsSpan().SequenceEqual(catalog) && floor.FloorValid) return ResultErr.CreateSuccess();
            }
        }
        byte[] combined = new byte[signature.Length + catalog.Length];
        signature.CopyTo(combined, 0);
        catalog.CopyTo(combined, signature.Length);
        byte[] floorBytes = FloorBytes(parsed.Generation, hash);
        using var writer = BeginWrite();
        Step("Boden speichern");
        StorageLock.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
            PackageStore.ReplaceFileStorageLocked(FloorPath, floorBytes, textLine: true);
        });
        Step("Katalog speichern");
        StorageLock.Run(() => PackageStore.ReplaceFileStorageLocked(CatalogPath, combined));
        return ResultErr.CreateSuccess();
    }

    /// <summary>Bekannter Stand: hoechste Generation aus Boden und gespeichertem Katalog, die dort angenommenen Inhalte (SHA-256) und ob etwas Vorhandenes unlesbar war.</summary>
    private readonly record struct Floor(long Generation, HashSet<string> Hashes, bool Damaged, bool FloorValid);

    private Floor KnownGeneration(out byte[]? storedCatalog)
    {
        storedCatalog = null;
        long generation = 0;
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        bool damaged = false;
        void Offer(long candidate, string hash)
        {
            if (candidate > generation || hashes.Count == 0) { generation = candidate; hashes.Clear(); }
            if (candidate == generation) hashes.Add(hash);
        }
        var floors = StorageLock.Run(() => ReadFloorsStorageLocked());
        bool floorValid = false;
        foreach (byte[] bytes in floors)
            if (TryParseFloor(bytes, out long value, out string hash)) { Offer(value, hash); floorValid = true; }
            else damaged = true;
        try
        {
            var stored = ReadStored();
            if (stored != null)
            {
                var old = PackageCatalog2.Parse(stored.Value.Catalog);
                if (old.IsOK())
                {
                    storedCatalog = stored.Value.Catalog;
                    Offer(old.Value!.Generation, PackageTrust.Sha256Hex(storedCatalog));
                }
                else damaged = true;
            }
        }
        catch (InvalidDataException) { damaged = true; }
        return new Floor(generation, hashes, damaged, floorValid);
    }

    private long HighestValidFloor()
    {
        long highest = 0;
        foreach (byte[] bytes in StorageLock.Run(() => ReadFloorsStorageLocked()))
            if (TryParseFloor(bytes, out long value, out _) && value > highest) highest = value;
        return highest;
    }

    // Boden: Textdatei mit Generation, SHA-256 des Katalogs und einer Pruefsumme ueber beide Zeilen (erkennt gekuerzte oder
    // verfaelschte Bytes; gegen gezieltes Faelschen mit Plattenzugriff schuetzt sie nicht, das kann ohne Schluessel nichts).
    private static byte[] FloorBytes(long generation, string hash)
    {
        string body = $"zehos-catalog-floor 1\ngeneration={generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}\nsha256={hash}\n";
        return System.Text.Encoding.ASCII.GetBytes(body + "check=" + PackageTrust.Sha256Hex(System.Text.Encoding.ASCII.GetBytes(body)) + "\n");
    }

    internal static bool TryParseFloor(byte[] bytes, out long generation, out string hash)
    {
        generation = 0;
        hash = "";
        if (bytes.Length > FloorMaximumBytes) return false;
        // Nicht-ASCII wird hier zu '?', der Vergleich mit der neu erzeugten Fassung am Ende weist das ab.
        string[] lines = System.Text.Encoding.ASCII.GetString(bytes).Split('\n');
        if (lines.Length != 5 || lines[0] != "zehos-catalog-floor 1" || lines[4] != "") return false;
        if (!lines[1].StartsWith("generation=", StringComparison.Ordinal) || !lines[2].StartsWith("sha256=", StringComparison.Ordinal)
            || !lines[3].StartsWith("check=", StringComparison.Ordinal)) return false;
        if (!long.TryParse(lines[1].AsSpan("generation=".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out generation)) return false;
        hash = lines[2]["sha256=".Length..];
        if (hash.Length != 64) return false;
        var expected = FloorBytes(generation, hash);
        return expected.AsSpan().SequenceEqual(bytes);
    }

    // Alle vorhandenen Fassungen (Ziel, .old, .new) werden gelesen, ohne zu schreiben; die hoechste gueltige gilt. Eine .new ist
    // vollstaendig geschrieben (erst nach .wip umbenannt), kann also hoechstens einen spaeter nicht gespeicherten Katalog nennen:
    // Dann geht nur genau dieser Katalog wieder durch, das ist sicher.
    private List<byte[]> ReadFloorsStorageLocked()
    {
        var found = new List<byte[]>(3);
        foreach (string path in new[] { FloorPath, FloorPath + ".old", FloorPath + ".new" })
        {
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            byte[] bytes = new byte[Math.Min(stream.Length, FloorMaximumBytes + 1)];
            stream.ReadExactly(bytes);
            found.Add(bytes);
        }
        return found;
    }

    /// <summary>
    /// <c>pkg install</c>: stuft die angeforderten Pakete samt fehlender oder zu alter Abhaengigkeiten aus dem gespeicherten Katalog
    /// und aktiviert die ganze Zielmenge auf einmal. Schon vorhandene, nur automatisch installierte Pakete werden zu angeforderten.
    /// </summary>
    public ResultObject<PackageChange2> Install(IPackageSource source, IReadOnlyList<string> ids, CancellationToken cancellation,
        Action<PackageProgress>? progress = null)
    {
        if (ids == null || ids.Count == 0) return Fail(ResultCode.MissingArgument, "Kein Paket angegeben.");
        return Run(source, progress, (catalog, installed) => PackageResolver.Install(catalog, installed, ids), ids, cancellation);
    }

    /// <summary><c>pkg upgrade</c>: erneuert alle Pakete mit neuerer Katalogversion samt Abhaengigkeiten in einem Wechsel; nie auf kleinere Versionen.</summary>
    public ResultObject<PackageChange2> Upgrade(IPackageSource source, CancellationToken cancellation, Action<PackageProgress>? progress = null) =>
        Run(source, progress, PackageResolver.Upgrade, [], cancellation);

    /// <summary><c>pkg remove</c>: entfernt ein Paket aus der aktiven Menge, wenn kein anderes aktives Paket davon abhaengt.</summary>
    public ResultObject<PackageChange2> Remove(string id, CancellationToken cancellation) =>
        Shrink(installed =>
        {
            var allowed = PackageResolver.CheckRemove(installed, id);
            return allowed.IsOK() ? ResultObjectFactory.CreateSuccess(new[] { id }) : ResultObjectFactory.CreateFailure<string[]>(allowed);
        }, cancellation);

    /// <summary><c>pkg remove --unused</c>: entfernt alle automatisch installierten Pakete, die kein angefordertes Paket mehr braucht, in einem Wechsel.</summary>
    public ResultObject<PackageChange2> RemoveUnused(CancellationToken cancellation) =>
        Shrink(installed => ResultObjectFactory.CreateSuccess(PackageResolver.Unused(installed)), cancellation);

    private ResultObject<PackageChange2> Run(IPackageSource source, Action<PackageProgress>? progress,
        Func<PackageCatalog2, IReadOnlyList<InstalledPackage2>, ResultObject<CatalogEntry2[]>> plan, IReadOnlyList<string> requested, CancellationToken cancellation)
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<PackageChange2>(NoTrust());
        if (source == null) return Fail(ResultCode.MissingArgument, "Keine Paketquelle.");
        var begun = Begin();
        if (!begun.IsOK()) return ResultObjectFactory.CreateFailure<PackageChange2>(begun);
        try { return RunBegun(source, progress, plan, requested, cancellation); }
        catch (OperationCanceledException) { return Fail(ResultCode.Failure, "Paketauftrag abgebrochen; der bisherige Bestand gilt."); }
        finally { End(); }
    }

    private ResultObject<PackageChange2> RunBegun(IPackageSource source, Action<PackageProgress>? progress,
        Func<PackageCatalog2, IReadOnlyList<InstalledPackage2>, ResultObject<CatalogEntry2[]>> plan, IReadOnlyList<string> requested, CancellationToken cancellation)
    {
        var catalog = ReadCatalog();
        if (!catalog.IsOK()) return ResultObjectFactory.CreateFailure<PackageChange2>(catalog.Status);
        if (catalog.Value == null) return Fail(ResultCode.Failure, "Noch kein Katalog v2 geladen; zuerst pkg update.");
        var active = store.ReadActive();
        if (!active.IsOK()) return ResultObjectFactory.CreateFailure<PackageChange2>(active.Status);
        var installed = Installed(active.Value!);
        var planned = plan(catalog.Value, installed);
        if (!planned.IsOK()) return ResultObjectFactory.CreateFailure<PackageChange2>(planned.Status);
        var entries = planned.Value!;

        // Zielmenge: bisherige Menge, ersetzt oder ergaenzt um den Plan. Angefordert bleibt, was es war; dazu die genannten Kennungen.
        var target = new Dictionary<string, (PackageVersion2 Version, bool Requested)>(StringComparer.Ordinal);
        foreach (var package in installed) target[package.Id] = (package.Version, package.Requested);
        foreach (var entry in entries)
            target[entry.Id] = (entry.Version, target.TryGetValue(entry.Id, out var old) && old.Requested);
        bool flagged = false;
        foreach (string id in requested)
            if (target.TryGetValue(id, out var value) && !value.Requested)
            {
                target[id] = (value.Version, true);
                flagged = true;
            }
        if (entries.Length == 0 && !flagged) return ResultObjectFactory.CreateSuccess(new PackageChange2([], [], false));

        var space = CheckSpace(entries);
        if (!space.IsOK()) return ResultObjectFactory.CreateFailure<PackageChange2>(space);
        foreach (var entry in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            Step("stufen " + entry.Id);
            progress?.Invoke(new PackageProgress("Paket laden", entry.Id + " " + entry.Version, 0, 0));
            var staged = store.Stage(source, new CatalogEntry2Key(entry.Id, entry.Version, entry.ManifestSha256), cancellation);
            if (!staged.IsOK())
                return Fail(staged.Status.Code, $"{entry.Id} {entry.Version}: {staged.Status.Message} Der bisherige Bestand gilt.");
        }
        cancellation.ThrowIfCancellationRequested();
        Step("aktivieren");
        progress?.Invoke(new PackageProgress("Bestand wechseln", "active2", 0, 0));
        var activated = store.Activate(ToSet(target), cancellation);
        if (!activated.IsOK()) return ResultObjectFactory.CreateFailure<PackageChange2>(activated);
        return ResultObjectFactory.CreateSuccess(new PackageChange2(entries, [], true));
    }

    private ResultObject<PackageChange2> Shrink(Func<IReadOnlyList<InstalledPackage2>, ResultObject<string[]>> select, CancellationToken cancellation)
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<PackageChange2>(NoTrust());
        var begun = Begin();
        if (!begun.IsOK()) return ResultObjectFactory.CreateFailure<PackageChange2>(begun);
        try
        {
            var active = store.ReadActive();
            if (!active.IsOK()) return ResultObjectFactory.CreateFailure<PackageChange2>(active.Status);
            var installed = Installed(active.Value!);
            var selected = select(installed);
            if (!selected.IsOK()) return ResultObjectFactory.CreateFailure<PackageChange2>(selected.Status);
            var removed = new HashSet<string>(selected.Value!, StringComparer.Ordinal);
            if (removed.Count == 0) return ResultObjectFactory.CreateSuccess(new PackageChange2([], [], false));
            var target = new Dictionary<string, (PackageVersion2 Version, bool Requested)>(StringComparer.Ordinal);
            foreach (var package in installed)
                if (!removed.Contains(package.Id)) target[package.Id] = (package.Version, package.Requested);
            cancellation.ThrowIfCancellationRequested();
            Step("aktivieren");
            // Ein einziger Wechsel entfernt die ganze Auswahl; Activate prueft die Abhaengigkeiten der Restmenge noch einmal.
            var activated = store.Activate(ToSet(target), cancellation);
            if (!activated.IsOK()) return ResultObjectFactory.CreateFailure<PackageChange2>(activated);
            string[] names = [.. removed];
            Array.Sort(names, StringComparer.Ordinal);
            return ResultObjectFactory.CreateSuccess(new PackageChange2([], names, true));
        }
        catch (OperationCanceledException) { return Fail(ResultCode.Failure, "Paketauftrag abgebrochen; der bisherige Bestand gilt."); }
        finally { End(); }
    }

    // Vorab: Ein Auftrag, der sicher nicht passt, laedt nichts. Ohne Angabe (Host, unbekanntes Dateisystem) entscheidet erst Stage.
    private ResultErr CheckSpace(CatalogEntry2[] entries)
    {
        if (freeBytes == null || entries.Length == 0) return ResultErr.CreateSuccess();
        long required = 0;
        foreach (var entry in entries) required += entry.ContentBytes + OverheadBytesPerPackage;
        var free = freeBytes();
        if (!free.IsOK()) return ResultErr.CreateFailure(ResultCode.Failure, "Freier Platz nicht ermittelbar; nichts geladen: " + free.Status.Message);
        return free.Value >= required ? ResultErr.CreateSuccess()
            : ResultErr.CreateFailure(ResultCode.Failure, $"Zu wenig Platz: gebraucht etwa {required} Bytes, frei {free.Value}. Nichts geladen.");
    }

    private static List<InstalledPackage2> Installed(ActivePackage2[] active)
    {
        var installed = new List<InstalledPackage2>(active.Length);
        foreach (var package in active)
            installed.Add(new InstalledPackage2(package.Manifest.Id, package.Manifest.Version, package.Requested, package.Manifest.Dependencies));
        return installed;
    }

    private static List<(string Id, PackageVersion2 Version, bool Requested)> ToSet(Dictionary<string, (PackageVersion2 Version, bool Requested)> target)
    {
        var set = new List<(string Id, PackageVersion2 Version, bool Requested)>(target.Count);
        foreach (var pair in target) set.Add((pair.Key, pair.Value.Version, pair.Value.Requested));
        return set;
    }

    private readonly record struct StoredCatalog(byte[] Catalog);

    // Datei: 64 Bytes Signatur, dann der Katalog (wie v1); Signaturfehler ist InvalidDataException. Liest ohne zu schreiben die
    // Fassung, die RestorePendingStorageLocked uebernehmen wuerde: Laege nach einem Abbruch nur .old vor, gaelte sonst kein
    // Katalog, und ein aelterer wuerde wieder angenommen (Generationsrueckschritt).
    // Die Signatur (bis 4 MiB) wird ausserhalb der Speicher-Sperre geprueft, damit andere Plattenzugriffe nicht so lange warten.
    private StoredCatalog? ReadStored()
    {
        byte[]? bytes = StorageLock.Run(() => ReadStoredStorageLocked());
        if (bytes == null) return null;
        byte[] signature = bytes[..PackageTrust.SignatureBytes], catalog = bytes[PackageTrust.SignatureBytes..];
        if (!trust!.Verify(catalog, signature)) throw new InvalidDataException("Signatur");
        return new StoredCatalog(catalog);
    }

    private byte[]? ReadStoredStorageLocked()
    {
        string? path = File.Exists(CatalogPath) ? CatalogPath : File.Exists(CatalogPath + ".old") ? CatalogPath + ".old"
            : File.Exists(CatalogPath + ".new") ? CatalogPath + ".new" : null;
        if (path == null) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        if (stream.Length < PackageTrust.SignatureBytes || stream.Length > PackageTrust.SignatureBytes + PackageCatalog2.MaximumBytes)
            throw new InvalidDataException("Groesse");
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private ResultErr Begin()
    {
        lock (gate)
        {
            if (busy) return ResultErr.CreateFailure(ResultCode.Failure, "Paketdienst v2 bearbeitet gerade einen Auftrag.");
            busy = true;
            return ResultErr.CreateSuccess();
        }
    }

    private void End()
    {
        lock (gate) busy = false;
    }

    private IDisposable? BeginWrite() => tryBeginWrite == null ? null :
        tryBeginWrite() ?? throw new IOException(MigrationWriteGate.BusyMessage);

    private void Step(string what) => ServiceStep?.Invoke(what);

    private static ResultObject<PackageChange2> Fail(ResultCode code, string message) => ResultObjectFactory.CreateFailure<PackageChange2>(code, message);

    private static ResultErr NoTrust() => ResultErr.CreateFailure(ResultCode.Failure, "Dieser Build enthaelt keinen Paketschluessel; Pakete sind deaktiviert.");

    private static ResultObject<PackageCatalog2?> DamagedCatalog() =>
        ResultObjectFactory.CreateFailure<PackageCatalog2?>(ResultCode.Failure, "Gespeicherter Katalog v2 beschaedigt; pkg update ausfuehren.");
}
