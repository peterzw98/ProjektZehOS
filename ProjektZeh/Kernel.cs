using System;
using System.Diagnostics;
using ProjektZeh.Runtime;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.SystemServices;
using ProjektZeh.Shared;
using ProjektZeh.Scheduling;
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
    private readonly BackgroundTaskService tasks = new();
    private readonly CosmosRoundRobinScheduler scheduler = new();

    public Kernel()
    {
        systemLog = new SystemLog(128, GetElapsedMilliseconds, new SerialLogSink());
        interactive = new InteractiveSession(CreateShell, output, GetElapsedMilliseconds, systemLog);
    }

    private ShellService CreateShell()
    {
        var files = new FileService("/data");
        return new ShellService(
        [
            .. new FileShellCommands(files).Commands,
            .. new StorageShellCommands(storage).Commands,
            .. new TaskShellCommands(tasks, DescribeScheduler).Commands,
            .. new PowerShellCommands(PreparePowerAction, Sys.Power.Reboot, Sys.Power.Shutdown).Commands
        ], systemLog, GetAvailableUptime,
            $"ZehOS {typeof(Kernel).Assembly.GetName().Version}; Cosmos {typeof(Sys.Kernel).Assembly.GetName().Version}");
    }

    protected override void BeforeRun()
    {
        systemLog.Information("Boot", "ZehOS startet.");
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
        });
        systemLog.Information("Network", "Uebersprungen: ZehOS-Netzwerkdienst noch nicht implementiert.");
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
        interactive.Run(startDesktop);
    }

    private ResultErr PreparePowerAction()
    {
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
