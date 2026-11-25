using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Org.BouncyCastle.Crypto.Digests;
using ProjektZeh.Shared;
using ProjektZeh.Storage;
using ProjektZeh.Storage.Migration;
using ProjektZeh.Update;

namespace ProjektZeh.Packages;

/// <summary>Aktive Version eines Pakets im Bestand v2; Requested unterscheidet angeforderte von automatisch installierten Paketen.</summary>
internal sealed record ActivePackage2(PackageManifest2 Manifest, bool Requested, string Directory)
{
    public string FilesDirectory => Path.Combine(Directory, "files");
}

/// <summary>Was der signierte Katalog v2 ueber eine Version zusagt; das gestufte Manifest muss genau diesen Hash haben.</summary>
internal readonly record struct CatalogEntry2Key(string Id, PackageVersion2 Version, string ManifestSha256);

/// <summary>
/// Paketbestand des Formats 2 (Plan P3, noch nicht angeschlossen). Ablage unter <c>&lt;root&gt;/v2/</c>:
/// <c>&lt;id&gt;/&lt;version&gt;-r&lt;release&gt;/{manifest,manifest.sig,files/...}</c> und genau eine Datei <c>active2</c> mit der
/// aktiven Menge. Nur <c>active2</c> veroeffentlicht Versionen; sie wechselt in einem Schritt ueber die Sicherungslogik von
/// <see cref="PackageStore.ReplaceFileStorageLocked"/>, damit nach einem Abbruch die alte oder die neue Menge gilt, nie eine
/// Mischung. Der Bestand v1 (<c>apps/</c>, Katalog, Quelle) liegt daneben und wird nie angefasst.
/// </summary>
/// <remarks>
/// Jede Signatur wird vor <see cref="PackageManifest2.Parse"/> geprueft: Der Parser bekommt nur Text des Herausgebers zu sehen
/// (Review von #668: die Pfadmenge im Parser hasht ohne Salt). Schreibende Operationen schliessen sich innerhalb einer Instanz
/// aus; nicht aktive Versionen entfernt nur <see cref="Reconcile"/>, das vor dem Start von Paketprogrammen laufen muss, weil es
/// noch keine Belegung laufender Versionen gibt.
/// </remarks>
internal sealed class PackageStore2(string root, PackageTrust? trust, UpdateVersion kernel, Func<IDisposable?>? tryBeginWrite = null)
{
    /// <summary>Hoechstens so viele Pakete in der aktiven Menge (wie im Katalog v2).</summary>
    internal const int MaximumPackages = 4096;
    /// <summary>Groesse von active2: 4096 Zeilen zu hoechstens etwa 160 Zeichen passen mit Abstand.</summary>
    internal const int MaximumActiveBytes = 1024 * 1024;
    /// <summary>Alle Manifeste einer Menge liegen zugleich im Speicher; Grenze nach L1-C pruefen.</summary>
    internal const long MaximumManifestBytes = 16L * 1024 * 1024;
    /// <summary>Dateien und Links aller Pakete einer Menge zusammen; begrenzt Speicher und Zeit der Konfliktpruefung.</summary>
    internal const int MaximumPaths = 131072;
    private const string Header = "zehos-active 2";
    private const string LinePrefix = "package=";
    private const int BufferBytes = 64 * 1024;
    private readonly object gate = new();
    private bool busy;
    // In dieser Instanz vollstaendig gestufte Versionen ("id/version-rN") mit dem Hash ihres Manifests. Die Dateien wurden beim
    // Schreiben gehasht; im laufenden Kernel aendert nur dieser Bestand sie. Nach einem Neustart entfernt Reconcile die Stufen.
    private readonly Dictionary<string, string> staged = new(StringComparer.Ordinal);

    /// <summary>Testnaht: wird vor jedem veraendernden Dateischritt aufgerufen (Abbruch- und Schreibfehlertests).</summary>
    internal Action<string>? FileStep { get; init; }

    private string Packages => Path.Combine(root, "v2");
    private string ActivePath => Path.Combine(Packages, "active2");

    private sealed record ActiveLine(string Id, PackageVersion2 Version, bool Requested, string Sha256);

