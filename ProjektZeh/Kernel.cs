using System;
using System.Diagnostics;
using ProjektZeh.Runtime;
using ProjektZeh.Installer;
using ProjektZeh.Update;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.SystemServices;
using ProjektZeh.Shared;
using ProjektZeh.Security;
using ProjektZeh.Scheduling;
using ProjektZeh.Messaging;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.FileSystem;
using Cosmos.Kernel.System.Input;
using Cosmos.Kernel.System.Storage;
using Cosmos.Kernel.System.Timers;
using Sys = Cosmos.Kernel.System;

namespace ProjektZeh;

public sealed class Kernel : Sys.Kernel
{
    private readonly CosmosStorageService storage = new();
    private readonly ConsoleTextOutput output = new();
    private readonly InteractiveSession interactive;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly SystemLog systemLog;
    private bool startDesktop;
    private static UpdateVersion CurrentVersion
    {
        get
        {
            var version = typeof(Kernel).Assembly.GetName().Version!;
            return new UpdateVersion(version.Major, version.Minor, version.Build);
        }
    }
    private readonly UpdateController updates = new(CurrentVersion, endpoint => new CosmosHttpUpdateSource(endpoint), new FileUpdateTarget());
#if ZEHOS_INSTALLER
    private readonly IInstallService installer;
#endif
    private readonly AccountService accounts = new("/data", new PasswordHasher(CosmosSaltSource.Fill));
    private static bool SecurityRecovery =>
#if ZEHOS_SECURITY_RECOVERY
        true;
#else
        false;
#endif
    private readonly BackgroundTaskService tasks = new();
    private readonly CosmosRoundRobinScheduler scheduler = new();
    private readonly MessageDemoService messages;

    public Kernel()
    {
        messages = new MessageDemoService(tasks);
        systemLog = new SystemLog(128, GetElapsedMilliseconds, new SerialLogSink());
#if ZEHOS_INSTALLER
        installer = new CosmosInstallService(CurrentVersion.ToString(), () =>
        {
            var prepared = PreparePowerAction();
            if (!prepared.IsOK()) return prepared;
            Sys.Power.Reboot();
            return ResultErr.CreateSuccess();
        });
#endif
        var desktopIdentity = accounts.CreateSession();
        interactive = new InteractiveSession(CreateShell(desktopIdentity), () => CreateTerminalShell(desktopIdentity),
            () => new FileService(desktopIdentity.HomeDirectory ?? "/data", access: accounts.Access, session: desktopIdentity),
            output, GetElapsedMilliseconds, systemLog, accounts, desktopIdentity, new CosmosSystemInfo(storage, VersionText).Sections);
    }

    /// <summary>Neue Terminals starten als angemeldeter Desktopbenutzer, behalten danach aber ihre eigene Identitaet.</summary>
    private ShellService CreateTerminalShell(UserSession desktopIdentity)
    {
        var identity = accounts.CreateSession();
        identity.LoginAs(desktopIdentity);
        return CreateShell(identity);
    }

    private static string VersionText =>
        $"ZehOS {typeof(Kernel).Assembly.GetName().Version}; Cosmos {typeof(Sys.Kernel).Assembly.GetName().Version}";

    private ShellService CreateShell(UserSession identity)
    {
        var files = new FileService(identity.HomeDirectory ?? "/data", access: accounts.Access, session: identity);
        ShellService? shell = null;
        shell = new ShellService(
        [
            .. new FileShellCommands(files).Commands,
            .. new UpdateShellCommands(updates, () => accounts.IsAdmin(identity)).Commands,
#if ZEHOS_INSTALLER
            // Das Live-Installationsmedium startet ohne Kontendatei in einer lokalen Einrichtungssitzung.
            .. new SetupShellCommands(installer, () => true).Commands,
#endif
            .. new StorageShellCommands(storage, () => SecurityRecovery || accounts.IsAdmin(identity), () => { accounts.Load(); }).Commands,
            .. new AccountShellCommands(accounts, identity, files, prepare => shell!.BeginSecretInput(prepare), SecurityRecovery,
                name => shell!.HasCommand(name)).Commands,
            .. new TaskShellCommands(tasks, DescribeScheduler).Commands,
            .. new MessageShellCommands(messages).Commands,
            .. new PowerShellCommands(PreparePowerAction, Sys.Power.Reboot, Sys.Power.Shutdown).Commands
        ], systemLog, GetAvailableUptime,
            $"ZehOS {typeof(Kernel).Assembly.GetName().Version}; Cosmos {typeof(Sys.Kernel).Assembly.GetName().Version}",
            command => accounts.Authorize(identity, command));
        shell.OnClose = () => accounts.ReleaseSession(identity);
        return shell;
    }

