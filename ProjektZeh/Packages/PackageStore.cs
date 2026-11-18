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

internal sealed record PackageProgress(string Phase, string Path, long CompletedBytes, long TotalBytes);

internal sealed record InstalledPackage(PackageManifest Manifest, string Directory)
{
    public string FilesDirectory => Path.Combine(Directory, "files");
}

/// <summary>
/// Geschuetzter Paketbestand unter einem <c>.zehos-</c>-Verzeichnis, das der normale Dateidienst nicht freigibt. Aufbau:
/// <c>apps/&lt;id&gt;/&lt;version&gt;/{manifest,manifest.sig,files/...}</c> und je Paket ein Marker <c>active</c> mit der
/// aktiven Version. Nur der Marker veroeffentlicht eine Version; ein unvollstaendiges Versionsverzeichnis ist nie startbar.
/// Eine zweite Installed-Datenbank gibt es nicht: Marker plus signiertes Manifest sind maszgeblich.
/// </summary>
internal sealed class PackageStore(string root, PackageTrust? trust, UpdateVersion kernel, Func<IDisposable?>? tryBeginWrite = null)
{
    public const string DefaultSource = "https://packages.pjoter.de/";
    private const int BufferBytes = 64 * 1024;
    private const string NewSuffix = ".new";
    private readonly object gate = new();
    private readonly Dictionary<string, int> inUse = new(StringComparer.Ordinal);
    private readonly HashSet<string> busy = new(StringComparer.Ordinal);
    // Seit dem Boot vollstaendig gepruefte Versionen ("id/version"). Der normale Dateidienst erreicht .zehos-Pfade nicht;
    // im laufenden Kernel aendert nur dieser Store die Dateien. Nach einem Neustart wird wieder geprueft.
    private readonly HashSet<string> verified = new(StringComparer.Ordinal);

    private string Apps => Path.Combine(root, "apps");
    private string CatalogPath => Path.Combine(root, "catalog.signed");
    private string SourcePath => Path.Combine(root, "source");

    public bool HasTrust => trust != null;

