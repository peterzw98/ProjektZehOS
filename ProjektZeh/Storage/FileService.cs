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
    private readonly Action<string, string, bool> moveFile;
    private readonly FileAccessControl? access;
    private readonly UserSession? session;

    public string CurrentDirectory { get; private set; }

    public FileService(string initialDirectory, Action<string, string, bool>? moveFile = null,
        FileAccessControl? access = null, UserSession? session = null)
    {
        CurrentDirectory = initialDirectory;
        this.moveFile = moveFile ?? File.Move;
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
            if (access == null) return ResultObjectFactory.CreateSuccess(entries);
            var visible = new List<string>();
            foreach (string entry in entries)
                if (access.Check(session!, entry, FilePermission.Read).IsOK()) visible.Add(entry);
            return ResultObjectFactory.CreateSuccess(visible.ToArray());
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<string[]>(exception);
        }
    }

    /// <summary>
    /// Liest eine Textdatei und schliesst sie anschliessend.
    /// </summary>
    public ResultObject<string> ReadText(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK())
        {
            return resolved;
        }

        try
        {
            Authorize(resolved.Value!, FilePermission.Read);
            using var reader = new StreamReader(resolved.Value!);
            var buffer = new char[MaximumTextLength + 1];
            int length = reader.ReadBlock(buffer, 0, buffer.Length);
            if (length > MaximumTextLength)
            {
                return ResultObjectFactory.CreateFailure<string>(TextTooLarge());
            }

            return ResultObjectFactory.CreateSuccess(new string(buffer, 0, length));
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<string>(exception);
        }
    }

    /// <summary>
    /// Ersetzt den Inhalt einer Textdatei.
    /// </summary>
    public ResultErr WriteText(string? path, string? text)
    {
        if (text is null)
        {
            return ResultErr.CreateFailure(ResultCode.MissingArgument,
                "cause='InvalidInput' message='Der Dateiinhalt fehlt.'");
        }

        if (text.Length > MaximumTextLength)
        {
            return TextTooLarge();
        }

        var resolved = ResolvePath(path);
        if (!resolved.IsOK())
        {
            return resolved.Status;
        }

        string? temporaryPath = null;
        bool temporaryCreated = false;
        FileStream? temporaryStream = null;
        try
        {
            Authorize(resolved.Value!, FilePermission.Write, true);
            byte[] data = new UTF8Encoding(false, true).GetBytes(text);
            temporaryPath = Path.Combine(Path.GetDirectoryName(resolved.Value!)!, $".zehos-{Guid.NewGuid():N}.tmp");
            // Ungepuffert, damit Schreibfehler vor dem Schliessen des Handles auftreten.
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                temporaryStream = stream;
                temporaryCreated = true;
                stream.Write(data);
                stream.Flush(true);
            }

            ReplaceFile(temporaryPath, resolved.Value!);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsFileError(exception))
        {
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

    private void ReplaceFile(string temporaryPath, string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            moveFile(temporaryPath, targetPath, false);
            return;
        }

        // Cosmos haengt beim Ueberschreiben einen festen Sicherungsnamen an das Ziel an.
        string backupPath = Path.Combine(Path.GetDirectoryName(targetPath)!, $".zehos-{Guid.NewGuid():N}.bak");
        moveFile(targetPath, backupPath, false);
        try
        {
            moveFile(temporaryPath, targetPath, false);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            try
            {
                moveFile(backupPath, targetPath, false);
            }
            catch (Exception restoreException) when (IsFileError(restoreException))
            {
                throw new IOException($"Wiederherstellung fehlgeschlagen. Die Originaldatei liegt unter '{backupPath}'.",
                    new AggregateException(exception, restoreException));
            }

            throw;
        }

        File.Delete(backupPath);
    }

    /// <summary>
    /// Erstellt ein Verzeichnis samt fehlenden Elternverzeichnissen.
    /// </summary>
    public ResultErr CreateDirectory(string? path)
    {
        var resolved = ResolvePath(path);
        if (!resolved.IsOK())
        {
            return resolved.Status;
        }

        try
        {
            Authorize(resolved.Value!, FilePermission.Write, true);
            Directory.CreateDirectory(resolved.Value!);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsFileError(exception))
        {
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

    internal ResultObject<string> ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ResultObjectFactory.CreateFailure<string>(ResultCode.InvalidArgument,
                "cause='InvalidPath' message='Ein Pfad muss angegeben werden.'");
        }

        try
        {
            return ResultObjectFactory.CreateSuccess(Path.GetFullPath(path, CurrentDirectory));
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return ResultObjectFactory.CreateFromException<string>(exception);
        }
    }

    private static bool IsFileError(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    }

    private void Authorize(string path, FilePermission permission, bool creating = false)
    {
        if (access == null) return;
        if (session == null || !access.Check(session, path, permission, creating).IsOK())
            throw new UnauthorizedAccessException("Zugriff verweigert.");
        // Vorhandene, auch ueber FAT-Kurznamen erreichbare Eintraege muessen explizit zugeordnet sein.
        if (creating && !access.Contains(path) && (File.Exists(path) || Directory.Exists(path)))
            throw new UnauthorizedAccessException("Vorhandener Eintrag ohne Rechtezuordnung.");
        if (creating && !access.RegisterCreated(session, path).IsOK())
            throw new UnauthorizedAccessException("Rechte konnten nicht gespeichert werden; Zugriff gesperrt.");
    }

    internal void ResetDirectory(string directory) => CurrentDirectory = directory;

    private static ResultErr TextTooLarge()
    {
        return ResultErr.CreateFailure(ResultCode.InvalidArgument,
            $"cause='TextTooLarge' message='Textdateien sind auf {MaximumTextLength} Zeichen begrenzt.'");
    }
}
