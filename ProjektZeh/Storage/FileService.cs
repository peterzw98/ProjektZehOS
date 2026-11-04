using System;
using System.IO;
using ProjektZeh.Shared;

namespace ProjektZeh.Storage;

/// <summary>
/// Dateizugriff mit eigenem Arbeitsverzeichnis und gemeinsamen Fehlerergebnissen.
/// </summary>
internal sealed class FileService
{
    internal const int MaximumTextLength = 65536;

    public string CurrentDirectory { get; private set; }

    public FileService(string initialDirectory)
    {
        CurrentDirectory = initialDirectory;
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
            return ResultObjectFactory.CreateSuccess(Directory.GetFileSystemEntries(resolved.Value!));
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

        try
        {
            File.WriteAllText(resolved.Value!, text);
            return ResultErr.CreateSuccess();
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            return ResultErr.CreateFromException(exception);
        }
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

    private ResultObject<string> ResolvePath(string? path)
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

    private static ResultErr TextTooLarge()
    {
        return ResultErr.CreateFailure(ResultCode.InvalidArgument,
            $"cause='TextTooLarge' message='Textdateien sind auf {MaximumTextLength} Zeichen begrenzt.'");
    }
}
