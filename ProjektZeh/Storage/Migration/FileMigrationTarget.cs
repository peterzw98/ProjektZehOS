using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProjektZeh.Security;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage.Migration;

/// <summary>Dateiseite eines bereits geprueften Ziel-Mounts; Cosmos liefert Pfadwaechter, Statistik und Sync.</summary>
internal sealed class FileMigrationTarget(string root, Func<ResultObject<long>> freeBytes, Func<ResultObject<long>> freeInodes,
    Func<ResultErr> sync, Func<string, bool, ResultErr> pathGuard, Action<string, string, bool>? moveFile = null) : IMigrationTarget
{
    private readonly string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private readonly Action<string, string, bool> move = moveFile ?? File.Move;

    /// <summary>Hoechstens so viele Bytes kopiert ein gesperrter Abschnitt; groessere Dateien geben die Sperre dazwischen frei.</summary>
    private const int CopyChunk = 64 * 1024;

    // Der Pfadwaechter fragt das Dateisystem ab; deshalb nur unter der Speicher-Sperre.
    private string ResolveStorageLocked(string path, bool creating = false, bool entry = false)
    {
        if (!KernelPath.IsAbsoluteNormalized(path) || (entry && path == "/"))
            throw new IOException("cause='MigrationPathInvalid' message='Kein normalisierter Zielpfad: " + path + "'");
        if (path != "/")
            foreach (string part in path[1..].Split('/'))
                if (!PathComponent.IsValid(part, allowSystemNames: true))
                    throw new IOException("cause='MigrationPathInvalid' message='Ungueltiger Zielpfad: " + path + "'");
        string physical = path == "/" ? directory : Path.GetFullPath(Path.Combine(directory, path[1..].Replace('/', Path.DirectorySeparatorChar)));
        if (physical != directory && !physical.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("cause='MigrationPathInvalid' message='Pfad verlaesst das Ziel.'");
        var guarded = FileAccessControl.RunGuard(pathGuard, physical, creating);
        // Fehlende Elternkomponente heisst "nicht vorhanden": eine Journalabsicht vor dem Anlegen (G5, blue) ist dann keine Ursache
        // fuer einen Abbruch der Wiederholung (Spec E4.4 Test 4). Symlinks und fremde Mounts bleiben harte Fehler.
        if (!guarded.IsOK() && guarded.Message.Contains("cause='Missing'", StringComparison.Ordinal)) throw new DirectoryNotFoundException(guarded.Message);
        if (!guarded.IsOK()) throw new IOException(guarded.Message);
        return physical;
    }

    public ResultErr CreateDirectory(string path)
    {
        try { StorageLock.Run(() => Directory.CreateDirectory(ResolveStorageLocked(path, creating: true))); return ResultErr.CreateSuccess(); }
        catch (Exception exception) when (IsIoError(exception)) { return ResultErr.CreateFromException(exception); }
    }

    public ResultErr WriteFile(string path, Stream content)
    {
        try
        {
            var (destination, temporary, backup, fresh) = StorageLock.Run(() => PrepareWriteStorageLocked(path));
            if (fresh)
            {
                CopyToNew(destination, content);
                return ResultErr.CreateSuccess();
            }

            CopyToNew(temporary, content);
            // Umbenennen, Sichern und Wiederherstellen als ein Abschnitt: kein anderer Zugriff sieht einen halben Austausch.
            StorageLock.Run(() => InstallStorageLocked(destination, temporary, backup));
            return ResultErr.CreateSuccess();
        }
        // Temp-/Sicherungsreste bleiben als journalisierte eigene Spuren fuer eine Wiederholung erhalten.
        catch (Exception exception) when (IsIoError(exception)) { return ResultErr.CreateFromException(exception); }
    }

    /// <summary>Zielpfade und Art des Schreibens; fresh = direkt unter dem endgueltigen Namen anlegen.</summary>
    private (string Destination, string Temporary, string Backup, bool Fresh) PrepareWriteStorageLocked(string path)
    {
        string destination = ResolveStorageLocked(path, creating: true, entry: true);
        string temporary = ResolveStorageLocked(path + IMigrationTarget.TemporarySuffix, creating: true, entry: true);
        string backup = ResolveStorageLocked(path + IMigrationTarget.BackupSuffix, creating: true, entry: true);
        if (Directory.Exists(destination)) throw new IOException("Zieldatei ist ein Verzeichnis: " + path);
        // Neue Datei direkt unter dem endgueltigen Namen (Review peter-review P2-1): Cosmos-Rename legt auf ext2 einen
        // neuen Eintrag an und entfernt den alten; liegt der Temp-Eintrag als erster in einem neuen Verzeichnisblock,
        // beschaedigt das Entfernen das Verzeichnis (e2fsck Exit 12). Gegen einen Absturz schuetzt hier schon die
        // W-Zeile im Journal: die Wiederholung loescht die halbe Datei. Temp und Rename nur noch beim Ersetzen.
        if (!File.Exists(destination) && !File.Exists(backup)) return (destination, temporary, backup, true);

        // Nur eine vorhandene Hauptfassung erlaubt es, eine aeltere, eigene Sicherung zu verwerfen.
        if (File.Exists(destination)) File.Delete(backup);
        return (destination, temporary, backup, false);
    }

    private void InstallStorageLocked(string destination, string temporary, string backup)
    {
        bool saved = File.Exists(backup), installed = false;
        try
        {
            if (File.Exists(destination)) { move(destination, backup, false); saved = true; }
            move(temporary, destination, false);
            installed = true;
            if (saved) File.Delete(backup);
        }
        catch (Exception exception) when (IsIoError(exception))
        {
            if (saved && File.Exists(backup))
            {
                try
                {
                    if (installed) move(destination, temporary, false);
                    move(backup, destination, false);
                }
                catch (Exception restore) when (IsIoError(restore))
                {
                    throw new IOException("Wiederherstellung fehlgeschlagen; Original unter '" + backup + "'.", new AggregateException(exception, restore));
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Legt <paramref name="physical"/> neu an und kopiert <paramref name="content"/> stueckweise hinein. Die Sperre gilt je
    /// Stueck (Lesen der Quelle und Schreiben), damit eine grosse Datei Hauptschleife und Worker nicht fuer die ganze Kopie
    /// aufhaelt; Oeffnen, Flush und Schliessen sind eigene Abschnitte.
    /// </summary>
    private static void CopyToNew(string physical, Stream content)
    {
        var output = StorageLock.Run(() => new FileStream(physical, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1));
        try
        {
            byte[] buffer = new byte[CopyChunk];
            int copied;
            do copied = StorageLock.Run(() => CopyChunkStorageLocked(content, output, buffer));
            while (copied > 0);
            StorageLock.Run(() => output.Flush(true));
        }
        finally
        {
            StorageLock.Run(output.Dispose);
        }
    }

    private static int CopyChunkStorageLocked(Stream content, Stream output, byte[] buffer)
    {
        int read = content.Read(buffer, 0, buffer.Length);
        if (read > 0) output.Write(buffer, 0, read);
        return read;
    }

    public ResultErr AppendJournal(string line, bool flush)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            StorageLock.Run(() =>
            {
                string path = ResolveStorageLocked(MigrationJournal.Path, creating: true, entry: true);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 1);
                stream.Write(bytes);
                if (flush) stream.Flush(true);
            });
            if (!flush) return ResultErr.CreateSuccess();
            // Cosmos schreibt beim ersten Sync noch Metadaten nach seinem Flush; die zweite Schranke macht sie dauerhaft.
            var result = sync();
            return result.IsOK() ? sync() : result;
        }
        catch (Exception exception) when (IsIoError(exception)) { return ResultErr.CreateFromException(exception); }
    }

    public ResultErr Delete(string path)
    {
        try
        {
            StorageLock.Run(() =>
            {
                string physical = ResolveStorageLocked(path, entry: true);
                FileAttributes attributes = File.GetAttributes(physical);
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(physical);
                else File.Delete(physical);
            });
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsMissing(exception)) { return ResultErr.CreateFailure(ResultCode.NotFound, exception.Message); }
        catch (Exception exception) when (IsIoError(exception)) { return ResultErr.CreateFromException(exception); }
    }

    public ResultObject<Stream> ReadBack(string path)
    {
        try
        {
            // Der Aufrufer liest den Strom spaeter; StorageLockReadStream sperrt dann jedes Lesestueck einzeln.
            try { return ResultObjectFactory.CreateSuccess<Stream>(StorageLock.Run(() => new StorageLockReadStream(File.OpenRead(ResolveStorageLocked(path, creating: true, entry: true))))); }
            catch (Exception exception) when (IsMissing(exception) && path == LayoutMarker.Path)
            {
                // Ein Absturz zwischen Sichern und Rename darf den alten Marker lesbar lassen, ohne jetzt zu schreiben.
                return ResultObjectFactory.CreateSuccess<Stream>(StorageLock.Run(() => new StorageLockReadStream(File.OpenRead(ResolveStorageLocked(path + IMigrationTarget.BackupSuffix, creating: true, entry: true)))));
            }
        }
        catch (Exception exception) when (IsMissing(exception)) { return ResultObjectFactory.CreateFailure<Stream>(ResultCode.NotFound, exception.Message); }
        catch (Exception exception) when (IsIoError(exception)) { return ResultObjectFactory.CreateFromException<Stream>(exception); }
    }

    public ResultObject<string[]> List()
    {
        try
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            pending.Push("/");
            while (pending.Count != 0)
            {
                string parent = pending.Pop();
                // Ein gesperrter Abschnitt je Verzeichnis, nicht fuer den ganzen Baum.
                StorageLock.Run(() => ListDirectoryStorageLocked(parent, result, pending));
            }
            result.Sort(StringComparer.Ordinal);
            return ResultObjectFactory.CreateSuccess(result.ToArray());
        }
        catch (Exception exception) when (IsIoError(exception)) { return ResultObjectFactory.CreateFromException<string[]>(exception); }
    }

    private void ListDirectoryStorageLocked(string parent, List<string> result, Stack<string> pending)
    {
        foreach (string child in Directory.GetFileSystemEntries(ResolveStorageLocked(parent)))
        {
            string path = KernelPath.Combine(parent, Path.GetFileName(child));
            FileAttributes attributes = File.GetAttributes(ResolveStorageLocked(path));
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Symlink im Migrationsziel: " + path);
            result.Add(path);
            // Einschliesslich erzeugter Eltern bleibt ein gueltiger Plan unter dem doppelten Quellinventarlimit.
            if (result.Count > 2 * MigrationInventory.MaximumEntries + LayoutMigration.InstallerEntries.Length)
                throw new IOException("Zu viele Eintraege im Migrationsziel.");
            if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
        }
    }

    public ResultObject<long> FreeBytes() => freeBytes();
    public ResultObject<long> FreeInodes() => freeInodes();
    public ResultErr Sync() => sync();
    private static bool IsMissing(Exception exception) => exception is FileNotFoundException or DirectoryNotFoundException;
    private static bool IsIoError(Exception exception) => exception is IOException or UnauthorizedAccessException;
}

/// <summary>
/// Lesestrom ueber einer geoeffneten Datei, der jeden Zugriff einzeln unter <see cref="StorageLock"/> ausfuehrt (Migration).
/// Die Quellpruefung sieht Lesen auf offenen Stroemen nicht; so haelt eine grosse Datei die Sperre nur je Lesestueck
/// (hoechstens 1 MiB) und nicht vom Oeffnen bis zum Schliessen. Schliessen ist ebenfalls ein Plattenzugriff (Cosmos-FAT
/// schreibt beim Release).
/// </summary>
internal sealed class StorageLockReadStream(Stream inner) : Stream
{
    private const int MaximumRead = 1024 * 1024;

    /// <summary>Oeffnet <paramref name="path"/> lesend unter der Sperre.</summary>
    public static StorageLockReadStream OpenRead(string path) => StorageLock.Run(() => new StorageLockReadStream(File.OpenRead(path)));

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => StorageLock.Run(() => inner.Length);
    public override long Position { get => StorageLock.Run(() => inner.Position); set => StorageLock.Run(() => inner.Position = value); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => StorageLock.Run(() => inner.Seek(offset, origin));
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // Read(Span) faellt auf diese Ueberladung zurueck (Stream-Basisklasse); ein Span laesst sich nicht im Lambda halten.
    public override int Read(byte[] buffer, int offset, int count) =>
        StorageLock.Run(() => inner.Read(buffer, offset, Math.Min(count, MaximumRead)));

    protected override void Dispose(bool disposing)
    {
        if (disposing) StorageLock.Run(inner.Dispose);
        base.Dispose(disposing);
    }
}
