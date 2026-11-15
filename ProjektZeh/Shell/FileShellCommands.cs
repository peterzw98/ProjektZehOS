using System.Globalization;
using ProjektZeh.Shared;
using ProjektZeh.Storage;

namespace ProjektZeh.Shell;

/// <summary>
/// Verbindet Dateibefehle mit dem wiederverwendbaren Dateidienst.
/// </summary>
internal sealed class FileShellCommands
{
    private readonly FileService files;

    public ShellCommand[] Commands { get; }

    public FileShellCommands(FileService files)
    {
        this.files = files;
        Commands =
        [
            new("pwd", "pwd", "Zeigt das Arbeitsverzeichnis.", false, WriteWorkingDirectory),
            new("cd", "cd <Pfad>", "Wechselt das Arbeitsverzeichnis.", true, ChangeDirectory),
            new("ls", "ls [Pfad]", "Listet Dateien und Verzeichnisse.", true, ListEntries),
            new("cat", "cat <Datei>", "Liest eine Textdatei.", true, ReadText),
            new("mkdir", "mkdir <Pfad>", "Erstellt ein Verzeichnis.", true, CreateDirectory),
            new("cp", "cp <Quelle> <Ziel>", "Kopiert eine Datei; bestehende Ziele werden abgelehnt.", true,
                (args, _) => args.Length == 2 ? files.CopyFile(args[0], args[1]) : InvalidArguments("cp <Quelle> <Ziel>")),
            new("mv", "mv <Quelle> <Ziel>", "Verschiebt eine Datei samt Rechten; kein Ueberschreiben.", true,
                (args, _) => args.Length == 2 ? files.MoveFile(args[0], args[1]) : InvalidArguments("mv <Quelle> <Ziel>")),
            new("rm", "rm <Datei>", "Loescht genau eine Datei, niemals rekursiv.", true,
                (args, _) => args.Length == 1 && !args[0].StartsWith('-') ? files.DeleteFile(args[0]) : InvalidArguments("rm <Datei>")),
            new("rmdir", "rmdir <Verzeichnis>", "Loescht ein leeres Verzeichnis.", true,
                (args, _) => args.Length == 1 ? files.DeleteDirectory(args[0]) : InvalidArguments("rmdir <Verzeichnis>")),
            new("touch", "touch <Datei>", "Erstellt eine leere Datei oder erhaelt vorhandenen Inhalt; Zeitstempel dateisystemabhaengig.", true,
                (args, _) => args.Length == 1 ? files.Touch(args[0]) : InvalidArguments("touch <Datei>")),
            new("find", "find [Pfad] [Teilname]", "Sucht lesbare Eintraege, maximal 1024; keine Verknuepfungen.", true, Find),
            new("du", "du [Pfad]", "Summiert lesbare Dateiinhalte in Bytes (maximal 1024 Eintraege).", true, DiskUsage),
            new("df", "df [Pfad]", "Zeigt tatsaechliche Gesamtkapazitaet und freien Platz in Bytes.", true, DiskFree),
            new("write", "write <Datei> <Text>", "Ersetzt den Inhalt einer Textdatei.", true, WriteText)
        ];
    }

    private ResultErr WriteWorkingDirectory(string[] arguments, ITextOutput output)
    {
        output.WriteLine(files.CurrentDirectory);
        return ResultErr.CreateSuccess();
    }

    private ResultErr ChangeDirectory(string[] arguments, ITextOutput output)
    {
        if (arguments.Length != 1)
        {
            return InvalidArguments("cd <Pfad>");
        }

        return files.ChangeDirectory(arguments[0]);
    }

    private ResultErr ListEntries(string[] arguments, ITextOutput output)
    {
        if (arguments.Length > 1)
        {
            return InvalidArguments("ls [Pfad]");
        }

        string path = ".";
        if (arguments.Length == 1)
        {
            path = arguments[0];
        }

        var result = files.ListEntries(path);
        if (!result.IsOK())
        {
            return result.Status;
        }

        foreach (string entry in result.Value!)
        {
            output.WriteLine(entry);
        }

        return ResultErr.CreateSuccess();
    }

    private ResultErr ReadText(string[] arguments, ITextOutput output)
    {
        if (arguments.Length != 1)
        {
            return InvalidArguments("cat <Datei>");
        }

        var result = files.ReadText(arguments[0]);
        if (!result.IsOK())
        {
            return result.Status;
        }

        output.WriteLine(result.Value!);
        return ResultErr.CreateSuccess();
    }

    private ResultErr CreateDirectory(string[] arguments, ITextOutput output)
    {
        if (arguments.Length != 1)
        {
            return InvalidArguments("mkdir <Pfad>");
        }

        return files.CreateDirectory(arguments[0]);
    }

    private ResultErr WriteText(string[] arguments, ITextOutput output)
    {
        if (arguments.Length < 2)
        {
            return InvalidArguments("write <Datei> <Text>");
        }

        return files.WriteText(arguments[0], string.Join(" ", arguments, 1, arguments.Length - 1));
    }

    private ResultErr Find(string[] arguments, ITextOutput output)
    {
        if (arguments.Length > 2) return InvalidArguments("find [Pfad] [Teilname]");
        var result = files.FindEntries(arguments.Length > 0 ? arguments[0] : ".", arguments.Length > 1 ? arguments[1] : "");
        if (!result.IsOK()) return result.Status;
        foreach (var entry in result.Value!) output.WriteLine(entry.Path);
        return ResultErr.CreateSuccess();
    }

    private ResultErr DiskUsage(string[] arguments, ITextOutput output)
    {
        if (arguments.Length > 1) return InvalidArguments("du [Pfad]");
        string path = arguments.Length == 1 ? arguments[0] : ".";
        var result = files.GetDirectorySize(path);
        if (!result.IsOK()) return result.Status;
        output.WriteLine(result.Value.ToString(CultureInfo.InvariantCulture) + " Bytes " + path);
        return ResultErr.CreateSuccess();
    }

    private ResultErr DiskFree(string[] arguments, ITextOutput output)
    {
        if (arguments.Length > 1) return InvalidArguments("df [Pfad]");
        var result = files.GetDiskSpace(arguments.Length == 1 ? arguments[0] : ".");
        if (!result.IsOK()) return result.Status;
        output.WriteLine("Gesamt: " + result.Value.TotalBytes.ToString(CultureInfo.InvariantCulture) + " Bytes; frei: " +
            result.Value.AvailableBytes.ToString(CultureInfo.InvariantCulture) + " Bytes");
        return ResultErr.CreateSuccess();
    }
    private static ResultErr InvalidArguments(string usage)
    {
        return ResultErr.CreateFailure(ResultCode.InvalidArgument,
            $"cause='InvalidArguments' message='Verwendung: {usage}'");
    }
}
