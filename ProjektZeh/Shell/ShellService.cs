using System;

namespace ProjektZeh.Shell;

/// <summary>
/// Wertet Shellbefehle unabhaengig von der Benutzeroberflaeche aus.
/// </summary>
internal sealed class ShellService
{
    private readonly ShellCommand[] commands;

    public ShellService()
    {
        commands = new[]
        {
            new ShellCommand("help", "help", "Zeigt die verfuegbaren Befehle.", false, WriteHelp),
            new ShellCommand("echo", "echo [Text ...]", "Gibt die Argumente als Text aus.", true, WriteEcho),
            new ShellCommand("clear", "clear", "Leert die Konsole.", false, ClearOutput),
            new ShellCommand("about", "about", "Zeigt Informationen ueber ZehOS.", false, WriteAbout)
        };
    }

    /// <summary>
    /// Fuehrt eine Befehlszeile aus und gibt Eingabefehler an den Aufrufer zurueck.
    /// </summary>
    /// <param name="line"></param>
    /// <param name="output"></param>
    /// <returns></returns>
    public ResultErr ExecuteLine(string? line, ITextOutput? output)
    {
        var parseResult = CommandLineParser.Parse(line);
        if (!parseResult.IsOK())
        {
            return new ResultErr(parseResult.eResult, parseResult.sErrMsg);
        }

        string[] tokens = parseResult.Value;
        if (tokens.Length == 0)
        {
            return new ResultErr();
        }

        string commandName = tokens[0];
        foreach (ShellCommand command in commands)
        {
            if (string.Equals(command.Name, commandName, StringComparison.OrdinalIgnoreCase))
            {
                var arguments = new string[tokens.Length - 1];
                Array.Copy(tokens, 1, arguments, 0, arguments.Length);
                return command.Execute(arguments, output);
            }
        }

        return new ResultErr(EnmErrCode.UnknownCommand,
            $"cause='UnknownCommand' message='Unbekannter Befehl: {commandName}. Hilfe: help.'");
    }

    private void WriteHelp(string[] arguments, ITextOutput output)
    {
        foreach (ShellCommand command in commands)
        {
            output.WriteLine($"{command.Usage} - {command.Description}");
        }
    }

    private static void WriteEcho(string[] arguments, ITextOutput output)
    {
        output.WriteLine(string.Join(" ", arguments));
    }

    private static void ClearOutput(string[] arguments, ITextOutput output)
    {
        output.Clear();
    }

    private static void WriteAbout(string[] arguments, ITextOutput output)
    {
        output.WriteLine("ZehOS - ein Betriebssystemprojekt in C# mit Cosmos Gen3.");
    }
}