    public ResultObject<InstalledPackage[]> ReadInstalled()
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<InstalledPackage[]>(NoTrust());
        try
        {
            var installed = new List<InstalledPackage>();
            string[]? directories = StorageLock.Run(() => Directory.Exists(Apps) ? Directory.GetDirectories(Apps) : null);
            if (directories == null) return ResultObjectFactory.CreateSuccess(installed.ToArray());
            foreach (string directory in directories)
            {
                string id = Path.GetFileName(directory);
                if (!PackageManifest.IsValidId(id)) continue;
                // Beschaedigte Pakete bleiben gesperrt und unsichtbar; pkg list meldet sie ueber ReadActive einzeln.
                var active = ReadActive(id);
                if (active.IsOK() && active.Value != null) installed.Add(active.Value);
            }
            installed.Sort(static (left, right) => string.CompareOrdinal(left.Manifest.Id, right.Manifest.Id));
            return ResultObjectFactory.CreateSuccess(installed.ToArray());
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<InstalledPackage[]>(exception);
        }
    }

    /// <summary>Liefert die aktive Version (ohne Marker: Erfolg ohne Wert) und prueft Signatur und Markerbindung.</summary>
    public ResultObject<InstalledPackage> ReadActive(string id)
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<InstalledPackage>(NoTrust());
        try
        {
            string marker = Path.Combine(Apps, id, "active");
            if (!StorageLock.Run(() => File.Exists(marker))) return ResultObjectFactory.CreateSuccess<InstalledPackage>(null);
            byte[] text = ReadSmall(marker, 32);
            if (!UpdateVersion.TryParse(Encoding.ASCII.GetString(text).TrimEnd('\n'), out var version)) return Damaged<InstalledPackage>(id);
            string directory = Path.Combine(Apps, id, version.ToString());
            byte[] manifestBytes = ReadSmall(Path.Combine(directory, "manifest"), PackageManifest.MaximumBytes);
            byte[] signature = ReadSmall(Path.Combine(directory, "manifest.sig"), PackageTrust.SignatureBytes);
            var manifest = PackageManifest.Parse(manifestBytes);
            if (!trust.Verify(manifestBytes, signature) || !manifest.IsOK() || manifest.Value!.Id != id || manifest.Value.Version != version)
                return Damaged<InstalledPackage>(id);
            return ResultObjectFactory.CreateSuccess(new InstalledPackage(manifest.Value, directory));
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return Damaged<InstalledPackage>(id, exception.Message);
        }
    }

    /// <summary>Sichert die aktive Version gegen Entfernen und Aktualisieren, bis <see cref="Release"/> folgt.</summary>
    public ResultObject<InstalledPackage> Acquire(string id)
    {
        lock (gate)
        {
            if (busy.Contains(id)) return ResultObjectFactory.CreateFailure<InstalledPackage>(ResultCode.Failure, $"Paket {id} wird gerade geaendert.");
            var active = ReadActive(id);
            if (!active.IsOK()) return active;
            if (active.Value == null) return ResultObjectFactory.CreateFailure<InstalledPackage>(ResultCode.NotFound, $"Paket {id} ist nicht installiert.");
            // Nach einer Kernelrueckstufung liegt eine neuere Paketversion vor, deren Hostfunktionen fehlen koennen.
            var supported = RequireKernel(active.Value.Manifest);
            if (!supported.IsOK()) return ResultObjectFactory.CreateFailure<InstalledPackage>(supported);
            inUse[id] = inUse.GetValueOrDefault(id) + 1;
            return active;
        }
    }

    public void Release(string id)
    {
        lock (gate)
        {
            if (!inUse.TryGetValue(id, out int count)) return;
            if (count <= 1) inUse.Remove(id);
            else inUse[id] = count - 1;
        }
    }

    /// <summary>
    /// Wie <see cref="VerifyFiles"/>, aber hoechstens einmal je Version und Boot: unter Emulation dauert das Hashen der
    /// Doom-WAD vor jedem Start sonst bis zu 30 s.
    /// </summary>
    public ResultErr VerifyOnce(InstalledPackage package, CancellationToken cancellation)
    {
        string key = package.Manifest.Id + "/" + package.Manifest.Version;
        lock (gate) if (verified.Contains(key)) return ResultErr.CreateSuccess();
        var result = VerifyFiles(package, cancellation);
        if (result.IsOK()) lock (gate) verified.Add(key);
        return result;
    }

    /// <summary>Prueft alle gespeicherten Dateien erneut gegen das signierte Manifest; eine spaetere Veraenderung sperrt den Start.</summary>
    public static ResultErr VerifyFiles(InstalledPackage package, CancellationToken cancellation)
    {
        try
        {
            byte[] buffer = new byte[BufferBytes];
            foreach (var file in package.Manifest.Files)
            {
                bool intact;
                string path = Path.Combine(package.FilesDirectory, file.Path);
                // Grosse Dateien (Doom-WAD) nicht unter einer einzigen Sperre hashen: Oeffnen, jeder Leseblock und Schliessen
                // nehmen die Speicher-Sperre einzeln, damit andere Threads dazwischen auf die Platte zugreifen koennen.
                var stream = StorageLock.Run(() => new FileStream(path, FileMode.Open, FileAccess.Read));
                try { intact = Matches(stream, file, buffer, cancellation); }
                finally { StorageLock.Run(() => stream.Dispose()); }
                if (!intact) return Modified(file.Path);
            }
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "Paketdateien nicht lesbar: " + exception.Message);
        }
    }

    private static bool Matches(FileStream stream, PackageFile file, byte[] buffer, CancellationToken cancellation)
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

    /// <summary>
    /// Installiert oder aktualisiert aus <paramref name="source"/> unter <paramref name="prefix"/> (manifest, manifest.sig,
    /// files/). Mit <paramref name="expectedManifestSha256"/> muss das Manifest exakt dem Katalogeintrag entsprechen.
    /// Rueckstufungen und Aenderungen an laufenden Paketen werden abgelehnt.
    /// Der interne Fortschrittsempfaenger speichert nur den Zustand und darf nicht werfen; Anzeige und Formatierung
    /// erfolgen spaeter in der Ereignisschleife. Fertig wird erst nach dem Veroeffentlichen gemeldet.
    /// </summary>
    public ResultObject<PackageManifest> Install(IPackageSource source, string prefix, string? expectedManifestSha256, CancellationToken cancellation, Action<PackageProgress>? progress = null)
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<PackageManifest>(NoTrust());
        progress?.Invoke(new("Manifest laden", prefix, 0, 0));
        var manifestBytes = source.Read(prefix + "manifest", PackageManifest.MaximumBytes, cancellation);
        if (!manifestBytes.IsOK()) return ResultObjectFactory.CreateFailure<PackageManifest>(manifestBytes.Status);
        var signature = source.Read(prefix + "manifest.sig", PackageTrust.SignatureBytes, cancellation);
        if (!signature.IsOK()) return ResultObjectFactory.CreateFailure<PackageManifest>(signature.Status);
        if (expectedManifestSha256 != null && !string.Equals(PackageTrust.Sha256Hex(manifestBytes.Value!), expectedManifestSha256, StringComparison.Ordinal))
            return ResultObjectFactory.CreateFailure<PackageManifest>(ResultCode.InvalidArgument, "Manifest passt nicht zum signierten Katalog.");
        progress?.Invoke(new("Signatur pruefen", prefix, 0, 0));
        if (!trust.Verify(manifestBytes.Value!, signature.Value!))
            return ResultObjectFactory.CreateFailure<PackageManifest>(ResultCode.InvalidArgument, "Paketsignatur ungueltig.");
        var parsed = PackageManifest.Parse(manifestBytes.Value!);
        if (!parsed.IsOK()) return parsed;
        var manifest = parsed.Value!;
        // Vor jedem Dateizugriff pruefen: Eine abgelehnte Version laedt nichts und laesst die installierte unberuehrt.
        var supported = RequireKernel(manifest);
        if (!supported.IsOK()) return ResultObjectFactory.CreateFailure<PackageManifest>(supported);
        var begun = Begin(manifest.Id);
        if (!begun.IsOK()) return ResultObjectFactory.CreateFailure<PackageManifest>(begun);
        try { return Publish(source, prefix, parsed, signature.Value!, progress, cancellation); }
        catch (OperationCanceledException) { return ResultObjectFactory.CreateFailure<PackageManifest>(ResultCode.Failure, "Installation abgebrochen."); }
        catch (Exception exception) { return ResultObjectFactory.CreateFromException<PackageManifest>(exception); }
        finally { lock (gate) busy.Remove(manifest.Id); }
    }

    private ResultObject<PackageManifest> Publish(IPackageSource source, string prefix, ResultObject<PackageManifest> parsed, byte[] signature,
        Action<PackageProgress>? progress, CancellationToken cancellation)
    {
        using var writer = BeginWrite();
        var manifest = parsed.Value!;
        var current = ReadActive(manifest.Id);
        InstalledPackage? previous = current.IsOK() ? current.Value : null;
        // Auch bei beschaedigtem Paket gilt die Version aus dem Marker als Rueckstufungsgrenze, sofern es diese Version
        // tatsaechlich gibt; ein Marker ohne Versionsordner ist Datenmuell und darf eine Reparatur nicht verhindern.
        UpdateVersion? marked = ReadMarker(Path.Combine(Apps, manifest.Id));
        if (marked is { } markedVersion && !StorageLock.Run(() => Directory.Exists(Path.Combine(Apps, manifest.Id, markedVersion.ToString())))) marked = null;
        UpdateVersion? installed = previous?.Manifest.Version ?? marked;
        int order = installed?.CompareTo(manifest.Version) ?? -1;
        if (order > 0)
            return ResultObjectFactory.CreateFailure<PackageManifest>(ResultCode.Failure, $"Rueckstufung abgelehnt: installiert ist {manifest.Id} {installed}.");
        // Dieselbe Version nur dann ablehnen, wenn sie intakt ist; sonst repariert die Neuinstallation veraenderte Dateien.
        if (order == 0 && previous != null && VerifyFiles(previous, cancellation).IsOK())
            return ResultObjectFactory.CreateFailure<PackageManifest>(ResultCode.Failure, $"{manifest.Id} {manifest.Version} ist bereits installiert.");
        string target = Path.Combine(Apps, manifest.Id, manifest.Version.ToString());
        ResultErr written;
        try { written = WriteVersion(source, prefix, manifest, signature, target, progress, cancellation); }
        catch (Exception)
        {
            // Auch bei Abbruch oder Dateifehler keine halbe Version liegen lassen.
            TryDeleteTree(target);
            throw;
        }
        if (!written.IsOK())
        {
            TryDeleteTree(target);
            return ResultObjectFactory.CreateFailure<PackageManifest>(written);
        }
        StorageLock.Run(() => ReplaceFileStorageLocked(Path.Combine(Apps, manifest.Id, "active"), Encoding.ASCII.GetBytes(manifest.Version + "\n")));
        // WriteVersion hat jede Datei beim Schreiben gehasht; ein erneuter Durchlauf vor dem ersten Start ist unnoetig.
        lock (gate) verified.Add(manifest.Id + "/" + manifest.Version);
        // Bleibt die alte Version wegen eines Fehlers liegen, entfernt Reconcile sie beim naechsten Start.
        if (previous != null && !string.Equals(previous.Directory, target, StringComparison.Ordinal)) TryDeleteTree(previous.Directory);
        long total = 0;
        foreach (var file in manifest.Files) total += file.Size;
        progress?.Invoke(new("Fertig", manifest.Id, total, total));
        return parsed;
    }

    private static ResultErr WriteVersion(IPackageSource source, string prefix, PackageManifest manifest, byte[] signature, string target, Action<PackageProgress>? progress, CancellationToken cancellation)
    {
        // Reste eines abgebrochenen Versuchs derselben Version sind nie aktiv gewesen und werden verworfen.
        string files = Path.Combine(target, "files");
        StorageLock.Run(() =>
        {
            if (Directory.Exists(target)) DeleteTreeStorageLocked(target);
            Directory.CreateDirectory(files);
        });
        long total = 0, completed = 0;
        foreach (var file in manifest.Files) total += file.Size;
        foreach (var file in manifest.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke(new("Download", manifest.Id + "/" + file.Path, completed, total));
            string path = Path.Combine(files, file.Path.Replace('/', Path.DirectorySeparatorChar));
            var digest = new Sha256Digest();
            ResultErr copied;
            // Die Quelle kann aus dem Netz lesen: nur Oeffnen, jeder Schreibblock (HashingStream) und Schliessen sperren.
            var stream = StorageLock.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            });
            var hashing = new HashingStream(stream, digest, count =>
            {
                completed += count;
                progress?.Invoke(new("Download", manifest.Id + "/" + file.Path, completed, total));
            });
            try { copied = source.Copy(prefix + "files/" + file.Path, file.Size, hashing, cancellation); }
            finally { StorageLock.Run(() => stream.Dispose()); }
            if (copied.IsOK() && hashing.Written != file.Size) copied = ResultErr.CreateFailure(ResultCode.Failure, file.Path + " ist unvollstaendig.");
            if (!copied.IsOK()) return copied;
            progress?.Invoke(new("Datei pruefen", manifest.Id + "/" + file.Path, completed, total));
            if (!string.Equals(Finish(digest), file.Sha256, StringComparison.Ordinal))
                return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Pruefsumme falsch: " + file.Path);
        }
        StorageLock.Run(() =>
        {
            File.WriteAllBytes(Path.Combine(target, "manifest"), manifest.Bytes);
            File.WriteAllBytes(Path.Combine(target, "manifest.sig"), signature);
        });
        return ResultErr.CreateSuccess();
    }

    private ResultErr RequireKernel(PackageManifest manifest) =>
        manifest.MinimumKernel is { } required && required.CompareTo(kernel) > 0
            ? ResultErr.CreateFailure(ResultCode.Failure,
                $"{manifest.Id} {manifest.Version} braucht ZehOS {required} oder neuer; laufender Kernel ist {kernel}. Zuerst ZehOS aktualisieren.")
            : ResultErr.CreateSuccess();

    /// <summary>Entfernt nur die eigenen Paketdateien; Benutzerdaten liegen nicht im Paketbestand.</summary>
    public ResultErr Remove(string id)
    {
        if (!PackageManifest.IsValidId(id)) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Ungueltige Paketkennung.");
        var begun = Begin(id);
        if (!begun.IsOK()) return begun;
        string directory = Path.Combine(Apps, id);
        try
        {
            using var writer = BeginWrite();
            if (!StorageLock.Run(() => File.Exists(Path.Combine(directory, "active")))) return ResultErr.CreateFailure(ResultCode.NotFound, $"Paket {id} ist nicht installiert.");
            // Zuerst den Marker loeschen: ab dann ist das Paket nicht mehr startbar, auch wenn das Aufraeumen scheitert.
            StorageLock.Run(() => File.Delete(Path.Combine(directory, "active")));
            StorageLock.Run(() => DeleteTreeStorageLocked(directory));
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception)
        {
            return ResultErr.CreateFailure(ResultCode.Failure, $"Paket {id} nicht vollstaendig entfernt: " + exception.Message);
        }
        finally { lock (gate) busy.Remove(id); }
    }

    public ResultObject<PackageCatalog> ReadCatalog()
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<PackageCatalog>(NoTrust());
        try
        {
            if (!StorageLock.Run(() => File.Exists(CatalogPath))) return ResultObjectFactory.CreateSuccess<PackageCatalog>(null);
            byte[] stored = ReadSmall(CatalogPath, PackageTrust.SignatureBytes + PackageCatalog.MaximumBytes);
            if (stored.Length < PackageTrust.SignatureBytes) return DamagedCatalog();
            byte[] signature = stored[..PackageTrust.SignatureBytes], catalog = stored[PackageTrust.SignatureBytes..];
            if (!trust.Verify(catalog, signature)) return DamagedCatalog();
            var parsed = PackageCatalog.Parse(catalog);
            return parsed.IsOK() ? parsed : DamagedCatalog();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<PackageCatalog>(exception);
        }
    }

    /// <summary>Nimmt einen signierten Katalog nur an, wenn seine Generation nicht kleiner als die gespeicherte ist.</summary>
    public ResultObject<PackageCatalog> AcceptCatalog(byte[] catalog, byte[] signature)
    {
        if (trust == null) return ResultObjectFactory.CreateFailure<PackageCatalog>(NoTrust());
        if (!trust.Verify(catalog, signature)) return ResultObjectFactory.CreateFailure<PackageCatalog>(ResultCode.InvalidArgument, "Katalogsignatur ungueltig.");
        var parsed = PackageCatalog.Parse(catalog);
        if (!parsed.IsOK()) return parsed;
        lock (gate)
        {
            var stored = ReadCatalog();
            // Ein beschaedigter gespeicherter Katalog sperrt keine Aktualisierung; die Rueckstufungssperre der Pakete bleibt.
            if (stored.IsOK() && stored.Value != null)
            {
                if (parsed.Value!.Generation < stored.Value.Generation)
                    return ResultObjectFactory.CreateFailure<PackageCatalog>(ResultCode.Failure,
                        $"Katalog ist aelter als der bekannte Stand (Generation {parsed.Value.Generation} < {stored.Value.Generation}).");
            }
            try
            {
                using var writer = BeginWrite();
                byte[] combined = new byte[signature.Length + catalog.Length];
                signature.CopyTo(combined, 0);
                catalog.CopyTo(combined, signature.Length);
                StorageLock.Run(() =>
                {
                    Directory.CreateDirectory(root);
                    ReplaceFileStorageLocked(CatalogPath, combined);
                });
                return parsed;
            }
            catch (Exception exception) when (FileErrors.IsFileError(exception))
            {
                return ResultObjectFactory.CreateFromException<PackageCatalog>(exception);
            }
        }
    }

    public ResultObject<string> ReadSource()
    {
        try
        {
            if (!StorageLock.Run(() => File.Exists(SourcePath))) return ResultObjectFactory.CreateSuccess(DefaultSource);
            return ResultObjectFactory.CreateSuccess(Encoding.ASCII.GetString(ReadSmall(SourcePath, 2048)).TrimEnd('\n'));
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<string>(exception);
        }
    }

    public ResultErr WriteSource(string url)
    {
        if (!IsValidSource(url)) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Quelle muss eine HTTPS-URL ohne Zugangsdaten sein und auf / enden.");
        try
        {
            using var writer = BeginWrite();
            StorageLock.Run(() =>
            {
                Directory.CreateDirectory(root);
                ReplaceFileStorageLocked(SourcePath, Encoding.ASCII.GetBytes(url + "\n"));
            });
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultErr.CreateFromException(exception);
        }
    }

    private IDisposable? BeginWrite() => tryBeginWrite == null ? null :
        tryBeginWrite() ?? throw new IOException(MigrationWriteGate.BusyMessage);

    public static bool IsValidSource(string url) =>
        url.Length <= 512 && url.EndsWith('/') && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    /// <summary>
    /// Raeumt nach einem Abbruch auf: halb ersetzte Dateien abschliessen, nicht aktive Versionen und Pakete ohne Marker
    /// loeschen. Laeuft beim Einbinden des Datenlaufwerks, bevor Pakete gestartet werden koennen.
    /// </summary>
    public ResultErr Reconcile()
    {
        try
        {
            using var writer = BeginWrite();
            StorageLock.Run(() => RestorePendingStorageLocked(CatalogPath));
            StorageLock.Run(() => RestorePendingStorageLocked(SourcePath));
            string[]? directories = StorageLock.Run(() => Directory.Exists(Apps) ? Directory.GetDirectories(Apps) : null);
            if (directories == null) return ResultErr.CreateSuccess();
            foreach (string directory in directories)
            {
                string id = Path.GetFileName(directory);
                lock (gate) if (busy.Contains(id) || inUse.ContainsKey(id)) continue;
                string marker = Path.Combine(directory, "active");
                // Ohne Marker wurde nie etwas veroeffentlicht: der Ordner ist ein Rest und darf weg.
                bool published = StorageLock.Run(() =>
                {
                    RestorePendingStorageLocked(marker);
                    if (File.Exists(marker)) return true;
                    DeleteTreeStorageLocked(directory);
                    return false;
                });
                if (!published) continue;
                // Ein unlesbarer Marker oder einer ohne Zielordner loescht nichts; das Paket bleibt gesperrt statt Daten zu verlieren.
                if (ReadMarker(directory)?.ToString() is not { } active) continue;
                StorageLock.Run(() =>
                {
                    if (!Directory.Exists(Path.Combine(directory, active))) return;
                    foreach (string version in Directory.GetDirectories(directory))
                        if (!string.Equals(Path.GetFileName(version), active, StringComparison.Ordinal)) DeleteTreeStorageLocked(version);
                });
            }
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "Paketbestand nicht aufgeraeumt: " + exception.Message);
        }
    }

    // Version aus dem Marker eines Paketordners; null bei fehlendem, zu grossem oder ungueltigem Marker.
    private static UpdateVersion? ReadMarker(string directory)
    {
        string marker = Path.Combine(directory, "active");
        try
        {
            if (!StorageLock.Run(() => File.Exists(marker))) return null;
            return UpdateVersion.TryParse(Encoding.ASCII.GetString(ReadSmall(marker, 32)).TrimEnd('\n'), out var version) ? version : null;
        }
        catch (Exception exception) when (FileErrors.IsFileError(exception))
        {
            return null;
        }
    }

    private ResultErr Begin(string id)
    {
        lock (gate)
        {
            if (busy.Contains(id)) return ResultErr.CreateFailure(ResultCode.Failure, $"Paket {id} wird bereits geaendert.");
            if (inUse.ContainsKey(id)) return ResultErr.CreateFailure(ResultCode.Failure, $"Paket {id} laeuft gerade; zuerst die App schliessen.");
            busy.Add(id);
            return ResultErr.CreateSuccess();
        }
    }

    // Neue Fassung vollstaendig schreiben, erst dann die alte loeschen; RestorePendingStorageLocked schliesst einen Abbruch dazwischen ab.
    private static void ReplaceFileStorageLocked(string path, byte[] bytes)
    {
        string temporary = path + NewSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(temporary)) File.Delete(temporary);
        File.WriteAllBytes(temporary, bytes);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }

    private static void RestorePendingStorageLocked(string path)
    {
        string temporary = path + NewSuffix;
        if (!File.Exists(temporary)) return;
        if (File.Exists(path)) File.Delete(temporary);
        else File.Move(temporary, path);
    }

    // Cosmos-FAT bietet kein geprueftes rekursives Directory.Delete; deshalb Dateien und Ordner einzeln entfernen.
    private static void DeleteTreeStorageLocked(string directory)
    {
        foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
        foreach (string child in Directory.GetDirectories(directory)) DeleteTreeStorageLocked(child);
        Directory.Delete(directory);
    }

    private static void TryDeleteTree(string directory)
    {
        try { StorageLock.Run(() => { if (Directory.Exists(directory)) DeleteTreeStorageLocked(directory); }); }
        catch (Exception exception) when (FileErrors.IsFileError(exception)) { /* Reconcile entfernt den Rest beim naechsten Einbinden. */ }
    }

    // Kleine Dateien (hoechstens Kataloggroesse, 256 KiB) werden in einem gesperrten Abschnitt gelesen.
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
    private static ResultErr Modified(string path) => ResultErr.CreateFailure(ResultCode.Failure, $"Paketdatei {path} wurde veraendert; Start gesperrt.");
    private static ResultObject<T> Damaged<T>(string id, string? detail = null) =>
        ResultObjectFactory.CreateFailure<T>(ResultCode.Failure, $"Paket {id} ist beschaedigt und gesperrt{(detail == null ? "" : ": " + detail)}. Neu installieren oder entfernen.");
    private static ResultObject<PackageCatalog> DamagedCatalog() =>
        ResultObjectFactory.CreateFailure<PackageCatalog>(ResultCode.Failure, "Gespeicherter Katalog beschaedigt; pkg update ausfuehren.");

    private sealed class HashingStream(Stream target, Sha256Digest digest, Action<int> progress) : Stream
    {
        public long Written { get; private set; }
        // Das Ziel ist eine Paketdatei; jeder Block wird einzeln gesperrt, die Quelle liest ungesperrt (auch aus dem Netz).
        public override void Write(byte[] buffer, int offset, int count)
        {
            digest.BlockUpdate(buffer, offset, count);
            StorageLock.Run(() => target.Write(buffer, offset, count));
            Written += count;
            progress(count);
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
