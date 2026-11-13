using System;
using System.Threading;
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
    private readonly Func<string, ResultErr>? authorize;
    private Func<string, Func<Func<ResultErr>>>? secretInput;
    private Thread? passwordWorker;
    private Func<ResultErr>? completion;
    private int passwordComplete;
    private bool closed;
    private static int runningPasswordWorkers;
    internal static bool HasRunningPasswordOperations => Volatile.Read(ref runningPasswordWorkers) != 0;
    internal Action? OnClose { get; set; }
    public bool IsSecretInput => secretInput != null;
    public bool IsBusy => passwordWorker != null;

    public void BeginSecretInput(Func<string, Func<Func<ResultErr>>> prepare) => secretInput = prepare;

    public bool Poll(ITextOutput output)
    {
        if (passwordWorker == null || Volatile.Read(ref passwordComplete) == 0 || !passwordWorker.Join(0)) return false;
        passwordWorker = null;
        var apply = completion;
        completion = null;
        if (!closed)
        {
            var result = apply!();
            output.WriteLine(result.IsOK() ? "Vorgang abgeschlossen." : result.Message);
        }
        return true;
    }

    public void Close()
    {
        closed = true;
        secretInput = null;
        OnClose?.Invoke();
    }

    public ShellService(SystemLog? systemLog = null, Func<long?>? getUptimeMilliseconds = null, string? version = null)
        : this(Array.Empty<ShellCommand>(), systemLog, getUptimeMilliseconds, version)
    {
    }

    public ShellService(ShellCommand[] additionalCommands, SystemLog? systemLog = null,
        Func<long?>? getUptimeMilliseconds = null, string? version = null, Func<string, ResultErr>? authorize = null)
    {
        this.authorize = authorize;
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
        if (closed || IsBusy) return ResultErr.CreateFailure(ResultCode.Failure, "Kennwortoperation laeuft oder Sitzung geschlossen.");
        if (secretInput != null)
        {
            // Geschlossene Fenster duerfen die globale Grenze nicht durch neue Sitzungen umgehen.
            if (Interlocked.Increment(ref runningPasswordWorkers) > 2)
            {
                Interlocked.Decrement(ref runningPasswordWorkers);
                return ResultErr.CreateFailure(ResultCode.Failure, "Kennwortdienst ausgelastet. Nach Abschluss erneut eingeben.");
            }
            var prepare = secretInput;
            Func<Func<ResultErr>> work;
            try { work = prepare(line ?? ""); }
            catch
            {
                Interlocked.Decrement(ref runningPasswordWorkers);
                return ResultErr.CreateFailure(ResultCode.Failure, "Kennwortoperation konnte nicht vorbereitet werden.");
            }
            secretInput = null;
            Volatile.Write(ref passwordComplete, 0);
            passwordWorker = new Thread(() =>
            {
                try { completion = work(); }
                catch (Exception)
                {
                    // Ausnahmen koennen Kennwortdetails enthalten und werden deshalb nicht ausgegeben.
                    completion = () => ResultErr.CreateFailure(ResultCode.Failure, "Kennwortoperation fehlgeschlagen. Kennwortlaenge oder sichere Zufallsquelle pruefen.");
                }
                Interlocked.Decrement(ref runningPasswordWorkers);
                Volatile.Write(ref passwordComplete, 1);
            });
            try { passwordWorker.Start(); }
            catch
            {
                passwordWorker = null;
                Interlocked.Decrement(ref runningPasswordWorkers);
                return ResultErr.CreateFailure(ResultCode.Failure, "Kennwortoperation konnte nicht gestartet werden.");
            }
            return ResultErr.CreateSuccess();
        }
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
                var allowed = authorize?.Invoke(command.Name) ?? ResultErr.CreateSuccess();
                if (!allowed.IsOK())
                {
                    return allowed;
                }

                var arguments = new string[tokens.Length - 1];
                Array.Copy(tokens, 1, arguments, 0, arguments.Length);
                return command.Execute(arguments, output);
            }
        }

        return ResultErr.CreateFailure(ResultCode.NotFound,
            $"cause='UnknownCommand' message='Unbekannter Befehl: {commandName}. Hilfe: help.'");
    }

    public bool HasCommand(string name)
    {
        foreach (ShellCommand command in commands)
            if (string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
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
