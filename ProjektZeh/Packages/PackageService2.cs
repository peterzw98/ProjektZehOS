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

    private string CatalogPath => Path.Combine(root, "v2", "catalog2.signed");

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
            return parsed.IsOK() ? ResultObjectFactory.CreateSuccess<PackageCatalog2?>(parsed.Value) : DamagedCatalog();
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

    private ResultErr Accept(PackageCatalog2 parsed, byte[] catalog, byte[] signature)
    {
        StoredCatalog? stored;
        // Ein unlesbarer gespeicherter Katalog sperrt keine Aktualisierung; die Rueckstufungssperre der Pakete (active2) bleibt.
        try { stored = ReadStored(); }
        catch (InvalidDataException) { stored = null; }
        if (stored != null && PackageCatalog2.Parse(stored.Value.Catalog) is { } old && old.IsOK())
        {
            long known = old.Value!.Generation;
            if (parsed.Generation < known)
                return ResultErr.CreateFailure(ResultCode.Failure, $"Katalog v2 ist aelter als der bekannte Stand (Generation {parsed.Generation} < {known}).");
            if (parsed.Generation == known)
                return stored.Value.Catalog.AsSpan().SequenceEqual(catalog) ? ResultErr.CreateSuccess()
                    : ResultErr.CreateFailure(ResultCode.Failure, $"Katalog v2 nennt die bekannte Generation {known} mit anderem Inhalt; abgelehnt.");
        }
        byte[] combined = new byte[signature.Length + catalog.Length];
        signature.CopyTo(combined, 0);
        catalog.CopyTo(combined, signature.Length);
        using var writer = BeginWrite();
        Step("Katalog speichern");
        StorageLock.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
            PackageStore.ReplaceFileStorageLocked(CatalogPath, combined);
        });
        return ResultErr.CreateSuccess();
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
