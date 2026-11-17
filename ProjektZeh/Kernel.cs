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
using ProjektZeh.Network;
using ProjektZeh.Packages;
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
    private readonly CosmosSystemFiles systemFiles;
    private bool startDesktop;
    private static UpdateVersion CurrentVersion
    {
        get
        {
            var version = typeof(Kernel).Assembly.GetName().Version!;
            return new UpdateVersion(version.Major, version.Minor, version.Build);
        }
    }
    // Etappe 4.1: einmal gewaehltes Pfadprofil; Root wird erst mit 4.3 gewaehlt, bis dahin gilt immer Legacy (/data).
    private static readonly SystemPaths Paths = SystemPaths.Legacy;
#if ZEHOS_INSTALLER
    private readonly IInstallService installer;
#else
    private readonly UpdateController updates = new(CurrentVersion, endpoint => new CosmosHttpUpdateSource(endpoint),
        new FileUpdateTarget(Paths.UpdateRoot, CosmosDirectoryEntryLimit.ForDirectory), Paths.AdminRoot, CosmosDirectoryEntryLimit.ForDirectory);
#endif
    // /data ist FAT: Vergleich ohne Gross-/Kleinschreibung; der Waechter sperrt Symlinks und fremde Mounts (Etappe 3).
    private readonly AccountService accounts = new(Paths.AdminRoot, new PasswordHasher(CosmosSaltSource.Fill), Paths.Comparison,
        CosmosPathGuard.For(Paths.AdminRoot), CosmosDirectoryEntryLimit.ForDirectory)
    {
        MutationBlockedReason = CosmosInstalledIdentity.MutationBlockedReason,
    };
    // Unter einem .zehos-Verzeichnis, das der normale Dateidienst nicht freigibt; nur PackageStore schreibt dort.
    private readonly PackageStore packages = new(Paths.PackagesDirectory, PackageTrust.FromBuild(), CurrentVersion);
    private static bool SecurityRecovery =>
#if ZEHOS_SECURITY_RECOVERY
        true;
#else
        false;
#endif
    private readonly BackgroundTaskService tasks = new();
    private readonly CosmosNetworkService networkService = new();
    private readonly NetworkAutoConfiguration automaticNetwork;
    private readonly CosmosRoundRobinScheduler scheduler = new();
    private readonly MessageDemoService messages;
    private readonly UserSession desktopIdentity;

    public Kernel()
    {
        messages = new MessageDemoService(tasks);
        // Die Ersteinrichtung setzt ein tatsaechlich eingebundenes /data voraus (nicht nur eine fehlende Datei).
        accounts.IsDataMounted = () => VfsManager.TryGetMount(Paths.AdminRoot, out _);
        systemLog = new SystemLog(128, GetElapsedMilliseconds, new SerialLogSink());
        systemFiles = new CosmosSystemFiles(VersionText, GetElapsedMilliseconds, systemLog);
        automaticNetwork = new(networkService, tasks,
            text => Cosmos.Kernel.System.Diagnostics.Log.WriteString("NET " + text + "\n"));
#if ZEHOS_INSTALLER
        installer = new CosmosInstallService(CurrentVersion.ToString(), () =>
        {
            var prepared = PreparePowerAction();
            if (!prepared.IsOK()) return prepared;
            Sys.Power.Reboot();
            return ResultErr.CreateSuccess();
        });
#endif
        desktopIdentity = accounts.CreateSession();
#if ZEHOS_WASM
        // Etappe 13.4: App-Speicher unter /data mit demselben Waechter wie die Kontendatei.
        Runtime.Wasm.WasmRuntime.SavePathGuard = CosmosPathGuard.For(Paths.AdminRoot);
        Runtime.Wasm.WasmRuntime.SaveDiskSpace = CosmosSystemInfo.DiskSpace;
        Runtime.Wasm.WasmRuntime.SaveComparison = Paths.Comparison;
#endif
        interactive = new InteractiveSession(CreateShell(desktopIdentity), () => CreateTerminalShell(desktopIdentity),
            () => new FileService(desktopIdentity.HomeDirectory ?? Paths.StartDirectory, access: accounts.Access, session: desktopIdentity,
                diskSpace: CosmosSystemInfo.DiskSpace, directoryEntryLimit: CosmosDirectoryEntryLimit.ForDirectory,
                virtualSource: systemFiles.Produce, isRegularFile: CosmosSystemFiles.IsRegularFile),
            output, GetElapsedMilliseconds, systemLog, accounts, desktopIdentity, new CosmosSystemInfo(storage, VersionText).Sections,
#if ZEHOS_WASM
            ListPackageApplications, () => networkService.Status);
#else
            null, () => networkService.Status);
