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

    private string Resolve(string path, bool creating = false, bool entry = false)
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
        if (!guarded.IsOK()) throw new IOException(guarded.Message);
        return physical;
    }

    public ResultErr CreateDirectory(string path)
    {
        try { Directory.CreateDirectory(Resolve(path, creating: true)); return ResultErr.CreateSuccess(); }
        catch (Exception exception) when (IsIoError(exception)) { return ResultErr.CreateFromException(exception); }
    }

    public ResultErr WriteFile(string path, Stream content)
    {
        try
        {
            string destination = Resolve(path, creating: true, entry: true);
            string temporary = Resolve(path + IMigrationTarget.TemporarySuffix, creating: true, entry: true);
            string backup = Resolve(path + IMigrationTarget.BackupSuffix, creating: true, entry: true);
            if (Directory.Exists(destination)) throw new IOException("Zieldatei ist ein Verzeichnis: " + path);
            // Nur eine vorhandene Hauptfassung erlaubt es, eine aeltere, eigene Sicherung zu verwerfen.
            if (File.Exists(destination)) File.Delete(backup);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                content.CopyTo(output, 64 * 1024);
                output.Flush(true);
            }
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
            return ResultErr.CreateSuccess();
        }
        // Temp-/Sicherungsreste bleiben als journalisierte eigene Spuren fuer eine Wiederholung erhalten.
        catch (Exception exception) when (IsIoError(exception)) { return ResultErr.CreateFromException(exception); }
    }

    public ResultErr AppendJournal(string line, bool flush)
    {
        try
        {
            string path = Resolve(MigrationJournal.Path, creating: true, entry: true);
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, 1))
            {
                stream.Write(bytes);
                if (flush) stream.Flush(true);
            }
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
            string physical = Resolve(path, entry: true);
            FileAttributes attributes = File.GetAttributes(physical);
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(physical);
            else File.Delete(physical);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsMissing(exception)) { return ResultErr.CreateFailure(ResultCode.NotFound, exception.Message); }
        catch (Exception exception) when (IsIoError(exception)) { return ResultErr.CreateFromException(exception); }
    }

    public ResultObject<Stream> ReadBack(string path)
    {
        try
        {
            try { return ResultObjectFactory.CreateSuccess<Stream>(File.OpenRead(Resolve(path, creating: true, entry: true))); }
            catch (Exception exception) when (IsMissing(exception) && path == LayoutMarker.Path)
            {
                // Ein Absturz zwischen Sichern und Rename darf den alten Marker lesbar lassen, ohne jetzt zu schreiben.
                return ResultObjectFactory.CreateSuccess<Stream>(File.OpenRead(Resolve(path + IMigrationTarget.BackupSuffix, creating: true, entry: true)));
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
                foreach (string child in Directory.GetFileSystemEntries(Resolve(parent)))
                {
                    string path = KernelPath.Combine(parent, Path.GetFileName(child));
                    FileAttributes attributes = File.GetAttributes(Resolve(path));
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Symlink im Migrationsziel: " + path);
                    result.Add(path);
                    // Einschliesslich erzeugter Eltern bleibt ein gueltiger Plan unter dem doppelten Quellinventarlimit.
                    if (result.Count > 2 * MigrationInventory.MaximumEntries + LayoutMigration.InstallerEntries.Length)
                        throw new IOException("Zu viele Eintraege im Migrationsziel.");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                }
            }
            result.Sort(StringComparer.Ordinal);
            return ResultObjectFactory.CreateSuccess(result.ToArray());
        }
        catch (Exception exception) when (IsIoError(exception)) { return ResultObjectFactory.CreateFromException<string[]>(exception); }
    }

    public ResultObject<long> FreeBytes() => freeBytes();
    public ResultObject<long> FreeInodes() => freeInodes();
    public ResultErr Sync() => sync();
    private static bool IsMissing(Exception exception) => exception is FileNotFoundException or DirectoryNotFoundException;
    private static bool IsIoError(Exception exception) => exception is IOException or UnauthorizedAccessException;
}
