using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using ProjektZeh.Security;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

/// <summary>
/// Dateizugriff mit eigenem Arbeitsverzeichnis und gemeinsamen Fehlerergebnissen.
/// </summary>
internal sealed class FileService
{
    internal const int MaximumTextLength = 65536;
    internal const int MaximumExt2DirectoryEntries = 8190;
    private readonly Action<string, string, bool> moveFile;
    private readonly Func<string, ResultObject<(long TotalBytes, long AvailableBytes)>>? diskSpace;
    private readonly Func<string, int?>? directoryEntryLimit;
    private readonly FileAccessControl? access;
    private readonly UserSession? session;
    // Ohne Rechteverwaltung (Kontendatei) prueft dieser Waechter Symlinks und Mountgrenzen; mit Rechteverwaltung deren eigener.
    private readonly Func<string, bool, ResultErr>? pathGuard;

    public string CurrentDirectory { get; private set; }

    public FileService(string initialDirectory, Action<string, string, bool>? moveFile = null,
        FileAccessControl? access = null, UserSession? session = null,
        Func<string, ResultObject<(long TotalBytes, long AvailableBytes)>>? diskSpace = null,
        Func<string, bool, ResultErr>? pathGuard = null, Func<string, int?>? directoryEntryLimit = null)
    {
        this.pathGuard = pathGuard ?? access?.PathGuard;
        CurrentDirectory = initialDirectory;
        this.moveFile = moveFile ?? File.Move;
        this.diskSpace = diskSpace;
        this.directoryEntryLimit = directoryEntryLimit;
        this.access = access;
        this.session = session;
    }

    /// <summary>
    /// Listet Dateien und Unterverzeichnisse.
    /// </summary>
    public ResultObject<string[]> ListEntries(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK())
        {
            return ResultObjectFactory.CreateFailure<string[]>(resolved.Status);
        }

