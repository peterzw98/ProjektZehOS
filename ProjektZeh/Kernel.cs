using System;
using System.Diagnostics;
using ProjektZeh.Runtime;
using ProjektZeh.Shell;
using ProjektZeh.Storage;
using ProjektZeh.SystemServices;
using ProjektZeh.Shared;
using ProjektZeh.Security;
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
    private readonly AccountService accounts = new("/data", new PasswordHasher(CosmosSaltSource.Fill));
    private const bool SecurityRecovery =
#if ZEHOS_SECURITY_RECOVERY
        true;
#else
        false;
#endif

    public Kernel()
    {
        systemLog = new SystemLog(128, GetElapsedMilliseconds, new SerialLogSink());
        interactive = new InteractiveSession(CreateShell, output, GetElapsedMilliseconds, systemLog);
    }

    private ShellService CreateShell()
    {
        var identity = accounts.CreateSession();
        var files = new FileService("/data", access: accounts.Access, session: identity);
        ShellService? shell = null;
        shell = new ShellService(
        [
            .. new FileShellCommands(files).Commands,
            .. new StorageShellCommands(storage, () => SecurityRecovery || accounts.IsAdmin(identity), () => { accounts.Load(); }).Commands,
            .. new AccountShellCommands(accounts, identity, files, prepare => shell!.BeginSecretInput(prepare), SecurityRecovery).Commands,
            .. new PowerShellCommands(PreparePowerAction, Sys.Power.Reboot, Sys.Power.Shutdown).Commands
        ], systemLog, GetAvailableUptime,
            $"ZehOS {typeof(Kernel).Assembly.GetName().Version}; Cosmos {typeof(Sys.Kernel).Assembly.GetName().Version}");
        shell.OnClose = () => accounts.ReleaseSession(identity);
        return shell;
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
        var security = accounts.Load();
        if (!security.IsOK()) output.WriteLine("Konten/Rechte gesperrt. Sicherheits-Recovery erforderlich; vorhandene Dateien werden nicht freigegeben.");
        if (SecurityRecovery) output.WriteLine("Sicherheits-Recovery aktiv: security recover <Adminname> startet die ausdrueckliche Neueinrichtung.");
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
        interactive.Run(startDesktop);
    }

    private ResultErr PreparePowerAction()
    {
        if (!Sys.Power.IsSupported)
        {
            return ResultErr.CreateFailure(ResultCode.Failure,
                "cause='PowerUnsupported' message='Cosmos unterstuetzt auf diesem System keine Energieaktionen.'");
        }

        return VfsManager.TryGetMount("/data", out _) ? storage.Unmount() : ResultErr.CreateSuccess();
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