    protected override void BeforeRun()
    {
        systemLog.Information("Boot", $"ZehOS {CurrentVersion} startet.");
        var boot = new BootService(systemLog);
        boot.RunStep("Input", () => KeyboardManager.IsEnabled
            ? ResultErr.CreateSuccess()
            : ResultErr.CreateFailure(ResultCode.Failure, "Cosmos-Tastatureingabe ist deaktiviert."));
        bool forceRecovery = false;
#if ZEHOS_RECOVERY
        forceRecovery = true;
#endif
        if (!boot.InitializeDisplay(forceRecovery, InitializeDisplay, RestoreTextConsole))
        {
            systemLog.Error("Boot", "Keine Textkonsole verfuegbar; Diagnose nur seriell.");
            Stop();
            return;
        }

        boot.RunStep("Storage", () =>
        {
            if (!StorageManager.IsEnabled || !StorageManager.IsInitialized)
            {
                return ResultErr.CreateFailure(ResultCode.Failure, "Cosmos-Dateidienst ist nicht initialisiert.");
            }

            systemLog.Information("Storage", $"Erkannte Laufwerke: {StorageManager.DeviceCount}.");
#if ZEHOS_INSTALLER
            output.WriteLine("ZehOS Setup: setup list, dann setup install <Laufwerk> [--erase].");
            return ResultErr.CreateSuccess();
#else
            var mounted = storage.Mount("sata0");
            if (mounted.IsOK())
            {
                output.WriteLine("Datenlaufwerk unter /data eingebunden.");
            }
            else
            {
                output.WriteLine(mounted.Message);
                output.WriteLine("Laufwerke: disk list. Leere Testplatte: disk init sata0, dann disk mount sata0.");
            }

            return mounted;
#endif
        });
        systemLog.Information("Network", "Updates initialisieren IPv4 bei Bedarf per DHCP.");
#if !ZEHOS_INSTALLER
        var security = accounts.Load();
        if (!security.IsOK() && !accounts.NeedsSetup) output.WriteLine("Konten/Rechte gesperrt. Sicherheits-Recovery erforderlich; vorhandene Dateien werden nicht freigegeben.");
        if (SecurityRecovery) output.WriteLine("Sicherheits-Recovery aktiv: security recover <Adminname> startet die ausdrueckliche Neueinrichtung.");
#endif
        if (boot.IsRecoveryMode)
        {
            output.WriteLine("Wiederherstellungsmodus: Textshell. Diagnose: version, uptime, log.");
        }
        startDesktop = !boot.IsRecoveryMode;
        output.WriteLine("ZehOS Shell - 'help' zeigt die verfuegbaren Befehle.");
        systemLog.Information("Shell", "Textshell ist bereit.");
    }

    protected override void Run()
    {
        InstallScheduler();
#if ZEHOS_THREAD_PROBE
        if (!CosmosThreadProbe.Run())
        {
            Stop();
            return;
        }
#endif
#if ZEHOS_SECURITY_PROBE
        if (!CosmosSecurityProbe.Run()) { Stop(); return; }
#endif
#if ZEHOS_INSTALLER
        if (startDesktop)
        {
            // Der grafische Assistent ist der Normalfall; bei einem Anzeigefehler bleibt die Text-Einrichtung mit setup bedienbar.
            try { new InstallerSession(installer).Run(); }
            catch (Exception exception)
            {
                systemLog.Error("Setup", $"Grafische Einrichtung fehlgeschlagen: {exception.Message}");
                RestoreTextConsole();
                output.WriteLine("Grafische Einrichtung nicht verfuegbar. Text-Einrichtung: setup list, setup install <Laufwerk> [--erase].");
            }

            startDesktop = false;
        }
#endif
        interactive.Run(startDesktop);
    }

    private ResultErr PreparePowerAction()
    {
        if (ShellService.HasRunningPasswordOperations)
            return ResultErr.CreateFailure(ResultCode.Failure, "Kennwortoperationen laufen noch. Energieaktion nach Abschluss erneut ausfuehren.");

        if (!Sys.Power.IsSupported)
        {
            return ResultErr.CreateFailure(ResultCode.Failure,
                "cause='PowerUnsupported' message='Cosmos unterstuetzt auf diesem System keine Energieaktionen.'");
        }

        if (!tasks.StopAll(2000)) return ResultErr.CreateFailure(ResultCode.Failure, "Hintergrundaufgaben laufen noch; Energieaktion abgebrochen.");
        return VfsManager.TryGetMount("/data", out _) ? storage.Unmount() : ResultErr.CreateSuccess();
    }

#pragma warning disable COSMOS0001
    private void InstallScheduler()
    {
        Cosmos.Kernel.Core.Scheduler.SchedulerManager.SetScheduler(scheduler);
        systemLog.Information("Scheduler", $"{scheduler.Name}; Zeitscheibe={CosmosRoundRobinScheduler.QuantumNs}ns");
    }
#pragma warning restore COSMOS0001

    private string[] DescribeScheduler()
    {
        var snapshot = scheduler.Snapshot();
        return [$"{scheduler.Name}; quantum={CosmosRoundRobinScheduler.QuantumNs}ns; preemptions={snapshot.Preemptions}",
            "Auswahl (aelteste zuerst, letzte128 Thread-IDs): " + string.Join(",", snapshot.ThreadIds)];
    }

    private long GetElapsedMilliseconds()
    {
        return clock.ElapsedMilliseconds;
    }

    private long? GetAvailableUptime()
    {
        return TimerManager.IsInitialized ? GetElapsedMilliseconds() : null;
    }

    private static ResultErr InitializeDisplay()
    {
        // Die Textkonsole bleibt als Rueckfall erreichbar; der Desktop startet nach den Bootschritten.
        return RestoreTextConsole();
    }

    private static ResultErr RestoreTextConsole()
    {
        if (!KernelConsole.IsInitialized && !KernelConsole.Initialize())
        {
            return ResultErr.CreateFailure(ResultCode.Failure, "Cosmos-Textkonsole konnte nicht initialisiert werden.");
        }

        Console.Clear();
        return ResultErr.CreateSuccess();
    }
}
