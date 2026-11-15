using System;
using System.Threading;
using ProjektZeh.Storage;
using ProjektZeh.Shared;
using ProjektZeh.SystemServices;

namespace ProjektZeh.Shell;

/// <summary>
/// Wertet Shellbefehle unabhaengig von der Benutzeroberflaeche aus.
/// </summary>
internal sealed partial class ShellService
{
    public string CurrentDirectory => files?.CurrentDirectory ?? "";
    public bool ExitRequested { get; private set; }
    public string? PagerText { get; private set; }
    public void DismissPager() => PagerText = null;
    public void ResetExitRequest() => ExitRequested = false;
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

    /// <summary>Verwirft eine noch nicht beantwortete Kennwortabfrage, etwa bei Abmeldung oder neuer Anmeldung.</summary>
    public void CancelSecretInput() => secretInput = null;

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
        if (closed) return;
        closed = true;
        history.Clear(); aliases.Clear(); DismissPager();
        secretInput = null;
        OnClose?.Invoke();
    }

    public ShellService(SystemLog? systemLog = null, Func<long?>? getUptimeMilliseconds = null, string? version = null, FileService? files = null, Func<string>? getDate = null, Func<string>? getMemory = null, Func<int>? getSessionGeneration = null)
        : this(Array.Empty<ShellCommand>(), systemLog, getUptimeMilliseconds, version, null, files, getDate, getMemory, getSessionGeneration)
    {
    }

    public ShellService(ShellCommand[] additionalCommands, SystemLog? systemLog = null,
        Func<long?>? getUptimeMilliseconds = null, string? version = null, Func<string, ResultErr>? authorize = null, FileService? files = null, Func<string>? getDate = null, Func<string>? getMemory = null, Func<int>? getSessionGeneration = null)
    {
        this.getSessionGeneration = getSessionGeneration;
        sessionGeneration = getSessionGeneration?.Invoke() ?? 0;
        this.files = files;
        this.getDate = getDate;
        this.getMemory = getMemory;
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
            .. CreateFeatureCommands(),
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
        SyncIdentity();
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
        return ExecuteFeatures(line, output);
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