#endif
    }

    /// <summary>Neue Terminals starten als angemeldeter Desktopbenutzer, behalten danach aber ihre eigene Identitaet.</summary>
    private ShellService CreateTerminalShell(UserSession desktopIdentity)
    {
        var identity = accounts.CreateSession();
        identity.LoginAs(desktopIdentity);
        return CreateShell(identity);
    }

#if ZEHOS_WASM
    // Startmenueeintraege aus dem Paketbestand; beschaedigte oder nicht signierte Pakete erscheinen nicht.
    private Apps.ExternalApplication[] ListPackageApplications()
    {
        var installed = packages.ReadInstalled();
        if (!installed.IsOK()) return [];
        var apps = new Apps.ExternalApplication[installed.Value!.Length];
        for (int index = 0; index < apps.Length; index++)
        {
            string id = installed.Value[index].Manifest.Id;
            apps[index] = new(id, installed.Value[index].Manifest.Name,
                painter => Runtime.Wasm.WasmRuntime.IsBusy ? null : new Apps.Wasm.WasmGameApp(packages, id, painter,
                    text => Cosmos.Kernel.System.Diagnostics.Log.WriteString("APP " + id + " " + text + "\n"), SaveRootFor));
        }
        return apps;
    }

    /// <summary>
    /// Etappe 13.4: /save einer App fuer das angemeldete Desktopkonto, nur mit eingebundenem /data. Der Ordner wird bei
    /// Bedarf ueber einen Dateidienst ohne Rechteverwaltung mit Waechter angelegt; ohne ihn laeuft die App ohne Speichern.
    /// </summary>
    private string? SaveRootFor(string packageId)
    {
        if (!accounts.IsDataMounted() || desktopIdentity.UserId is not { } user) return null;
        var path = Paths.AppState(packageId, user);
        if (!path.IsOK()) return null;
        var created = new FileService(Paths.AdminRoot, pathGuard: CosmosPathGuard.For(Paths.AdminRoot)).CreateDirectory(path.Value!);
        if (!created.IsOK())
        {
            systemLog.Error("Apps", "App-Speicher fuer " + packageId + " nicht angelegt: " + created.Message);
            return null;
        }
        return path.Value;
    }