        try
        {
            Authorize(resolved.Value!, FilePermission.Read);
            string[] entries = Directory.GetFileSystemEntries(resolved.Value!);
            var visible = new List<string>();
            foreach (string entry in entries)
                if (Visible(entry)) visible.Add(entry);
            return ResultObjectFactory.CreateSuccess(visible.ToArray());
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<string[]>(exception);
        }
    }

    public ResultObject<FileEntry[]> ListDirectory(string? path)
    {
        var listed = ListEntries(path);
        if (!listed.IsOK()) return ResultObjectFactory.CreateFailure<FileEntry[]>(listed.Status);
        try
        {
            var entries = new FileEntry[listed.Value!.Length];
            for (int index = 0; index < entries.Length; index++)
            {
                string name = listed.Value[index];
                entries[index] = new(name, (File.GetAttributes(name) & FileAttributes.Directory) != 0);
            }
            Array.Sort(entries, static (left, right) =>
            {
                int type = right.IsDirectory.CompareTo(left.IsDirectory);
                return type != 0 ? type : string.Compare(left.Path, right.Path, StringComparison.Ordinal);
            });
            return ResultObjectFactory.CreateSuccess(entries);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<FileEntry[]>(exception);
        }
    }

    /// <summary>
    /// Liest eine Textdatei und schliesst sie anschliessend. Mit <paramref name="allowMissing"/> liefert eine fehlende
    /// Datei Erfolg ohne Wert; alle anderen Fehler, auch ein fehlendes Verzeichnis, bleiben Fehler.
    /// </summary>
    public ResultObject<string> ReadText(string? path, bool allowMissing = false, int maximumLength = MaximumTextLength)
    {
        var resolved = ResolvePath(path, creating: allowMissing);
        if (!resolved.IsOK())
        {
            return resolved;
        }

        try
        {
            if (allowMissing && access != null && !access.Contains(resolved.Value!))
            {
                if (session == null || !access.Check(session, resolved.Value!, FilePermission.Write, true).IsOK())
                    throw new UnauthorizedAccessException("Zugriff verweigert.");
                // Nur ein wirklich fehlendes Ziel darf ohne Rechtezuordnung als leer gelten.
                _ = File.GetAttributes(resolved.Value!);
                throw new UnauthorizedAccessException("Vorhandener Eintrag ohne Rechtezuordnung.");
            }
            Authorize(resolved.Value!, FilePermission.Read);
            using var reader = new StreamReader(resolved.Value!);
            var text = ReadToEnd(reader, maximumLength);
            return text == null
                ? ResultObjectFactory.CreateFailure<string>(TextTooLarge(maximumLength))
                : ResultObjectFactory.CreateSuccess(text);
        }
        catch (FileNotFoundException) when (allowMissing)
        {
            return ResultObjectFactory.CreateSuccess<string>(null);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<string>(exception);
        }
    }

    /// <summary>Liest bis EOF (nicht nach Stream.Length: /proc-Dateien melden 0); null, wenn mehr als <paramref name="maximumLength"/> Zeichen kommen.</summary>
    internal static string? ReadToEnd(TextReader reader, int maximumLength)
    {
        var text = new StringBuilder();
        var chunk = new char[4096];
        int read;
        while ((read = reader.Read(chunk, 0, (int)Math.Min(chunk.Length, (long)maximumLength + 1 - text.Length))) > 0)
        {
            text.Append(chunk, 0, read);
            if (text.Length > maximumLength)
            {
                return null;
            }
        }

        return text.ToString();
    }

    public ResultObject<byte[]> ReadBytes(string? path, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return ResultObjectFactory.CreateFailure<byte[]>(resolved.Status);
        try
        {
            Authorize(resolved.Value!, FilePermission.Read);
            using var stream = new FileStream(resolved.Value!, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            if (length < 0 || length > maximumLength)
                return ResultObjectFactory.CreateFailure<byte[]>(ResultCode.InvalidArgument, "Die Bilddatei ist zu gross.");
            var data = new byte[(int)length];
            stream.ReadExactly(data);
            return ResultObjectFactory.CreateSuccess(data);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<byte[]>(exception);
        }
    }

    /// <summary>
    /// Ersetzt den Inhalt einer Textdatei.
    /// </summary>
    public ResultErr WriteText(string? path, string? text, int maximumLength = MaximumTextLength)
    {
        if (text is null)
        {
            return ResultErr.CreateFailure(ResultCode.MissingArgument,
                "cause='InvalidInput' message='Der Dateiinhalt fehlt.'");
        }

        if (text.Length > maximumLength)
        {
            return TextTooLarge(maximumLength);
        }

        return WriteData(path, () => new UTF8Encoding(false, true).GetBytes(text), false);
    }

    /// <summary>Schreibt gepruefte Binaerdaten; auf Wunsch bleibt das Original als feste .bak-Datei erhalten.</summary>
    internal ResultErr WriteBytes(string path, byte[] data, bool keepBackup = false)
    {
        return WriteData(path, () => data, keepBackup);
    }

    private ResultErr WriteData(string? path, Func<byte[]> getData, bool keepBackup)
    {
        var resolved = ResolvePath(path, creating: true);
        if (!resolved.IsOK())
        {
            return resolved.Status;
        }

        string? temporaryPath = null;
        bool temporaryCreated = false;
        FileStream? temporaryStream = null;
        bool registered = false;
        bool backupRegistered = false;
        try
        {
            registered = Authorize(resolved.Value!, FilePermission.Write, true);
            if (keepBackup) backupRegistered = Authorize(resolved.Value! + ".bak", FilePermission.Write, true);
            byte[] data = getData();
            temporaryPath = Path.Combine(Path.GetDirectoryName(resolved.Value!)!, $".zehos-{Guid.NewGuid():N}.tmp");
            EnsureDirectoryEntryAvailable(temporaryPath);
            // Ungepuffert, damit Schreibfehler vor dem Schliessen des Handles auftreten.
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                temporaryStream = stream;
                temporaryCreated = true;
                stream.Write(data);
                stream.Flush(true);
            }

            ReplaceFile(temporaryPath, resolved.Value!, keepBackup);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            if (registered) access!.ForgetCreated(resolved.Value!);
            if (backupRegistered) access!.ForgetCreated(resolved.Value! + ".bak");
            if (temporaryCreated)
            {
                try
                {
                    // shortcut: Bei ENOSPC bleibt der Stream in Cosmos 3.0.94 offen; nach Runtime-Update erneut pruefen.
                    temporaryStream!.Dispose();
                    File.Delete(temporaryPath!);
                }
                catch (Exception cleanupException) when (IsFileError(cleanupException))
                {
                    return ResultErr.CreateFromException(new AggregateException(exception, cleanupException));
                }
            }

            return ResultErr.CreateFromException(exception);
        }
    }

    private void ReplaceFile(string temporaryPath, string targetPath, bool keepBackup = false)
    {
        if (!File.Exists(targetPath))
        {
            moveFile(temporaryPath, targetPath, false);
            return;
        }

        // Cosmos haengt beim Ueberschreiben einen festen Sicherungsnamen an das Ziel an.
        string backupPath = keepBackup ? targetPath + ".bak" :
            Path.Combine(Path.GetDirectoryName(targetPath)!, $".zehos-{Guid.NewGuid():N}.bak");
        string? previousBackup = null;
        if (keepBackup && File.Exists(backupPath))
        {
            previousBackup = Path.Combine(Path.GetDirectoryName(targetPath)!, $".zehos-{Guid.NewGuid():N}.bak");
            moveFile(backupPath, previousBackup, false);
        }
        try
        {
            moveFile(targetPath, backupPath, false);
            try
            {
                moveFile(temporaryPath, targetPath, false);
            }
            catch (Exception exception) when (IsFileError(exception))
            {
                try { moveFile(backupPath, targetPath, false); }
                catch (Exception restoreException) when (IsFileError(restoreException))
                {
                    throw new IOException($"Wiederherstellung fehlgeschlagen. Die Originaldatei liegt unter '{backupPath}'.",
                        new AggregateException(exception, restoreException));
                }
                throw;
            }
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            // Liegt das Original nach gescheiterter Wiederherstellung noch unter backupPath, bleibt die aeltere Sicherung unter ihrem Ausweichnamen.
            if (previousBackup != null && !File.Exists(backupPath))
            {
                try { moveFile(previousBackup, backupPath, false); }
                catch (Exception restoreException) when (IsFileError(restoreException))
                {
                    throw new IOException($"Vorherige Sicherung liegt unter '{previousBackup}'.", new AggregateException(exception, restoreException));
                }
            }
            throw;
        }

        if (!keepBackup) File.Delete(backupPath);
        if (previousBackup != null) File.Delete(previousBackup);
    }

    /// <summary>
    /// Erstellt ein Verzeichnis samt fehlenden Elternverzeichnissen.
    /// </summary>
    public ResultErr CreateDirectory(string? path)
    {
        var resolved = ResolvePath(path, creating: true);
        if (!resolved.IsOK())
        {
            return resolved.Status;
        }

        bool registered = false;
        try
        {
            registered = Authorize(resolved.Value!, FilePermission.Write, true);
            EnsureDirectoryEntryAvailable(resolved.Value!, createParents: true);
            Directory.CreateDirectory(resolved.Value!);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            if (registered) access!.ForgetCreated(resolved.Value!);
            return ResultErr.CreateFromException(exception);
        }
    }

    /// <summary>
    /// Wechselt nur bei einem erreichbaren Verzeichnis den Shellpfad.
    /// </summary>
    public ResultErr ChangeDirectory(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK())
        {
            return resolved.Status;
        }

        try
        {
            Authorize(resolved.Value!, FilePermission.Read);
            FileAttributes attributes = File.GetAttributes(resolved.Value!);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                return ResultErr.CreateFailure(ResultCode.InvalidArgument,
                    $"cause='NotDirectory' message='Kein Verzeichnis: {resolved.Value}'");
            }

            CurrentDirectory = resolved.Value!;
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return ResultErr.CreateFromException(exception);
        }
    }

    public ResultErr CopyFile(string? source, string? target) => TransferFile(source, target, false);
    public ResultErr MoveFile(string? source, string? target) => TransferFile(source, target, true);

    private ResultErr TransferFile(string? source, string? target, bool moving)
    {
        var from = ResolvePath(source);
        if (!from.IsOK()) return from.Status;
        var to = ResolvePath(target, creating: true);
        if (!to.IsOK()) return to.Status;
        bool registered = false;
        string? temporary = null;
        FileStream? temporaryStream = null;
        try
        {
            Authorize(from.Value!, moving ? FilePermission.Read | FilePermission.Write : FilePermission.Read);
            if (moving) Authorize(Path.GetDirectoryName(from.Value!)!, FilePermission.Write);
            // Oeffnen prueft auch fehlende Quellen, bevor Zielrechte angelegt werden.
            using (var input = new FileStream(from.Value!, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (access != null && (session == null || !access.Check(session, to.Value!, FilePermission.Write, true).IsOK()))
                    throw new UnauthorizedAccessException("Zugriff verweigert.");
                if (File.Exists(to.Value!) || Directory.Exists(to.Value!) || (access?.Contains(to.Value!) ?? false))
                    throw new IOException("Das Ziel existiert bereits.");
                // Gleichverzeichniges Rename verbraucht dauerhaft keinen weiteren Eintrag.
                if (moving && !string.Equals(Path.GetDirectoryName(from.Value!), Path.GetDirectoryName(to.Value!), StringComparison.Ordinal))
                    EnsureDirectoryEntryAvailable(to.Value!);
                if (moving && access != null)
                {
                    if (!access.PrepareMove(from.Value!, to.Value!).IsOK())
                        throw new UnauthorizedAccessException("Rechte konnten nicht gespeichert werden.");
                    registered = true;
                }
                else registered = Authorize(to.Value!, FilePermission.Write, true);
                if (!moving)
                {
                    temporary = Path.Combine(Path.GetDirectoryName(to.Value!)!, $".zehos-{Guid.NewGuid():N}.tmp");
                    EnsureDirectoryEntryAvailable(temporary);
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1);
                    temporaryStream = output;
                    input.CopyTo(output, 4096);
                    output.Flush(true);
                }
            }
            moveFile(moving ? from.Value! : temporary!, to.Value!, false);
            temporary = null;
            if (moving && access != null) return access.RemoveEntry(from.Value!);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            if (registered) access!.ForgetCreated(to.Value!);
            if (temporary != null)
            {
                try { temporaryStream?.Dispose(); File.Delete(temporary); }
                catch (Exception cleanupException) when (IsFileError(cleanupException))
                { return ResultErr.CreateFromException(new AggregateException(exception, cleanupException)); }
            }
            return ResultErr.CreateFromException(exception);
        }
    }

    public ResultErr DeleteFile(string? path) => DeleteEntry(path, false);
    public ResultErr DeleteDirectory(string? path) => DeleteEntry(path, true);

    private ResultErr DeleteEntry(string? path, bool directory)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return resolved.Status;
        try
        {
            Authorize(resolved.Value!, FilePermission.Write);
            Authorize(Path.GetDirectoryName(resolved.Value!)!, FilePermission.Write);
            StringComparison comparison = access?.Comparison ?? StringComparison.OrdinalIgnoreCase;
            if (directory && (string.Equals(resolved.Value, CurrentDirectory, comparison) ||
                CurrentDirectory.StartsWith(Path.TrimEndingDirectorySeparator(resolved.Value!) + Path.DirectorySeparatorChar, comparison)))
                throw new IOException("Das Arbeitsverzeichnis oder dessen Elternverzeichnis darf nicht geloescht werden.");
            FileAttributes attributes = File.GetAttributes(resolved.Value!);
            if (((attributes & FileAttributes.Directory) != 0) != directory)
                throw new IOException(directory ? "Kein Verzeichnis." : "Keine Datei.");
            if (directory) Directory.Delete(resolved.Value!, false);
            else File.Delete(resolved.Value!);
            return access?.RemoveEntry(resolved.Value!) ?? ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsFileError(exception))
        { return ResultErr.CreateFromException(exception); }
    }

    public ResultErr Touch(string? path)
    {
        var resolved = ResolvePath(path, creating: true);
        if (!resolved.IsOK()) return resolved.Status;
        bool registered = false;
        try
        {
            registered = Authorize(resolved.Value!, FilePermission.Write, true);
            EnsureDirectoryEntryAvailable(resolved.Value!);
            using (var stream = new FileStream(resolved.Value!, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                stream.Flush(true);
            // Cosmos 3.0.94 bestaetigt Zeitstempel ohne Aenderung; auf dem Host werden sie aktualisiert.
            File.SetLastWriteTimeUtc(resolved.Value!, DateTime.UtcNow);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            if (registered) access!.ForgetCreated(resolved.Value!);
            return ResultErr.CreateFromException(exception);
        }
    }

    public ResultObject<long> GetFileSize(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return ResultObjectFactory.CreateFailure<long>(resolved.Status);
        try
        {
            Authorize(resolved.Value!, FilePermission.Read);
            using var stream = new FileStream(resolved.Value!, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ResultObjectFactory.CreateSuccess(stream.Length);
        }
        catch (Exception exception) when (IsFileError(exception))
        { return ResultObjectFactory.CreateFromException<long>(exception); }
    }

    public ResultObject<(long TotalBytes, long AvailableBytes)> GetDiskSpace(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return ResultObjectFactory.CreateFailure<(long, long)>(resolved.Status);
        try
        {
            Authorize(resolved.Value!, FilePermission.Read);
            _ = File.GetAttributes(resolved.Value!);
            return diskSpace?.Invoke(resolved.Value!) ??
                ResultObjectFactory.CreateFailure<(long, long)>(ResultCode.Failure, "Speicherstatistik ist ohne Dateisystemadapter nicht verfuegbar.");
        }
        catch (Exception exception) when (IsFileError(exception))
        { return ResultObjectFactory.CreateFromException<(long, long)>(exception); }
    }

    internal const int MaximumSearchEntries = 1024;

    public ResultObject<FileEntry[]> FindEntries(string? path, string name = "")
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return ResultObjectFactory.CreateFailure<FileEntry[]>(resolved.Status);
        var pending = new Stack<string>();
        var found = new List<FileEntry>();
        pending.Push(resolved.Value!);
        int visited = 0;
        try
        {
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                Authorize(directory, FilePermission.Read);
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new NotSupportedException("Die Suche folgt keinen Verknuepfungen.");
                // Schrittweise lesen: Auch unsichtbare Eintraege duerfen das Arbeitsbudget nicht umgehen.
                foreach (string entryPath in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++visited > MaximumSearchEntries)
                        return ResultObjectFactory.CreateFailure<FileEntry[]>(ResultCode.InvalidArgument, "Suche auf 1024 Eintraege begrenzt.");
                    // Vor GetAttributes: Cosmos folgt dort Symlinks.
                    if (!Visible(entryPath)) continue;
                    var attributes = File.GetAttributes(entryPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var entry = new FileEntry(entryPath, (attributes & FileAttributes.Directory) != 0);
                    if (Path.GetFileName(entry.Path).Contains(name, StringComparison.OrdinalIgnoreCase)) found.Add(entry);
                    if (entry.IsDirectory) pending.Push(entry.Path);
                }
            }
        }
        catch (Exception exception) when (IsFileError(exception))
        { return ResultObjectFactory.CreateFromException<FileEntry[]>(exception); }
        return ResultObjectFactory.CreateSuccess(found.ToArray());
    }

    public ResultObject<long> GetDirectorySize(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK()) return ResultObjectFactory.CreateFailure<long>(resolved.Status);
        try
        {
            Authorize(resolved.Value!, FilePermission.Read);
            if ((File.GetAttributes(resolved.Value!) & FileAttributes.Directory) == 0) return GetFileSize(path);
        }
        catch (Exception exception) when (IsFileError(exception))
        { return ResultObjectFactory.CreateFromException<long>(exception); }
        var entries = FindEntries(path);
        if (!entries.IsOK()) return ResultObjectFactory.CreateFailure<long>(entries.Status);
        long size = 0;
        foreach (var entry in entries.Value!)
        {
            if (entry.IsDirectory) continue;
            var length = GetFileSize(entry.Path);
            if (!length.IsOK()) return length;
            size += length.Value;
        }
        return ResultObjectFactory.CreateSuccess(size);
    }

    /// <summary>
    /// Absoluter Pfad, erst nach dem Pfadwaechter (Etappe 3): jeder Symlink und jede fremde Mountgrenze wird abgelehnt,
    /// ein fehlendes Blatt nur bei <paramref name="creating"/>. Restrisiko: zwischen Pruefung und IO kann sich der Baum aendern
    /// (ZehOS selbst legt keine Symlinks an).
    /// </summary>
    internal ResultObject<string> ResolvePath(string? path, bool creating = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ResultObjectFactory.CreateFailure<string>(ResultCode.InvalidArgument,
                "cause='InvalidPath' message='Ein Pfad muss angegeben werden.'");
        }

        try
        {
            string full = Path.GetFullPath(path, CurrentDirectory);
            var guarded = FileAccessControl.RunGuard(pathGuard, full, creating);
            return guarded.IsOK() ? ResultObjectFactory.CreateSuccess(full) : ResultObjectFactory.CreateFailure<string>(guarded);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<string>(exception);
        }
    }

    private void EnsureDirectoryEntryAvailable(string path, bool createParents = false)
    {
        if (directoryEntryLimit == null || File.Exists(path) || Directory.Exists(path)) return;
        string? parent = Path.GetDirectoryName(path);
        // mkdir -p benoetigt nur im ersten vorhandenen Elternordner einen neuen Eintrag; darunter sind die Ordner neu.
        while (createParents && parent != null && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
        if (parent == null || !Directory.Exists(parent)) return;
        int? maximum = directoryEntryLimit(parent);
        if (maximum == null) return;
        int count = 0;
        foreach (string _ in Directory.EnumerateFileSystemEntries(parent))
        {
            if (++count >= maximum.Value)
                throw new IOException($"cause='DirectoryEntryLimit' message='Verzeichnis {parent} ist auf {maximum.Value} Eintraege begrenzt; zuerst einen Eintrag entfernen oder einen anderen Ordner verwenden.'");
        }
    }

    private static bool IsFileError(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    }

    /// <returns>true, wenn dafuer ein neuer Rechte-Eintrag angelegt wurde.</returns>
    private bool Authorize(string path, FilePermission permission, bool creating = false)
    {
        if (access == null) return false;
        if (session == null || !access.Check(session, path, permission, creating).IsOK())
            throw new UnauthorizedAccessException("Zugriff verweigert.");
        if (!creating || access.Contains(path)) return false;
        // Vorhandene, auch ueber FAT-Kurznamen erreichbare Eintraege muessen explizit zugeordnet sein.
        if (File.Exists(path) || Directory.Exists(path))
            throw new UnauthorizedAccessException("Vorhandener Eintrag ohne Rechtezuordnung.");
        if (!access.RegisterCreated(session, path).IsOK())
            throw new UnauthorizedAccessException("Rechte konnten nicht gespeichert werden; Zugriff gesperrt.");
        return true;
    }

    internal void ResetDirectory(string directory) => CurrentDirectory = directory;

    /// <summary>Ein Listeneintrag ist sichtbar nach Rechten, ohne Rechteverwaltung nach dem Pfadwaechter.</summary>
    private bool Visible(string entry) => access != null
        ? access.Check(session!, entry, FilePermission.Read).IsOK()
        : FileAccessControl.RunGuard(pathGuard, entry, false).IsOK();

    private static ResultErr TextTooLarge(int maximumLength)
    {
        return ResultErr.CreateFailure(ResultCode.InvalidArgument,
            $"cause='TextTooLarge' message='Textdateien sind auf {maximumLength} Zeichen begrenzt.'");
    }
}