    /// <summary>
    /// Liest und prueft die aktive Menge: Signatur vor dem Parsen, Manifesthash gegen active2, Abhaengigkeiten und Konflikte.
    /// Fehlt active2, ist der Bestand leer; jede Beschaedigung ist ein Fehler, nie ein leerer Bestand. Schreibt nichts: Ein
    /// halb ersetztes active2 wird so gelesen, wie <see cref="PackageStore.RestorePendingStorageLocked"/> es abschliessen wuerde.
    /// </summary>
    public ResultObject<ActivePackage2[]> ReadActive()
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<ActivePackage2[]>(NoTrust());
        try
        {
            var lines = ReadLines();
            if (!lines.IsOK()) return ResultObjectFactory.CreateFailure<ActivePackage2[]>(lines.Status);
            var packages = new ActivePackage2[lines.Value!.Length];
            var manifests = new PackageManifest2[packages.Length];
            long total = 0;
            for (int index = 0; index < packages.Length; index++)
            {
                var line = lines.Value[index];
                string directory = VersionDirectory(line.Id, line.Version);
                var loaded = Load(directory, line.Id, line.Version, line.Sha256, ref total);
                if (!loaded.IsOK()) return ResultObjectFactory.CreateFailure<ActivePackage2[]>(Damaged(loaded.Status.Message));
                manifests[index] = loaded.Value!;
                packages[index] = new ActivePackage2(loaded.Value!, line.Requested, directory);
            }
            string? conflict = CheckSet(manifests);
            return conflict != null ? ResultObjectFactory.CreateFailure<ActivePackage2[]>(Damaged(conflict))
                : ResultObjectFactory.CreateSuccess(packages);
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFailure<ActivePackage2[]>(Damaged(exception.Message));
        }
    }

    /// <summary>
    /// Laedt eine Version aus <paramref name="source"/> (<c>&lt;id&gt;/&lt;version&gt;-r&lt;release&gt;/</c>) in ihren Ordner, ohne
    /// sie zu aktivieren. Reihenfolge: Katalog-Hash, Signatur, erst dann Parsen, Mindest-Kernel, Dateien (beim Schreiben gehasht),
    /// zuletzt Manifest und Signatur. Bei Fehler oder Abbruch bleibt kein Ordner liegen; die aktive Menge bleibt unberuehrt.
    /// Eine bereits aktive Version wird nicht ueberschrieben, sondern nur geprueft.
    /// </summary>
    public ResultObject<PackageManifest2> Stage(IPackageSource source, CatalogEntry2Key expected, CancellationToken cancellation)
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<PackageManifest2>(NoTrust());
        if (expected.Id == null || !PackageManifest.IsValidId(expected.Id) || !IsSha256(expected.ManifestSha256))
            return ResultObjectFactory.CreateFailure<PackageManifest2>(ResultCode.InvalidArgument, "Ungueltiger Katalogeintrag.");
        var begun = Begin();
        if (!begun.IsOK()) return ResultObjectFactory.CreateFailure<PackageManifest2>(begun);
        try { return StageBegun(source, expected, cancellation); }
        catch (OperationCanceledException) { return ResultObjectFactory.CreateFailure<PackageManifest2>(ResultCode.Failure, "Stufen abgebrochen."); }
        // Wie PackageStore.Install: Quellen lesen auch aus dem Netz; jeder Fehler wird zum Ergebnis, die Stufe ist dann entfernt.
        catch (Exception exception) { return ResultObjectFactory.CreateFromException<PackageManifest2>(exception); }
        finally { End(); }
    }

    private ResultObject<PackageManifest2> StageBegun(IPackageSource source, CatalogEntry2Key expected, CancellationToken cancellation)
    {
        string prefix = expected.Id + "/" + expected.Version + "/";
        var manifestBytes = source.Read(prefix + "manifest", PackageManifest2.MaximumBytes, cancellation);
        if (!manifestBytes.IsOK()) return ResultObjectFactory.CreateFailure<PackageManifest2>(manifestBytes.Status);
        var signature = source.Read(prefix + "manifest.sig", PackageTrust.SignatureBytes, cancellation);
        if (!signature.IsOK()) return ResultObjectFactory.CreateFailure<PackageManifest2>(signature.Status);
        if (!string.Equals(PackageTrust.Sha256Hex(manifestBytes.Value!), expected.ManifestSha256, StringComparison.Ordinal))
            return ResultObjectFactory.CreateFailure<PackageManifest2>(ResultCode.InvalidArgument, "Manifest passt nicht zum signierten Katalog.");
        if (!trust!.Verify(manifestBytes.Value!, signature.Value!))
            return ResultObjectFactory.CreateFailure<PackageManifest2>(ResultCode.InvalidArgument, "Paketsignatur ungueltig.");
        var parsed = PackageManifest2.Parse(manifestBytes.Value!);
        if (!parsed.IsOK()) return parsed;
        var manifest = parsed.Value!;
        if (manifest.Id != expected.Id || manifest.Version != expected.Version)
            return ResultObjectFactory.CreateFailure<PackageManifest2>(ResultCode.InvalidArgument, "Manifest nennt eine andere Kennung oder Version als der Katalog.");
        // Vor jedem Dateizugriff: Eine Version, die dieser Kernel nicht aktivieren wuerde, laedt nichts.
        var supported = RequireKernel(manifest);
        if (!supported.IsOK()) return ResultObjectFactory.CreateFailure<PackageManifest2>(supported);

        using var writer = BeginWrite();
        string key = Key(manifest.Id, manifest.Version);
        string target = VersionDirectory(manifest.Id, manifest.Version);
        // Nach einem Doppelfehler liegt active2 nur als .old vor; erst abschliessen, dann entscheiden, ob die Version aktiv ist.
        Step("active2 abschliessen");
        StorageLock.Run(() => PackageStore.RestorePendingStorageLocked(ActivePath, true));
        var lines = ReadLines();
        if (lines.IsOK() && Array.Exists(lines.Value!, line => line.Id == manifest.Id && line.Version == manifest.Version))
        {
            // Die aktive Version darf nicht geloescht und neu geschrieben werden: Ein Abbruch liesse sonst einen aktiven, halben
            // Ordner zurueck. Ist sie intakt, gilt sie als gestuft; eine Reparatur an Ort und Stelle gehoert nicht zu P3.
            var stored = Load(target, manifest.Id, manifest.Version, expected.ManifestSha256);
            if (!stored.IsOK() || !VerifyFiles(new ActivePackage2(stored.Value!, false, target), cancellation).IsOK())
                return ResultObjectFactory.CreateFailure<PackageManifest2>(ResultCode.Failure,
                    $"{manifest.Id} {manifest.Version} ist aktiv, aber beschaedigt; zuerst eine andere Version aktivieren.");
            lock (gate) staged[key] = expected.ManifestSha256;
            return parsed;
        }
        lock (gate) staged.Remove(key);
        ResultErr written;
        try { written = WriteVersion(source, prefix, manifest, signature.Value!, target, cancellation); }
        catch (Exception)
        {
            // Auch bei Abbruch oder Dateifehler keine halbe Stufe liegen lassen; scheitert das, entfernt Reconcile sie.
            TryDeleteTree(target);
            throw;
        }
        if (!written.IsOK())
        {
            TryDeleteTree(target);
            return ResultObjectFactory.CreateFailure<PackageManifest2>(written);
        }
        lock (gate) staged[key] = expected.ManifestSha256;
        return parsed;
    }

    private ResultErr WriteVersion(IPackageSource source, string prefix, PackageManifest2 manifest, byte[] signature, string target, CancellationToken cancellation)
    {
        // Reste eines abgebrochenen Versuchs derselben Version waren nie aktiv (oben geprueft) und werden verworfen.
        if (StorageLock.Run(() => Directory.Exists(target))) DeleteTree(target);
        string files = Path.Combine(target, "files");
        Step("Ordner anlegen");
        StorageLock.Run(() => Directory.CreateDirectory(files));
        foreach (var file in manifest.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.Combine(files, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var digest = new Sha256Digest();
            ResultErr copied;
            long stored = -1;
            Step("Datei anlegen " + file.Path);
            // Die Quelle kann aus dem Netz lesen: nur Oeffnen, jeder Schreibblock und Schliessen sperren (wie PackageStore).
            var stream = StorageLock.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            });
            var hashing = new HashingStream(stream, digest, () => Step("Datei schreiben " + file.Path));
            try { copied = source.Copy(prefix + "files/" + file.Path, file.Size, hashing, cancellation); }
            finally
            {
                // Cosmos kuerzt bei vollem Datentraeger still: die gespeicherte Laenge lesen. Ein Fehler hier darf den Fehler aus
                // Copy nicht verdecken; stored bleibt dann -1.
                StorageLock.Run(() =>
                {
                    try { stream.Flush(true); stored = stream.Length; }
                    catch (Exception exception) when (FileErrors.IsFileError(exception)) { }
                    finally { stream.Dispose(); }
                });
            }
            if (copied.IsOK() && stored != file.Size)
                copied = ResultErr.CreateFailure(ResultCode.Failure, $"{file.Path} ist unvollstaendig gespeichert ({stored} von {file.Size} Bytes); Datentraeger voll?");
            if (copied.IsOK() && hashing.Written != file.Size) copied = ResultErr.CreateFailure(ResultCode.Failure, file.Path + " ist unvollstaendig.");
            if (!copied.IsOK()) return copied;
            if (!string.Equals(Finish(digest), file.Sha256, StringComparison.Ordinal))
                return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Pruefsumme falsch: " + file.Path);
        }
        // Manifest und Signatur zuletzt: Fehlt eines oder ist es gekuerzt, scheitert jede spaetere Pruefung dieser Stufe.
        Step("Manifest schreiben");
        StorageLock.Run(() => FileService.WriteAllBytesCompleteStorageLocked(Path.Combine(target, "manifest"), manifest.Bytes));
        Step("Signatur schreiben");
        StorageLock.Run(() => FileService.WriteAllBytesCompleteStorageLocked(Path.Combine(target, "manifest.sig"), signature));
        return ResultErr.CreateSuccess();
    }

    /// <summary>
    /// Macht <paramref name="set"/> zur aktiven Menge. Vor dem Wechsel: jede Version in dieser Instanz gestuft oder schon aktiv,
    /// Signatur (vor dem Parsen) und Manifesthash unveraendert, keine Rueckstufung, <c>min-kernel</c>, alle <c>depends</c> in der
    /// Menge erfuellt, kein Pfad in zwei Paketen, kein <c>provides</c> doppelt, Linkketten ueber Paketgrenzen begrenzt. Danach
    /// wechselt genau eine Datei (active2). Alte Versionen bleiben bis zum naechsten <see cref="Reconcile"/> liegen.
    /// </summary>
    public ResultErr Activate(IReadOnlyList<(string Id, PackageVersion2 Version, bool Requested)> set, CancellationToken cancellation)
    {
        if (trust == null) return NoTrust();
        if (set == null || set.Count > MaximumPackages) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Mehr als 4096 Pakete.");
        var sorted = new List<(string Id, PackageVersion2 Version, bool Requested)>(set);
        foreach (var entry in sorted)
            if (entry.Id == null || !PackageManifest.IsValidId(entry.Id)) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Ungueltige Paketkennung.");
        sorted.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        for (int index = 1; index < sorted.Count; index++)
            if (sorted[index - 1].Id == sorted[index].Id)
                return ResultErr.CreateFailure(ResultCode.InvalidArgument, $"Paket {sorted[index].Id} steht doppelt in der Menge.");
        var begun = Begin();
        if (!begun.IsOK()) return begun;
        try { return ActivateBegun(sorted, cancellation); }
        catch (OperationCanceledException) { return ResultErr.CreateFailure(ResultCode.Failure, "Aktivieren abgebrochen; der bisherige Bestand gilt."); }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "Aktivieren gescheitert; der bisherige Bestand gilt: " + exception.Message);
        }
        finally { End(); }
    }

    private ResultErr ActivateBegun(List<(string Id, PackageVersion2 Version, bool Requested)> set, CancellationToken cancellation)
    {
        using var writer = BeginWrite();
        Step("active2 abschliessen");
        StorageLock.Run(() => PackageStore.RestorePendingStorageLocked(ActivePath, true));
        // Ein unlesbares active2 verhindert keine Reparatur durch eine vollstaendig gestufte Menge; es liefert dann nur weder
        // wiederverwendbare Versionen noch eine Rueckstufungsgrenze.
        var current = ReadLines();
        ActiveLine[] active = current.IsOK() ? current.Value! : [];
        var manifests = new PackageManifest2[set.Count];
        var lines = new ActiveLine[set.Count];
        long total = 0;
        for (int index = 0; index < set.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var (id, version, requested) = set[index];
            var previous = Array.Find(active, line => line.Id == id);
            if (previous != null && previous.Version.CompareTo(version) > 0)
                return ResultErr.CreateFailure(ResultCode.Failure, $"Rueckstufung abgelehnt: aktiv ist {id} {previous.Version}.");
            string? sha;
            lock (gate) staged.TryGetValue(Key(id, version), out sha);
            if (sha == null && previous != null && previous.Version == version) sha = previous.Sha256;
            if (sha == null) return ResultErr.CreateFailure(ResultCode.Failure, $"{id} {version} ist nicht gestuft.");
            var loaded = Load(VersionDirectory(id, version), id, version, sha, ref total);
            if (!loaded.IsOK()) return loaded.Status;
            var supported = RequireKernel(loaded.Value!);
            if (!supported.IsOK()) return supported;
            manifests[index] = loaded.Value!;
            lines[index] = new ActiveLine(id, version, requested, sha);
        }
        string? conflict = CheckSet(manifests);
        if (conflict != null) return ResultErr.CreateFailure(ResultCode.Failure, conflict);
        byte[] text = Format(lines);
        Step("active2 ersetzen");
        StorageLock.Run(() =>
        {
            Directory.CreateDirectory(Packages);
            PackageStore.ReplaceFileStorageLocked(ActivePath, text, Move, textLine: true);
        });
        lock (gate) foreach (var line in lines) staged.Remove(Key(line.Id, line.Version));
        return ResultErr.CreateSuccess();
    }

    /// <summary>
    /// Beim Einbinden, bevor Paketprogramme laufen: halb ersetztes active2 abschliessen, dann unvollstaendige Stufen und nicht
    /// aktive Versionen entfernen. Ist active2 unlesbar, wird nichts geloescht; der Bestand bleibt gesperrt statt Daten zu verlieren.
    /// Ordner, die keine gueltige Kennung oder Version tragen, bleiben unberuehrt.
    /// </summary>
    public ResultErr Reconcile()
    {
        var begun = Begin();
        if (!begun.IsOK()) return begun;
        try
        {
            using var writer = BeginWrite();
            Step("active2 abschliessen");
            StorageLock.Run(() => PackageStore.RestorePendingStorageLocked(ActivePath, true));
            string[]? directories = StorageLock.Run(() => Directory.Exists(Packages) ? Directory.GetDirectories(Packages) : null);
            if (directories == null) return ResultErr.CreateSuccess();
            var lines = ReadLines();
            if (!lines.IsOK()) return ResultErr.CreateFailure(ResultCode.Failure, lines.Status.Message + " Nichts aufgeraeumt.");
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines.Value!) keep.Add(Key(line.Id, line.Version));
            lock (gate) foreach (string key in staged.Keys) keep.Add(key);
            foreach (string directory in directories)
            {
                string id = Path.GetFileName(directory);
                if (!PackageManifest.IsValidId(id)) continue;
                foreach (string version in StorageLock.Run(() => Directory.GetDirectories(directory)))
                {
                    string name = Path.GetFileName(version);
                    if (!PackageVersion2.TryParse(name, out var parsed) || parsed.ToString() != name) continue;
                    if (!keep.Contains(Key(id, parsed))) DeleteTree(version);
                }
                if (StorageLock.Run(() => Directory.GetFileSystemEntries(directory).Length == 0))
                {
                    Step("Ordner entfernen " + id);
                    StorageLock.Run(() => Directory.Delete(directory));
                }
            }
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "Paketbestand v2 nicht aufgeraeumt: " + exception.Message);
        }
        finally { End(); }
    }

    /// <summary>Prueft alle gespeicherten Dateien gegen das signierte Manifest; eine spaetere Veraenderung sperrt den Start.</summary>
    public static ResultErr VerifyFiles(ActivePackage2 package, CancellationToken cancellation)
    {
        try
        {
            byte[] buffer = new byte[BufferBytes];
            foreach (var file in package.Manifest.Files)
            {
                bool intact;
                string path = Path.Combine(package.FilesDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                // Grosse Dateien nicht unter einer einzigen Sperre hashen: Oeffnen, jeder Leseblock und Schliessen sperren einzeln.
                var stream = StorageLock.Run(() => new FileStream(path, FileMode.Open, FileAccess.Read));
                try { intact = Matches(stream, file, buffer, cancellation); }
                finally { StorageLock.Run(() => stream.Dispose()); }
                if (!intact) return ResultErr.CreateFailure(ResultCode.Failure, $"Paketdatei {file.Path} wurde veraendert; Start gesperrt.");
            }
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "Paketdateien nicht lesbar: " + exception.Message);
        }
    }

    private static bool Matches(FileStream stream, PackageFile2 file, byte[] buffer, CancellationToken cancellation)
    {
        if (StorageLock.Run(() => stream.Length) != file.Size) return false;
        var digest = new Sha256Digest();
        for (long left = file.Size; left > 0;)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, left);
            int read = StorageLock.Run(() => stream.Read(buffer, 0, count));
            if (read <= 0) return false;
            digest.BlockUpdate(buffer, 0, read);
            left -= read;
        }
        return string.Equals(Finish(digest), file.Sha256, StringComparison.Ordinal);
    }

    private ResultObject<PackageManifest2> Load(string directory, string id, PackageVersion2 version, string sha256)
    {
        long total = 0;
        return Load(directory, id, version, sha256, ref total);
    }

    /// <summary>
    /// Liest Manifest und Signatur einer Version und prueft in dieser Reihenfolge: Gesamtgroesse der Menge, Signatur, Hash,
    /// erst dann Parsen, Kennung und Version. Dateifehler werfen (Aufrufer melden sie).
    /// </summary>
    private ResultObject<PackageManifest2> Load(string directory, string id, PackageVersion2 version, string sha256, ref long total)
    {
        string name = id + " " + version;
        byte[] bytes = ReadSmall(Path.Combine(directory, "manifest"), PackageManifest2.MaximumBytes);
        byte[] signature = ReadSmall(Path.Combine(directory, "manifest.sig"), PackageTrust.SignatureBytes);
        total += bytes.Length;
        if (total > MaximumManifestBytes) return Failure<PackageManifest2>("Manifeste der Menge zusammen groesser als 16 MiB.");
        if (!trust!.Verify(bytes, signature)) return Failure<PackageManifest2>($"{name}: Paketsignatur ungueltig.");
        if (!string.Equals(PackageTrust.Sha256Hex(bytes), sha256, StringComparison.Ordinal))
            return Failure<PackageManifest2>($"{name}: Manifest wurde seit dem Stufen oder Aktivieren veraendert.");
        var parsed = PackageManifest2.Parse(bytes);
        if (!parsed.IsOK()) return Failure<PackageManifest2>($"{name}: " + parsed.Status.Message);
        if (parsed.Value!.Id != id || parsed.Value.Version != version) return Failure<PackageManifest2>($"{name}: Manifest gehoert zu einer anderen Version.");
        return parsed;
    }

    /// <summary>
    /// Regeln ueber Paketgrenzen: Abhaengigkeiten in der Menge erfuellt, provides eindeutig, kein Pfad doppelt (ohne
    /// Gross-/Kleinschreibung, FAT32), kein Pfad Ordner eines Pfads eines anderen Pakets, Linkketten ueber Paketgrenzen hoechstens
    /// 40 Links und 200 Zeichen ohne Kreis. Innerhalb eines Pakets hat <see cref="PackageManifest2.Parse"/> dasselbe geprueft.
    /// Liefert null oder die Fehlermeldung.
    /// </summary>
    private static string? CheckSet(PackageManifest2[] set)
    {
        var versions = new Dictionary<string, PackageVersion2>(StringComparer.Ordinal);
        foreach (var manifest in set) versions[manifest.Id] = manifest.Version;
        var provided = new Dictionary<string, string>(StringComparer.Ordinal);
        int count = 0;
        foreach (var manifest in set)
        {
            foreach (var dependency in manifest.Dependencies)
            {
                if (!versions.TryGetValue(dependency.Id, out var present))
                    return $"{manifest.Id} braucht {dependency.Id}, das nicht in der Menge ist.";
                if (present.CompareTo(dependency.Minimum) < 0)
                    return $"{manifest.Id} braucht {dependency}, in der Menge ist {dependency.Id} {present}.";
            }
            foreach (string name in manifest.Provides)
                if (!provided.TryAdd(name, manifest.Id)) return $"{name} wird von {provided[name]} und {manifest.Id} bereitgestellt.";
            count += manifest.Files.Length + manifest.Links.Length;
            if (count > MaximumPaths) return "Mehr als 131072 Dateien und Links in der Menge.";
        }
        // Eigentuemer je Pfad; FAT32 unterscheidet keine Gross-/Kleinschreibung, also auch hier nicht.
        var owners = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in set)
        {
            foreach (var file in manifest.Files)
                if (!owners.TryAdd(file.Path, manifest.Id)) return Conflict(file.Path, owners[file.Path], manifest.Id);
            foreach (var link in manifest.Links)
            {
                if (!owners.TryAdd(link.Path, manifest.Id)) return Conflict(link.Path, owners[link.Path], manifest.Id);
                links.Add(link.Path, Resolve(link));
            }
        }
        foreach (var (path, owner) in owners)
            for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (owners.TryGetValue(path[..slash], out string? parent))
                    return $"Pfad {path[..slash]} von {parent} ist Ordner von {path} aus {owner}.";
        return CheckLinkChains(links);
    }

    private static string Conflict(string path, string first, string second) =>
        $"Pfad {path} liegt in {first} und {second}.";

    // Folgt jedem Linkziel durch alle Links der Menge (wie CheckLinkChains im Parser, aber paketuebergreifend).
    private static string? CheckLinkChains(Dictionary<string, string> links)
    {
        foreach (var (path, target) in links)
        {
            string current = target;
            for (int hops = 0; ; hops++)
            {
                if (IsSameOrNested(current, path)) return $"Link {path} zeigt ueber Paketgrenzen auf sich selbst oder darunter.";
                int prefix = LinkPrefix(links, current, out string? through);
                if (prefix < 0) break;
                if (hops == PackageManifest2.MaximumLinkHops) return $"Mehr als 40 Links in Folge ab {path}.";
                current = through + current[prefix..];
                if (current.Length > PackageManifest2.MaximumPathLength) return $"Linkkette ab {path} ergibt einen Pfad ueber 200 Zeichen.";
            }
        }
        return null;
    }

    // Laenge des kuerzesten Praefixes (an '/' oder ganz), der ein Link ist, sonst -1; through ist dessen aufgeloestes Ziel.
    private static int LinkPrefix(Dictionary<string, string> links, string path, out string? through)
    {
        for (int end = path.IndexOf('/'); ; end = path.IndexOf('/', end + 1))
        {
            int length = end < 0 ? path.Length : end;
            if (links.TryGetValue(path[..length], out through)) return length;
            if (end < 0) return -1;
        }
    }

    private static bool IsSameOrNested(string path, string link)
    {
        int shorter = Math.Min(path.Length, link.Length);
        if (string.Compare(path, 0, link, 0, shorter, StringComparison.OrdinalIgnoreCase) != 0) return false;
        if (path.Length == link.Length) return true;
        return path.Length < link.Length ? link[shorter] == '/' : path[shorter] == '/';
    }

    // Ziel relativ zur Wurzel; der Parser hat Form und Ergebnis schon geprueft (fuehrende "../", danach normale Komponenten).
    private static string Resolve(PackageLink link)
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

    // Liest die Zeilen von active2 ohne Signaturpruefung, aber streng; fehlt die Datei (auch als Ersatzfassung), ist die Menge leer.
    private ResultObject<ActiveLine[]> ReadLines()
    {
        byte[]? bytes = StorageLock.Run(() =>
        {
            string? path = EffectiveActivePathStorageLocked();
            return path == null ? null : ReadSmallStorageLocked(path, MaximumActiveBytes);
        });
        if (bytes == null) return ResultObjectFactory.CreateSuccess<ActiveLine[]>([]);
        return Parse(bytes);
    }

    // Welche Fassung RestorePendingStorageLocked(textLine: true) uebernehmen wuerde, ohne etwas zu veraendern.
    private string? EffectiveActivePathStorageLocked()
    {
        string old = ActivePath + ".old", temporary = ActivePath + ".new";
        if (File.Exists(ActivePath)) return ActivePath;
        if (File.Exists(old)) return old;
        if (!File.Exists(temporary)) return null;
        using var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read);
        if (stream.Length == 0) return null;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n' ? temporary : null;
    }

    private static ResultObject<ActiveLine[]> Parse(byte[] bytes)
    {
        foreach (byte value in bytes)
            if (value != '\n' && value is < 0x20 or > 0x7e) return Failure<ActiveLine[]>("active2 enthaelt ungueltige Zeichen.");
        string text = Encoding.ASCII.GetString(bytes);
        if (!text.EndsWith("\nend\n", StringComparison.Ordinal)) return Failure<ActiveLine[]>("active2 ist unvollstaendig.");
        string[] rows = text[..^5].Split('\n');
        if (rows[0] != Header) return Failure<ActiveLine[]>("active2 hat ein unbekanntes Format.");
        if (rows.Length - 1 > MaximumPackages) return Failure<ActiveLine[]>("active2 nennt mehr als 4096 Pakete.");
        var lines = new ActiveLine[rows.Length - 1];
        for (int index = 1; index < rows.Length; index++)
        {
            string[] parts = rows[index].StartsWith(LinePrefix, StringComparison.Ordinal) ? rows[index][LinePrefix.Length..].Split(' ') : [];
            if (parts.Length != 4 || !PackageManifest.IsValidId(parts[0]) || !PackageVersion2.TryParse(parts[1], out var version) ||
                version.ToString() != parts[1] || parts[2] is not ("requested" or "auto") || !IsSha256(parts[3]))
                return Failure<ActiveLine[]>($"active2: Zeile {index + 1} ist ungueltig.");
            if (index > 1 && string.CompareOrdinal(lines[index - 2].Id, parts[0]) >= 0)
                return Failure<ActiveLine[]>("active2 ist nicht nach Kennung sortiert oder nennt ein Paket doppelt.");
            lines[index - 1] = new ActiveLine(parts[0], version, parts[2] == "requested", parts[3]);
        }
        return ResultObjectFactory.CreateSuccess(lines);
    }

    private static byte[] Format(ActiveLine[] lines)
    {
        var text = new StringBuilder(Header).Append('\n');
        foreach (var line in lines)
            text.Append(LinePrefix).Append(line.Id).Append(' ').Append(line.Version.ToString()).Append(' ')
                .Append(line.Requested ? "requested" : "auto").Append(' ').Append(line.Sha256).Append('\n');
        return Encoding.ASCII.GetBytes(text.Append("end\n").ToString());
    }

    private ResultErr RequireKernel(PackageManifest2 manifest) =>
        manifest.MinimumKernel is { } required && required.CompareTo(kernel) > 0
            ? ResultErr.CreateFailure(ResultCode.Failure,
                $"{manifest.Id} {manifest.Version} braucht ZehOS {required} oder neuer; laufender Kernel ist {kernel}. Zuerst ZehOS aktualisieren.")
            : ResultErr.CreateSuccess();

    private ResultErr Begin()
    {
        lock (gate)
        {
            if (busy) return ResultErr.CreateFailure(ResultCode.Failure, "Paketbestand v2 wird gerade geaendert.");
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

    private void Step(string what) => FileStep?.Invoke(what);

    // Umbenennen fuer ReplaceFileStorageLocked, damit Abbruchtests jeden Zwischenstand von active2 erreichen.
    private void Move(string from, string to)
    {
        Step("verschieben " + Path.GetFileName(from) + " -> " + Path.GetFileName(to));
        File.Move(from, to);
    }

    // Cosmos-FAT bietet kein geprueftes rekursives Directory.Delete; Dateien und Ordner einzeln, jeder Schritt einzeln gesperrt.
    private void DeleteTree(string directory)
    {
        foreach (string file in StorageLock.Run(() => Directory.GetFiles(directory)))
        {
            Step("loeschen " + file);
            StorageLock.Run(() => File.Delete(file));
        }
        foreach (string child in StorageLock.Run(() => Directory.GetDirectories(directory))) DeleteTree(child);
        Step("Ordner entfernen " + directory);
        StorageLock.Run(() => Directory.Delete(directory));
    }

    private void TryDeleteTree(string directory)
    {
        try { if (StorageLock.Run(() => Directory.Exists(directory))) DeleteTree(directory); }
        catch (Exception exception) when (FileErrors.IsFileError(exception)) { /* Reconcile entfernt den Rest beim naechsten Einbinden. */ }
    }

    private string VersionDirectory(string id, PackageVersion2 version) => Path.Combine(Packages, id, version.ToString());

    private static string Key(string id, PackageVersion2 version) => id + "/" + version;

    private static bool IsSha256(string? text)
    {
        if (text == null || text.Length != 64) return false;
        foreach (char c in text) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    private static byte[] ReadSmall(string path, int maximumBytes) => StorageLock.Run(() => ReadSmallStorageLocked(path, maximumBytes));

    private static byte[] ReadSmallStorageLocked(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        if (stream.Length > maximumBytes) throw new IOException(Path.GetFileName(path) + " ist zu gross.");
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string Finish(Sha256Digest digest)
    {
        byte[] hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        return Convert.ToHexStringLower(hash);
    }

    private static ResultErr NoTrust() => ResultErr.CreateFailure(ResultCode.Failure, "Dieser Build enthaelt keinen Paketschluessel; Pakete sind deaktiviert.");
    private static ResultErr Damaged(string detail) => ResultErr.CreateFailure(ResultCode.Failure, "Paketbestand v2 ist beschaedigt und gesperrt: " + detail);
    private static ResultObject<T> Failure<T>(string message) => ResultObjectFactory.CreateFailure<T>(ResultCode.Failure, message);

    private sealed class HashingStream(Stream target, Sha256Digest digest, Action beforeWrite) : Stream
    {
        public long Written { get; private set; }
        // Das Ziel ist eine Paketdatei; jeder Block wird einzeln gesperrt, die Quelle liest ungesperrt (auch aus dem Netz).
        public override void Write(byte[] buffer, int offset, int count)
        {
            beforeWrite();
            digest.BlockUpdate(buffer, offset, count);
            StorageLock.Run(() => target.Write(buffer, offset, count));
            Written += count;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => StorageLock.Run(() => target.Flush());
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