#endif
    // Nach jedem Einbinden, beim Boot wie bei disk mount: halbe Installationen abschliessen oder entfernen.
    private void ReconcilePackages()
    {
        var reconciled = packages.Reconcile();
        if (!reconciled.IsOK()) systemLog.Error("Packages", reconciled.Message);
    }

    private static string VersionText =>
        $"ZehOS {typeof(Kernel).Assembly.GetName().Version}{ManifestSignature.BuildLabel}; Cosmos {typeof(Sys.Kernel).Assembly.GetName().Version}";

    private ShellService CreateShell(UserSession identity)
    {
        var files = new FileService(identity.HomeDirectory ?? Paths.StartDirectory, access: accounts.Access, session: identity,
            diskSpace: CosmosSystemInfo.DiskSpace, directoryEntryLimit: CosmosDirectoryEntryLimit.ForDirectory,
            virtualSource: systemFiles.Produce, isRegularFile: CosmosSystemFiles.IsRegularFile);
        var network = new NetworkShellCommands(networkService, tasks, () => accounts.IsAdmin(identity),
            text => Cosmos.Kernel.System.Diagnostics.Log.WriteString("NET " + text + "\n"), () => identity.Generation);
        var packageCommands = new PackageShellCommands(packages, url => new CosmosHttpsPackageSource(url), path => files.IsVirtualPath(path)
                ? ResultObjectFactory.CreateFailure<string>(ResultCode.InvalidArgument, "cause='VirtualSource' message='Pakete nur aus normalen Verzeichnissen installieren.'")
                : files.ResolvePath(path), tasks,
            () => accounts.IsAdmin(identity), text => Cosmos.Kernel.System.Diagnostics.Log.WriteString("PKG " + text + "\n"), () => identity.Generation);
        ShellService? shell = null;
        shell = new ShellService(
        [
            .. new FileShellCommands(files).Commands,
#if ZEHOS_INSTALLER
            // Das Live-Installationsmedium startet ohne Kontendatei in einer lokalen Einrichtungssitzung.
            .. new SetupShellCommands(installer, () => true).Commands,
#else
            .. new UpdateShellCommands(updates, () => accounts.IsAdmin(identity)).Commands,
#endif
            .. new StorageShellCommands(storage, () => SecurityRecovery || accounts.IsAdmin(identity), () => { accounts.SetupBlockedReason = null; accounts.Load(); ReconcilePackages(); systemLog.Information("Storage", "Laufwerksaenderung abgeschlossen."); }, text => systemLog.Information("Storage", "Befehl: " + text)).Commands,
            .. new AccountShellCommands(accounts, identity, files, prepare => shell!.BeginSecretInput(prepare), SecurityRecovery,
                name => shell!.HasCommand(name)).Commands,
            .. network.Commands,
#if ZEHOS_WASM && !ZEHOS_INSTALLER
            // Ohne Wasm-Loader liessen sich Pakete zwar installieren, aber nicht starten.
            .. packageCommands.Commands,
#endif
            .. new TaskShellCommands(tasks, DescribeScheduler).Commands,
            .. new MessageShellCommands(messages).Commands,
            .. new PowerShellCommands(PreparePowerAction, Sys.Power.Reboot, Sys.Power.Shutdown).Commands
        ], systemLog, GetAvailableUptime,
            VersionText,
            command => accounts.Authorize(identity, command), files: files, getMemory: CosmosSystemInfo.MemoryText, getSessionGeneration: () => identity.Generation);
        shell.TakeJobProgress = packageCommands.TakeProgress;
        shell.TakeJobResult = () => network.TakeResult() ?? packageCommands.TakeResult();
        shell.OnClose = () => { network.Close(); packageCommands.Close(); accounts.ReleaseSession(identity); };
        return shell;
    }

    protected override void BeforeRun()
    {
        systemLog.Information("Boot", $"ZehOS {CurrentVersion}{ManifestSignature.BuildLabel} startet.");
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
            // Gasttests (scripts/test-storage-data-disk.py) lesen Groesse und Einbindung aus dem seriellen Protokoll.
            var listed = storage.GetDevices();
            if (listed.IsOK())
                foreach (var listedDevice in listed.Value!)
                    systemLog.Information("Storage", $"Laufwerk {listedDevice.Name}: {listedDevice.SizeBytes} Bytes, {listedDevice.PartitionCount} Partition(en)");
#if ZEHOS_INSTALLER
            output.WriteLine("ZehOS Setup: setup list, dann setup install <Laufwerk> [--erase].");
            return ResultErr.CreateSuccess();
#else
            var mounted = storage.MountData(out string? setupBlockedReason);
            accounts.SetupBlockedReason = setupBlockedReason;
            if (mounted.IsOK())
            {
                output.WriteLine("Datenlaufwerk unter /data eingebunden.");
                systemLog.Information("Storage", "Datenlaufwerk unter /data eingebunden.");
                ReconcilePackages();
            }
            else
            {
                output.WriteLine(mounted.Message);
                output.WriteLine("Laufwerke: disk list. Leere Platte einrichten: disk init <Name>, einbinden: disk use <Name> --yes (Konten nur beim Boot oder per security recover).");
            }

            return mounted;
#endif
        });
        // In beiden Profilen und unabhaengig von /data (Spec E5b/E6b §0.2); ein Fehler wird nur protokolliert.
        boot.RunStep("VirtualFiles", MountVirtualFiles);
#if !ZEHOS_INSTALLER
        systemLog.Information("Network", $"Cosmos: aktiviert={Cosmos.Kernel.System.Network.NetworkManager.IsEnabled}, initialisiert={Cosmos.Kernel.System.Network.NetworkManager.IsInitialized}, Adapter={Cosmos.Kernel.System.Network.NetworkManager.DeviceCount}, Link={Cosmos.Kernel.System.Network.NetworkManager.LinkUp}.");
        if (Cosmos.Kernel.System.Network.NetworkManager.IsEnabled && Cosmos.Kernel.System.Network.NetworkManager.IsInitialized)
        {
            for (int i = 0; i < Cosmos.Kernel.System.Network.NetworkManager.DeviceCount; i++)
            {
                var adapter = Cosmos.Kernel.System.Network.NetworkManager.GetAdapter(i);
                if (adapter.IsValid) systemLog.Information("Network", $"{adapter.Name}, MAC {adapter.MacAddress}, Link={adapter.LinkUp}.");
            }
        }
        // Ersteinrichtung nur beim Boot: nur dieser Load oeffnet das Fenster, spaetere (disk mount/use/init) bieten kein Konto an.
        accounts.SetupWindowOpen = true;
        var security = accounts.Load();
        accounts.SetupWindowOpen = false;
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
#if ZEHOS_WASM && !ZEHOS_INSTALLER
        // Vor dem ersten Netzabruf, solange der 8-MiB-Workerstack noch sicher Platz findet (WasmRuntime.StartWorker).
        Runtime.Wasm.WasmRuntime.StartWorker();
