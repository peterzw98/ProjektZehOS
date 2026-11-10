using System;
using ProjektZeh.Shared;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Shell;

/// <summary>
/// Wertet Shellbefehle unabhaengig von der Benutzeroberflaeche aus.
/// </summary>
internal sealed class ShellService
{
    private readonly ShellCommand[] commands;
    private readonly SystemLog? systemLog;
    private readonly Func<long?>? getUptimeMilliseconds;
    private readonly string? version;

    public ShellService(SystemLog? systemLog = null, Func<long?>? getUptimeMilliseconds = null, string? version = null)
        : this(Array.Empty<ShellCommand>(), systemLog, getUptimeMilliseconds, version)
    {
    }

    public ShellService(ShellCommand[] additionalCommands, SystemLog? systemLog = null,
        Func<long?>? getUptimeMilliseconds = null, string? version = null)
    {
        this.systemLog = systemLog;
        this.getUptimeMilliseconds = getUptimeMilliseconds;
        this.version = version;
        commands =
        [
            new ShellCommand("help", "help", "Zeigt die verfuegbaren Befehle.", false, WriteHelp),
            new ShellCommand("echo", "echo [Text ...]", "Gibt die Argumente als Text aus.", true, WriteEcho),
            new ShellCommand("clear", "clear", "Leert die Konsole.", false, ClearOutput),
            new ShellCommand("about", "about", "Zeigt Informationen ueber ZehOS.", false, WriteAbout),
            new ShellCommand("version", "version", "Zeigt die geladenen Kernelversionen.", false, WriteVersion),
            new ShellCommand("uptime", "uptime", "Zeigt die Laufzeit seit ZehOS-Start.", false, WriteUptime),
            new ShellCommand("log", "log", "Zeigt die gepufferten Systemdiagnosen.", false, WriteLog),
            .. additionalCommands
        ];
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
            return parseResult.Status;
        }

        string[] tokens = parseResult.Value!;
        if (tokens.Length == 0)
        {
            return ResultErr.CreateSuccess();
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

        return ResultErr.CreateFailure(ResultCode.NotFound,
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

    private void WriteVersion(string[] arguments, ITextOutput output)
    {
        output.WriteLine(version ?? "Versionsinformationen sind nicht verfuegbar.");
    }

    private void WriteUptime(string[] arguments, ITextOutput output)
    {
        long? milliseconds = getUptimeMilliseconds?.Invoke();
        output.WriteLine(milliseconds.HasValue
            ? $"Laufzeit seit ZehOS-Start: {milliseconds.Value} ms"
            : "Laufzeit ist nicht verfuegbar: keine initialisierte monotone Uhr.");
    }

    private void WriteLog(string[] arguments, ITextOutput output)
    {
        if (systemLog is null)
        {
            output.WriteLine("Systemprotokoll ist nicht verfuegbar.");
            return;
        }

        SystemLogEntry[] entries = systemLog.GetEntries();
        output.WriteLine($"Systemprotokoll: {entries.Length}/{systemLog.Capacity} Eintraege (aelteste zuerst).");
        foreach (SystemLogEntry entry in entries)
        {
            output.WriteLine(entry.ToString());
        }
    }
}
