using System;
using System.Threading;
using ProjektZeh.Storage;
using ProjektZeh.Security;
using ProjektZeh.Scheduling;
using ProjektZeh.Shared;
using ProjektZeh.SystemServices;
using ProjektZeh.Runtime.Wasm;

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
    /// <summary>Gemeinsamer Kennwort-Worker aller Shells; der Kernel startet ihn beim Boot (<see cref="StartPasswordWorker"/>).</summary>
    private static readonly DedicatedWorker PasswordWorker = new();
    private bool passwordPending;
    private Func<ResultErr>? completion;
    private int passwordComplete;
    private bool closed;
    private static int runningPasswordWorkers;
    internal static bool HasRunningPasswordOperations => Volatile.Read(ref runningPasswordWorkers) != 0;
    internal Action? OnClose { get; set; }
    internal Func<string?>? TakeJobResult { get; set; }
    internal Func<string?>? TakeJobProgress { get; set; }
    internal Func<ITextOutput, bool>? InterruptJob { get; set; }
    public bool IsSecretInput => secretInput != null;
    public bool IsBusy => passwordPending;

    /// <summary>Legt den Kennwort-Worker beim Boot an, solange ein Thread-Start noch sicher Platz findet.</summary>
    internal static ResultErr StartPasswordWorker() => PasswordWorker.Start();

    public bool TryInterrupt(ITextOutput output) => !closed && !IsSecretInput && InterruptJob?.Invoke(output) == true;

    public void BeginSecretInput(Func<string, Func<Func<ResultErr>>> prepare) => secretInput = prepare;

    /// <summary>Verwirft eine noch nicht beantwortete Kennwortabfrage, etwa bei Abmeldung oder neuer Anmeldung.</summary>
    public void CancelSecretInput() => secretInput = null;

    public bool Poll(ITextOutput output)
    {
        bool written = false;
        if (!closed && TakeJobProgress?.Invoke() is string progress)
        {
            if (output is IProgressOutput live) live.WriteProgress(progress);
            else output.WriteLine(progress);
            written = true;
        }
        while (!closed && TakeJobResult?.Invoke() is string text)
        {
            output.WriteLine(text);
            written = true;
        }
        if (!passwordPending || Volatile.Read(ref passwordComplete) == 0) return written;
        passwordPending = false;
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
            new ShellCommand("frametimes", "frametimes [on|off]", "Zerlegt die Bildzeit von Paket-Apps in Anteile (Bericht je 60 s im Protokoll).", true, ExecuteFrametimes),
            new ShellCommand("wasmaot", "wasmaot [on|off]", "Startet Paket-Apps mit passendem .aot nativ ueber WAMR (Standard aus).", true, ExecuteWasmAot),
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
            var started = PasswordWorker.Post(() =>
            {
                try { completion = work(); }
                catch (HardwareRandomUnavailableException)
                {
                    completion = () => ResultErr.CreateFailure(ResultCode.Failure, "Ohne RDRAND/RDSEED/virtio-rng keine Kontoanlage oder Kennwortaenderung.");
                }
                catch (Exception)
                {
                    // Ausnahmen koennen Kennwortdetails enthalten und werden deshalb nicht ausgegeben.
                    completion = () => ResultErr.CreateFailure(ResultCode.Failure, "Kennwortoperation fehlgeschlagen. Kennwortlaenge oder sichere Zufallsquelle pruefen.");
                }
                Interlocked.Decrement(ref runningPasswordWorkers);
                Volatile.Write(ref passwordComplete, 1);
            });
            if (!started.IsOK())
            {
                Interlocked.Decrement(ref runningPasswordWorkers);
                return ResultErr.CreateFailure(ResultCode.Failure, "Kennwortoperation konnte nicht gestartet werden: " + started.Message);
            }
            passwordPending = true;
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

    private static ResultErr ExecuteFrametimes(string[] arguments, ITextOutput output)
    {
        var profile = WasmFrameProfile.Shared;
        bool on = arguments.Length == 1 && string.Equals(arguments[0], "on", StringComparison.OrdinalIgnoreCase);
        if (on || arguments.Length == 1 && string.Equals(arguments[0], "off", StringComparison.OrdinalIgnoreCase)) profile.SetEnabled(on);
        else if (arguments.Length != 0) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: frametimes [on|off]");
        output.WriteLine((profile.Enabled ? "Bildzeitmessung an. " : "Bildzeitmessung aus. ") + profile.Peek());
        return ResultErr.CreateSuccess();
    }

    // Gilt fuer den naechsten App-Start; eine laufende App behaelt ihre Laufzeit.
    private static ResultErr ExecuteWasmAot(string[] arguments, ITextOutput output)
    {
        bool on = arguments.Length == 1 && string.Equals(arguments[0], "on", StringComparison.OrdinalIgnoreCase);
        if (on || arguments.Length == 1 && string.Equals(arguments[0], "off", StringComparison.OrdinalIgnoreCase)) WasmEngineSelection.AotEnabled = on;
        else if (arguments.Length != 0) return ResultErr.CreateFailure(ResultCode.InvalidArgument, "Verwendung: wasmaot [on|off]");
        output.WriteLine((WasmEngineSelection.AotEnabled ? "WAMR-AOT an" : "WAMR-AOT aus") + $" (Kennung {WasmEngineSelection.AotAbi}).");
        if (!WasmEngineSelection.AotBuiltIn) output.WriteLine("WAMR-AOT ist in diesem Kernel nicht verfuegbar; Apps laufen unter wasm3.");
        return ResultErr.CreateSuccess();
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
