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

    private static ResultErr InvalidArguments(string usage)
    {
        return ResultErr.CreateFailure(ResultCode.InvalidArgument,
            $"cause='InvalidArguments' message='Verwendung: {usage}'");
    }
}