#endif
#if ZEHOS_NETWORK_PROBE
        if (!CosmosNetworkProbe.Run(tasks)) { Stop(); return; }
#endif
#if ZEHOS_THREAD_PROBE
        if (!CosmosThreadProbe.Run())
        {
            Stop();
            return;
        }
#endif
#if ZEHOS_SIMD_PROBE
        if (!CosmosSimdProbe.Run())
        {
            Stop();
            return;
        }
#endif
#if ZEHOS_SECURITY_PROBE
        if (!CosmosSecurityProbe.Run()) { Stop(); return; }
#endif
#if ZEHOS_STORAGE_PROBE
        if (RunStorageProbeAndPowerOff()) return;
#endif
#if ZEHOS_WASM_PROBE
        if (!CosmosWasmProbe.Run()) { Stop(); return; }
#endif
#if ZEHOS_AOT_PROBE
        if (!CosmosAotProbe.Run()) { Stop(); return; }
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
#if !ZEHOS_INSTALLER
        StartAutomaticNetwork();
#endif
        interactive.Run(startDesktop);
    }

#if ZEHOS_STORAGE_PROBE
    // scripts/test-storage-ext2-guest.py wartet nach STORAGE_PROBE DONE auf das Gastende, auch nach FAIL; der Gast
    // bootet deshalb nie weiter. Als Methode, damit der restliche Run()-Code nicht als unerreichbar gilt (CS0162).
    private bool RunStorageProbeAndPowerOff()
    {
        CosmosExt2GuestProbe.Run();
        var power = PreparePowerAction();
        if (power.IsOK()) Sys.Power.Shutdown();
        Cosmos.Kernel.System.Diagnostics.Log.WriteString($"STORAGE_PROBE shutdown FAIL {power.Message}\n");
        Stop();
        return true;
    }

#endif
    private ResultErr PreparePowerAction()
    {
        if (ShellService.HasRunningPasswordOperations)
            return ResultErr.CreateFailure(ResultCode.Failure, "Kennwortoperationen laufen noch. Energieaktion nach Abschluss erneut ausfuehren.");

        if (!Sys.Power.IsSupported)
        {
            return ResultErr.CreateFailure(ResultCode.Failure,
                "cause='PowerUnsupported' message='Cosmos unterstuetzt auf diesem System keine Energieaktionen.'");
        }

        automaticNetwork.Stop();
        if (!tasks.StopAll(2000))
        {
#if !ZEHOS_INSTALLER
            StartAutomaticNetwork();
#endif
            return ResultErr.CreateFailure(ResultCode.Failure, "Hintergrundaufgaben laufen noch; Energieaktion abgebrochen.");
        }
        var unmounted = VfsManager.TryGetMount(Paths.AdminRoot, out _) ? storage.Unmount() : ResultErr.CreateSuccess();
#if !ZEHOS_INSTALLER
        if (!unmounted.IsOK()) StartAutomaticNetwork();
#endif
        return unmounted;
    }

#if !ZEHOS_INSTALLER
    private void StartAutomaticNetwork()
    {
        var started = automaticNetwork.Start();
        if (!started.IsOK()) systemLog.Warning("Network", started.Message);
    }
#endif

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

    /// <summary>Registriert und haengt zehos-proc, zehos-sys (nur lesbar) und zehos-dev ein; die Baeume sind unabhaengig,
    /// ein Fehler verhindert die anderen nicht und alle Fehler stehen in einer Meldung.</summary>
    private ResultErr MountVirtualFiles()
    {
        var failed = new System.Collections.Generic.List<string>();
        foreach (var (name, root, flags) in new[] { ("zehos-proc", "/proc", MountFlags.ReadOnly), ("zehos-sys", "/sys", MountFlags.ReadOnly), ("zehos-dev", "/dev", MountFlags.None) })
        {
            if (!VfsManager.RegisterFileSystem(name, new CosmosVirtualFileSystemType(root, systemFiles.Produce))) failed.Add(name + " nicht registriert");
            else if (!VfsManager.TryMount(name, "", flags, root, out _)) failed.Add(root + " nicht eingehaengt");
        }

        return failed.Count == 0 ? ResultErr.CreateSuccess()
            : ResultErr.CreateFailure(ResultCode.Failure, $"cause='MountFailed' message='{string.Join(", ", failed)}.'");
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

        if (KernelConsole.Default is { } console) console.Font = ProjektZeh.Graphics.CosmosGermanFont.Default;
        Console.Clear();
        return ResultErr.CreateSuccess();
    }
}
